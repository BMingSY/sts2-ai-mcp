# STS2 AI MCP v2 Implementation Plan

Status: draft for review

This plan intentionally keeps v1 alive. v2 is a new AI-safe protocol surface layered on existing low-level state/action code.

---

## Branch Strategy

- Current branch: `design/sts2-ai-mcp-v2`
- First review artifact: design documents only.
- After approval, implementation can continue on this branch or a follow-up branch.
- v1 debug endpoints stay available until v2 has passed real-run validation.

---

## Milestones

### M0: Design Freeze

Deliverables:

- `docs/ai-mcp-v2-design.md`
- `docs/ai-mcp-v2-api.md`
- `docs/ai-mcp-v2-implementation-plan.md`

Exit criteria:

- Decision window shape accepted.
- API endpoint names accepted.
- AI-safe MCP tool list accepted.
- Known dangerous v1 actions are explicitly excluded from AI-safe v2.
- MCP-side run decision logging accepted as best-effort, non-blocking behavior.

### M1: Decision Window Skeleton

Mod-side deliverables:

- `DecisionWindowService`
- `DecisionWindowPayload`
- `DecisionChoicePayload`
- `GET /v2/decision/current`
- `POST /v2/decision/wait`

Initial scope:

- Return `available=false` while unstable.
- Return stable decisions for `combat`, `map`, `reward`, `event`, `rest`, `shop`, `chest`, `modal`, and `game_over`.
- Include `decision_id`, `choice_signature`, phase, summary, context, and choices.

No action execution changes yet.

Validation:

- Unit tests for decision id and choice signature stability.
- Simulated state changes verify no decision is emitted during unstable transitions.

### M2: Action Registry and Freshness Validation

Deliverables:

- `ActionRegistry`
- `POST /v2/decision/act`
- stale decision rejection
- action id to existing v1 action implementation mapping

Initial scope:

- Combat `play_card`, `end_turn`
- Reward `claim_reward`, `choose_reward_card`, `skip_reward_cards`
- Map `choose_map_node`
- Event/rest/shop/chest can follow once the registry pattern is proven.

Validation:

- Submitting an old `decision_id` returns `stale_decision`.
- Submitting an unknown `action_id` returns `invalid_action`.
- Reordered rewards or changed hand cards invalidate old choices.

### M3: Relevant Game Data Hydration

Deliverables:

- `GameDataHydrator`
- `knowledge.relevant`
- `knowledge.metadata` with game/mod/data source versioning
- keyword glossary extraction for visible card/event/power text
- `POST /v2/data/lookup`

Initial scope:

- hand cards
- reward cards
- current enemies
- current player/enemy powers
- current relics and potions
- current event options

Validation:

- Unknown boss decision includes monster metadata and power descriptions.
- Reward card decision includes card descriptions and upgrade preview when available.
- Token size remains manageable for normal combat and reward screens.
- Cached data is marked stale or lower-confidence when it does not match the loaded game version.

### M4: MCP Decision Logging

Deliverables:

- MCP-side `DecisionLogService`
- automatic run log creation under `agent_knowledge/run_logs/`
- automatic append after each `take_action`
- optional `append_decision_note` MCP helper if automatic logging is insufficient

Validation:

- A short mocked run creates one log file.
- Each action records `decision_id`, `action_id`, phase, state summary, selected label, note, and result.
- Logging failure is returned as a warning and does not block a game action.

### M5: MCP v2 Profile

Deliverables:

- MCP tool profile `ai_safe_v2`
- tools:
  - `health_check`
  - `wait_for_decision`
  - `get_current_decision`
  - `take_action`
  - `lookup_game_data`
  - optional `append_decision_note`

Validation:

- Existing v1 guided/full profiles still work.
- `ai_safe_v2` does not expose `resolve_rewards`, `collect_rewards_and_proceed`, direct per-action tools, or debug console actions.
- `discard_potion` is visible when the game exposes it, with an irreversible/caution tag.
- A simple MCP client can play one action at a time using only decision ids and action ids.

### M6: Real-Run Regression

Use known failure modes as acceptance tests:

