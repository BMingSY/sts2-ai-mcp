using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;

namespace STS2AIMCP.Game;

internal static class ActionTraceService
{
    private const int MaxEvents = 4096;
    private const string HarmonyId = "sts2.ai.mcp.action-trace";
    private const string LogPrefix = "[STS2AIMCP.ActionTrace]";

    private static readonly object Gate = new();
    private static readonly List<ActionTraceEventPayload> Events = new();
    private static readonly Dictionary<GameAction, long> StartedActions = new(ReferenceEqualityComparer.Instance);
    private static Harmony? _harmony;
    private static long _sequence;

    public static long Cursor => Interlocked.Read(ref _sequence);

    public static void Initialize()
    {
        if (_harmony != null)
        {
            return;
        }

        _harmony = new Harmony(HarmonyId);
        _harmony.PatchAll(typeof(ActionTraceService).Assembly);
        Log.Info($"{LogPrefix} Engine action hooks installed");
    }

    public static void Shutdown()
    {
        // The Mod shuts down with the game process, so patches do not outlive the
        // target assembly. Older bundled Harmony builds do not expose UnpatchSelf.
        _harmony = null;
    }

    public static ActionTracePayload SnapshotSince(long afterSequence)
    {
        lock (Gate)
        {
            var earliest = Events.Count == 0 ? Cursor + 1 : Events[0].sequence;
            return new ActionTracePayload
            {
                schema_version = 1,
                source = "engine_game_action_hooks",
                after_sequence = Math.Max(0, afterSequence),
                latest_sequence = Cursor,
                truncated = afterSequence > 0 && afterSequence < earliest - 1,
                coverage = new ActionTraceCoveragePayload
                {
                    queued_game_actions = true,
                    generic_hook_actions = true,
                    arbitrary_non_action_callbacks = false
                },
                events = Events.Where(item => item.sequence > afterSequence).ToArray()
            };
        }
    }

    internal static void RecordStarted(GameAction action)
    {
        lock (Gate)
        {
            if (StartedActions.ContainsKey(action))
            {
                return;
            }

            var sequence = NextSequence();
            StartedActions[action] = sequence;
            Append(BuildEvent(action, sequence, "started", null));
        }
    }

    internal static void RecordCompleted(GameAction action)
    {
        lock (Gate)
        {
            if (!StartedActions.TryGetValue(action, out var startedSequence))
            {
                startedSequence = NextSequence();
                StartedActions[action] = startedSequence;
                Append(BuildEvent(action, startedSequence, "started", "observed_at_completion"));
            }

            var status = action.Exception == null ? "completed" : "failed";
            Append(BuildEvent(action, NextSequence(), status, null, startedSequence));
            StartedActions.Remove(action);
        }
    }

    internal static void RecordCancelled(GameAction action)
    {
        lock (Gate)
        {
            var startedSequence = StartedActions.TryGetValue(action, out var existing)
                ? existing
                : (long?)null;
            Append(BuildEvent(action, NextSequence(), "cancelled", null, startedSequence));
            StartedActions.Remove(action);
        }
    }

    private static long NextSequence()
    {
        return Interlocked.Increment(ref _sequence);
    }

    private static void Append(ActionTraceEventPayload item)
    {
        Events.Add(item);
        if (Events.Count > MaxEvents)
        {
            Events.RemoveRange(0, Events.Count - MaxEvents);
        }
    }

    private static ActionTraceEventPayload BuildEvent(
        GameAction action,
        long sequence,
        string phase,
        string? note,
        long? startedSequence = null)
    {
        return new ActionTraceEventPayload
        {
            sequence = sequence,
            timestamp_utc = DateTime.UtcNow.ToString("O"),
            phase = phase,
            started_sequence = startedSequence,
            action_id = action.Id,
            action_name = action.GetType().Name,
            action_type = action.GetType().FullName ?? action.GetType().Name,
            owner_id = action.OwnerId.ToString(),
            multiplayer_action_type = action.ActionType.ToString(),
            engine_state = action.State.ToString(),
            hook_id = action is GenericHookGameAction hook ? hook.HookId : null,
            exception = action.Exception?.GetBaseException().Message,
            note = note,
            details = BuildDetails(action)
        };
    }

