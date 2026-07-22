using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using STS2AIMCP.Server;

namespace STS2AIMCP.Game;

internal static class DecisionWindowService
{
    public const string ProtocolVersion = "2026-07-18-v2-draft";

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>>? _modelDbGameDataIndex;

    internal const int DecisionVersion = 6;
    private const string DefaultProfile = "ai_safe";
    private const string ModVersion = "0.1.9";
    private static readonly TimeSpan CombatStableDelay = TimeSpan.FromMilliseconds(500);
    private static readonly object CombatStabilityGate = new();
    private static readonly object InFlightDecisionGate = new();
    private static string? _lastCombatStabilitySignature;
    private static DateTime _lastCombatStabilityChangedUtc = DateTime.MinValue;
    private static string? _inFlightDecisionId;

    private static readonly Regex DealDamageRegex = new(
        @"\bDeal\s+(?<amount>\d+)\s+damage",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GainBlockRegex = new(
        @"\bGain\s+(?<amount>\d+)\s+Block",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimesRegex = new(
        @"\b(?<amount>\d+|X)\s+times?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChineseTimesRegex = new(
        @"(?<amount>\d+|[一二两三四五六七八九十]+)\s*次",
        RegexOptions.Compiled);

    private static readonly Regex DynamicVarPlaceholderRegex = new(
        @"\{(?<name>[A-Za-z][A-Za-z0-9_]*):[^}]+\}",
        RegexOptions.Compiled);

    private static readonly Regex ApplyPowerRegex = new(
        @"\b(?:Apply|Gain)\s+(?<amount>\d+)\s+(?<power>[A-Za-z][A-Za-z '\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DecisionCurrentPayload GetCurrent(DecisionRequestOptions options)
    {
        var state = GameStateService.BuildStatePayload();
        if (!TryBuildDecision(state, options, out var decision, out var reason))
        {
            return new DecisionCurrentPayload
            {
                available = false,
                reason = reason,
                screen = state.screen,
                last_transition = state.screen,
                raw_state = options.include_raw_state ? state : null
            };
        }

        if (IsDecisionInFlight(decision.decision_id))
        {
            return new DecisionCurrentPayload
            {
                available = false,
                reason = "action_in_flight",
                screen = state.screen,
                last_transition = "action_in_flight",
                raw_state = options.include_raw_state ? state : null
            };
        }

        return new DecisionCurrentPayload
        {
            available = true,
            decision = decision,
            raw_state = options.include_raw_state ? state : null
        };
    }

    public static async Task<DecisionActResponsePayload> ActAsync(DecisionActRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.decision_id))
        {
            throw new ApiException(400, "invalid_request", "decision_id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.action_id))
        {
            throw new ApiException(400, "invalid_request", "action_id is required.");
        }

        var current = GetCurrent(new DecisionRequestOptions
        {
            profile = DefaultProfile,
            include_raw_state = true,
            include_relevant_game_data = false
        });

        if (!current.available || current.decision == null)
        {
            throw new ApiException(
                409,
                "decision_unavailable",
                "No stable decision is currently available.",
                new
                {
                    reason = current.reason,
                    screen = current.screen
                },
                retryable: true);
        }

        var decision = current.decision;
        if (!string.Equals(decision.decision_id, request.decision_id, StringComparison.Ordinal))
        {
            throw new ApiException(
                409,
                "stale_decision",
                "Decision window changed. Call wait_for_decision again.",
                new
                {
                    expected_decision_id = request.decision_id,
                    actual_decision_id = decision.decision_id
                },
                retryable: true);
        }

        var choice = decision.choices.FirstOrDefault(candidate =>
            string.Equals(candidate.action_id, request.action_id, StringComparison.Ordinal));

        if (choice == null)
        {
            throw new ApiException(
                409,
                "invalid_action",
                "action_id does not exist in this decision.",
                new
                {
                    decision_id = request.decision_id,
                    action_id = request.action_id
                });
        }

        if (IsActionHiddenByProfile(choice, DefaultProfile))
        {
            throw new ApiException(
                409,
                "action_not_allowed",
                "Action is hidden by the active profile.",
                new
                {
                    decision_id = request.decision_id,
                    action_id = request.action_id,
                    profile = DefaultProfile
                });
        }

        var actionRequest = BuildActionRequest(choice, request);
        var beforeState = current.raw_state;
        var traceCursor = ActionTraceService.Cursor;
        MarkDecisionInFlight(decision.decision_id);
        ActionResponsePayload actionResponse;
        try
        {
            actionResponse = await GameActionService.ExecuteAsync(actionRequest);
        }
        catch
        {
            ReleaseInFlightDecision(decision.decision_id);
            throw;
        }
        var actionTrace = ActionTraceService.SnapshotSince(traceCursor);
        DecisionWindowPayload? nextDecision = null;

        if (actionResponse.stable &&
            TryBuildDecision(actionResponse.state, new DecisionRequestOptions
            {
                profile = DefaultProfile,
                include_raw_state = false,
                include_relevant_game_data = false
            }, out var builtNextDecision, out _) &&
            builtNextDecision.decision_id != decision.decision_id)
        {
            nextDecision = builtNextDecision;
            ReleaseInFlightDecision(decision.decision_id);
        }

        return new DecisionActResponsePayload
        {
            action_id = choice.action_id,
            kind = choice.kind,
            status = actionResponse.status,
            stable = actionResponse.stable,
            message = actionResponse.message,
            previous_decision_id = decision.decision_id,
            result_delta = BuildResultDelta(beforeState, actionResponse.state, actionTrace),
            action_trace_cursor = traceCursor,
            action_trace = actionTrace,
            next_decision = nextDecision
        };
    }

    public static DecisionPreviewResponsePayload Preview(DecisionPreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.decision_id) || string.IsNullOrWhiteSpace(request.action_id))
        {
            throw new ApiException(400, "invalid_request", "decision_id and action_id are required.");
        }

        var current = GetCurrent(new DecisionRequestOptions
        {
            profile = DefaultProfile,
            include_raw_state = false,
            include_relevant_game_data = false
        });
        if (!current.available || current.decision == null)
        {
            throw new ApiException(409, "decision_unavailable", "No stable decision is currently available.", retryable: true);
        }

        var decision = current.decision;
        if (!string.Equals(decision.decision_id, request.decision_id, StringComparison.Ordinal))
        {
            throw new ApiException(409, "stale_decision", "Decision window changed. Read the current decision again.", new
            {
                expected_decision_id = request.decision_id,
                actual_decision_id = decision.decision_id
            }, retryable: true);
        }

        var choice = decision.choices.FirstOrDefault(candidate =>
            string.Equals(candidate.action_id, request.action_id, StringComparison.Ordinal));
        if (choice == null)
        {
            throw new ApiException(409, "invalid_action", "action_id does not exist in this decision.");
        }

        var complete = ReadPreviewCompleteness(choice.preview);
        return new DecisionPreviewResponsePayload
        {
            decision_id = decision.decision_id,
            action_id = choice.action_id,
            kind = choice.kind,
            mutation_performed = false,
            complete = complete,
            incomplete = !complete,
            source = ResolvePreviewSource(choice.kind),
            preview = choice.preview,
            coverage = new
            {
                live_engine_dynamic_values = choice.kind == "play_card",
                live_target_modifiers = choice.kind == "play_card",
                ordered_intent_hits = choice.kind == "end_turn",
                transactional_engine_dry_run = false
            },
            limitations = complete
                ? Array.Empty<string>()
                : new[]
                {
                    "The current STS2 runtime does not expose a transactional combat clone/rollback API.",
                    "Unsupported downstream hooks remain explicit in preview.unmodeled_effects instead of being guessed."
                }
        };
    }

    private static bool ReadPreviewCompleteness(object? preview)
    {
        if (preview == null)
        {
            return false;
        }

        foreach (var name in new[] { "preview_complete", "complete" })
        {
            object? value = preview is IReadOnlyDictionary<string, object?> readOnly && readOnly.TryGetValue(name, out var readOnlyValue)
                ? readOnlyValue
                : preview is IDictionary<string, object?> dictionary && dictionary.TryGetValue(name, out var dictionaryValue)
                    ? dictionaryValue
                    : preview.GetType().GetProperty(name)?.GetValue(preview);
            if (value is bool flag)
            {
                return flag;
            }
        }

        return false;
    }

    private static string ResolvePreviewSource(string kind)
    {
        return kind switch
        {
            "play_card" => "live_engine_dynamic_values_and_target_modifiers",
            "end_turn" => "ordered_live_intent_simulation",
            _ => "current_decision_metadata"
        };
    }

    private static object BuildResultDelta(GameStatePayload? before, GameStatePayload after, ActionTracePayload actionTrace)
    {
        var changes = new List<object>();

        void AddNumber(string path, int? oldValue, int? newValue)
        {
            if (oldValue == newValue)
            {
                return;
            }

            changes.Add(new
            {
                path,
                before = oldValue,
                after = newValue,
                delta = oldValue.HasValue && newValue.HasValue ? newValue.Value - oldValue.Value : (int?)null
            });
        }

        void AddValue(string path, string? oldValue, string? newValue)
        {
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                return;
            }

            changes.Add(new { path, before = oldValue, after = newValue });
        }

        AddValue("screen", before?.screen, after.screen);
        AddNumber("turn", before?.turn, after.turn);
        AddNumber("run.floor", before?.run?.floor, after.run?.floor);
        AddNumber("run.ascension", before?.run?.ascension ?? before?.character_select?.ascension, after.run?.ascension ?? after.character_select?.ascension);
        AddNumber("player.current_hp", before?.combat?.player.current_hp ?? before?.run?.current_hp, after.combat?.player.current_hp ?? after.run?.current_hp);
        AddNumber("player.block", before?.combat?.player.block, after.combat?.player.block);
        AddNumber("player.energy", before?.combat?.player.energy, after.combat?.player.energy);
        AddNumber("player.stars", before?.combat?.player.stars, after.combat?.player.stars);
        AddNumber("combat.cards_played_this_turn", before?.combat?.cards_played_this_turn, after.combat?.cards_played_this_turn);
        AddNumber("piles.draw.count", before?.combat?.piles.draw.count, after.combat?.piles.draw.count);
        AddNumber("piles.discard.count", before?.combat?.piles.discard.count, after.combat?.piles.discard.count);
        AddNumber("piles.exhaust.count", before?.combat?.piles.exhaust.count, after.combat?.piles.exhaust.count);

        if (before?.combat != null && after.combat != null)
        {
            foreach (var oldEnemy in before.combat.enemies)
            {
                var newEnemy = after.combat.enemies.FirstOrDefault(candidate =>
                    string.Equals(candidate.enemy_ref, oldEnemy.enemy_ref, StringComparison.Ordinal) ||
                    (candidate.index == oldEnemy.index && string.Equals(candidate.enemy_id, oldEnemy.enemy_id, StringComparison.OrdinalIgnoreCase)));
                if (newEnemy == null)
                {
                    continue;
                }

                var prefix = $"enemies[{oldEnemy.index}:{oldEnemy.enemy_id}]";
                AddNumber($"{prefix}.current_hp", oldEnemy.current_hp, newEnemy.current_hp);
                AddNumber($"{prefix}.block", oldEnemy.block, newEnemy.block);
            }
        }

        if (before?.run != null && after.run != null)
        {
            foreach (var oldRelic in before.run.relics)
            {
                var newRelic = after.run.relics.FirstOrDefault(candidate =>
                    string.Equals(candidate.relic_id, oldRelic.relic_id, StringComparison.OrdinalIgnoreCase));
                if (newRelic != null)
                {
                    AddNumber($"relics[{oldRelic.relic_id}].stack", oldRelic.stack, newRelic.stack);
                }
            }

            var maxPotionSlots = Math.Max(before.run.potions.Length, after.run.potions.Length);
            for (var index = 0; index < maxPotionSlots; index += 1)
            {
                var oldPotion = index < before.run.potions.Length ? before.run.potions[index].potion_id : null;
                var newPotion = index < after.run.potions.Length ? after.run.potions[index].potion_id : null;
                AddValue($"potions[{index}].potion_id", oldPotion, newPotion);
            }
        }

        return new
        {
            schema_version = 1,
            source = "pre_post_state_comparison",
            complete = false,
            changes = changes.ToArray(),
            trigger_events = actionTrace.events,
            trigger_events_complete = actionTrace.coverage.queued_game_actions && actionTrace.coverage.generic_hook_actions,
            limitations = new[]
            {
                "Only state exposed before and after the action is compared.",
                "The ordered trigger list covers queued GameAction and GenericHookGameAction execution; arbitrary callbacks that do not enqueue an action are outside trace coverage."
            }
        };
    }