| Scenario | Expected v2 Behavior |
| --- | --- |
| New combat draw transition | No combat decision until full stable hand/action window |
| New turn draw transition | No `play_card` or `end_turn` choice from partial hand |
| Act 1 boss reward | Exposes card choices; does not auto-pick first card |
| Reward claim index shifts | Old reward choices become stale after one claim |
| Hand index shifts after play | Old combat choices become stale |
| Knowledge Demon curse choice | Exposes `selection.cards[]` as decision choices |
| Forced-end-only state | End-turn remains visible when legal, with risk tags; no human confirmation gate |
| High-risk event option | Lethal/HP-loss tags visible before action |

Exit criteria:

- Run logs show only v2 decisions during normal play.
- No user-facing play requires raw v1 action calls except explicit debugging.

---

## Implementation Notes

### DecisionWindowService

Responsibilities:

- Read the current game state on the game thread.
- Detect phase.
- Apply phase-specific stability rules.
- Build choices.
- Build `choice_signature`.
- Cache the current stable decision.
- Invalidate the decision whenever relevant state changes.

Suggested internal methods:

```csharp
DecisionWindowResult GetCurrent(Profile profile);
Task<DecisionWindowResult> WaitAsync(Profile profile, string? afterDecisionId, TimeSpan timeout);
bool TryBuildDecision(GameStatePayload state, Profile profile, out DecisionWindowPayload decision);
string BuildChoiceSignature(DecisionWindowPayload decision);
```

### ActionRegistry

Responsibilities:

- Store current decision choices.
- Resolve `action_id` to a server-side action plan.
- Validate `decision_id` and `choice_signature`.
- Execute through existing action helpers.
- Return completed/pending plus optional next decision.

Suggested action plan:

```csharp
internal sealed class DecisionActionPlan
{
    public string ActionId { get; init; }
    public string Kind { get; init; }
    public Func<Task<ActionResponsePayload>> ExecuteAsync { get; init; }
}
```

The first implementation may still call existing v1 action methods internally. The important difference is that v2 validates freshness before calling them.

### GameDataHydrator

Responsibilities:

- Collect ids from context and choices.
- Query in-process game model exports.
- Return compact relevant fields by phase.
- Avoid duplicating huge full collections in every decision.

---

## Test Plan

### Unit Tests

- Decision phase detection.
- Choice signature changes when hand, reward, target, or screen changes.
- Stable decision not emitted before configured delay.
- Stale decision rejection.
- AI-safe profile excludes dangerous choices.
- Relevant game data contains requested ids and compact fields.

### Integration Tests

- Mocked state/action client through MCP v2 tools.
- HTTP endpoint smoke tests for current/wait/act/data.
- v1 still passes existing tests.

### Live Validation

- Start game from Steam.
- Verify `/v2/decision/current` returns unavailable during transitions.
- Verify `/v2/decision/wait` returns stable combat decisions.
- Play a short run using only v2 MCP tools.
- Record run log entries with `decision_id` and `action_id`.
- Confirm normal play does not require any human confirmation prompt from v2.

---

## Rollback Plan

- v1 endpoints and MCP profiles remain unchanged.
- v2 endpoints are additive under `/v2`.
- If v2 decision building fails, `ai_safe_v2` reports `decision_unavailable` and the operator can fall back to v1 debug tools.
- No v1 behavior should be removed until v2 has passed a full run validation.

---

## Review Checklist

- Are the endpoint names acceptable?
- Strict decision binding is accepted: `take_action` must include `decision_id + action_id`; should `choice_signature` also be echoed for diagnostics?
- Strict stale-decision rejection is still open for validation. Default proposal: reject stale actions and force `wait_for_decision` again.
- Lethal/risky choices remain visible with risk tags; no human confirmation gate in normal play.
- `discard_potion` remains visible when the game exposes it.
- `proceed` means "continue/next"; in `ai_safe_v2`, it should only appear when it cannot skip unresolved rewards/cards/relics/events/selections.
- Should `wait_for_decision` include raw v1 state in debug profile only?
- Is compact runtime game-data hydration enough for card/reward/event choices while remaining robust to game updates?
