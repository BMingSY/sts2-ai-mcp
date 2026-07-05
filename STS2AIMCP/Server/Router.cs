using System.Net;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.IO;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using STS2AIMCP.Game;

namespace STS2AIMCP.Server;

internal static class Router
{
    private const string ServiceName = "sts2-ai-agent";
    private const string ProtocolVersion = "2026-03-11-v1";
    private const string ModVersion = "0.5.0";
    private const string LogPrefix = "[STS2AIMCP.Router]";

    private static long _requestCounter;

    public static async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var seq = Interlocked.Increment(ref _requestCounter);
        var requestId = $"req_{DateTime.UtcNow:yyyyMMdd_HHmmss_ffff}_{seq}";
        var request = context.Request;
        var response = context.Response;
        var stopwatch = Stopwatch.StartNew();
        var statusCode = 500;

        try
        {
            Log.Info($"{LogPrefix} {requestId} {request.HttpMethod} {request.Url?.AbsolutePath}");

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/health")
            {
                var gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown";
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = new
                    {
                        service = ServiceName,
                        mod_version = ModVersion,
                        protocol_version = ProtocolVersion,
                        v2_protocol_version = DecisionWindowService.ProtocolVersion,
                        game_version = gameVersion,
                        status = "ready",
                        compatibility = new
                        {
                            status = "untested",
                            supported_game_versions = Array.Empty<string>(),
                            expected_godot_version = "4.5.1.m.12",
                            notes = new[]
                            {
                                "STS2 updates may require a rebuilt mod.",
                                "Do not pack the PCK with Godot 4.6.x unless explicitly testing a compatibility override."
                            }
                        },
                        capabilities = new
                        {
                            decision_v2 = true,
                            decision_profiles = new[] { "ai_safe", "debug", "full" },
                            endpoints = new[]
                            {
                                "/v2/decision/current",
                                "/v2/decision/wait",
                                "/v2/decision/act",
                                "/v2/data/lookup"
                            }
                        }
                    }
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/state")
            {
                var state = await GameThread.InvokeAsync(GameStateService.BuildStatePayload);
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = state
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/actions/available")
            {
                var payload = await GameThread.InvokeAsync(GameStateService.BuildAvailableActionsPayload);
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = payload
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/events/stream")
            {
                statusCode = await HandleEventStreamAsync(response, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/v2/decision/current")
            {
                var options = new DecisionRequestOptions
                {
                    profile = request.QueryString["profile"],
                    include_raw_state = ParseBool(request.QueryString["include_raw_state"]),
                    include_relevant_game_data = ParseBool(request.QueryString["include_relevant_game_data"])
                };
                var payload = await GameThread.InvokeAsync(() => DecisionWindowService.GetCurrent(options));
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = payload
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/v2/decision/wait")
            {
                var waitRequest = await JsonHelper.DeserializeAsync<DecisionWaitRequest>(request.InputStream, cancellationToken)
                    ?? new DecisionWaitRequest();
                var payload = await WaitForDecisionAsync(waitRequest, cancellationToken);
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = payload
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/v2/decision/act")
            {
                var actRequest = await JsonHelper.DeserializeAsync<DecisionActRequest>(request.InputStream, cancellationToken);
                if (actRequest == null)
                {
                    throw new ApiException(400, "invalid_request", "Request body is required.");
                }

                var actionResponse = await GameThread.InvokeAsync(() => DecisionWindowService.ActAsync(actRequest));
                actionResponse = await FillMissingNextDecisionAsync(actionResponse, cancellationToken);
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = actionResponse
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/v2/data/lookup")
            {
                var lookupRequest = await JsonHelper.DeserializeAsync<GameDataLookupRequest>(request.InputStream, cancellationToken)
                    ?? new GameDataLookupRequest();
                var payload = await GameThread.InvokeAsync(() => DecisionWindowService.LookupGameData(lookupRequest));
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = payload
                });
                statusCode = 200;
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/action")
            {
                var actionRequest = await JsonHelper.DeserializeAsync<ActionRequest>(request.InputStream, cancellationToken);
                if (actionRequest?.action == null)
                {
                    throw new ApiException(400, "invalid_request", "Request body must contain an action field.");
                }

                var actionResponse = await GameThread.InvokeAsync(() => GameActionService.ExecuteAsync(actionRequest));
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    request_id = requestId,
                    data = actionResponse
                });
                statusCode = 200;
                return;
            }

            statusCode = 404;
            await WriteErrorAsync(response, statusCode, "not_found", "Route not found.", requestId);
        }
        catch (ApiException ex)
        {
            statusCode = ex.StatusCode;
            await WriteErrorAsync(response, ex.StatusCode, ex.Code, ex.Message, requestId, ex.Details, ex.Retryable);
        }
        catch (Exception ex)
        {
            Log.Error($"{LogPrefix} {requestId} Failed: {ex}");
            statusCode = 500;
            await WriteErrorAsync(response, statusCode, "internal_error", "Unhandled server error.", requestId);
        }
        finally
        {
            Log.Info($"{LogPrefix} {requestId} Completed {statusCode} in {stopwatch.ElapsedMilliseconds}ms");
            response.Close();
        }
    }

    public static Task WriteErrorAsync(
        HttpListenerResponse response,
        int statusCode,
        string code,
        string message,
        string? requestId = null,
        object? details = null,
        bool retryable = false)
    {
        return WriteJsonAsync(response, statusCode, new
        {
            ok = false,
            request_id = requestId ?? $"req_{DateTime.UtcNow:yyyyMMdd_HHmmss_ffff}_{Interlocked.Increment(ref _requestCounter)}",
            error = new
            {
                code,
                message,
                details,
                retryable
            }
        });
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed == "1";
    }

    private static async Task<DecisionActResponsePayload> FillMissingNextDecisionAsync(
        DecisionActResponsePayload actionResponse,
        CancellationToken cancellationToken)
    {
        if (!actionResponse.stable || actionResponse.next_decision != null)
        {
            return actionResponse;
        }

        try
        {
            var latest = await WaitForDecisionAsync(new DecisionWaitRequest
            {
                timeout_ms = 1_500,
                profile = "ai_safe",
                include_raw_state = false,
                include_relevant_game_data = false,
                after_decision_id = actionResponse.previous_decision_id
            }, cancellationToken);

            if (!latest.available || latest.decision == null)
            {
                return actionResponse;
            }

            return new DecisionActResponsePayload
            {
                action_id = actionResponse.action_id,
                kind = actionResponse.kind,
                status = actionResponse.status,
                stable = actionResponse.stable,
                message = actionResponse.message,
                previous_decision_id = actionResponse.previous_decision_id,
                next_decision = latest.decision
            };
        }
        catch (ApiException ex) when (ex.Retryable)
        {
            return actionResponse;
        }
    }

    private static async Task<DecisionCurrentPayload> WaitForDecisionAsync(
        DecisionWaitRequest waitRequest,
        CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Clamp(waitRequest.timeout_ms ?? 20_000, 100, 120_000);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        DecisionCurrentPayload? latest = null;

        while (DateTime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            latest = await GameThread.InvokeAsync(() => DecisionWindowService.GetCurrent(new DecisionRequestOptions
            {
                profile = waitRequest.profile,
                include_raw_state = waitRequest.include_raw_state,
                include_relevant_game_data = waitRequest.include_relevant_game_data
            }));

            if (latest.available &&
                latest.decision != null &&
                !string.Equals(latest.decision.decision_id, waitRequest.after_decision_id, StringComparison.Ordinal))
            {
                return latest;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        var reason = latest?.reason == "state_unstable"
            ? "state_unstable"
            : "decision_unavailable";
        var statusCode = reason == "state_unstable" ? 503 : 409;
        throw new ApiException(
            statusCode,
            reason,
            reason == "state_unstable"
                ? "Game is in a transition and should be waited on."
                : "No stable decision is currently available.",
            new
            {
                screen = latest?.screen,
                last_transition = latest?.last_transition
            },
            retryable: true);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        var json = JsonHelper.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.LongLength;

        await response.OutputStream.WriteAsync(bytes);
    }

    private static async Task<int> HandleEventStreamAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.ContentEncoding = Encoding.UTF8;
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = GameEventService.Instance.Subscribe();

        try
        {
            await WriteSseCommentAsync(response, "stream opened");

            while (!cancellationToken.IsCancellationRequested)
            {
                var waitForEvent = subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var completedTask = await Task.WhenAny(waitForEvent, heartbeat);

                if (completedTask == heartbeat)
                {
                    await WriteSseCommentAsync(response, "heartbeat");
                    continue;
                }

                if (!await waitForEvent)
                {
                    break;
                }

                while (subscription.Reader.TryRead(out var envelope))
                {
                    await WriteSseEventAsync(response, envelope);
                }
            }

            return 200;
        }
        catch (OperationCanceledException)
        {
            return 200;
        }
        catch (HttpListenerException)
        {
            // Client disconnected.
            return 200;
        }
        catch (IOException)
        {
            // Client disconnected.
            return 200;
        }
        catch (ObjectDisposedException)
        {
            // Response stream is already closed.
            return 200;
        }
    }

    private static async Task WriteSseEventAsync(HttpListenerResponse response, GameEventEnvelope envelope)
    {
        await WriteSseRawAsync(response, $"id: {envelope.event_id}\n");
        await WriteSseRawAsync(response, $"event: {envelope.type}\n");

        var json = JsonHelper.Serialize(envelope);
        foreach (var line in json.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            await WriteSseRawAsync(response, $"data: {line}\n");
        }

        await WriteSseRawAsync(response, "\n");
        await response.OutputStream.FlushAsync();
    }

    private static async Task WriteSseCommentAsync(HttpListenerResponse response, string comment)
    {
        await WriteSseRawAsync(response, $": {comment}\n\n");
        await response.OutputStream.FlushAsync();
    }

    private static ValueTask WriteSseRawAsync(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return response.OutputStream.WriteAsync(bytes);
    }
}