    public static GameDataLookupPayload LookupGameData(GameDataLookupRequest request)
    {
        var state = GameStateService.BuildStatePayload();
        var indexes = BuildGameDataIndex(state);
        var requestedFields = request.fields?
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim())
            .ToArray() ?? Array.Empty<string>();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var item in request.items ?? Array.Empty<GameDataLookupItemRequest>())
        {
            var collection = item.collection?.Trim() ?? string.Empty;
            var id = item.id?.Trim() ?? string.Empty;
            var key = $"{collection}:{id}";
            if (string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(id))
            {
                result[key] = null;
                continue;
            }

            if (!indexes.TryGetValue(collection, out var collectionIndex) ||
                !TryGetIndexedItem(collectionIndex, id, out var value))
            {
                result[key] = null;
                continue;
            }

            result[key] = FilterFields(value, requestedFields);
        }

        return new GameDataLookupPayload
        {
            items = result,
            metadata = new Dictionary<string, object?>
            {
                ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
                ["mod_version"] = ModVersion,
                ["data_source"] = "loaded_game_model",
                ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["content_hash"] = null
            }
        };
    }

    public static GameDataExportPayload ExportGameData()
    {
        var indexes = BuildModelDbGameDataIndex();
        var collections = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var (collection, index) in indexes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var exported = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in index.Values)
            {
                if (!item.TryGetValue("id", out var idValue) || string.IsNullOrWhiteSpace(idValue?.ToString()))
                {
                    continue;
                }

                exported[idValue!.ToString()!] = item;
            }

            collections[collection] = exported;
        }

        return new GameDataExportPayload
        {
            collections = collections,
            metadata = new Dictionary<string, object?>
            {
                ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
                ["mod_version"] = ModVersion,
                ["data_source"] = "loaded_game_model",
                ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["content_hash"] = null
            }
        };
    }

    public static GameDataSearchPayload SearchGameData(GameDataSearchRequest request)
    {
        var query = request.query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ApiException(400, "invalid_request", "query is required.");
        }

        var indexes = BuildModelDbGameDataIndex();
        var requestedCollections = request.collections?
            .Where(collection => !string.IsNullOrWhiteSpace(collection))
            .Select(collection => collection.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limit = Math.Clamp(request.limit ?? 25, 1, 100);

        var matches = indexes
            .Where(pair => requestedCollections == null || requestedCollections.Count == 0 || requestedCollections.Contains(pair.Key))
            .SelectMany(collection => collection.Value.Values
                .DistinctBy(
                    item => item.TryGetValue("id", out var id) ? id?.ToString() ?? string.Empty : string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => new
                {
                    collection = collection.Key,
                    item,
                    score = ScoreGameDataMatch(item, query)
                }))
            .Where(candidate => candidate.score >= 0)
            .OrderBy(candidate => candidate.score)
            .ThenBy(candidate => candidate.collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.item.TryGetValue("id", out var id) ? id?.ToString() : null, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(candidate => new GameDataSearchItemPayload
            {
                collection = candidate.collection,
                id = candidate.item.TryGetValue("id", out var id) ? id?.ToString() ?? string.Empty : string.Empty,
                name = candidate.item.TryGetValue("name", out var name) ? name?.ToString() : null,
                model_type = candidate.item.TryGetValue("model_type", out var modelType) ? modelType?.ToString() : null,
                description = candidate.item.TryGetValue("description", out var description) ? description?.ToString() : null,
                match_rank = candidate.score
            })
            .ToArray();

        return new GameDataSearchPayload
        {
            query = query,
            collections = requestedCollections?.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>(),
            matches = matches,
            available_collections = indexes.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            metadata = BuildGameDataMetadata()
        };
    }

    public static GameDataIdListPayload ListModelIds(string? collection, string? query, int offset, int limit)
    {
        var normalizedCollection = collection?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedCollection))
        {
            throw new ApiException(400, "invalid_request", "collection is required.");
        }

        var indexes = BuildModelDbGameDataIndex();
        if (!indexes.TryGetValue(normalizedCollection, out var index))
        {
            throw new ApiException(404, "unknown_collection", $"Game-data collection '{normalizedCollection}' was not found.", new
            {
                collection = normalizedCollection,
                available_collections = indexes.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            });
        }

        var normalizedQuery = query?.Trim() ?? string.Empty;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 250);
        var allItems = index.Values
            .DistinctBy(
                item => item.TryGetValue("id", out var id) ? id?.ToString() ?? string.Empty : string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Select(item => new GameDataIdItemPayload
            {
                id = item.TryGetValue("id", out var id) ? id?.ToString() ?? string.Empty : string.Empty,
                name = item.TryGetValue("name", out var name) ? name?.ToString() : null,
                model_type = item.TryGetValue("model_type", out var modelType) ? modelType?.ToString() : null
            })
            .Where(item =>
                string.IsNullOrWhiteSpace(normalizedQuery) ||
                item.id.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                (item.name?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.model_type?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GameDataIdListPayload
        {
            collection = normalizedCollection,
            query = string.IsNullOrWhiteSpace(normalizedQuery) ? null : normalizedQuery,
            offset = safeOffset,
            limit = safeLimit,
            total = allItems.Length,
            items = allItems.Skip(safeOffset).Take(safeLimit).ToArray(),
            available_collections = indexes.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            metadata = BuildGameDataMetadata()
        };
    }

    private static int ScoreGameDataMatch(Dictionary<string, object?> item, string query)
    {
        static string Text(Dictionary<string, object?> source, string field) =>
            source.TryGetValue(field, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

        var id = Text(item, "id");
        var name = Text(item, "name");
        var modelType = Text(item, "model_type");
        var description = Text(item, "description");
        if (id.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (id.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (id.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 5;
        if (modelType.Contains(query, StringComparison.OrdinalIgnoreCase)) return 6;
        if (description.Contains(query, StringComparison.OrdinalIgnoreCase)) return 7;
        return -1;
    }

    private static Dictionary<string, object?> BuildGameDataMetadata()
    {
        return new Dictionary<string, object?>
        {
            ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
            ["mod_version"] = ModVersion,
            ["data_source"] = "loaded_game_model",
            ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
            ["content_hash"] = null
        };
    }

    private static bool TryBuildDecision(
        GameStatePayload state,
        DecisionRequestOptions options,
        out DecisionWindowPayload decision,
        out string reason)
    {
        decision = new DecisionWindowPayload();
        reason = string.Empty;

        var profile = NormalizeProfile(options.profile);
        var phase = ResolvePhase(state);
        var choices = BuildChoices(state, phase, profile);

        if (phase == "unknown")
        {
            ResetCombatStability();
            reason = "state_unstable";
            return false;
        }

        if (phase != "combat")
        {
            ResetCombatStability();
        }

        if (phase == "combat" && IsCombatDecisionUnstable(state, choices))
        {
            reason = "state_unstable";
            return false;
        }

        if (!HasMeaningfulChoices(choices) && phase != "game_over")
        {
            reason = "decision_unavailable";
            return false;
        }

        var summary = BuildSummary(state);
        var context = BuildContext(state, phase);
        var signature = BuildChoiceSignature(state, phase, summary, choices);
        var decisionId = BuildDecisionId(state, phase, signature);
        var knowledge = BuildKnowledge(state, options.include_relevant_game_data);

        decision = new DecisionWindowPayload
        {
            decision_id = decisionId,
            decision_version = DecisionVersion,
            state_version = state.state_version,
            protocol_version = ProtocolVersion,
            run_id = state.run_id,
            created_at_utc = DateTime.UtcNow.ToString("O"),
            stable = true,
            phase = phase,
            screen = state.screen,
            choice_signature = signature,
            summary = summary,
            context = context,
            choices = choices.ToArray(),
            knowledge = knowledge,
            diagnostics = options.include_raw_state ? new { raw_state = state } : null
        };
        return true;
    }

    private static string NormalizeProfile(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile)
            ? DefaultProfile
            : profile.Trim().ToLowerInvariant();

        return normalized switch
        {
            "debug" => "debug",
            "full" => "full",
            _ => DefaultProfile
        };
    }

    private static string ResolvePhase(GameStatePayload state)
    {
        if (state.modal != null)
        {
            return "modal";
        }

        if (state.reward?.pending_card_choice == true)
        {
            return "reward";
        }

        return state.screen switch
        {
            "MAIN_MENU" => "main_menu",
            "CHARACTER_SELECT" => "character_select",
            "MULTIPLAYER_LOBBY" => "character_select",
            "COMBAT" => "combat",
            "CARD_SELECTION" => "combat_selection",
            "CARDS_VIEW" => "combat_selection",
            "MAP" => "map",
            "REWARD" => "reward",
            "EVENT" => "event",
            "REST" => "rest",
            "SHOP" => "shop",
            "CHEST" => "chest",
            "GAME_OVER" => "game_over",
            _ => "unknown"
        };
    }

    private static List<DecisionChoicePayload> BuildChoices(GameStatePayload state, string phase, string profile)
    {
        var choices = new List<DecisionChoicePayload>();
        var actions = new HashSet<string>(state.available_actions, StringComparer.OrdinalIgnoreCase);

        if (state.modal != null)
        {
            if (actions.Contains("confirm_modal") || state.modal.can_confirm)
            {
                choices.Add(NoArgChoice(
                    "modal:confirm",
                    "confirm_modal",
                    "Confirm modal",
                    state.modal.confirm_label,
                    "confirm_modal",
                    state.screen));
            }

            if (actions.Contains("dismiss_modal") || state.modal.can_dismiss)
            {
                choices.Add(NoArgChoice(
                    "modal:dismiss",
                    "dismiss_modal",
                    "Dismiss modal",
                    state.modal.dismiss_label,
                    "dismiss_modal",
                    state.screen));
            }

            return choices;
        }

        if (phase != "combat")
        {
            AddRunPotionChoices(choices, state, actions, phase);
        }

        switch (phase)
        {
            case "combat":
                AddCombatChoices(choices, state, actions);
                break;
            case "combat_selection":
            case "selection":
                AddSelectionChoices(choices, state, actions, phase);
                break;
            case "map":
                AddMapChoices(choices, state);
                break;
            case "reward":
                AddRewardChoices(choices, state, actions);
                break;
            case "event":
                AddEventChoices(choices, state);
                break;
            case "rest":
                AddRestChoices(choices, state, actions);
                break;
            case "shop":
                AddShopChoices(choices, state, actions);
                break;
            case "chest":
                AddChestChoices(choices, state, actions);
                break;
            case "main_menu":
                AddMainMenuChoices(choices, state, actions, profile);
                break;
            case "character_select":
                AddCharacterSelectChoices(choices, state, actions);
                break;
            case "game_over":
                if (actions.Contains("return_to_main_menu") || state.game_over?.can_return_to_main_menu == true)
                {
                    choices.Add(NoArgChoice(
                        "game_over:return_to_main_menu",
                        "return_to_main_menu",
                        "Return to main menu",
                        null,
                        "return_to_main_menu",
                        state.screen));
                }

                break;
        }

        return choices
            .Where(choice => !IsActionHiddenByProfile(choice, profile))
            .OrderBy(choice => choice.action_id, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddCombatChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var combat = state.combat;
        if (combat == null)
        {
            return;
        }

        if (actions.Contains("use_potion") && state.run != null)
        {
            foreach (var potion in state.run.potions.Where(potion => potion.can_use))
            {
                if (potion.requires_target && potion.valid_target_indices.Length > 0)
                {
                    foreach (var targetIndex in potion.valid_target_indices)
                    {
                        var targetRef = $"{NormalizeTargetSpace(potion.target_index_space)}:{targetIndex}";
                        choices.Add(IndexChoice(
                            $"combat:use_potion:{potion.index}:{targetRef}",
                            "use_potion",
                            $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()} on {targetRef}",
                            potion.description,
                            "use_potion",
                            state.screen,
                            optionIndex: potion.index,
                            targetIndex: targetIndex,
                            sourceExtra: new Dictionary<string, object?>
                            {
                                ["potion_id"] = potion.potion_id,
                                ["target_ref"] = targetRef
                            }));
                    }
                }
                else
                {
                    choices.Add(IndexChoice(
                        $"combat:use_potion:{potion.index}",
                        "use_potion",
                        $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                        potion.description,
                        "use_potion",
                        state.screen,
                        optionIndex: potion.index,
                        sourceExtra: new Dictionary<string, object?>
                        {
                            ["potion_id"] = potion.potion_id
                        }));
                }
            }
        }

        if (actions.Contains("discard_potion") && state.run != null)
        {
            foreach (var potion in state.run.potions.Where(potion => potion.can_discard))
            {
                choices.Add(IndexChoice(
                    $"combat:discard_potion:{potion.index}",
                    "discard_potion",
                    $"Discard potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                    potion.description,
                    "discard_potion",
                    state.screen,
                    optionIndex: potion.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["potion_id"] = potion.potion_id
                    }));
            }
        }

        if (actions.Contains("play_card"))
        {
            foreach (var card in combat.hand.Where(card => card.playable))
            {
                if (card.requires_target && card.valid_target_indices.Length > 0)
                {
                    foreach (var targetIndex in card.valid_target_indices)
                    {
                        var targetRef = $"{NormalizeTargetSpace(card.target_index_space)}:{targetIndex}";
                        choices.Add(IndexChoice(
                            $"combat:play:{card.index}:{targetRef}",
                            "play_card",
                            $"Play {card.name} on {targetRef}",
                            BuildCardSummary(card.rules_text, card.keywords, card.mods),
                            "play_card",
                            state.screen,
                            cardIndex: card.index,
                            targetIndex: targetIndex,
                            sourceExtra: BuildPlayCardSource(combat, card, targetIndex, targetRef),
                            preview: BuildPlayCardPreview(combat, card, targetIndex)));
                    }
                }
                else
                {
                    choices.Add(IndexChoice(
                        $"combat:play:{card.index}",
                        "play_card",
                        $"Play {card.name}",
                        BuildCardSummary(card.rules_text, card.keywords, card.mods),
                        "play_card",
                        state.screen,
                        cardIndex: card.index,
                        sourceExtra: BuildPlayCardSource(combat, card, null, null),
                        preview: BuildPlayCardPreview(combat, card, null)));
                }
            }
        }

        if (actions.Contains("end_turn"))
        {
            var incomingDamage = combat.incoming_damage;
            var unblockedDamage = Math.Max(0, incomingDamage - combat.player.block);
            var endTurnSimulation = combat.end_turn_simulation;
            var riskTags = new List<string>();
            if (incomingDamage > 0)
            {
                riskTags.Add("incoming_damage");
            }

            if (endTurnSimulation.will_kill_player)
            {
                riskTags.Add("lethal");
            }

            if (endTurnSimulation.automatic_consumables.Length > 0)
            {
                riskTags.Add("automatic_consumable");
            }

            choices.Add(NoArgChoice(
                "combat:end_turn",
                "end_turn",
                "End turn",
                incomingDamage > 0 ? $"Incoming damage: {incomingDamage}." : null,
                "end_turn",
                state.screen,
                riskTags.ToArray(),
                new Dictionary<string, object?>
                {
                    ["incoming_damage"] = incomingDamage,
                    ["unblocked_damage"] = unblockedDamage,
                    ["player_powers"] = FormatPowers(combat.player.powers),
                    ["enemy_powers"] = combat.enemies
                        .Where(enemy => enemy.is_alive)
                        .Select(enemy => new
                        {
                            enemy.index,
                            enemy.name,
                            powers = FormatPowers(enemy.powers)
                        })
                        .ToArray()
                },
            preview: BuildEndTurnPreview(combat)));
        }
    }

    private static Dictionary<string, object?> BuildPlayCardSource(
        CombatPayload combat,
        CombatHandCardPayload card,
        int? targetIndex,
        string? targetRef)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["card_ref"] = card.card_ref,
            ["card_id"] = card.card_id,
            ["card_name"] = card.name,
            ["card_keywords"] = card.keywords,
            ["card_mods"] = card.mods,
            ["card_modifier_details"] = card.modifier_details,
            ["card_affliction_id"] = card.affliction_id,
            ["card_affliction_name"] = card.affliction_name,
            ["card_affliction_description"] = card.affliction_description,
            ["player_powers"] = FormatPowers(combat.player.powers)
        };

        if (targetIndex.HasValue)
        {
            source["target_ref"] = targetRef;

            if (string.Equals(card.target_index_space, "enemies", StringComparison.OrdinalIgnoreCase))
            {
                var enemy = combat.enemies.FirstOrDefault(candidate => candidate.index == targetIndex.Value);
                if (enemy != null)
                {
                    source["target_entity_ref"] = enemy.enemy_ref;
                    source["target_hp"] = enemy.current_hp;
                    source["target_block"] = enemy.block;
                    source["target_powers"] = FormatPowers(enemy.powers);
                    source["target_intent"] = enemy.intent;
                    source["target_intents"] = enemy.intents;
                }
            }
            else if (string.Equals(card.target_index_space, "players", StringComparison.OrdinalIgnoreCase))
            {
                if (targetIndex.Value >= 0 && targetIndex.Value < combat.players.Length)
                {
                    var player = combat.players[targetIndex.Value];
                    source["target_entity_ref"] = player.player_id;
                    source["target_hp"] = player.current_hp;
                    source["target_block"] = player.block;
                }
            }
        }

        return source;
    }

    private static object BuildPlayCardPreview(
        CombatPayload combat,
        CombatHandCardPayload card,
        int? targetIndex)
    {
        var notes = new List<string>();
        var isBound = IsBoundAffliction(card);
        if (!string.IsNullOrWhiteSpace(card.affliction_description))
        {
            notes.Add($"Affliction {card.affliction_name ?? card.affliction_id}: {card.affliction_description}");
        }

        if (isBound)
        {
            notes.Add("Only one Bound card can be played each turn; playing this card blocks the other Bound cards for the rest of the turn.");
        }

        var damageVar = ResolveReferencedDynamicVar(card, "Damage", "CalculatedDamage");
        var parsedDamageBase = ExtractFirstInt(DealDamageRegex, card.rules_text);
        var damageBase = DynamicVarInt(damageVar, value => value.enchanted_value) ?? parsedDamageBase;
        var hitCount = ResolveHitCount(card, combat.player.energy, combat.player.stars);
        var damageTargets = ResolvePreviewTargets(combat, card, targetIndex);
        var damagePerHit = damageVar != null
            ? DynamicVarInt(damageVar, value => value.preview_value)
            : damageBase.HasValue
                ? EstimateCardDamagePerHit(card, combat.player.powers, damageBase.Value, notes)
                : null;
        if (damageVar != null)
        {
            notes.Add("Used the game's live dynamic-variable preview for card damage.");
        }
        var targetResults = damagePerHit.HasValue && damageTargets.Length > 0
            ? damageTargets
                .Select(enemy => BuildDamagePreviewForEnemy(
                    card,
                    enemy,
                    damagePerHit.Value,
                    hitCount))
                .ToArray()
            : Array.Empty<object>();
        var blockVar = ResolveReferencedDynamicVar(card, "Block", "CalculatedBlock");
        var parsedBlockBase = ExtractFirstInt(GainBlockRegex, card.rules_text);
        var blockBase = DynamicVarInt(blockVar, value => value.enchanted_value) ?? parsedBlockBase;
        var blockGain = blockVar != null
            ? DynamicVarInt(blockVar, value => value.preview_value)
            : blockBase.HasValue
                ? EstimateCardBlockGain(combat.player.powers, blockBase.Value, notes)
                : null;
        if (blockVar != null)
        {
            notes.Add("Used the game's live dynamic-variable preview for card Block.");
        }
        var modifierBlockGain = EstimateModifierBlockGain(card, notes);
        if (blockGain.HasValue && modifierBlockGain != 0)
        {
            blockGain += modifierBlockGain;
        }
        var powersApplied = MergePowerPreviews(
            ExtractAppliedPowers(card.rules_text),
            BuildPowersAppliedFromCombatDynamicVars(card.dynamic_vars));
        var unmodeledEffects = new List<string>();
        if (damageTargets.Length > 0 &&
            (damageVar == null || damageTargets.Any(enemy =>
                ResolveReferencedTargetDynamicVar(card, enemy.index, "Damage", "CalculatedDamage") == null)))
        {
            unmodeledEffects.Add(
                "The game's target-aware damage preview was unavailable for at least one target; fallback damage may omit target-specific hooks.");
        }
        var blockEqualsDamage = string.Equals(card.card_id, "FISTICUFFS", StringComparison.OrdinalIgnoreCase) ||
            card.rules_text.Contains("equal to the damage dealt", StringComparison.OrdinalIgnoreCase) ||
            card.rules_text.Contains("等量于所造成伤害的格挡", StringComparison.Ordinal);
        if (blockEqualsDamage)
        {
            unmodeledEffects.Add("Block derived from actual damage dealt is target- and trigger-dependent; the Block amount is not included in the simple block estimate.");
            notes.Add("This card links Block to actual damage dealt; consult each target's final damage and treat downstream Block triggers as unresolved.");
        }

        if ((blockBase.HasValue || blockEqualsDamage) && HasPower(combat.player.powers, "JUGGERNAUT", "Juggernaut"))
        {
            unmodeledEffects.Add("Juggernaut or another on-Block trigger may deal secondary damage; secondary trigger damage is not included in damage.targets[].");
        }

        if (card.costs_x && combat.player.energy == 0)
        {
            notes.Add("Card is X-cost with current energy 0; preview assumes X=0 unless another effect modifies X.");
        }

        if (damageBase == null && blockBase == null && powersApplied.Length == 0)
        {
            notes.Add("No simple damage, block, or power amount could be parsed from card text.");
        }

        if (card.rules_text.Contains("random", StringComparison.OrdinalIgnoreCase) ||
            card.rules_text.Contains("随机", StringComparison.Ordinal) ||
            card.target_type.Contains("Random", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Random targeting/effects are not deterministic in this preview.");
            unmodeledEffects.Add("Random targeting or random effects are not resolved by this preview.");
        }

        return new
        {
            estimate_confidence = unmodeledEffects.Count > 0
                ? "partial"
                : ResolvePreviewConfidence(card, damageBase, blockBase, powersApplied, notes),
            preview_complete = unmodeledEffects.Count == 0,
            effect_scope = "direct_card_values_and_exposed_target_modifiers",
            card_id = card.card_id,
            card_name = card.name,
            card_type = card.card_type,
            energy_cost = card.energy_cost,
            star_cost = card.star_cost,
            x_value = card.costs_x ? combat.player.energy : (int?)null,
            affliction = string.IsNullOrWhiteSpace(card.affliction_id)
                ? null
                : new
                {
                    id = card.affliction_id,
                    name = card.affliction_name,
                    description = card.affliction_description,
                    amount = card.affliction_amount
                },
            shared_play_limit = isBound
                ? new
                {
                    group_id = "bound_cards",
                    max_plays_per_turn = 1,
                    affected_hand_indices = combat.hand
                        .Where(IsBoundAffliction)
                        .Select(candidate => candidate.index)
                        .ToArray()
                }
                : null,
            damage = damageBase.HasValue
                ? new
                {
                    base_per_hit = damageBase,
                    pre_target_per_hit = damagePerHit,
                    pre_target_total_damage = damagePerHit * hitCount,
                    estimated_per_hit = damagePerHit,
                    hit_count = hitCount,
                    estimated_total_before_block = damagePerHit * hitCount,
                    targets = targetResults
                }
                : null,
            block = blockBase.HasValue
                ? new
                {
                    base_amount = blockBase,
                    estimated_gain = blockGain,
                    block_after = blockGain.HasValue ? combat.player.block + blockGain.Value : (int?)null
                }
                : null,
            linked_effects = blockEqualsDamage
                ? new object[]
                {
                    new
                    {
                        type = "block_equal_to_damage_dealt",
                        source_damage = "damage.targets[].final_total_damage",
                        status = "not_fully_simulated",
                        downstream_triggers_included = false
                    }
                }
                : Array.Empty<object>(),
            powers_applied = powersApplied,
            unmodeled_effects = unmodeledEffects.Distinct(StringComparer.Ordinal).ToArray(),
            notes = notes.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static bool IsBoundAffliction(CombatHandCardPayload card)
    {
        return string.Equals(card.affliction_id, "BOUND", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.affliction_name, "Bound", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.affliction_name, "魂缚", StringComparison.Ordinal);
    }

    private static object BuildEndTurnPreview(CombatPayload combat)
    {
        var simulation = combat.end_turn_simulation;
        return new
        {
            estimate_confidence = simulation.intent_damage_complete ? "high_for_exposed_attack_intents" : "partial",
            complete = false,
            incomplete = true,
            simulation_scope = simulation.scope,
            incoming_damage = combat.incoming_damage,
            current_block = combat.player.block,
            unblocked_damage = combat.unblocked_damage,
            current_hp = combat.player.current_hp,
            hp_after = simulation.hp_after,
            block_after = simulation.block_after,
            will_kill_player = simulation.will_kill_player,
            intent_damage_complete = simulation.intent_damage_complete,
            hit_timeline = simulation.hit_timeline,
            automatic_consumables = simulation.automatic_consumables,
            unmodeled_effects = simulation.unmodeled_effects,
            lethal_risks = combat.lethal_risks,
            enemy_intents = combat.enemies
                .Where(enemy => enemy.is_alive)
                .Select(enemy => new
                {
                    enemy.index,
                    enemy.enemy_id,
                    enemy.name,
                    enemy.intent,
                    enemy.move_id,
                    enemy.intents
                })
                .ToArray()
        };
    }

    private static CombatEnemyPayload[] ResolvePreviewTargets(
        CombatPayload combat,
        CombatHandCardPayload card,
        int? targetIndex)
    {
        if (string.Equals(card.target_index_space, "enemies", StringComparison.OrdinalIgnoreCase) && targetIndex.HasValue)
        {
            return combat.enemies
                .Where(enemy => enemy.index == targetIndex.Value && enemy.is_alive)
                .ToArray();
        }

        if (card.target_type.Contains("AllEnemies", StringComparison.OrdinalIgnoreCase))
        {
            return combat.enemies
                .Where(enemy => enemy.is_alive && enemy.is_hittable)
                .ToArray();
        }

        return Array.Empty<CombatEnemyPayload>();
    }

    private static int EstimateCardDamagePerHit(
        CombatHandCardPayload card,
        CombatPowerPayload[] playerPowers,
        int baseDamage,
        List<string> notes)
    {
        var damage = baseDamage;
        if (IsAttackCard(card))
        {
            var strength = GetPowerAmount(playerPowers, "STRENGTH", "Strength");
            if (strength != 0)
            {
                damage = Math.Max(0, damage + strength);
                notes.Add($"Applied Strength {strength:+#;-#;0} to attack damage.");
            }

            if (HasPower(playerPowers, "WEAK", "Weak"))
            {
                damage = (int)Math.Floor(damage * 0.75m);
                notes.Add("Applied Weak: attack damage is reduced by 25%.");
            }
        }

        return Math.Max(0, damage);
    }

    private static object BuildDamagePreviewForEnemy(
        CombatHandCardPayload card,
        CombatEnemyPayload enemy,
        int damagePerHit,
        int hitCount)
    {
        var notes = new List<string>();
        var targetDamageVar = ResolveReferencedTargetDynamicVar(
            card,
            enemy.index,
            "Damage",
            "CalculatedDamage");
        var targetPreviewPerHit = DynamicVarInt(targetDamageVar, value => value.preview_value);
        var adjustedPerHit = targetPreviewPerHit ?? damagePerHit;
        if (targetPreviewPerHit.HasValue)
        {
            notes.Add("Used the game's target-aware dynamic-variable preview, including card calculations and global damage hooks.");
        }
        else
        {
            notes.Add("Target-aware game preview was unavailable; retained the targetless preview as a conservative fallback.");
        }

        var totalDamage = Math.Max(0, adjustedPerHit * Math.Max(1, hitCount));
        var blockDamage = Math.Min(enemy.block, totalDamage);
        var hpDamage = Math.Max(0, totalDamage - enemy.block);

        return new
        {
            target_index = enemy.index,
            enemy_id = enemy.enemy_id,
            name = enemy.name,
            current_hp = enemy.current_hp,
            current_block = enemy.block,
            pre_target_per_hit = damagePerHit,
            final_per_hit = adjustedPerHit,
            estimated_per_hit = adjustedPerHit,
            hit_count = hitCount,
            final_total_damage = totalDamage,
            estimated_total_damage = totalDamage,
            estimated_block_damage = blockDamage,
            estimated_hp_damage = hpDamage,
            estimated_remaining_hp = Math.Max(0, enemy.current_hp - hpDamage),
            will_kill = hpDamage >= enemy.current_hp && enemy.current_hp > 0,
            notes = notes.ToArray()
        };
    }

    private static int EstimateCardBlockGain(
        CombatPowerPayload[] playerPowers,
        int baseBlock,
        List<string> notes)
    {
        var block = baseBlock;
        var dexterity = GetPowerAmount(playerPowers, "DEXTERITY", "Dexterity");
        if (dexterity != 0)
        {
            block = Math.Max(0, block + dexterity);
            notes.Add($"Applied Dexterity {dexterity:+#;-#;0} to card Block.");
        }

        if (HasPower(playerPowers, "FRAIL", "Frail"))
        {
            block = (int)Math.Floor(block * 0.75m);
            notes.Add("Applied Frail: Block gained from cards is reduced by 25%.");
        }

        return Math.Max(0, block);
    }

    private static int EstimateModifierBlockGain(CombatHandCardPayload card, List<string> notes)
    {
        var additiveBlock = card.modifier_details
            .Where(modifier =>
                string.Equals(modifier.modifier_id, "ADROIT", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(modifier.description) &&
                 (modifier.description.Contains("gain", StringComparison.OrdinalIgnoreCase) ||
                  modifier.description.Contains("获得", StringComparison.Ordinal)) &&
                 (modifier.description.Contains("block", StringComparison.OrdinalIgnoreCase) ||
                  modifier.description.Contains("格挡", StringComparison.Ordinal))))
            .Sum(modifier => modifier.amount ?? 0);

        if (additiveBlock != 0)
        {
            notes.Add($"Applied structured card modifiers: Block {additiveBlock:+#;-#;0}.");
        }

        return additiveBlock;
    }

    private static object[] ExtractAppliedPowers(string rulesText)
    {
        if (string.IsNullOrWhiteSpace(rulesText))
        {
            return Array.Empty<object>();
        }

        return ApplyPowerRegex.Matches(rulesText)
            .Select(match => new
            {
                power = match.Groups["power"].Value.Trim().TrimEnd('.'),
                amount = int.TryParse(match.Groups["amount"].Value, out var amount) ? amount : 0
            })
            .Where(item => item.amount > 0 && !string.IsNullOrWhiteSpace(item.power))
            .Cast<object>()
            .ToArray();
    }

    private static object[] MergePowerPreviews(params object[][] groups)
    {
        return groups
            .SelectMany(group => group)
            .GroupBy(item => ReadAnonymousString(item, "power"), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => new
            {
                power = group.Key,
                amount = group.Sum(item => ReadAnonymousInt(item, "amount") ?? 0)
            })
            .Where(item => item.amount > 0)
            .Cast<object>()
            .ToArray();
    }

    private static object[] BuildPowersAppliedFromDynamicVars(Dictionary<string, object?> vars)
    {
        return vars
            .Where(pair => pair.Key.EndsWith("Power", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new
            {
                power = pair.Key[..^"Power".Length],
                amount = ConvertToNullableInt(pair.Value) ?? 0
            })
            .Where(item => item.amount > 0)
            .Cast<object>()
            .ToArray();
    }

    private static object[] BuildPowersAppliedFromCombatDynamicVars(
        IReadOnlyDictionary<string, CombatCardDynamicVarPayload> vars)
    {
        return vars
            .Where(pair => pair.Key.EndsWith("Power", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new
            {
                power = pair.Key[..^"Power".Length],
                amount = DecimalToInt(pair.Value.preview_value)
            })
            .Where(item => item.amount > 0)
            .Cast<object>()
            .ToArray();
    }

    private static string ReadAnonymousString(object item, string propertyName)
    {
        return ReadMemberValue(item, propertyName)?.ToString() ?? string.Empty;
    }

    private static int? ReadAnonymousInt(object item, string propertyName)
    {
        return ConvertToNullableInt(ReadMemberValue(item, propertyName));
    }

    private static int ResolveHitCount(CombatHandCardPayload card, int energy, int stars)
    {
        var repeatVar = ResolveReferencedDynamicVar(card, "Repeat", "Hits", "HitCount");
        var repeat = DynamicVarInt(repeatVar, value => value.preview_value);
        if (repeat.HasValue)
        {
            return Math.Max(1, repeat.Value);
        }

        var match = TimesRegex.Match(card.rules_text ?? string.Empty);
        if (match.Success)
        {
            var value = match.Groups["amount"].Value;
            if (value.Equals("X", StringComparison.OrdinalIgnoreCase))
            {
                if (card.star_costs_x)
                {
                    return Math.Max(0, stars);
                }

                return Math.Max(0, energy);
            }

            return int.TryParse(value, out var parsed) ? Math.Max(1, parsed) : 1;
        }

        var chineseMatch = ChineseTimesRegex.Match(card.rules_text ?? string.Empty);
        if (chineseMatch.Success)
        {
            return ParseChineseCount(chineseMatch.Groups["amount"].Value) ?? 1;
        }

        return 1;
    }

    private static CombatCardDynamicVarPayload? ResolveReferencedDynamicVar(
        CombatHandCardPayload card,
        params string[] fallbackNames)
    {
        return ResolveReferencedDynamicVar(card.raw_rules_text, card.dynamic_vars, fallbackNames);
    }

    private static CombatCardDynamicVarPayload? ResolveReferencedTargetDynamicVar(
        CombatHandCardPayload card,
        int targetIndex,
        params string[] fallbackNames)
    {
        return card.target_dynamic_vars.TryGetValue(targetIndex, out var dynamicVars)
            ? ResolveReferencedDynamicVar(card.raw_rules_text, dynamicVars, fallbackNames)
            : null;
    }

    private static CombatCardDynamicVarPayload? ResolveReferencedDynamicVar(
        string? rawRulesText,
        IReadOnlyDictionary<string, CombatCardDynamicVarPayload> dynamicVars,
        params string[] fallbackNames)
    {
        foreach (Match match in DynamicVarPlaceholderRegex.Matches(rawRulesText ?? string.Empty))
        {
            var name = match.Groups["name"].Value;
            if (fallbackNames.Any(fallback =>
                    name.Contains(fallback, StringComparison.OrdinalIgnoreCase)) &&
                dynamicVars.TryGetValue(name, out var referenced))
            {
                return referenced;
            }
        }

        foreach (var name in fallbackNames)
        {
            if (dynamicVars.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? DynamicVarInt(
        CombatCardDynamicVarPayload? value,
        Func<CombatCardDynamicVarPayload, decimal> selector)
    {
        return value == null ? null : DecimalToInt(selector(value));
    }

    private static int DecimalToInt(decimal value)
    {
        return (int)Math.Truncate(value);
    }

    private static int? ParseChineseCount(string value)
    {
        if (int.TryParse(value, out var numeric))
        {
            return Math.Max(1, numeric);
        }

        return value switch
        {
            "一" => 1,
            "二" or "两" => 2,
            "三" => 3,
            "四" => 4,
            "五" => 5,
            "六" => 6,
            "七" => 7,
            "八" => 8,
            "九" => 9,
            "十" => 10,
            _ => null
        };
    }

    private static int? ExtractFirstInt(Regex regex, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = regex.Match(text);
        return match.Success && int.TryParse(match.Groups["amount"].Value, out var amount)
            ? amount
            : null;
    }

    private static bool IsAttackCard(CombatHandCardPayload card)
    {
        return string.Equals(card.card_type, "Attack", StringComparison.OrdinalIgnoreCase) ||
            card.rules_text.Contains("Deal", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPowerAmount(IEnumerable<CombatPowerPayload> powers, string powerId, string name)
    {
        return powers
            .Where(power =>
                PowerIdMatches(power.power_id, powerId) ||
                string.Equals(power.name, name, StringComparison.OrdinalIgnoreCase))
            .Sum(power => power.amount ?? 0);
    }

    private static bool HasPower(IEnumerable<CombatPowerPayload> powers, string powerId, string name)
    {
        return powers.Any(power =>
            (PowerIdMatches(power.power_id, powerId) ||
             string.Equals(power.name, name, StringComparison.OrdinalIgnoreCase)) &&
            (power.amount ?? 1) != 0);
    }

    private static bool PowerIdMatches(string? actual, string expected)
    {
        static string Normalize(string? value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            return normalized.EndsWith("_POWER", StringComparison.OrdinalIgnoreCase)
                ? normalized[..^"_POWER".Length]
                : normalized;
        }

        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePreviewConfidence(
        CombatHandCardPayload card,
        int? damageBase,
        int? blockBase,
        object[] powersApplied,
        IReadOnlyCollection<string> notes)
    {
        if (damageBase.HasValue || blockBase.HasValue || powersApplied.Length > 0)
        {
            if (card.rules_text.Contains("random", StringComparison.OrdinalIgnoreCase) ||
                notes.Any(note => note.Contains("could be parsed", StringComparison.OrdinalIgnoreCase)))
            {
                return "medium";
            }

            return "medium";
        }

        return "low";
    }

    private static string? BuildCardSummary(
        string? rulesText,
        string[]? keywords,
        string[]? mods)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rulesText))
        {
            parts.Add(rulesText);
        }

        if (keywords is { Length: > 0 })
        {
            parts.Add($"Keywords: {string.Join(", ", keywords)}");
        }

        if (mods is { Length: > 0 })
        {
            parts.Add($"Mods: {string.Join(", ", mods)}");
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static object[] FormatPowers(IEnumerable<CombatPowerPayload> powers)
    {
        return powers
            .Select(power => new
            {
                power.power_id,
                power.name,
                power.description,
                power.amount,
                power.is_debuff,
                power.stack_type,
                line = power.amount.HasValue
                    ? $"{power.name} {power.amount.Value}"
                    : power.name
            })
            .Cast<object>()
            .ToArray();
    }

    private static void AddSelectionChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions,
        string phase)
    {
        if (state.selection != null && actions.Contains("select_card_bundle"))
        {
            foreach (var bundle in state.selection.bundles)
            {
                var names = string.Join(", ", bundle.cards.Select(card => card.name));
                var summary = string.Join(" | ", bundle.cards.Select(card =>
                    $"{card.name}: {BuildCardSummary(card.rules_text, card.keywords, card.mods)}"));
                choices.Add(IndexChoice(
                    $"{phase}:select_card_bundle:{bundle.index}",
                    "select_card_bundle",
                    $"Select card bundle {bundle.index + 1}: {names}",
                    summary,
                    "select_card_bundle",
                    state.screen,
                    optionIndex: bundle.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["bundle_index"] = bundle.index,
                        ["cards"] = bundle.cards.Select(card => new
                        {
                            card.card_id,
                            card.name,
                            card.rules_text,
                            card.keywords,
                            card.mods
                        }).ToArray()
                    }));
            }
        }

        if (state.selection != null && actions.Contains("select_deck_card"))
        {
            foreach (var card in state.selection.cards)
            {
                choices.Add(IndexChoice(
                    $"{phase}:select_deck_card:{card.index}",
                    "select_deck_card",
                    $"Select {card.name}",
                    BuildCardSummary(card.rules_text, card.keywords, card.mods),
                    "select_deck_card",
                    state.screen,
                    optionIndex: card.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["card_ref"] = card.card_ref,
                        ["card_id"] = card.card_id,
                        ["card_name"] = card.name,
                        ["keywords"] = card.keywords,
                        ["mods"] = card.mods,
                        ["selectable"] = card.selectable
                    }));
            }
        }

        if (actions.Contains("confirm_selection") || state.selection?.can_confirm == true)
        {
            choices.Add(NoArgChoice(
                $"{phase}:confirm_selection",
                "confirm_selection",
                "Confirm selection",
                state.selection?.prompt,
                "confirm_selection",
                state.screen));
        }

        if (actions.Contains("close_cards_view"))
        {
            choices.Add(NoArgChoice(
                $"{phase}:close_cards_view",
                "close_cards_view",
                "Close cards view",
                null,
                "close_cards_view",
                state.screen));
        }
    }

    private static void AddRunPotionChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions,
        string phase)
    {
        if (state.run == null)
        {
            return;
        }

        var scope = string.IsNullOrWhiteSpace(phase) ? "run" : phase;

        if (actions.Contains("use_potion"))
        {
            foreach (var potion in state.run.potions.Where(potion => potion.can_use))
            {
                if (potion.requires_target && potion.valid_target_indices.Length > 0)
                {
                    foreach (var targetIndex in potion.valid_target_indices)
                    {
                        var targetRef = $"{NormalizeTargetSpace(potion.target_index_space)}:{targetIndex}";
                        choices.Add(IndexChoice(
                            $"{scope}:use_potion:{potion.index}:{targetRef}",
                            "use_potion",
                            $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()} on {targetRef}",
                            potion.description,
                            "use_potion",
                            state.screen,
                            optionIndex: potion.index,
                            targetIndex: targetIndex,
                            sourceExtra: new Dictionary<string, object?>
                            {
                                ["potion_id"] = potion.potion_id,
                                ["target_ref"] = targetRef,
                                ["usage"] = potion.usage
                            }));
                    }
                }
                else
                {
                    choices.Add(IndexChoice(
                        $"{scope}:use_potion:{potion.index}",
                        "use_potion",
                        $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                        potion.description,
                        "use_potion",
                        state.screen,
                        optionIndex: potion.index,
                        sourceExtra: new Dictionary<string, object?>
                        {
                            ["potion_id"] = potion.potion_id,
                            ["usage"] = potion.usage
                        }));
                }
            }
        }

        if (actions.Contains("discard_potion"))
        {
            var hasPotionReward = state.reward?.rewards.Any(reward =>
                string.Equals(reward.reward_type, "Potion", StringComparison.OrdinalIgnoreCase)) == true;

            foreach (var potion in state.run.potions.Where(potion => potion.can_discard))
            {
                choices.Add(IndexChoice(
                    $"{scope}:discard_potion:{potion.index}",
                    "discard_potion",
                    hasPotionReward
                        ? $"Discard potion {potion.name ?? potion.potion_id ?? potion.index.ToString()} to make room"
                        : $"Discard potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                    potion.description,
                    "discard_potion",
                    state.screen,
                    optionIndex: potion.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["potion_id"] = potion.potion_id,
                        ["opens_reward_potion_slot"] = hasPotionReward
                    }));
            }
        }
    }

    private static void AddMapChoices(List<DecisionChoicePayload> choices, GameStatePayload state)
    {
        if (state.map == null)
        {
            return;
        }

        foreach (var node in state.map.available_nodes)
        {
            choices.Add(IndexChoice(
                $"map:choose:{node.index}:{node.row}:{node.col}",
                "choose_map_node",
                $"Choose {node.node_type} node ({node.row},{node.col})",
                null,
                "choose_map_node",
                state.screen,
                optionIndex: node.index,
                riskTags: new[] { "irreversible" },
                sourceExtra: new Dictionary<string, object?>
                {
                    ["row"] = node.row,
                    ["col"] = node.col,
                    ["node_type"] = node.node_type,
                    ["state"] = node.state
                }));
        }
    }

    private static void AddRewardChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var reward = state.reward;
        if (reward == null)
        {
            return;
        }

        if (actions.Contains("claim_reward"))
        {
            foreach (var option in reward.rewards.Where(option => option.claimable))
            {
                var isPotion = string.Equals(option.reward_type, "Potion", StringComparison.OrdinalIgnoreCase);
                var rewardName = string.IsNullOrWhiteSpace(option.name) ? option.description : option.name;
                var summary = string.IsNullOrWhiteSpace(option.details) ? option.description : option.details;
                if (isPotion)
                {
                    summary = $"Potion belt has an open slot; claiming this reward does not require discarding a potion. {summary}";
                }

                choices.Add(IndexChoice(
                    $"reward:claim:{option.index}",
                    "claim_reward",
                    $"Claim {option.reward_type}: {rewardName}",
                    summary,
                    "claim_reward",
                    state.screen,
                    optionIndex: option.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["reward_type"] = option.reward_type,
                        ["reward_item_id"] = option.item_id,
                        ["potion_id"] = isPotion ? option.item_id : null,
                        ["potion_slot_available"] = isPotion ? true : null,
                        ["requires_potion_discard"] = isPotion ? false : null
                    }));
            }
        }

        if (actions.Contains("choose_reward_card"))
        {
            foreach (var card in reward.card_options)
            {
                choices.Add(IndexChoice(
                    $"reward:choose_card:{card.index}",
                    "choose_reward_card",
                    $"Choose reward card {card.name}",
                    BuildCardSummary(card.rules_text, card.keywords, card.mods),
                    "choose_reward_card",
                    state.screen,
                    optionIndex: card.index,
                    riskTags: new[] { "deck_thickening", "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["card_id"] = card.card_id,
                        ["card_name"] = card.name,
                        ["keywords"] = card.keywords,
                        ["mods"] = card.mods
                    }));
            }
        }

        if (actions.Contains("skip_reward_cards"))
        {
            foreach (var alternative in reward.alternatives.DefaultIfEmpty(new RewardAlternativePayload
            {
                index = 0,
                label = "Skip"
            }))
            {
                choices.Add(IndexChoice(
                    $"reward:skip_cards:{alternative.index}",
                    "skip_reward_cards",
                    string.IsNullOrWhiteSpace(alternative.label) ? "Skip reward cards" : alternative.label,
                    null,
                    "skip_reward_cards",
                    state.screen,
                    optionIndex: alternative.index));
            }
        }

        if (reward.can_proceed && actions.Contains("skip_rewards_and_proceed"))
        {
            var hasRemainingRewards = reward.rewards.Any(option => option.claimable);
            choices.Add(NoArgChoice(
                "reward:proceed",
                "proceed",
                hasRemainingRewards ? "Skip remaining rewards and proceed" : "Proceed",
                hasRemainingRewards ? "Leave any remaining reward items unclaimed and return to the map." : null,
                "skip_rewards_and_proceed",
                state.screen,
                hasRemainingRewards ? new[] { "irreversible" } : null,
                new Dictionary<string, object?>
                {
                    ["skips_remaining_rewards"] = hasRemainingRewards
                }));
        }
    }

    private static void AddEventChoices(List<DecisionChoicePayload> choices, GameStatePayload state)
    {
        if (state.@event == null)
        {
            return;
        }

        foreach (var option in state.@event.options.Where(option => !option.is_locked))
        {
            var kind = option.is_proceed ? "proceed" : "choose_event_option";
            var riskTags = new List<string>();
            if (option.will_kill_player)
            {
                riskTags.Add("lethal");
            }
            else if (LooksLikeHpLoss(option))
            {
                riskTags.Add("hp_loss");
            }

            if (LooksLikeCurse(option))
            {
                riskTags.Add("curse");
            }

            if (!option.is_proceed)
            {
                riskTags.Add("irreversible");
            }

            choices.Add(IndexChoice(
                $"event:option:{option.index}",
                kind,
                string.IsNullOrWhiteSpace(option.title) ? $"Choose event option {option.index}" : option.title,
                option.description,
                "choose_event_option",
                state.screen,
                optionIndex: option.index,
                riskTags: riskTags.ToArray(),
                sourceExtra: new Dictionary<string, object?>
                {
                    ["event_id"] = state.@event.event_id,
                    ["text_key"] = option.text_key,
                    ["is_proceed"] = option.is_proceed
                }));
        }
    }

    private static void AddRestChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        if (state.rest == null)
        {
            return;
        }

        if (actions.Contains("choose_rest_option"))
        {
            foreach (var option in state.rest.options.Where(option => option.is_enabled))
            {
                choices.Add(IndexChoice(
                    $"rest:option:{option.index}:{option.option_id}",
                    "choose_rest_option",
                    option.title,
                    option.description,
                    "choose_rest_option",
                    state.screen,
                    optionIndex: option.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["option_id"] = option.option_id
                    }));
            }
        }

        if (actions.Contains("proceed"))
        {
            choices.Add(NoArgChoice("rest:proceed", "proceed", "Proceed", null, "proceed", state.screen));
        }
    }

    private static void AddShopChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var shop = state.shop;
        if (shop == null)
        {
            return;
        }

        if (actions.Contains("proceed"))
        {
            choices.Add(NoArgChoice("shop:proceed", "proceed", "Proceed", null, "proceed", state.screen));
        }

        if (actions.Contains("open_shop_inventory") || shop.can_open)
        {
            choices.Add(NoArgChoice("shop:open_inventory", "open_shop_inventory", "Open shop inventory", null, "open_shop_inventory", state.screen));
        }

        if (actions.Contains("close_shop_inventory") || shop.can_close)
        {
            choices.Add(NoArgChoice("shop:close_inventory", "close_shop_inventory", "Close shop inventory", null, "close_shop_inventory", state.screen));
        }

        if (actions.Contains("buy_card"))
        {
            foreach (var card in shop.cards.Where(card => card.is_stocked && card.enough_gold))
            {
                choices.Add(IndexChoice(
                    $"shop:buy_card:{card.index}",
                    "buy_card",
                    $"Buy {card.name} ({card.price}g)",
                    BuildCardSummary(card.rules_text, card.keywords, card.mods),
                    "buy_card",
                    state.screen,
                    optionIndex: card.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["card_id"] = card.card_id,
                        ["card_name"] = card.name,
                        ["keywords"] = card.keywords,
                        ["mods"] = card.mods,
                        ["price"] = card.price
                    }));
            }
        }

        if (actions.Contains("buy_relic"))
        {
            foreach (var relic in shop.relics.Where(relic => relic.is_stocked && relic.enough_gold))
            {
                choices.Add(IndexChoice(
                    $"shop:buy_relic:{relic.index}",
                    "buy_relic",
                    $"Buy {relic.name} ({relic.price}g)",
                    null,
                    "buy_relic",
                    state.screen,
                    optionIndex: relic.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["relic_id"] = relic.relic_id,
                        ["relic_name"] = relic.name,
                        ["price"] = relic.price
                    }));
            }
        }

        if (actions.Contains("buy_potion"))
        {
            foreach (var potion in shop.potions.Where(potion => potion.is_stocked && potion.enough_gold))
            {
                choices.Add(IndexChoice(
                    $"shop:buy_potion:{potion.index}",
                    "buy_potion",
                    $"Buy {potion.name ?? potion.potion_id ?? potion.index.ToString()} ({potion.price}g)",
                    potion.usage,
                    "buy_potion",
                    state.screen,
                    optionIndex: potion.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["potion_id"] = potion.potion_id,
                        ["potion_name"] = potion.name,
                        ["price"] = potion.price
                    }));
            }
        }

        if (actions.Contains("remove_card_at_shop") &&
            shop.card_removal is { available: true, enough_gold: true, used: false })
        {
            choices.Add(NoArgChoice(
                "shop:remove_card",
                "remove_card_at_shop",
                $"Remove a card ({shop.card_removal.price}g)",
                null,
                "remove_card_at_shop",
                state.screen,
                new[] { "irreversible" },
                new Dictionary<string, object?>
                {
                    ["price"] = shop.card_removal.price
                }));
        }
    }

    private static void AddChestChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        if (state.chest == null)
        {
            return;
        }

        if (actions.Contains("open_chest") && !state.chest.is_opened)
        {
            choices.Add(NoArgChoice("chest:open", "open_chest", "Open chest", null, "open_chest", state.screen));
        }

        foreach (var relic in state.chest.relic_options)
        {
            choices.Add(IndexChoice(
                $"chest:choose_relic:{relic.index}",
                "choose_treasure_relic",
                $"Choose {relic.name}",
                null,
                "choose_treasure_relic",
                state.screen,
                optionIndex: relic.index,
                riskTags: new[] { "irreversible" },
                sourceExtra: new Dictionary<string, object?>
                {
                    ["relic_id"] = relic.relic_id,
                    ["relic_name"] = relic.name
                }));
        }

        if (actions.Contains("proceed") && state.chest.is_opened && state.chest.relic_options.Length == 0)
        {
            choices.Add(NoArgChoice("chest:proceed", "proceed", "Proceed", null, "proceed", state.screen));
        }
    }

    private static void AddMainMenuChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions,
        string profile)
    {
        if (actions.Contains("continue_run"))
        {
            choices.Add(NoArgChoice("main_menu:continue_run", "continue_run", "Continue run", null, "continue_run", state.screen));
        }

        if (actions.Contains("open_character_select"))
        {
            choices.Add(NoArgChoice("main_menu:open_character_select", "open_character_select", "Open character select", null, "open_character_select", state.screen));
        }

        if (actions.Contains("open_timeline"))
        {
            choices.Add(NoArgChoice("main_menu:open_timeline", "open_timeline", "Open timeline", null, "open_timeline", state.screen));
        }

        if (actions.Contains("close_main_menu_submenu"))
        {
            choices.Add(NoArgChoice("main_menu:close_submenu", "close_main_menu_submenu", "Close submenu", null, "close_main_menu_submenu", state.screen));
        }

        if (actions.Contains("choose_timeline_epoch") && state.timeline != null)
        {
            foreach (var slot in state.timeline.slots.Where(slot => slot.is_actionable))
            {
                choices.Add(IndexChoice(
                    $"main_menu:choose_timeline_epoch:{slot.index}",
                    "choose_timeline_epoch",
                    $"Choose timeline epoch {slot.title}",
                    slot.state,
                    "choose_timeline_epoch",
                    state.screen,
                    optionIndex: slot.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["epoch_id"] = slot.epoch_id
                    }));
            }
        }

        if (actions.Contains("confirm_timeline_overlay"))
        {
            choices.Add(NoArgChoice("main_menu:confirm_timeline_overlay", "confirm_timeline_overlay", "Confirm timeline overlay", null, "confirm_timeline_overlay", state.screen));
        }

        if (actions.Contains("abandon_run") && profile != DefaultProfile)
        {
            choices.Add(NoArgChoice(
                "main_menu:abandon_run",
                "abandon_run",
                "Abandon run",
                null,
                "abandon_run",
                state.screen,
                new[] { "irreversible", "debug_only" }));
        }
    }

    private static void AddCharacterSelectChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var characterSelect = state.character_select ?? new CharacterSelectPayload
        {
            characters = state.multiplayer_lobby?.characters ?? Array.Empty<CharacterSelectOptionPayload>(),
            can_embark = false
        };

        if (actions.Contains("select_character"))
        {
            foreach (var character in characterSelect.characters.Where(character => !character.is_locked && !character.is_random))
            {
                var characterMaxAscension = character.max_ascension ?? characterSelect.max_ascension;
                for (var ascension = 0; ascension <= characterMaxAscension; ascension += 1)
                {
                    if (character.is_selected && ascension == characterSelect.ascension)
                    {
                        continue;
                    }

                    choices.Add(IndexChoice(
                        $"character_select:select:{character.character_id}:a{ascension}",
                        "select_character",
                        $"Select {character.name} / A{ascension}",
                        $"Set character and exact ascension in one action (unlocked range: A0-A{characterMaxAscension}).",
                        "select_character",
                        state.screen,
                        optionIndex: character.index,
                        sourceExtra: new Dictionary<string, object?>
                        {
                            ["character_id"] = character.character_id,
                            ["character_name"] = character.name,
                            ["ascension"] = ascension
                        }));
                }
            }
        }

        if (actions.Contains("close_main_menu_submenu"))
        {
            choices.Add(NoArgChoice(
                "character_select:close_main_menu_submenu",
                "close_main_menu_submenu",
                "Close character select",
                null,
                "close_main_menu_submenu",
                state.screen));
        }

        if (actions.Contains("embark") || characterSelect.can_embark)
        {
            choices.Add(NoArgChoice("character_select:embark", "embark", "Embark", null, "embark", state.screen));
        }

        if (actions.Contains("ready_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:ready", "ready_multiplayer_lobby", "Ready", null, "ready_multiplayer_lobby", state.screen));
        }

        if (actions.Contains("unready") || characterSelect.can_unready)
        {
            choices.Add(NoArgChoice("character_select:unready", "unready", "Unready", null, "unready", state.screen));
        }

        if (actions.Contains("host_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:host_multiplayer_lobby", "host_multiplayer_lobby", "Host multiplayer lobby", null, "host_multiplayer_lobby", state.screen));
        }

        if (actions.Contains("join_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:join_multiplayer_lobby", "join_multiplayer_lobby", "Join multiplayer lobby", null, "join_multiplayer_lobby", state.screen));
        }

        if (actions.Contains("disconnect_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:disconnect_multiplayer_lobby", "disconnect_multiplayer_lobby", "Disconnect multiplayer lobby", null, "disconnect_multiplayer_lobby", state.screen));
        }
    }

    private static DecisionChoicePayload NoArgChoice(
        string actionId,
        string kind,
        string label,
        string? summary,
        string v1Action,
        string screen,
        string[]? riskTags = null,
        Dictionary<string, object?>? sourceExtra = null,
        object? preview = null)
    {
        return BuildChoice(actionId, kind, label, summary, v1Action, screen, null, null, null, riskTags, sourceExtra, preview);
    }

    private static DecisionChoicePayload IndexChoice(
        string actionId,
        string kind,
        string label,
        string? summary,
        string v1Action,
        string screen,
        int? cardIndex = null,
        int? optionIndex = null,
        int? targetIndex = null,
        string[]? riskTags = null,
        Dictionary<string, object?>? sourceExtra = null,
        object? preview = null)
    {
        return BuildChoice(actionId, kind, label, summary, v1Action, screen, cardIndex, optionIndex, targetIndex, riskTags, sourceExtra, preview);
    }

    private static DecisionChoicePayload BuildChoice(
        string actionId,
        string kind,
        string label,
        string? summary,
        string v1Action,
        string screen,
        int? cardIndex,
        int? optionIndex,
        int? targetIndex,
        string[]? riskTags,
        Dictionary<string, object?>? sourceExtra,
        object? preview)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["screen"] = screen,
            ["v1_action"] = v1Action
        };

        if (cardIndex != null)
        {
            source["card_index"] = cardIndex.Value;
        }

        if (optionIndex != null)
        {
            source["option_index"] = optionIndex.Value;
        }

        if (targetIndex != null)
        {
            source["target_index"] = targetIndex.Value;
        }

        if (sourceExtra != null)
        {
            foreach (var (key, value) in sourceExtra)
            {
                source[key] = value;
            }
        }

        var tags = riskTags ?? Array.Empty<string>();
        return new DecisionChoicePayload
        {
            action_id = actionId,
            kind = kind,
            label = label,
            summary = summary,
            risk_tags = tags,
            attention = ResolveAttention(tags),
            source = source,
            params_schema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false
            },
            preview = preview
        };
    }

    private static string ResolveAttention(string[] riskTags)
    {
        if (riskTags.Contains("lethal"))
        {
            return "danger";
        }

        if (riskTags.Any(tag => tag is "incoming_damage" or "hp_loss" or "curse" or "irreversible" or "debug_only"))
        {
            return "caution";
        }

        return "normal";
    }

    private static bool IsActionHiddenByProfile(DecisionChoicePayload choice, string profile)
    {
        if (profile != DefaultProfile)
        {
            return false;
        }

        return choice.risk_tags.Contains("auto_flow") || choice.risk_tags.Contains("debug_only");
    }

    private static ActionRequest BuildActionRequest(DecisionChoicePayload choice, DecisionActRequest request)
    {
        var source = choice.source;
        return new ActionRequest
        {
            action = GetString(source, "v1_action") ?? choice.kind,
            card_index = GetInt(source, "card_index"),
            option_index = GetInt(source, "option_index"),
            target_index = GetInt(source, "target_index"),
            character_id = GetString(source, "character_id"),
            ascension = GetInt(source, "ascension"),
            client_context = new
            {
                source = "v2_decision",
                request.decision_id,
                request.action_id,
                request.client_note
            }
        };
    }

    private static string BuildDecisionId(GameStatePayload state, string phase, string signature)
    {
        var floor = state.run?.floor ?? 0;
        var turn = state.turn ?? 0;
        var shortHash = signature.StartsWith("sha256:", StringComparison.Ordinal)
            ? signature["sha256:".Length..Math.Min(signature.Length, "sha256:".Length + 12)]
            : HashText(signature)[..12];
        return $"{SanitizeIdPart(state.run_id)}:f{floor}:{state.screen.ToLowerInvariant()}:{phase}:t{turn}:{shortHash}";
    }

    private static string BuildChoiceSignature(
        GameStatePayload state,
        string phase,
        Dictionary<string, object?> summary,
        IReadOnlyList<DecisionChoicePayload> choices)
    {
        var input = JsonHelper.Serialize(new
        {
            state.run_id,
            state.screen,
            phase,
            state.turn,
            summary,
            selection = state.selection == null
                ? null
                : new
                {
                    state.selection.kind,
                    state.selection.min_select,
                    state.selection.max_select,
                    state.selection.selected_count,
                    state.selection.requires_confirmation,
                    state.selection.can_confirm
                },
            choices = choices.Select(choice => new
            {
                choice.action_id,
                choice.kind,
                choice.label,
                choice.risk_tags,
                choice.source,
                choice.preview
            }).ToArray()
        });

        return $"sha256:{HashText(input)}";
    }

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Dictionary<string, object?> BuildSummary(GameStatePayload state)
    {
        var combat = state.combat;
        var run = state.run;

        return new Dictionary<string, object?>
        {
            ["floor"] = run?.floor,
            ["turn"] = state.turn,
            ["character_id"] = run?.character_id ?? state.character_select?.selected_character_id,
            ["ascension"] = run?.ascension ?? state.character_select?.ascension,
            ["current_hp"] = combat?.player.current_hp ?? run?.current_hp,
            ["max_hp"] = combat?.player.max_hp ?? run?.max_hp,
            ["block"] = combat?.player.block,
            ["energy"] = combat?.player.energy,
            ["stars"] = combat?.player.stars,
            ["cards_played_this_turn"] = combat?.cards_played_this_turn,
            ["gold"] = run?.gold,
            ["incoming_damage"] = combat?.incoming_damage,
            ["draw_pile_count"] = combat?.piles.draw.count,
            ["discard_pile_count"] = combat?.piles.discard.count,
            ["exhaust_pile_count"] = combat?.piles.exhaust.count,
            ["draw_pile_non_attack_count"] = combat?.piles.draw.non_attack_count,
            ["draw_pile_skill_count"] = combat?.piles.draw.skill_count,
            ["draw_pile_block_card_count"] = combat?.piles.draw.block_card_count,
            ["draw_pile_defensive_out_count"] = combat?.piles.draw.defensive_out_count,
            ["draw_pile_non_attack_defensive_out_count"] = combat?.piles.draw.non_attack_defensive_out_count,
            ["draw_pile_skill_defensive_out_count"] = combat?.piles.draw.skill_defensive_out_count,
            ["player_powers"] = combat == null ? null : FormatPowers(combat.player.powers),
            ["enemy_powers"] = combat == null
                ? null
                : combat.enemies
                    .Where(enemy => enemy.is_alive)
                    .Select(enemy => new
                    {
                        enemy.index,
                        enemy.name,
                        powers = FormatPowers(enemy.powers)
                    })
                    .ToArray()
        };
    }

    private static Dictionary<string, object?> BuildContext(GameStatePayload state, string phase)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session"] = state.session
        };

        switch (phase)
        {
            case "combat":
                context["combat"] = state.combat;
                context["run"] = BuildRunContext(state.run);
                break;
            case "combat_selection":
            case "selection":
                context["selection"] = BuildSelectionContext(state.selection, state.run);
                context["run"] = BuildRunContext(state.run);
                break;
            case "map":
                context["map"] = BuildMapContext(state.map);
                context["run"] = BuildRunContext(state.run);
                break;
            case "reward":
                context["reward"] = state.reward;
                context["run"] = BuildRunContext(state.run);
                break;
            case "event":
                context["event"] = BuildEventContext(state.@event);
                context["run"] = BuildRunContext(state.run);
                break;
            case "rest":
                context["rest"] = BuildRestContext(state.rest, state.run);
                context["run"] = BuildRunContext(state.run);
                break;
            case "shop":
                context["shop"] = BuildShopContext(state);
                context["run"] = BuildRunContext(state.run);
                break;
            case "chest":
                context["chest"] = BuildChestContext(state);
                context["run"] = BuildRunContext(state.run);
                break;
            case "modal":
                context["modal"] = state.modal;
                break;
            case "character_select":
                context["character_select"] = state.character_select;
                context["multiplayer_lobby"] = state.multiplayer_lobby;
                break;
            case "game_over":
                context["game_over"] = state.game_over;
                break;
        }

        if (state.run != null)
        {
            context["run_analysis"] = BuildRunAnalysis(state.run);
        }

        return context;
    }

    private static object? BuildMapContext(MapPayload? map)
    {
        if (map == null)
        {
            return null;
        }

        return new
        {
            map.current_node,
            map.is_travel_enabled,
            map.is_traveling,
            map.map_generation_count,
            map.rows,
            map.cols,
            map.starting_node,
            map.boss_node,
            map.second_boss_node,
            boss_info = new
            {
                boss_node = map.boss_node,
                second_boss_node = map.second_boss_node,
                boss_encounter = map.boss_encounter,
                second_boss_encounter = map.second_boss_encounter,
                known_boss_encounter_id = map.boss_encounter?.encounter_id,
                known_boss_name = map.boss_encounter?.name,
                known_second_boss_encounter_id = map.second_boss_encounter?.encounter_id,
                known_second_boss_name = map.second_boss_encounter?.name
            },
            available_nodes = map.available_nodes.Select(node => new
            {
                node.index,
                node.row,
                node.col,
                node.node_type,
                node.state,
                route_summary = BuildRouteSummary(map, node)
            }).ToArray(),
            nodes = map.nodes
        };
    }

    private static object BuildRouteSummary(MapPayload map, MapNodePayload start)
    {
        var byCoord = map.nodes.ToDictionary(node => CoordKey(node.row, node.col), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(MapGraphNodePayload Node, int Depth)>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nearest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var startKey = CoordKey(start.row, start.col);
        var reachesBoss = false;

        if (byCoord.TryGetValue(startKey, out var startNode))
        {
            queue.Enqueue((startNode, 0));
        }

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            var key = CoordKey(node.row, node.col);
            if (!visited.Add(key))
            {
                continue;
            }

            counts[node.node_type] = counts.TryGetValue(node.node_type, out var count) ? count + 1 : 1;
            if (!nearest.ContainsKey(node.node_type))
            {
                nearest[node.node_type] = depth;
            }

            if (node.is_boss || node.is_second_boss)
            {
                reachesBoss = true;
                continue;
            }

            foreach (var child in node.children)
            {
                if (byCoord.TryGetValue(CoordKey(child.row, child.col), out var childNode))
                {
                    queue.Enqueue((childNode, depth + 1));
                }
            }
        }

        return new
        {
            reaches_boss = reachesBoss,
            reachable_nodes = Math.Max(0, visited.Count),
            counts_by_type = counts,
            nearest_depth_by_type = nearest,
            first_steps = startNodeChildren(map, start)
        };

        static object[] startNodeChildren(MapPayload mapPayload, MapNodePayload node)
        {
            var graphNode = mapPayload.nodes.FirstOrDefault(candidate =>
                candidate.row == node.row && candidate.col == node.col);
            if (graphNode == null)
            {
                return Array.Empty<object>();
            }

            return graphNode.children
                .Select(child => mapPayload.nodes.FirstOrDefault(candidate =>
                    candidate.row == child.row && candidate.col == child.col))
                .Where(child => child != null)
                .Select(child => new
                {
                    child!.row,
                    child.col,
                    child.node_type
                })
                .Cast<object>()
                .ToArray();
        }
    }

    private static string CoordKey(int row, int col)
    {
        return $"{row}:{col}";
    }

    private static object? BuildEventContext(EventPayload? eventPayload)
    {
        if (eventPayload == null)
        {
            return null;
        }

        return new
        {
            eventPayload.event_id,
            eventPayload.title,
            eventPayload.description,
            eventPayload.is_finished,
            eventPayload.dynamic_vars,
            options = eventPayload.options.Select(option => new
            {
                option.index,
                option.text_key,
                option.title,
                option.description,
                option.raw_title,
                option.raw_description,
                option.dynamic_vars,
                option.is_locked,
                option.is_proceed,
                option.will_kill_player,
                option.has_relic_preview,
                option.relic_preview,
                estimated_effects = BuildEventEffectPreview(option)
            }).ToArray()
        };
    }

    private static object? BuildRestContext(RestPayload? rest, RunPayload? run)
    {
        if (rest == null)
        {
            return null;
        }

        return new
        {
            current_hp = run?.current_hp,
            max_hp = run?.max_hp,
            missing_hp = run == null ? (int?)null : Math.Max(0, run.max_hp - run.current_hp),
            options = rest.options.Select(option => new
            {
                option.index,
                option.option_id,
                option.title,
                option.description,
                option.is_enabled,
                expected_values = BuildRestOptionPreview(option, run)
            }).ToArray()
        };
    }

    private static object BuildRestOptionPreview(RestOptionPayload option, RunPayload? run)
    {
        var text = $"{option.title} {option.description}";
        var heal = ExtractFirstNumberNear(text, "Heal");
        return new
        {
            heal_amount = heal,
            hp_after = heal.HasValue && run != null ? Math.Min(run.max_hp, run.current_hp + heal.Value) : (int?)null,
            upgrades_card = ContainsAny(text, "Upgrade", "Smith"),
            removes_card = ContainsAny(text, "Remove"),
            transforms_card = ContainsAny(text, "Transform"),
            disabled = !option.is_enabled
        };
    }

    private static object? BuildShopContext(GameStatePayload state)
    {
        var shop = state.shop;
        if (shop == null)
        {
            return null;
        }

        var indexes = BuildGameDataIndex(state);
        return new
        {
            shop.is_open,
            shop.can_open,
            shop.can_close,
            gold = state.run?.gold,
            cards = shop.cards.Select(card => new
            {
                card.index,
                card.category,
                card.card_id,
                card.name,
                card.upgraded,
                card.card_type,
                card.rarity,
                card.energy_cost,
                card.star_cost,
                card.costs_x,
                card.star_costs_x,
                card.rules_text,
                card.keywords,
                card.mods,
                card.price,
                card.on_sale,
                card.is_stocked,
                card.enough_gold
            }).ToArray(),
            relics = shop.relics.Select(relic => new
            {
                relic.index,
                relic.relic_id,
                relic.name,
                relic.rarity,
                relic.price,
                relic.is_stocked,
                relic.enough_gold,
                description = TryLookupString(indexes, "relics", relic.relic_id, "description")
            }).ToArray(),
            potions = shop.potions.Select(potion => new
            {
                potion.index,
                potion.potion_id,
                potion.name,
                potion.rarity,
                potion.usage,
                potion.price,
                potion.is_stocked,
                potion.enough_gold,
                description = TryLookupString(indexes, "potions", potion.potion_id, "description")
            }).ToArray(),
            card_removal = shop.card_removal == null
                ? null
                : new
                {
                    shop.card_removal.price,
                    shop.card_removal.available,
                    shop.card_removal.used,
                    shop.card_removal.enough_gold,
                    removable_cards = state.run?.deck
                        .Where(card => card.can_remove)
                        .Select(card => new
                        {
                            card.index,
                            card.card_id,
                            card.name,
                            card.upgraded,
                            card.card_type,
                            card.rarity
                        })
                        .ToArray(),
                    non_removable_cards = state.run?.deck
                        .Where(card => !card.can_remove)
                        .Select(card => new
                        {
                            card.index,
                            card.card_id,
                            card.name,
                            card.keywords,
                            card.mods
                        })
                        .ToArray()
                }
        };
    }

    private static object? BuildChestContext(GameStatePayload state)
    {
        var chest = state.chest;
        if (chest == null)
        {
            return null;
        }

        var indexes = BuildGameDataIndex(state);
        return new
        {
            chest.is_opened,
            chest.has_relic_been_claimed,
            relic_options = chest.relic_options.Select(relic => new
            {
                relic.index,
                relic.relic_id,
                relic.name,
                relic.rarity,
                description = TryLookupString(indexes, "relics", relic.relic_id, "description")
            }).ToArray()
        };
    }

    private static object BuildEventEffectPreview(EventOptionPayload option)
    {
        var text = $"{option.title} {option.description}";
        var hpLoss = ReadDynamicVarInt(option.dynamic_vars, "HpLoss") ??
            ExtractFirstNumberNearLocalized(text, ("Lose", "HP"), ("失去", "生命"));
        var gold = ReadDynamicVarInt(option.dynamic_vars, "Gold");
        var maxHpLoss = ReadDynamicVarInt(option.dynamic_vars, "MaxHpLoss");
        var relicName = option.relic_preview?.name ?? ReadDynamicVarString(option.dynamic_vars, "Relic");
        var gainsGold = ContainsOrderedTerms(text, ("Gain", "Gold"), ("获得", "金币"));
        var losesGold = ContainsOrderedTerms(text, ("Lose", "Gold"), ("失去", "金币"));

        return new
        {
            hp_loss = hpLoss,
            max_hp_loss = maxHpLoss,
            gold_gain = gainsGold ? gold : null,
            gold_loss = losesGold ? gold : null,
            relic_name = relicName,
            adds_curse_or_affliction = ContainsAny(text, "Curse", "Affliction", "诅咒", "异变"),
            removes_card = ContainsAny(text, "Remove", "移除"),
            transforms_card = ContainsAny(text, "Transform", "转化"),
            upgrades_card = ContainsAny(text, "Upgrade", "升级"),
            grants_relic = option.relic_preview != null || relicName != null || ContainsAny(text, "Relic", "遗物"),
            grants_potion = ContainsAny(text, "Potion", "药水"),
            will_kill_player = option.will_kill_player
        };
    }

    private static string? ReadDynamicVarString(IReadOnlyDictionary<string, object?> vars, string key)
    {
        return vars.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int? ExtractFirstNumberNearLocalized(
        string text,
        params (string first, string second)[] requiredTermPairs)
    {
        foreach (var (first, second) in requiredTermPairs)
        {
            if (ContainsAny(text, first) && ContainsAny(text, second))
            {
                var match = Regex.Match(text, @"\d+");
                if (match.Success && int.TryParse(match.Value, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool ContainsOrderedTerms(
        string text,
        params (string first, string second)[] requiredTermPairs)
    {
        foreach (var (first, second) in requiredTermPairs)
        {
            var firstIndex = text.IndexOf(first, StringComparison.OrdinalIgnoreCase);
            var secondIndex = firstIndex < 0
                ? -1
                : text.IndexOf(second, firstIndex + first.Length, StringComparison.OrdinalIgnoreCase);
            if (firstIndex >= 0 && secondIndex >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int? ExtractFirstNumberNear(string text, params string[] requiredTerms)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            requiredTerms.Any(term => !text.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var match = Regex.Match(text, @"\d+");
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static object? BuildSelectionContext(SelectionPayload? selection, RunPayload? run)
    {
        if (selection == null)
        {
            return null;
        }

        var listedCardCounts = selection.cards
            .GroupBy(
                card => BuildCardIdentityKey(card.card_id, card.name, card.upgraded, card.raw_rules_text),
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var unlistedDeckCards = new List<object>();
        if (run != null)
        {
            foreach (var card in run.deck)
            {
                var key = BuildCardIdentityKey(card.card_id, card.name, card.upgraded, card.rules_text);
                if (listedCardCounts.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    listedCardCounts[key] = remaining - 1;
                    continue;
                }

                unlistedDeckCards.Add(new
                {
                    card.index,
                    card.card_id,
                    card.name,
                    card.keywords,
                    card.mods,
                    card.can_remove,
                    exclusion_reason = card.can_remove
                        ? "not_listed_by_current_selection"
                        : "not_selectable_or_not_removable"
                });
            }
        }

        return new
        {
            selection.kind,
            selection.prompt,
            selection.min_select,
            selection.max_select,
            selection.selected_count,
            selection.requires_confirmation,
            selection.can_confirm,
            selection_note = "Only cards listed in selection.cards are legal choices for this selection. Do not choose cards from run.deck that are not listed here.",
            cards = selection.cards.Select(card => new
            {
                card.index,
                card.card_ref,
                card.card_id,
                card.name,
                card.upgraded,
                card.card_type,
                card.rarity,
                card.costs_x,
                card.star_costs_x,
                card.energy_cost,
                card.star_cost,
                card.rules_text,
                card.raw_rules_text,
                card.resolved_rules_text,
                card.dynamic_vars,
                card.powers_applied,
                card.keywords,
                card.mods,
                card.modifier_details,
                card.selectable,
                card.consequence_preview,
                deck_interaction = BuildSelectionDeckInteraction(card, run)
            }).ToArray(),
            unlisted_deck_cards = unlistedDeckCards.ToArray()
        };
    }

    private static object? BuildSelectionDeckInteraction(SelectionCardPayload card, RunPayload? run)
    {
        if (run == null)
        {
            return null;
        }

        var cardId = card.card_id.Trim().ToUpperInvariant();
        var expectedPowerId = cardId switch
        {
            "DISINTEGRATION" => "DISINTEGRATION_POWER",
            "MIND_ROT" => "MIND_ROT_POWER",
            "SLOTH" => "SLOTH_POWER",
            "WASTE_AWAY" => "WASTE_AWAY_POWER",
            _ => null
        };
        if (expectedPowerId == null)
        {
            return null;
        }

        var amount = card.powers_applied
            .FirstOrDefault(power => string.Equals(power.power_id, expectedPowerId, StringComparison.OrdinalIgnoreCase))
            ?.amount;
        var drawCards = run.deck.Where(IsDrawCard).ToArray();
        var energyCards = run.deck.Where(IsEnergyGenerationCard).ToArray();
        var zeroCostCards = run.deck.Where(candidate => !candidate.costs_x && candidate.energy_cost == 0).ToArray();
        var expensiveCards = run.deck.Where(candidate => !candidate.costs_x && candidate.energy_cost >= 2).ToArray();

        return cardId switch
        {
            "SLOTH" => new
            {
                interaction = "card_play_limit_vs_current_deck",
                max_cards_per_turn = amount,
                zero_cost_cards = BuildRunCardGroups(zeroCostCards),
                draw_cards = BuildRunCardGroups(drawCards),
                energy_generation_cards = BuildRunCardGroups(energyCards),
                conflicting_card_count = zeroCostCards
                    .Concat(drawCards)
                    .Concat(energyCards)
                    .Select(candidate => candidate.index)
                    .Distinct()
                    .Count()
            },
            "MIND_ROT" => new
            {
                interaction = "draw_reduction_vs_deck_size",
                draw_per_turn_delta = amount.HasValue ? -amount.Value : (decimal?)null,
                deck_size = run.deck.Length,
                draw_cards = BuildRunCardGroups(drawCards),
                natural_turns_to_see_deck_before = NaturalTurnsToSeeDeck(run.deck.Length, 5),
                natural_turns_to_see_deck_after = NaturalTurnsToSeeDeck(
                    run.deck.Length,
                    Math.Max(1, 5 - (int)Math.Max(0, amount ?? 0)))
            },
            "WASTE_AWAY" => new
            {
                interaction = "energy_reduction_vs_cost_curve",
                energy_per_turn_delta = amount.HasValue ? -amount.Value : (decimal?)null,
                max_energy_before = run.max_energy,
                projected_base_energy_after = amount.HasValue
                    ? Math.Max(0, run.max_energy - (int)Math.Max(0, amount.Value))
                    : (int?)null,
                cards_costing_two_or_more = BuildRunCardGroups(expensiveCards),
                energy_generation_cards = BuildRunCardGroups(energyCards)
            },
            "DISINTEGRATION" => new
            {
                interaction = "end_turn_self_damage_vs_current_hp",
                end_turn_self_damage = amount,
                current_hp = run.current_hp,
                hp_only_turns_to_zero = amount > 0
                    ? (int?)Math.Ceiling(run.current_hp / amount.Value)
                    : null,
                note = "HP-only horizon ignores Block, healing, relics, and later runtime changes."
            },
            _ => null
        };
    }

    private static string BuildCardIdentityKey(string cardId, string name, bool upgraded, string rulesText)
    {
        return string.Join(
            "\u001f",
            cardId,
            name,
            upgraded ? "1" : "0",
            rulesText);
    }

    private static object? BuildRunContext(RunPayload? run)
    {
        if (run == null)
        {
            return null;
        }

        return new
        {
            run.character_id,
            run.character_name,
            run.floor,
            run.current_hp,
            run.max_hp,
            run.gold,
            run.max_energy,
            run.boss_encounter,
            run.second_boss_encounter,
            deck_size = run.deck.Length,
            deck = run.deck.Select(card => new
            {
                card.index,
                card.card_id,
                card.name,
                card.upgraded,
                card.card_type,
                card.keywords,
                card.mods,
                card.can_remove
            }).ToArray(),
            non_removable_cards = run.deck
                .Where(card => !card.can_remove)
                .Select(card => new
                {
                    card.index,
                    card.card_id,
                    card.name,
                    card.keywords,
                    card.mods
                })
                .ToArray(),
            relics = run.relics,
            potions = run.potions
        };
    }

    private static object BuildRunAnalysis(RunPayload run)
    {
        var deck = run.deck;
        var basicCards = deck.Where(card => string.Equals(card.rarity, "Basic", StringComparison.OrdinalIgnoreCase)).ToArray();
        var drawCards = deck.Where(IsDrawCard).ToArray();
        var blockCards = deck.Where(IsBlockCard).ToArray();
        var energyCards = deck.Where(IsEnergyGenerationCard).ToArray();
        var exhaustCards = deck.Where(IsExhaustCard).ToArray();
        var powerCards = deck.Where(card => string.Equals(card.card_type, "Power", StringComparison.OrdinalIgnoreCase)).ToArray();
        var fixedCostCards = deck.Where(card => !card.costs_x && card.energy_cost >= 0).ToArray();
        var cardsSeenByTurnFour = Math.Min(deck.Length, 20);
        var signals = new List<string>();
        if (deck.Length >= 30)
        {
            signals.Add("large_deck_30_plus");
        }

        if (basicCards.Length >= 7)
        {
            signals.Add("many_basic_cards_7_plus");
        }

        if (deck.Count(card => string.Equals(card.card_type, "Curse", StringComparison.OrdinalIgnoreCase)) > 0)
        {
            signals.Add("contains_curse");
        }

        if (drawCards.Length == 0 && deck.Length >= 20)
        {
            signals.Add("no_explicit_draw_in_20_plus_card_deck");
        }

        return new
        {
            analysis_scope = "factual_deck_shape_and_natural_draw_probabilities",
            deck_size = deck.Length,
            upgraded_count = deck.Count(card => card.upgraded),
            removable_count = deck.Count(card => card.can_remove),
            non_removable_count = deck.Count(card => !card.can_remove),
            basic_card_count = basicCards.Length,
            starter_strike_count = basicCards.Count(card => card.card_id.StartsWith("STRIKE_", StringComparison.OrdinalIgnoreCase)),
            starter_defend_count = basicCards.Count(card => card.card_id.StartsWith("DEFEND_", StringComparison.OrdinalIgnoreCase)),
            curse_count = deck.Count(card => string.Equals(card.card_type, "Curse", StringComparison.OrdinalIgnoreCase)),
            type_counts = deck
                .GroupBy(card => card.card_type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            cost_curve = new
            {
                zero = fixedCostCards.Count(card => card.energy_cost == 0),
                one = fixedCostCards.Count(card => card.energy_cost == 1),
                two = fixedCostCards.Count(card => card.energy_cost == 2),
                three_plus = fixedCostCards.Count(card => card.energy_cost >= 3),
                x = deck.Count(card => card.costs_x),
                average_non_x = fixedCostCards.Length == 0
                    ? (double?)null
                    : Math.Round(fixedCostCards.Average(card => card.energy_cost), 3)
            },
            role_counts = new
            {
                draw = drawCards.Length,
                block = blockCards.Length,
                energy_generation = energyCards.Length,
                exhaust = exhaustCards.Length,
                power = powerCards.Length,
                vulnerable = deck.Count(IsVulnerableCard),
                weak = deck.Count(IsWeakCard)
            },
            role_cards = new
            {
                draw = BuildRunCardGroups(drawCards),
                block = BuildRunCardGroups(blockCards),
                energy_generation = BuildRunCardGroups(energyCards),
                exhaust = BuildRunCardGroups(exhaustCards),
                powers = BuildRunCardGroups(powerCards),
                basics = BuildRunCardGroups(basicCards)
            },
            natural_draw = new
            {
                assumption = "five cards per turn, no extra draw, no retain, no shuffle before the sample",
                cards_seen_by_turn_4 = cardsSeenByTurnFour,
                probability_any_power_by_turn_4 = ProbabilityAtLeastOne(deck.Length, powerCards.Length, cardsSeenByTurnFour),
                probability_any_draw_card_by_turn_4 = ProbabilityAtLeastOne(deck.Length, drawCards.Length, cardsSeenByTurnFour),
                power_cards = powerCards
                    .GroupBy(card => new { card.card_id, card.name })
                    .Select(group => new
                    {
                        group.Key.card_id,
                        group.Key.name,
                        copies = group.Count(),
                        probability_by_turn_4 = ProbabilityAtLeastOne(deck.Length, group.Count(), cardsSeenByTurnFour)
                    })
                    .ToArray()
            },
            signals = signals.ToArray(),
            limitations = new[]
            {
                "Role detection uses structured card fields plus current localized rules text.",
                "This analysis reports deck facts and access probabilities; it does not rank cards or promise boss readiness."
            }
        };
    }

    private static object[] BuildRunCardGroups(IEnumerable<DeckCardPayload> cards)
    {
        return cards
            .GroupBy(card => new { card.card_id, card.name })
            .Select(group => new
            {
                group.Key.card_id,
                group.Key.name,
                count = group.Count(),
                upgraded = group.Count(card => card.upgraded)
            })
            .OrderBy(item => item.card_id, StringComparer.Ordinal)
            .Cast<object>()
            .ToArray();
    }

    private static int? NaturalTurnsToSeeDeck(int deckSize, int cardsPerTurn)
    {
        return deckSize <= 0 || cardsPerTurn <= 0
            ? null
            : (int)Math.Ceiling(deckSize / (double)cardsPerTurn);
    }

    private static double ProbabilityAtLeastOne(int population, int successes, int draws)
    {
        if (population <= 0 || successes <= 0 || draws <= 0)
        {
            return 0;
        }

        successes = Math.Min(population, successes);
        draws = Math.Min(population, draws);
        if (draws > population - successes)
        {
            return 1;
        }

        var missProbability = 1d;
        for (var index = 0; index < draws; index++)
        {
            missProbability *= (population - successes - index) / (double)(population - index);
        }

        return Math.Round(1d - missProbability, 4);
    }

    private static bool IsDrawCard(DeckCardPayload card)
    {
        return ContainsAny(card.rules_text, "Draw", "抽");
    }

    private static bool IsBlockCard(DeckCardPayload card)
    {
        return card.keywords.Any(keyword => ContainsAny(keyword, "Block", "格挡")) ||
            ContainsAny(card.rules_text, "Block", "格挡");
    }

    private static bool IsEnergyGenerationCard(DeckCardPayload card)
    {
        var text = card.rules_text;
        return (ContainsAny(text, "Gain", "获得") && ContainsAny(text, "Energy", "能量")) ||
            (text.Contains("energyIcons", StringComparison.OrdinalIgnoreCase) && ContainsAny(text, "Gain", "获得"));
    }

    private static bool IsExhaustCard(DeckCardPayload card)
    {
        return card.keywords.Concat(card.mods).Any(value => ContainsAny(value, "Exhaust", "消耗"));
    }

    private static bool IsVulnerableCard(DeckCardPayload card)
    {
        return card.keywords.Any(keyword => ContainsAny(keyword, "Vulnerable", "易伤")) ||
            ContainsAny(card.rules_text, "Vulnerable", "易伤");
    }

    private static bool IsWeakCard(DeckCardPayload card)
    {
        return card.keywords.Any(keyword => ContainsAny(keyword, "Weak", "虚弱")) ||
            ContainsAny(card.rules_text, "Weak", "虚弱");
    }

    private static Dictionary<string, object?> BuildKnowledge(GameStatePayload state, bool includeRelevantGameData)
    {
        return new Dictionary<string, object?>
        {
            ["metadata"] = BuildKnowledgeMetadata(),
            ["relevant"] = includeRelevantGameData
                ? BuildRelevantGameData(state)
                : new Dictionary<string, object?>()
        };
    }

    private static Dictionary<string, object?> BuildKnowledgeMetadata()
    {
        return new Dictionary<string, object?>
        {
            ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
            ["mod_version"] = ModVersion,
            ["data_source"] = "loaded_game_model",
            ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
            ["content_hash"] = null
        };
    }

    private static Dictionary<string, object?> BuildRelevantGameData(GameStatePayload state)
    {
        var indexes = BuildGameDataIndex(state);
        var requested = CollectRelevantGameDataIds(state);
        var relevant = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["glossary"] = BuildCoreGlossary()
        };

        foreach (var (collection, ids) in requested.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!indexes.TryGetValue(collection, out var collectionIndex))
            {
                continue;
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (TryGetIndexedItem(collectionIndex, id, out var item))
                {
                    values[id] = FilterFields(item, GetRelevantFields(collection));
                }
            }

            if (values.Count > 0)
            {
                relevant[collection] = values;
            }
        }

        return relevant;
    }

    private static Dictionary<string, HashSet<string>> CollectRelevantGameDataIds(GameStatePayload state)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(string collection, string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (!result.TryGetValue(collection, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[collection] = set;
            }

            set.Add(id);
        }

        if (state.combat != null)
        {
            foreach (var card in state.combat.hand)
            {
                Add("cards", card.card_id);
            }

            foreach (var pile in new[] { state.combat.piles.draw, state.combat.piles.discard, state.combat.piles.exhaust })
            {
                foreach (var stack in pile.stacks)
                {
                    Add("cards", stack.card_id);
                }
            }

            foreach (var enemy in state.combat.enemies)
            {
                Add("monsters", enemy.enemy_id);
                foreach (var power in enemy.powers)
                {
                    Add("powers", power.power_id);
                }
            }

            foreach (var power in state.combat.player.powers)
            {
                Add("powers", power.power_id);
            }
        }

        if (state.reward != null)
        {
            foreach (var card in state.reward.card_options)
            {
                Add("cards", card.card_id);
            }

            foreach (var option in state.reward.rewards)
            {
                if (string.Equals(option.reward_type, "Potion", StringComparison.OrdinalIgnoreCase))
                {
                    Add("potions", option.item_id);
                }
            }
        }

        if (state.selection != null)
        {
            foreach (var card in state.selection.cards)
            {
                Add("cards", card.card_id);
            }
        }

        if (state.shop != null)
        {
            foreach (var card in state.shop.cards)
            {
                Add("cards", card.card_id);
            }

            foreach (var relic in state.shop.relics)
            {
                Add("relics", relic.relic_id);
            }

            foreach (var potion in state.shop.potions)
            {
                Add("potions", potion.potion_id);
            }
        }

        if (state.chest != null)
        {
            foreach (var relic in state.chest.relic_options)
            {
                Add("relics", relic.relic_id);
            }
        }

        if (state.run != null)
        {
            foreach (var relic in state.run.relics)
            {
                Add("relics", relic.relic_id);
            }

            foreach (var potion in state.run.potions.Where(potion => potion.occupied))
            {
                Add("potions", potion.potion_id);
            }

            foreach (var card in state.run.deck.Where(card => !card.can_remove))
            {
                Add("cards", card.card_id);
            }
        }

        if (state.@event != null)
        {
            Add("events", state.@event.event_id);
        }

        return result;
    }

    private static string[] GetRelevantFields(string collection)
    {
        return collection.ToLowerInvariant() switch
        {
            "cards" => new[]
            {
                "id", "name", "description", "type", "rarity", "target", "cost", "star_cost",
                "is_x_cost", "is_x_star_cost", "damage", "block", "hit_count", "powers_applied",
                "cards_draw", "energy_gain", "hp_loss", "keywords", "tags", "can_remove"
            },
            "monsters" => new[] { "id", "name", "type", "current_hp", "max_hp", "block", "intent", "move_id", "intents", "powers", "moves", "state_machine", "numeric_parameters", "damage_values", "block_values", "mechanics", "planning_notes", "data_completeness", "data_source_notes" },
            "powers" => new[] { "id", "name", "description", "type", "stack_type", "amount", "is_debuff" },
            "relics" => new[] { "id", "name", "description", "rarity", "stack", "is_melted", "price" },
            "potions" => new[] { "id", "name", "description", "rarity", "usage", "target", "can_use", "can_discard", "price" },
            "events" => new[] { "id", "name", "description", "options", "is_finished" },
            _ => Array.Empty<string>()
        };
    }

    private static Dictionary<string, object?> BuildCoreGlossary()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Strength"] = "Adds to Attack damage before multiplicative modifiers.",
            ["Weak"] = "Attack damage dealt is reduced by 25%.",
            ["Vulnerable"] = "Attack damage received is increased by 50%.",
            ["Dexterity"] = "Adds to Block gained from cards before multiplicative modifiers.",
            ["Frail"] = "Block gained from cards is reduced by 25%.",
            ["Block"] = "Block absorbs damage before HP loss.",
            ["Eternal"] = "Card cannot be removed from the deck."
        };
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BuildGameDataIndex(GameStatePayload state)
    {
        var indexes = CloneIndexes(BuildModelDbGameDataIndex());
        MergeIndexes(indexes, BuildVisibleGameDataIndex(state));
        return indexes;
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> CloneIndexes(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, Dictionary<string, object?>>(pair.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void MergeIndexes(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> target,
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> source)
    {
        foreach (var (collection, sourceIndex) in source)
        {
            if (!target.TryGetValue(collection, out var targetIndex))
            {
                targetIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                target[collection] = targetIndex;
            }

            foreach (var (id, item) in sourceIndex)
            {
                if (!targetIndex.TryGetValue(id, out var existing))
                {
                    targetIndex[id] = item;
                    continue;
                }

                var merged = new Dictionary<string, object?>(existing, StringComparer.Ordinal);
                foreach (var (field, value) in item)
                {
                    merged[field] = value;
                }

                targetIndex[id] = merged;
            }
        }
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BuildModelDbGameDataIndex()
    {
        if (_modelDbGameDataIndex != null)
        {
            return _modelDbGameDataIndex;
        }

        var indexes = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        AddModelCollection("cards", "AllCards");
        AddModelCollection("monsters", "Monsters");
        AddModelCollection("relics", "AllRelics");
        AddModelCollection("potions", "AllPotions");
        AddModelCollection("events", "AllEvents");
        AddModelCollection("events", "AllAncients");
        AddModelCollection("powers", "AllPowers");
        AddModelCollection("encounters", "AllEncounters");
        AddModelCollection("enchantments", "DebugEnchantments");
        AddCorePowerData(indexes);
        AddCoreMonsterData(indexes);

        _modelDbGameDataIndex = indexes;
        return _modelDbGameDataIndex;

        void AddModelCollection(string collection, string propertyName)
        {
            foreach (var model in EnumerateModelDbCollection(propertyName))
            {
                var item = BuildModelDbItem(collection, model);
                if (!item.TryGetValue("id", out var idValue))
                {
                    continue;
                }

                AddIndexedItem(indexes, collection, idValue?.ToString(), item);
            }
        }
    }

    private static IEnumerable<object> EnumerateModelDbCollection(string propertyName)
    {
        object? value;
        try
        {
            value = typeof(ModelDb).GetProperty(propertyName)?.GetValue(null);
        }
        catch
        {
            yield break;
        }

        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item != null)
            {
                yield return item;
            }
        }
    }

    private static Dictionary<string, object?> BuildModelDbItem(string collection, object model)
    {
        var id = ExtractModelId(model);
        var description = ReadModelText(model, "DynamicDescription", "SmartDescription", "Description", "RulesText", "Text");
        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["name"] = ExtractModelName(model, id),
            ["description"] = description,
            ["model_type"] = model.GetType().Name
        };

        switch (collection.ToLowerInvariant())
        {
            case "cards":
                AddCardModelFields(item, model, description);
                break;
            case "monsters":
                item["type"] = ReadModelValue(model, "Type")?.ToString();
                item["min_hp"] = ReadModelValue(model, "MinInitialHp") ?? ReadModelValue(model, "MinHp");
                item["max_hp"] = ReadModelValue(model, "MaxInitialHp") ?? ReadModelValue(model, "MaxHp");
                AddMonsterStateMachineFields(item, model);
                break;
            case "relics":
                item["rarity"] = ReadModelValue(model, "Rarity")?.ToString();
                break;
            case "potions":
                item["rarity"] = ReadModelValue(model, "Rarity")?.ToString();
                item["usage"] = ReadModelValue(model, "Usage")?.ToString();
                item["target"] = ReadModelValue(model, "TargetType")?.ToString();
                break;
            case "events":
                item["event_type"] = model.GetType().Name;
                break;
            case "powers":
                item["type"] = ReadModelValue(model, "Type")?.ToString();
                item["stack_type"] = ReadModelValue(model, "StackType")?.ToString();
                break;
        }

        return item;
    }

    private static void AddCardModelFields(Dictionary<string, object?> item, object model, string description)
    {
        var vars = ReadDynamicVars(model);
        item["type"] = ReadModelValue(model, "Type")?.ToString();
        item["rarity"] = ReadModelValue(model, "Rarity")?.ToString();
        item["target"] = ReadModelValue(model, "TargetType")?.ToString();
        item["cost"] = ReadEnergyCost(model);
        item["star_cost"] = ReadStarCost(model);
        item["is_x_cost"] = ReadBool(model, "HasEnergyCostX") || ReadNestedBool(model, "EnergyCost", "CostsX");
        item["is_x_star_cost"] = ReadBool(model, "HasStarCostX");
        item["damage"] = ExtractFirstInt(DealDamageRegex, description) ?? ReadDynamicVarInt(vars, "Damage");
        item["block"] = ExtractFirstInt(GainBlockRegex, description) ?? ReadDynamicVarInt(vars, "Block");
        item["hit_count"] = ExtractModelHitCount(description);
        item["powers_applied"] = MergePowerPreviews(ExtractAppliedPowers(description), BuildPowersAppliedFromDynamicVars(vars));
        item["keywords"] = ReadStringArray(model, "Keywords", "LocalKeywords");
        item["tags"] = ReadStringArray(model, "Tags");
        item["vars"] = vars.Count == 0 ? null : vars;
    }

    private static int? ReadEnergyCost(object model)
    {
        try
        {
            if (model is CardModel card)
            {
                return card.EnergyCost.GetWithModifiers(CostModifiers.All);
            }
        }
        catch
        {
        }

        return ReadNestedInt(model, "EnergyCost", "Canonical") ??
            ReadNestedInt(model, "CanonicalEnergyCost", "Canonical");
    }

    private static int? ReadStarCost(object model)
    {
        try
        {
            if (model is CardModel card)
            {
                return Math.Max(0, card.GetStarCostWithModifiers());
            }
        }
        catch
        {
        }

        return ReadInt(model, "CurrentStarCost");
    }

    private static int? ExtractModelHitCount(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = TimesRegex.Match(description);
        if (!match.Success || match.Groups["amount"].Value.Equals("X", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(match.Groups["amount"].Value, out var amount) ? amount : null;
    }

    private static object[] ReadMoveSummaries(object model)
    {
        var value = ReadModelValue(model, "Moves") ?? ReadModelValue(model, "MoveStateMachine");
        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            return Array.Empty<object>();
        }

        return enumerable
            .Cast<object?>()
            .Where(item => item != null)
            .Select(item =>
            {
                var id = ExtractModelId(item!);
                return new
                {
                    id,
                    name = ExtractModelName(item!, id)
                };
            })
            .Cast<object>()
            .ToArray();
    }

    private static void AddMonsterStateMachineFields(Dictionary<string, object?> item, object model)
    {
        if (model is not MonsterModel canonical)
        {
            item["moves"] = ReadMoveSummaries(model);
            item["data_completeness"] = "state_machine_unavailable";
            return;
        }

        try
        {
            var monster = canonical.ToMutable();
            var generateMethod = monster.GetType().GetMethod(
                "GenerateMoveStateMachine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var machine = generateMethod?.Invoke(monster, null) as MonsterMoveStateMachine;
            if (machine == null)
            {
                item["moves"] = Array.Empty<object>();
                item["data_completeness"] = "state_machine_unavailable";
                item["data_source_notes"] = "The runtime model did not return a move state machine.";
                return;
            }

            var initialState = ReadPrivateMember(machine, "_initialState") as MonsterState;
            var states = machine.States.Values
                .Distinct()
                .OrderBy(state => state.Id, StringComparer.Ordinal)
                .Select(BuildMonsterStatePayload)
                .ToArray();
            var moves = states
                .Where(state => state.TryGetValue("is_move", out var isMove) && isMove is true)
                .ToArray();

            item["moves"] = moves;
            item["state_machine"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schema_version"] = 1,
                ["source"] = "runtime_generate_move_state_machine",
                ["initial_state_id"] = initialState?.Id,
                ["states"] = states,
                ["state_count"] = states.Length,
                ["move_count"] = moves.Length,
                ["conditions_are_live_delegates"] = true
            };
            item["numeric_parameters"] = ReadMonsterNumericParameters(monster);
            item["data_completeness"] = "runtime_state_machine";
            item["data_source_notes"] = "Generated from the running game's MonsterMoveStateMachine, intent delegates, branch cooldowns, and repeat constraints.";
        }
        catch (Exception ex)
        {
            item["moves"] = ReadMoveSummaries(model);
            item["data_completeness"] = "state_machine_unavailable";
            item["data_source_notes"] = $"Runtime state-machine generation failed: {ex.GetBaseException().Message}";
        }
    }

    private static Dictionary<string, object?> BuildMonsterStatePayload(MonsterState state)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = state.Id,
            ["state_type"] = state.GetType().Name,
            ["is_move"] = state.IsMove,
            ["can_transition_away"] = state.CanTransitionAway,
            ["appears_in_logs"] = state.ShouldAppearInLogs
        };

        if (state is MoveState move)
        {
            result["must_perform_once"] = move.MustPerformOnceBeforeTransitioning;
            result["follow_up_state_id"] = move.FollowUpState?.Id ?? move.FollowUpStateId;
            result["intents"] = move.Intents.Select(BuildStaticIntentPayload).ToArray();
            return result;
        }

        if (state is ConditionalBranchState conditional)
        {
            var branches = ReadPrivateMember(conditional, "States") as System.Collections.IEnumerable;
            result["branches"] = branches == null
                ? Array.Empty<object>()
                : branches.Cast<object>().Select(BuildConditionalBranchPayload).ToArray();
            return result;
        }

        if (state is RandomBranchState random)
        {
            result["branches"] = random.States
                .Select(branch => BuildRandomBranchPayload(branch))
                .ToArray();
        }

        return result;
    }

    private static Dictionary<string, object?> BuildStaticIntentPayload(AbstractIntent intent)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["intent_type"] = intent.IntentType.ToString(),
            ["runtime_type"] = intent.GetType().Name
        };

        if (intent is AttackIntent attack)
        {
            decimal? baseDamage = null;
            try
            {
                baseDamage = attack.DamageCalc?.Invoke();
            }
            catch
            {
            }

            result["base_damage_per_hit"] = baseDamage;
            result["hits"] = attack.Repeats;
            result["base_total_damage"] = baseDamage.HasValue ? baseDamage.Value * attack.Repeats : null;
        }

        if (intent is StatusIntent status)
        {
            result["status_card_count"] = status.CardCount;
        }

        return result;
    }

    private static Dictionary<string, object?> BuildConditionalBranchPayload(object branch)
    {
        var condition = ReadPrivateMember(branch, "_conditionalLambda") as Delegate;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target_state_id"] = ReadPrivateMember(branch, "id")?.ToString(),
            ["condition_source"] = DescribeDelegate(condition),
            ["condition_canonical_default"] = SafeInvokeDelegate(condition)
        };
    }

    private static Dictionary<string, object?> BuildRandomBranchPayload(object branch)
    {
        var weight = ReadPrivateMember(branch, "weightLambda") as Delegate;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target_state_id"] = ReadPrivateMember(branch, "stateId")?.ToString(),
            ["cooldown"] = ReadPrivateMember(branch, "cooldown"),
            ["max_times"] = ReadPrivateMember(branch, "maxTimes"),
            ["repeat_type"] = ReadPrivateMember(branch, "repeatType")?.ToString(),
            ["weight_source"] = DescribeDelegate(weight),
            ["weight_canonical_default"] = SafeInvokeDelegate(weight)
        };
    }

    private static Dictionary<string, object?> ReadMonsterNumericParameters(MonsterModel monster)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in monster.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length > 0 ||
                !IsSimpleNumericType(property.PropertyType) ||
                !ContainsAny(property.Name, "Damage", "Repeat", "Count", "Amount", "Gain", "Threshold"))
            {
                continue;
            }

            try
            {
                result[property.Name] = property.GetValue(monster);
            }
            catch
            {
            }
        }

        return result;
    }

    private static bool IsSimpleNumericType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(byte) || underlying == typeof(sbyte) ||
            underlying == typeof(short) || underlying == typeof(ushort) ||
            underlying == typeof(int) || underlying == typeof(uint) ||
            underlying == typeof(long) || underlying == typeof(ulong) ||
            underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal);
    }

    private static object? ReadPrivateMember(object source, string memberName)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        try
        {
            return source.GetType().GetProperty(memberName, flags)?.GetValue(source)
                ?? source.GetType().GetField(memberName, flags)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static string? DescribeDelegate(Delegate? callback)
    {
        if (callback == null)
        {
            return null;
        }

        return $"{callback.Method.DeclaringType?.FullName}.{callback.Method.Name}";
    }

    private static object? SafeInvokeDelegate(Delegate? callback)
    {
        if (callback == null || callback.Method.GetParameters().Length != 0)
        {
            return null;
        }

        try
        {
            return callback.DynamicInvoke();
        }
        catch
        {
            return null;
        }
    }

    private static void AddCorePowerData(Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes)
    {
        foreach (var item in new[]
        {
            PowerItem("STRENGTH", "Strength", "Strength adds additional damage to Attacks.", "Buff"),
            PowerItem("WEAK", "Weak", "Weakened creatures deal 25% less damage with Attacks.", "Debuff"),
            PowerItem("VULNERABLE", "Vulnerable", "Vulnerable creatures take 50% more damage from Attacks.", "Debuff"),
            PowerItem("DEXTERITY", "Dexterity", "Dexterity improves Block gained from cards.", "Buff"),
            PowerItem("FRAIL", "Frail", "While Frail, gain 25% less Block from cards.", "Debuff"),
            PowerItem("FOCUS", "Focus", "Focus modifies orb values.", "Buff"),
            PowerItem("THORNS", "Thorns", "Deals damage back to attackers when hit.", "Buff")
        })
        {
            var id = item["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id) ||
                !indexes.TryGetValue("powers", out var powerIndex) ||
                !TryGetIndexedItem(powerIndex, id, out _))
            {
                AddIndexedItem(indexes, "powers", id, item);
            }
        }

        static Dictionary<string, object?> PowerItem(string id, string name, string description, string type)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = name,
                ["description"] = description,
                ["type"] = type
            };
        }
    }

    private static void AddCoreMonsterData(Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes)
    {
        MergeCuratedMonster(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "QUEEN",
            ["name"] = "Queen / 女王",
            ["type"] = "Boss",
            ["description"] = "Act 3 boss encountered with Torch Head Amalgam. Runtime intent values remain authoritative for exact damage.",
            ["data_completeness"] = "curated_partial",
            ["data_source_notes"] = "Observed v0.107.1 behavior; exact move base values and full RNG cycle are not available from ModelDb reflection.",
            ["mechanics"] = new[]
            {
                "Applies long-duration Frail, Weak, and Vulnerable.",
                "Chains of Binding can give Bound to early drawn cards, limiting Bound cards to one play per turn.",
                "Enrage turns increase Strength; observed multi-hit pressure escalated from 45 to 60 to 75 total damage as Strength rose.",
                "Fought alongside TORCH_HEAD_AMALGAM, whose continued attacks materially shorten the damage race."
            },
            ["planning_notes"] = new[]
            {
                "Treat the Torch Head as a priority target; a practical planning target is defeating it by turns 4-5.",
                "Re-read playability after every card because Bound can invalidate the rest of a planned line.",
                "Do not infer future exact damage from this curated record; use live intents and powers."
            },
            ["moves"] = new object[]
            {
                new { id = "ENRAGE_MOVE", effect = "Strength scaling / buff turn", exact_values = (object?)null },
                new { id = "OFF_WITH_YOUR_HEAD_MOVE", effect = "Multi-hit attack", exact_values = (object?)null },
                new { id = "EXECUTION_MOVE", effect = "Attack", exact_values = (object?)null }
            }
        });

        MergeCuratedMonster(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "TORCH_HEAD_AMALGAM",
            ["name"] = "Torch Head Amalgam / 火炬头聚合体",
            ["type"] = "BossMinion",
            ["description"] = "Queen encounter add that repeatedly contributes attack intent while the Queen scales.",
            ["data_completeness"] = "curated_partial",
            ["data_source_notes"] = "Observed v0.107.1 behavior; ModelDb does not currently expose a complete move table.",
            ["mechanics"] = new[]
            {
                "Adds recurring incoming damage beside the Queen.",
                "Leaving it alive consumes HP, Block, potions, and setup turns while the Queen gains Strength."
            },
            ["planning_notes"] = new[]
            {
                "Focus target and plan to defeat it by turns 4-5; still alive after turn 6 is a major readiness warning.",
                "Use live move_id/intents for exact damage because the static move table is incomplete."
            },
            ["moves"] = Array.Empty<object>()
        });

        void MergeCuratedMonster(Dictionary<string, object?> curated)
        {
            var id = curated["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (!indexes.TryGetValue("monsters", out var monsterIndex) || !TryGetIndexedItem(monsterIndex, id, out var existing))
            {
                AddIndexedItem(indexes, "monsters", id, curated);
                return;
            }

            foreach (var (field, value) in curated)
            {
                if (!existing.TryGetValue(field, out var current) || IsEmptyGameDataValue(current))
                {
                    existing[field] = value;
                }
            }
        }
    }

    private static bool IsEmptyGameDataValue(object? value)
    {
        if (value == null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return value is System.Collections.ICollection collection && collection.Count == 0;
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BuildVisibleGameDataIndex(GameStatePayload state)
    {
        var indexes = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        void Add(string collection, string? id, Dictionary<string, object?> item)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (!indexes.TryGetValue(collection, out var collectionIndex))
            {
                collectionIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                indexes[collection] = collectionIndex;
            }

            collectionIndex[id] = item;
            collectionIndex[id.ToUpperInvariant()] = item;
            collectionIndex[id.ToLowerInvariant()] = item;
        }

        void AddIfMissing(string collection, string? id, Dictionary<string, object?> item)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (indexes.TryGetValue(collection, out var collectionIndex) &&
                collectionIndex.ContainsKey(id))
            {
                return;
            }

            Add(collection, id, item);
        }

        if (state.combat != null)
        {
            foreach (var card in state.combat.hand)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["target"] = card.target_type,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["is_x_cost"] = card.costs_x,
                    ["is_x_star_cost"] = card.star_costs_x,
                    ["playable"] = card.playable,
                    ["unplayable_reason"] = card.unplayable_reason,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods
                });
            }

            foreach (var pile in new[] { state.combat.piles.draw, state.combat.piles.discard, state.combat.piles.exhaust })
            {
                foreach (var stack in pile.stacks)
                {
                    AddIfMissing("cards", stack.card_id, new Dictionary<string, object?>
                    {
                        ["id"] = stack.card_id,
                        ["name"] = stack.name,
                        ["description"] = stack.rules_text,
                        ["type"] = stack.card_type,
                        ["rarity"] = stack.rarity,
                        ["cost"] = stack.energy_cost,
                        ["star_cost"] = stack.star_cost,
                        ["is_x_cost"] = stack.costs_x,
                        ["is_x_star_cost"] = stack.star_costs_x,
                        ["pile"] = pile.pile,
                        ["pile_count"] = stack.count,
                        ["non_attack"] = stack.non_attack,
                        ["block_card"] = stack.block_card,
                        ["defensive_out"] = stack.defensive_out,
                        ["keywords"] = stack.keywords,
                        ["mods"] = stack.mods
                    });
                }
            }

            foreach (var enemy in state.combat.enemies)
            {
                Add("monsters", enemy.enemy_id, new Dictionary<string, object?>
                {
                    ["id"] = enemy.enemy_id,
                    ["name"] = enemy.name,
                    ["current_hp"] = enemy.current_hp,
                    ["max_hp"] = enemy.max_hp,
                    ["block"] = enemy.block,
                    ["intent"] = enemy.intent,
                    ["move_id"] = enemy.move_id,
                    ["intents"] = enemy.intents,
                    ["powers"] = enemy.powers
                });

                foreach (var power in enemy.powers)
                {
                    AddPower(power);
                }
            }

            foreach (var power in state.combat.player.powers)
            {
                AddPower(power);
            }
        }

        if (state.run != null)
        {
            foreach (var card in state.run.deck)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["is_x_cost"] = card.costs_x,
                    ["is_x_star_cost"] = card.star_costs_x,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods,
                    ["can_remove"] = card.can_remove
                });
            }

            foreach (var relic in state.run.relics)
            {
                Add("relics", relic.relic_id, new Dictionary<string, object?>
                {
                    ["id"] = relic.relic_id,
                    ["name"] = relic.name,
                    ["description"] = relic.description,
                    ["stack"] = relic.stack,
                    ["is_melted"] = relic.is_melted
                });
            }

            foreach (var potion in state.run.potions.Where(potion => potion.occupied))
            {
                Add("potions", potion.potion_id, new Dictionary<string, object?>
                {
                    ["id"] = potion.potion_id,
                    ["name"] = potion.name,
                    ["description"] = potion.description,
                    ["rarity"] = potion.rarity,
                    ["usage"] = potion.usage,
                    ["target"] = potion.target_type,
                    ["can_use"] = potion.can_use,
                    ["can_discard"] = potion.can_discard
                });
            }
        }

        if (state.reward != null)
        {
            foreach (var card in state.reward.card_options)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["upgraded"] = card.upgraded,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods
                });
            }

            foreach (var option in state.reward.rewards)
            {
                if (!string.Equals(option.reward_type, "Potion", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add("potions", option.item_id, new Dictionary<string, object?>
                {
                    ["id"] = option.item_id,
                    ["name"] = option.name,
                    ["description"] = option.details,
                    ["rarity"] = option.rarity,
                    ["usage"] = option.usage,
                    ["target"] = option.target_type,
                    ["reward_claimable"] = option.claimable
                });
            }
        }

        if (state.selection != null)
        {
            foreach (var card in state.selection.cards)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["upgraded"] = card.upgraded,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods,
                    ["selectable"] = card.selectable
                });
            }
        }

        if (state.shop != null)
        {
            foreach (var card in state.shop.cards)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["price"] = card.price,
                    ["enough_gold"] = card.enough_gold,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods
                });
            }

            foreach (var relic in state.shop.relics)
            {
                Add("relics", relic.relic_id, new Dictionary<string, object?>
                {
                    ["id"] = relic.relic_id,
                    ["name"] = relic.name,
                    ["rarity"] = relic.rarity,
                    ["price"] = relic.price,
                    ["enough_gold"] = relic.enough_gold
                });
            }

            foreach (var potion in state.shop.potions)
            {
                Add("potions", potion.potion_id, new Dictionary<string, object?>
                {
                    ["id"] = potion.potion_id,
                    ["name"] = potion.name,
                    ["rarity"] = potion.rarity,
                    ["usage"] = potion.usage,
                    ["price"] = potion.price,
                    ["enough_gold"] = potion.enough_gold
                });
            }
        }

        if (state.chest != null)
        {
            foreach (var relic in state.chest.relic_options)
            {
                Add("relics", relic.relic_id, new Dictionary<string, object?>
                {
                    ["id"] = relic.relic_id,
                    ["name"] = relic.name,
                    ["rarity"] = relic.rarity
                });
            }
        }

        if (state.@event != null)
        {
            Add("events", state.@event.event_id, new Dictionary<string, object?>
            {
                ["id"] = state.@event.event_id,
                ["name"] = state.@event.title,
                ["description"] = state.@event.description,
                ["options"] = state.@event.options,
                ["is_finished"] = state.@event.is_finished
            });
        }

        return indexes;

        void AddPower(CombatPowerPayload power)
        {
            Add("powers", power.power_id, new Dictionary<string, object?>
            {
                ["id"] = power.power_id,
                ["name"] = power.name,
                ["description"] = power.description,
                ["amount"] = power.amount,
                ["is_debuff"] = power.is_debuff,
                ["stack_type"] = power.stack_type
            });
        }
    }

    private static void AddIndexedItem(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes,
        string collection,
        string? id,
        Dictionary<string, object?> item)
    {
        if (string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!indexes.TryGetValue(collection, out var collectionIndex))
        {
            collectionIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            indexes[collection] = collectionIndex;
        }

        collectionIndex[id] = item;
        collectionIndex[id.ToUpperInvariant()] = item;
        collectionIndex[id.ToLowerInvariant()] = item;
    }

    private static string? TryLookupString(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes,
        string collection,
        string? id,
        string field)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !indexes.TryGetValue(collection, out var collectionIndex) ||
            !TryGetIndexedItem(collectionIndex, id, out var item) ||
            !item.TryGetValue(field, out var value))
        {
            return null;
        }

        return value?.ToString();
    }

    private static string ExtractModelId(object model)
    {
        foreach (var memberName in new[] { "Id", "ModelId", "MoveId" })
        {
            var value = ReadModelValue(model, memberName);
            var entry = ReadMemberValue(value, "Entry")?.ToString();
            if (!string.IsNullOrWhiteSpace(entry))
            {
                return entry;
            }

            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var typeName = model.GetType().Name;
        return Regex.Replace(typeName, "(Model|Power)$", string.Empty);
    }

    private static string ExtractModelName(object model, string fallback)
    {
        foreach (var memberName in new[] { "Title", "Name", "DisplayName" })
        {
            var text = TryCoerceText(ReadModelValue(model, memberName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeRulesText(text);
            }
        }

        return fallback;
    }

    private static string ReadModelText(object model, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var text = TryCoerceText(ReadModelValue(model, memberName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeRulesText(text);
            }
        }

        return string.Empty;
    }

    private static object? ReadModelValue(object? model, string memberName)
    {
        if (model == null)
        {
            return null;
        }

        return ReadMemberValue(model, memberName);
    }

    private static object? ReadMemberValue(object? instance, string memberName)
    {
        if (instance == null)
        {
            return null;
        }

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static;

        try
        {
            var type = instance is Type typeInstance ? typeInstance : instance.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(instance is Type ? null : instance);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(instance is Type ? null : instance);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string TryCoerceText(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        // Formatting a LocString without its dynamic vars logs an error; raw text preserves its placeholders.
        try
        {
            var method = value.GetType().GetMethod("GetRawText", flags, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType == typeof(string))
            {
                var rawText = method.Invoke(value, null) as string;
                if (!string.IsNullOrEmpty(rawText))
                {
                    return rawText;
                }
            }
        }
        catch
        {
        }

        try
        {
            var method = value.GetType().GetMethod("GetFormattedText", flags, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType == typeof(string))
            {
                return method.Invoke(value, null) as string ?? string.Empty;
            }
        }
        catch
        {
        }

        try
        {
            var textProperty = value.GetType().GetProperty("Text", flags);
            if (textProperty?.PropertyType == typeof(string))
            {
                return textProperty.GetValue(value) as string ?? string.Empty;
            }
        }
        catch
        {
        }

        return value.ToString() ?? string.Empty;
    }

    private static string NormalizeRulesText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value, @"\[(?:/?[^\]]+)\]", string.Empty);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string[] ReadStringArray(object model, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadModelValue(model, memberName);
            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                var values = enumerable
                    .Cast<object?>()
                    .Select(item => item?.ToString())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => NormalizeRulesText(text!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(text => text, StringComparer.Ordinal)
                    .ToArray();
                if (values.Length > 0)
                {
                    return values;
                }
            }
        }

        return Array.Empty<string>();
    }

    private static Dictionary<string, object?> ReadDynamicVars(object model)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var dynamicVars = ReadModelValue(model, "DynamicVars") ??
            ReadModelValue(model, "Vars") ??
            ReadModelValue(model, "DynamicVarSet");

        CollectDynamicVars(dynamicVars, result);
        return result;
    }

    private static void CollectDynamicVars(object? value, Dictionary<string, object?> result)
    {
        if (value == null || value is string)
        {
            return;
        }

        if (value is System.Collections.IDictionary dictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                AddDynamicVar(result, entry.Key?.ToString(), CoerceDynamicVarValue(entry.Value));
            }

            return;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                var key = ReadMemberValue(item, "Key")?.ToString() ??
                    ReadMemberValue(item, "Name")?.ToString() ??
                    ReadMemberValue(item, "Id")?.ToString();
                var rawValue = ReadMemberValue(item, "Value") ?? item;
                AddDynamicVar(result, key, CoerceDynamicVarValue(rawValue));
            }

            if (result.Count > 0)
            {
                return;
            }
        }

        foreach (var memberName in new[] { "Vars", "Values", "Items", "_vars", "_values" })
        {
            var nested = ReadMemberValue(value, memberName);
            if (nested == null || ReferenceEquals(nested, value))
            {
                continue;
            }

            CollectDynamicVars(nested, result);
            if (result.Count > 0)
            {
                return;
            }
        }
    }

    private static void AddDynamicVar(Dictionary<string, object?> result, string? key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var normalizedKey = key.Trim();
        if (normalizedKey.Contains('.', StringComparison.Ordinal))
        {
            normalizedKey = normalizedKey[(normalizedKey.LastIndexOf('.') + 1)..];
        }

        if (!result.ContainsKey(normalizedKey))
        {
            result[normalizedKey] = value;
        }
    }

    private static object? CoerceDynamicVarValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var direct = ConvertToNullableInt(value);
        if (direct.HasValue)
        {
            return direct.Value;
        }

        foreach (var memberName in new[] { "PreviewValue", "EnchantedValue", "BaseValue", "Value", "Amount" })
        {
            var memberValue = ReadMemberValue(value, memberName);
            var numeric = ConvertToNullableInt(memberValue);
            if (numeric.HasValue)
            {
                return numeric.Value;
            }
        }

        return TryCoerceText(value);
    }

    private static int? ReadDynamicVarInt(Dictionary<string, object?> vars, string key)
    {
        if (vars.TryGetValue(key, out var value))
        {
            return ConvertToNullableInt(value);
        }

        return null;
    }

    private static int? ConvertToNullableInt(object? value)
    {
        if (value == null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                float floatValue => (int)Math.Round(floatValue),
                double doubleValue => (int)Math.Round(doubleValue),
                decimal decimalValue => (int)Math.Round(decimalValue),
                _ when int.TryParse(value.ToString(), out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBool(object model, string memberName)
    {
        try
        {
            var value = ReadModelValue(model, memberName);
            return value != null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadNestedBool(object model, string outerMember, string innerMember)
    {
        try
        {
            var value = ReadMemberValue(ReadModelValue(model, outerMember), innerMember);
            return value != null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadInt(object model, string memberName)
    {
        try
        {
            var value = ReadModelValue(model, memberName);
            return value == null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadNestedInt(object model, string outerMember, string innerMember)
    {
        try
        {
            var value = ReadMemberValue(ReadModelValue(model, outerMember), innerMember);
            return value == null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetIndexedItem(
        Dictionary<string, Dictionary<string, object?>> index,
        string id,
        out Dictionary<string, object?> item)
    {
        if (index.TryGetValue(id, out item!) ||
            index.TryGetValue(id.ToUpperInvariant(), out item!) ||
            index.TryGetValue(id.ToLowerInvariant(), out item!))
        {
            return true;
        }

        item = new Dictionary<string, object?>();
        return false;
    }

    private static object FilterFields(Dictionary<string, object?> item, string[] fields)
    {
        if (fields.Length == 0)
        {
            return item;
        }

        return fields
            .Where(item.ContainsKey)
            .ToDictionary(field => field, field => item[field], StringComparer.Ordinal);
    }

    private static bool LooksLikeHpLoss(EventOptionPayload option)
    {
        var text = $"{option.title} {option.description}";
        return text.Contains("HP", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("Lose", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("loss", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeCurse(EventOptionPayload option)
    {
        var text = $"{option.title} {option.description}";
        return text.Contains("curse", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("affliction", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetSpace(string? targetIndexSpace)
    {
        if (string.IsNullOrWhiteSpace(targetIndexSpace))
        {
            return "target";
        }

        return targetIndexSpace.Contains("player", StringComparison.OrdinalIgnoreCase)
            ? "player"
            : "enemy";
    }

    private static bool IsCombatDecisionUnstable(GameStatePayload state, IReadOnlyList<DecisionChoicePayload> choices)
    {
        var combat = state.combat;
        if (combat == null)
        {
            ResetCombatStability();
            return true;
        }

        if (!HasMeaningfulChoices(choices))
        {
            return true;
        }

        if (combat.hand.Length == 0 &&
            choices.Count > 0 &&
            choices.All(choice => choice.kind == "end_turn") &&
            combat.cards_played_this_turn == 0)
        {
            return true;
        }

        return !IsCombatSnapshotStable(BuildCombatStabilitySignature(state, choices), DateTime.UtcNow);
    }

    private static bool HasMeaningfulChoices(IReadOnlyList<DecisionChoicePayload> choices)
    {
        return choices.Any(choice => !IsPassiveChoice(choice));
    }

    private static bool IsPassiveChoice(DecisionChoicePayload choice)
    {
        return choice.kind is "discard_potion" or "save_and_quit";
    }

    private static string BuildCombatStabilitySignature(GameStatePayload state, IReadOnlyList<DecisionChoicePayload> choices)
    {
        var combat = state.combat;
        return JsonHelper.Serialize(new
        {
            state.run_id,
            state.screen,
            floor = state.run?.floor,
            state.turn,
            state.available_actions,
            player = combat == null
                ? null
                : new
                {
                    combat.player.current_hp,
                    combat.player.max_hp,
                    combat.player.block,
                    combat.player.energy,
                    combat.player.stars,
                    powers = combat.player.powers.Select(PowerStabilityProjection).ToArray()
                },
            combat?.player_turn_number,
            combat?.player_turn_phase,
            combat?.cards_played_this_turn,
            combat?.attacks_played_this_turn,
            combat?.skills_played_this_turn,
            combat?.powers_played_this_turn,
            hand = combat?.hand.Select(card => new
            {
                card.index,
                card.card_id,
                card.upgraded,
                card.energy_cost,
                card.star_cost,
                card.playable,
                card.unplayable_reason,
                valid_targets = card.valid_target_indices
            }).ToArray(),
            piles = combat == null
                ? null
                : new
                {
                    draw = PileStabilityProjection(combat.piles.draw),
                    discard = PileStabilityProjection(combat.piles.discard),
                    exhaust = PileStabilityProjection(combat.piles.exhaust)
                },
            enemies = combat?.enemies.Select(enemy => new
            {
                enemy.index,
                enemy.enemy_id,
                enemy.current_hp,
                enemy.max_hp,
                enemy.block,
                enemy.is_alive,
                enemy.is_hittable,
                enemy.intent,
                enemy.move_id,
                intents = enemy.intents.Select(intent => new
                {
                    intent.index,
                    intent.intent_type,
                    intent.label,
                    intent.damage,
                    intent.hits,
                    intent.total_damage,
                    intent.status_card_count
                }).ToArray(),
                powers = enemy.powers.Select(PowerStabilityProjection).ToArray()
            }).ToArray(),
            choices = choices.Select(choice => new
            {
                choice.action_id,
                choice.kind,
                choice.risk_tags,
                choice.source,
                choice.preview
            }).ToArray()
        });
    }

    private static object PileStabilityProjection(CombatPilePayload pile)
    {
        return new
        {
            pile.count,
            pile.stack_count,
            pile.shown_stack_count,
            pile.truncated,
            pile.omitted_stack_count,
            pile.attack_count,
            pile.skill_count,
            pile.power_count,
            pile.status_count,
            pile.curse_count,
            pile.non_attack_count,
            pile.block_card_count,
            pile.defensive_out_count,
            pile.non_attack_defensive_out_count,
            pile.skill_defensive_out_count,
            stacks = pile.stacks.Select(stack => new
            {
                stack.card_id,
                stack.upgraded,
                stack.card_type,
                stack.energy_cost,
                stack.star_cost,
                stack.count
            }).ToArray()
        };
    }

    private static object PowerStabilityProjection(CombatPowerPayload power)
    {
        return new
        {
            power.index,
            power.power_id,
            power.amount,
            power.is_debuff
        };
    }

    private static bool IsCombatSnapshotStable(string signature, DateTime nowUtc)
    {
        lock (CombatStabilityGate)
        {
            if (!string.Equals(_lastCombatStabilitySignature, signature, StringComparison.Ordinal))
            {
                _lastCombatStabilitySignature = signature;
                _lastCombatStabilityChangedUtc = nowUtc;
                return false;
            }

            return nowUtc - _lastCombatStabilityChangedUtc >= CombatStableDelay;
        }
    }

    private static void ResetCombatStability()
    {
        lock (CombatStabilityGate)
        {
            _lastCombatStabilitySignature = null;
            _lastCombatStabilityChangedUtc = DateTime.MinValue;
        }
    }

    private static void MarkDecisionInFlight(string decisionId)
    {
        lock (InFlightDecisionGate)
        {
            _inFlightDecisionId = decisionId;
        }
    }

    private static bool IsDecisionInFlight(string decisionId)
    {
        lock (InFlightDecisionGate)
        {
            if (_inFlightDecisionId == null)
            {
                return false;
            }

            if (string.Equals(_inFlightDecisionId, decisionId, StringComparison.Ordinal))
            {
                return true;
            }

            // A different decision proves that the accepted action completed its
            // handoff. Release the lease as the new window becomes visible.
            _inFlightDecisionId = null;
            return false;
        }
    }

    private static void ReleaseInFlightDecision(string decisionId)
    {
        lock (InFlightDecisionGate)
        {
            if (string.Equals(_inFlightDecisionId, decisionId, StringComparison.Ordinal))
            {
                _inFlightDecisionId = null;
            }
        }
    }

    private static string? GetString(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int? GetInt(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string SanitizeIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? "run_unknown" : builder.ToString();
    }
}

internal sealed class DecisionRequestOptions
{
    public string? profile { get; init; }

    public bool include_raw_state { get; init; }

    public bool include_relevant_game_data { get; init; }
}

internal sealed class DecisionWaitRequest
{
    public int? timeout_ms { get; init; }

    public string? profile { get; init; }

    public bool include_raw_state { get; init; }

    public bool include_relevant_game_data { get; init; } = true;

    public string? after_decision_id { get; init; }
}

internal sealed class DecisionActRequest
{
    public string? decision_id { get; init; }

    public string? action_id { get; init; }

    public Dictionary<string, object?>? @params { get; init; }

    public string? client_note { get; init; }
}

internal sealed class DecisionPreviewRequest
{
    public string? decision_id { get; init; }

    public string? action_id { get; init; }
}

internal sealed class DecisionPreviewResponsePayload
{
    public string decision_id { get; init; } = string.Empty;

    public string action_id { get; init; } = string.Empty;

    public string kind { get; init; } = string.Empty;

    public bool mutation_performed { get; init; }

    public bool complete { get; init; }

    public bool incomplete { get; init; }

    public string source { get; init; } = string.Empty;

    public object? preview { get; init; }

    public object? coverage { get; init; }

    public string[] limitations { get; init; } = Array.Empty<string>();
}

internal sealed class DecisionCurrentPayload
{
    public bool available { get; init; }

    public DecisionWindowPayload? decision { get; init; }

    public string? reason { get; init; }

    public string? screen { get; init; }

    public string? last_transition { get; init; }

    public GameStatePayload? raw_state { get; init; }
}

internal sealed class DecisionWindowPayload
{
    public string decision_id { get; init; } = string.Empty;

    public int decision_version { get; init; }

    public int state_version { get; init; }

    public string protocol_version { get; init; } = DecisionWindowService.ProtocolVersion;

    public string run_id { get; init; } = "run_unknown";

    public string created_at_utc { get; init; } = string.Empty;

    public bool stable { get; init; }

    public string phase { get; init; } = "unknown";

    public string screen { get; init; } = "UNKNOWN";

    public string choice_signature { get; init; } = string.Empty;

    public Dictionary<string, object?> summary { get; init; } = new();

    public Dictionary<string, object?> context { get; init; } = new();

    public DecisionChoicePayload[] choices { get; init; } = Array.Empty<DecisionChoicePayload>();

    public Dictionary<string, object?> knowledge { get; init; } = new();

    public object? diagnostics { get; init; }
}

internal sealed class DecisionChoicePayload
{
    public string action_id { get; init; } = string.Empty;

    public string kind { get; init; } = string.Empty;

    public string label { get; init; } = string.Empty;

    public string? summary { get; init; }

    public string[] risk_tags { get; init; } = Array.Empty<string>();

    public string attention { get; init; } = "normal";

    public Dictionary<string, object?> source { get; init; } = new();

    public Dictionary<string, object?> params_schema { get; init; } = new();

    public object? preview { get; init; }
}

internal sealed class DecisionActResponsePayload
{
    public string action_id { get; init; } = string.Empty;

    public string kind { get; init; } = string.Empty;

    public string status { get; init; } = "failed";

    public bool stable { get; init; }

    public string message { get; init; } = string.Empty;

    public string previous_decision_id { get; init; } = string.Empty;

    public object? result_delta { get; init; }

    public long action_trace_cursor { get; init; }

    public ActionTracePayload? action_trace { get; init; }

    public DecisionWindowPayload? next_decision { get; init; }
}

internal sealed class GameDataLookupRequest
{
    public GameDataLookupItemRequest[] items { get; init; } = Array.Empty<GameDataLookupItemRequest>();

    public string[]? fields { get; init; }
}

internal sealed class GameDataLookupItemRequest
{
    public string? collection { get; init; }

    public string? id { get; init; }
}

internal sealed class GameDataLookupPayload
{
    public Dictionary<string, object?> items { get; init; } = new();

    public Dictionary<string, object?> metadata { get; init; } = new();
}

internal sealed class GameDataSearchRequest
{
    public string? query { get; init; }

    public string[]? collections { get; init; }

    public int? limit { get; init; }
}

internal sealed class GameDataSearchPayload
{
    public string query { get; init; } = string.Empty;

    public string[] collections { get; init; } = Array.Empty<string>();

    public GameDataSearchItemPayload[] matches { get; init; } = Array.Empty<GameDataSearchItemPayload>();

    public string[] available_collections { get; init; } = Array.Empty<string>();

    public Dictionary<string, object?> metadata { get; init; } = new();
}

internal sealed class GameDataSearchItemPayload
{
    public string collection { get; init; } = string.Empty;

    public string id { get; init; } = string.Empty;

    public string? name { get; init; }

    public string? model_type { get; init; }

    public string? description { get; init; }

    public int match_rank { get; init; }
}

internal sealed class GameDataIdListPayload
{
    public string collection { get; init; } = string.Empty;

    public string? query { get; init; }

    public int offset { get; init; }

    public int limit { get; init; }

    public int total { get; init; }

    public GameDataIdItemPayload[] items { get; init; } = Array.Empty<GameDataIdItemPayload>();

    public string[] available_collections { get; init; } = Array.Empty<string>();

    public Dictionary<string, object?> metadata { get; init; } = new();
}

internal sealed class GameDataIdItemPayload
{
    public string id { get; init; } = string.Empty;

    public string? name { get; init; }

    public string? model_type { get; init; }
}

internal sealed class GameDataExportPayload
{
    public Dictionary<string, Dictionary<string, object?>> collections { get; init; } = new();

    public Dictionary<string, object?> metadata { get; init; } = new();
}