    private static Dictionary<string, object?> BuildDetails(GameAction action)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var memberName in new[]
        {
            "Card", "Potion", "Power", "Relic", "Target", "Targets", "Creature", "Player",
            "Amount", "Damage", "Block", "Count", "Repeats", "Cmd", "InCombat", "RoundNumber"
        })
        {
            var value = ReadMember(action, memberName);
            var normalized = NormalizeDetailValue(value);
            if (normalized != null)
            {
                details[ToSnakeCase(memberName)] = normalized;
            }
        }

        return details;
    }

    private static object? ReadMember(object source, string memberName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            return source.GetType().GetProperty(memberName, flags)?.GetValue(source)
                ?? source.GetType().GetField(memberName, flags)?.GetValue(source)
                ?? source.GetType().GetField($"_{char.ToLowerInvariant(memberName[0])}{memberName[1..]}", flags)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static object? NormalizeDetailValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return value;
        }

        if (value.GetType().IsEnum)
        {
            return value.ToString();
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            return enumerable.Cast<object?>()
                .Take(32)
                .Select(NormalizeDetailValue)
                .Where(item => item != null)
                .ToArray();
        }

        var id = ReadMember(value, "Id");
        var modelId = ReadMember(value, "ModelId");
        var idEntry = id == null ? null : ReadMember(id, "Entry")?.ToString();
        var modelIdEntry = modelId == null ? null : ReadMember(modelId, "Entry")?.ToString();
        var name = ReadMember(value, "Name")?.ToString();
        var currentHp = ReadMember(value, "CurrentHp");
        if (idEntry != null || modelIdEntry != null || name != null || currentHp != null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = idEntry ?? modelIdEntry,
                ["name"] = name,
                ["current_hp"] = currentHp,
                ["runtime_type"] = value.GetType().Name
            };
        }

        var entry = ReadMember(value, "Entry")?.ToString();
        return entry ?? value.GetType().Name;
    }

    private static string ToSnakeCase(string value)
    {
        return string.Concat(value.Select((character, index) =>
            char.IsUpper(character) && index > 0 ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
    }
}

[HarmonyPatch(typeof(GameAction), nameof(GameAction.Execute))]
internal static class GameActionExecuteTracePatch
{
    private static void Prefix(GameAction __instance)
    {
        ActionTraceService.RecordStarted(__instance);
    }
}

[HarmonyPatch(typeof(ActionExecutor), "AfterActionFinished")]
internal static class ActionExecutorFinishedTracePatch
{
    private static void Prefix(GameAction action)
    {
        ActionTraceService.RecordCompleted(action);
    }
}

[HarmonyPatch(typeof(GameAction), nameof(GameAction.Cancel))]
internal static class GameActionCancelTracePatch
{
    private static void Prefix(GameAction __instance)
    {
        ActionTraceService.RecordCancelled(__instance);
    }
}

internal sealed class ActionTracePayload
{
    public int schema_version { get; init; }
    public string source { get; init; } = string.Empty;
    public long after_sequence { get; init; }
    public long latest_sequence { get; init; }
    public bool truncated { get; init; }
    public ActionTraceCoveragePayload coverage { get; init; } = new();
    public ActionTraceEventPayload[] events { get; init; } = Array.Empty<ActionTraceEventPayload>();
}

internal sealed class ActionTraceCoveragePayload
{
    public bool queued_game_actions { get; init; }
    public bool generic_hook_actions { get; init; }
    public bool arbitrary_non_action_callbacks { get; init; }
}

internal sealed class ActionTraceEventPayload
{
    public long sequence { get; init; }
    public string timestamp_utc { get; init; } = string.Empty;
    public string phase { get; init; } = string.Empty;
    public long? started_sequence { get; init; }
    public uint? action_id { get; init; }
    public string action_name { get; init; } = string.Empty;
    public string action_type { get; init; } = string.Empty;
    public string owner_id { get; init; } = string.Empty;
    public string multiplayer_action_type { get; init; } = string.Empty;
    public string engine_state { get; init; } = string.Empty;
    public uint? hook_id { get; init; }
    public string? exception { get; init; }
    public string? note { get; init; }
    public Dictionary<string, object?> details { get; init; } = new();
}
