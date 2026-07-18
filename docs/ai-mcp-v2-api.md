# STS2 AI MCP v2 API Draft

Status: draft for review

This document defines the v2 HTTP contract that the MCP server should wrap. The normal AI-facing MCP tools should map directly to these endpoints.

---

## Versioning

Protocol version: `2026-07-18-v2-draft`

v2 endpoints live under:

```text
/v2/decision/*
/v2/data/*
```

v1 endpoints remain available for debug and compatibility.

---

## Common Response Format

### Success

```json
{
  "ok": true,
  "request_id": "req_20260704_120000_1234",
  "data": {}
}
```

### Failure

```json
{
  "ok": false,
  "request_id": "req_20260704_120000_1234",
  "error": {
    "code": "stale_decision",
    "message": "Decision window changed. Call wait_for_decision again.",
    "retryable": true,
    "details": {}
  }
}
```

### Error Codes

| Code | HTTP | Retryable | Meaning |
| --- | --- | --- | --- |
| `invalid_request` | 400 | false | Bad body, missing field, bad enum |
| `not_found` | 404 | false | Unknown route or data item |
| `decision_unavailable` | 409 | true | No stable decision is currently available |
| `stale_decision` | 409 | true | Submitted `decision_id` is no longer current |
| `invalid_action` | 409 | false | `action_id` does not exist in this decision |
| `action_not_allowed` | 409 | false | Action is hidden by the active profile |
| `invalid_target` | 409 | false | Source target disappeared or is no longer legal |
| `state_unstable` | 503 | true | Game is in a transition and should be waited on |
| `state_unavailable` | 503 | true | Game state cannot be safely read |
| `internal_error` | 500 | false | Unexpected server failure |

---

## `GET /v2/decision/current`

Return the current stable decision if one exists. This endpoint does not wait.

### Query Parameters

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `profile` | string | `ai_safe` | `ai_safe`, `debug`, or `full` |
| `include_raw_state` | boolean | `false` | Include v1 raw state for diagnostics |

### Success With Decision

```json
{
  "ok": true,
  "data": {
    "available": true,
    "decision": {
      "decision_id": "WJT8A736AV:f33:combat:t6:0007",
      "decision_version": 4,
      "state_version": 12,
      "protocol_version": "2026-07-18-v2-draft",
      "run_id": "WJT8A736AV",
      "created_at_utc": "2026-07-04T12:00:00.0000000Z",
      "stable": true,
      "phase": "combat",
      "screen": "COMBAT",
      "choice_signature": "sha256:...",
      "summary": {
        "floor": 33,
        "turn": 6,
        "character_id": "DEFECT",
        "ascension": 0,
        "current_hp": 11,
        "max_hp": 75,
        "block": 0,
        "energy": 3,
        "stars": 0,
        "incoming_damage": 21
      },
      "context": {},
      "choices": [],
      "knowledge": {}
    }
  }
}
```

### Success Without Decision

```json
{
  "ok": true,
  "data": {
    "available": false,
    "reason": "state_unstable",
    "screen": "COMBAT",
    "last_transition": "combat_turn_changed"
  }
}
```

---

## `POST /v2/decision/wait`

Wait until a stable decision window exists, then return it.

### Request

```json
{
  "timeout_ms": 20000,
  "profile": "ai_safe",
  "include_raw_state": false,
  "include_relevant_game_data": true,
  "after_decision_id": "optional-old-decision-id"
}
```

### Behavior

- If a current stable decision exists and it is not `after_decision_id`, return it immediately.
- If the current decision is equal to `after_decision_id`, wait for a new one.
- If the game is transitioning, wait until stability passes.
- If timeout expires, return `decision_unavailable` or `state_unstable`.

### Response

Same `decision` shape as `GET /v2/decision/current`.

---

## `POST /v2/decision/act`

Execute one action from a decision window, then wait for the next stable decision window before returning.

### Request

```json
{
  "decision_id": "WJT8A736AV:f33:combat:t6:0007",
  "action_id": "combat:play:card-3:enemy-0",
  "params": {},
  "client_note": "optional decision reason for MCP run logs"
}
```

`params` is only for choices that still need client-provided values. The preferred v2 shape is to generate one `action_id` per fully bound legal choice, so most actions should use an empty object.

### Success

The normal success path includes `next_decision`. This keeps clients interactive: they choose one action, the game resolves animations/queues/transitions, and the response resumes only once the next AI-safe decision point is ready.

```json
{
  "ok": true,
  "data": {
    "action_id": "combat:play:card-3:enemy-0",
    "kind": "play_card",
    "status": "completed",
    "stable": true,
    "message": "Action completed; next decision is ready.",
    "previous_decision_id": "WJT8A736AV:f33:combat:t6:0007",
    "result_delta": {
      "schema_version": 1,
      "source": "pre_post_state_comparison",
      "complete": false,
      "changes": [
        {"path": "player.energy", "before": 3, "after": 2, "delta": -1},
        {"path": "enemies[0:QUEEN].current_hp", "before": 100, "after": 88, "delta": -12}
      ],
      "trigger_events": [],
      "trigger_events_complete": false
    },
    "next_decision": {
      "decision_id": "WJT8A736AV:f33:combat:t6:0008",
      "phase": "combat",
      "choices": []
    }
  }
}
```

### Pending Timeout

If the action was accepted but the next stable decision does not become ready before the server-side action wait timeout:

```json
{
  "ok": true,
  "data": {
    "action_id": "combat:end_turn",
    "kind": "end_turn",
    "status": "pending",
    "stable": false,
    "message": "Action accepted; next decision did not become ready before timeout. Call wait_for_decision.",
    "previous_decision_id": "WJT8A736AV:f33:combat:t6:0007",
    "next_decision": null
  }
}
```

The client should call `wait_for_decision` after a pending timeout. Normal play should not see this unless the game remains in a long transition or the Mod cannot classify the next state.

`result_delta` is deliberately conservative. It reports stable values visible before and after the action. It does not invent intermediate trigger order; until engine event hooks are available, `trigger_events_complete` remains false.

---

## Decision Shape

### Top Level

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `decision_id` | string | yes | Opaque current decision id |
| `decision_version` | number | yes | v2 decision model version |
| `state_version` | number | yes | underlying state model version |
| `protocol_version` | string | yes | protocol version |
| `run_id` | string | yes | seed/run id when known |
| `created_at_utc` | string | yes | ISO timestamp |
| `stable` | boolean | yes | must be true for AI-safe decisions |
| `phase` | string | yes | current decision phase |
| `screen` | string | yes | screen classification |
| `choice_signature` | string | yes | hash of choices and key state |
| `summary` | object | yes | compact log-friendly state |
| `context` | object | yes | phase-specific state |
| `choices` | array | yes | legal choices for the local player |
| `knowledge` | object | yes | relevant game data and glossary |
| `diagnostics` | object | no | debug details, hidden from normal MCP if large |

### Choice

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `action_id` | string | yes | Opaque action id valid only for this decision |
| `kind` | string | yes | Action kind |
| `label` | string | yes | Human/AI-readable action label |
| `summary` | string | no | Short effect text |
| `risk_tags` | string[] | yes | Risk markers such as `lethal`, `hp_loss`, `irreversible` |
| `attention` | string | yes | `normal`, `caution`, or `danger`; informational only, not a human confirmation gate |
| `source` | object | yes | Structured source references |
| `params_schema` | object | yes | JSON schema for any remaining params |
| `preview` | object | no | Best-effort predicted result |

Combat hand cards and selection candidates expose an opaque `card_ref` that remains stable for that runtime card object. Targeted combat choices expose `target_entity_ref` where available. Clients should use these references instead of carrying hand or enemy indices across decision windows.

### Risk Tags

Initial tag set:

| Tag | Meaning |
| --- | --- |
| `lethal` | Choice is expected to kill the player or allow lethal end-turn damage |
| `incoming_damage` | Choice ends or preserves incoming damage |
| `hp_loss` | Choice directly loses HP or max HP |
| `curse` | Choice adds a curse, affliction, or severe negative status |
| `deck_thickening` | Choice adds a card that may reduce consistency |
| `irreversible` | Cannot be undone this run |
| `auto_flow` | Automatically advances multiple rewards/screens; hidden in `ai_safe` |
| `debug_only` | Only available outside `ai_safe` |

---

## Phase Contexts

### Combat

`context.combat` should include:

- player HP, block, energy, stars, focus, powers, orbs
- hand cards with card instance refs, costs, playable status, valid targets,
  resolved `rules_text`, the localization template in `raw_rules_text`, and live
  `dynamic_vars` (`base_value`, `enchanted_value`, and game-computed
  `preview_value`)
- enemies with ids, `current_hp`/`max_hp`, block, powers, move ids, and
  structured intents
- draw/discard/exhaust summaries when available
- lethal risk fields

Combat card previews should prefer the live dynamic-variable snapshot over
localized text parsing. This keeps damage and Block previews available when the
raw localization template contains placeholders such as `{Damage:diff()}` or
`{Block:diff()}`.

Every decision backed by a live run also includes `context.run_analysis`. It is a
factual, non-prescriptive summary of deck size, upgrades/removability, starter and
curse counts, type counts, energy-cost curve, detected draw/Block/energy/exhaust/
power/Weak/Vulnerable roles, grouped role cards, coarse warning signals, and
hypergeometric natural-access probabilities through turn 4. Its assumptions and
limitations travel with the payload; it does not rank cards or promise boss
readiness.

Choices:

- one `play_card` per legal playable card-target pair
- one `play_card` for non-target cards
- one `use_potion` per legal potion-target pair
- `end_turn`, marked with incoming and lethal risk tags but still exposed as a normal game choice

### Reward

`context.reward` should include unresolved rewards and card options.

Choices:

- `claim_reward` per claimable non-card reward
- `choose_reward_card` per card option
- `skip_reward_cards` when available
- `proceed` only when all unresolved choices are gone

Do not emit `resolve_rewards` or `collect_rewards_and_proceed` in `ai_safe`.

`proceed` means clicking the game's continue/next button. In v2 `ai_safe`, it should only appear when continuing will not skip a meaningful player choice. For example, it is correct after all rewards are claimed or skipped, after a completed event only shows a continue button, or after a chest/relic flow is resolved. It should not appear while card choices, unclaimed rewards, relic choices, event branches, or deck selections still need a decision.

### Map

`context.map` should include current node, available nodes, visible child path summaries, and boss info.

Choices:

- `choose_map_node` per available node

### Character Select

The preferred setup action binds both decisions at once:

```json
{
  "action_id": "character_select:select:IRONCLAD:a10",
  "kind": "select_character",
  "label": "Select Ironclad / A10",
  "source": {
    "character_id": "IRONCLAD",
    "ascension": 10
  }
}
```

The Mod generates one fully bound choice per unlocked character/ascension pair. The default `ai_safe_v2` MCP profile exposes `select_character(character_id, ascension)` and resolves it against those exact choices; raw and guided profiles accept the same two fields. Index-based character selection and ascension `+1/-1` remain compatibility actions, but v2 does not expose them as normal choices.

### Selection

`context.selection.cards[]` is the authoritative legal candidate list. Each runtime
candidate includes `card_ref`, raw and resolved rules text, live `dynamic_vars`,
`powers_applied`, modifier details, and an optional `consequence_preview`.
Recognized Knowledge Demon choices currently describe the live runtime amount and
constraint for `DISINTEGRATION`, `MIND_ROT`, `SLOTH`, and `WASTE_AWAY`; when the
runtime amount is unavailable the preview is explicitly incomplete and must not be
filled from static model data. `deck_interaction` relates those constraints to the
current HP, deck size, cost curve, draw cards, or energy-generation cards without
claiming a strategic ranking.

### Event

`context.event` should include event id/title/description, resolved event-level
`dynamic_vars`, and options. Each option should expose its resolved title and
description, the original `raw_title` / `raw_description` localization templates,
the subset of `dynamic_vars` referenced by those templates, and a structured
`relic_preview` when the option's hover tips identify a relic. `estimated_effects`
must prefer these structured values over localized text parsing.

Choices:

- `choose_event_option` per unlocked option
- locked options should appear in context but not choices
- lethal or HP-loss options must have risk tags

### Rest

`context.rest` should include rest options and expected values.

Choices:

- `choose_rest_option` per enabled rest option
- follow-up upgrade/remove/select screens become `combat_selection` or `rest` subphase choices with `select_deck_card`

### Shop

`context.shop` should include inventory state, gold, stock, remove cost, and affordability.

Choices:

- open/close inventory
- buy card/relic/potion per available item
- remove card flow

### Chest

Choices:

- `open_chest`
- `choose_treasure_relic`
- `proceed` only after chest and relic choices are resolved

---

## `POST /v2/data/lookup`

Lookup game data directly.

### Request

```json
{
  "items": [
    { "collection": "cards", "id": "TEMPEST" },
    { "collection": "monsters", "id": "KNOWLEDGE_DEMON_BOSS" },
    { "collection": "powers", "id": "MIND_ROT" }
  ],
  "fields": ["id", "name", "description", "type", "moves", "damage_values"]
}
```

### Response

```json
{
  "ok": true,
  "data": {
    "items": {
      "cards:TEMPEST": {},
      "monsters:KNOWLEDGE_DEMON_BOSS": {},
      "powers:MIND_ROT": {}
    }
  }
}
```

The MCP wrapper serves this lookup from a versioned local snapshot. The direct Mod endpoint remains available for diagnostics and compatibility.

## `POST /v2/data/search`

Search live ModelDb entries by exact/partial ID, localized name, model type, or description. The request accepts `query`, optional `collections`, and a capped `limit`. Results include the owning collection so similarly named cards, monsters, encounters, and enchantments are not confused.

## `GET /v2/data/ids`

List IDs in one live collection with optional `query`, `offset`, and `limit` parameters. This is intended for development and GM scenario construction; the corresponding MCP tool is debug-gated.

## `POST /v2/data/export`

Export all static ModelDb collections once for the running game version. MCP stores the response under `mcp_server/data/versions/<game_version>/` and does not call this endpoint again while a valid snapshot exists.

Collections:

- `cards`
- `monsters`
- `powers`
- `relics`
- `potions`
- `events`
- `encounters`
- `enchantments`

The export metadata includes `game_version`, `mod_version`, `exported_at_utc`, and a content hash added by the MCP cache writer.

---

## Knowledge Metadata

Decision payloads and lookup responses should include data freshness metadata when available:

```json
{
  "knowledge": {
    "metadata": {
      "game_version": "v0.98.2",
      "mod_version": "0.7.2",
      "data_source": "loaded_game_model",
      "exported_at_utc": "2026-07-04T12:00:00.0000000Z",
      "content_hash": "sha256:..."
    },
    "relevant": {}
  }
}
```

`data_source` values:

| Value | Meaning |
| --- | --- |
| `loaded_game_model` | Exported from the currently running game process |
| `mcp_versioned_cache` | Served from the snapshot matching the running game version; preferred for static data |
| `mcp_cache` | Legacy MCP cache generated from a previous export |
| `checked_in_cache` | Served from repository data files; must be treated as potentially stale after game updates |

If the loaded game version and cached data version differ, the server should mark cached data as stale or omit it from `knowledge.relevant` and require explicit lookup.

Monster exports include the runtime-generated `state_machine`: its initial state, every move, conditional/random branches, cooldown/repeat constraints, intent kinds, and base attack values. Curated overrides may add prose such as Queen planning notes, but may only fill empty fields and must never replace a runtime state machine. Live `move_id`, intents, powers, and HP remain authoritative for the active combat.

### Preview Completeness

- Damage previews distinguish `pre_target_per_hit` from per-target `final_per_hit` and `final_total_damage`.
- Complex linked effects such as “gain Block equal to damage dealt” use `linked_effects`, `preview_complete=false`, and `unmodeled_effects` when downstream triggers are not simulated.
- End-turn previews expose an ordered `hit_timeline`, `automatic_consumables`, and final HP/Block. `FAIRY_IN_A_BOTTLE` is simulated at the lethal hit before later hits continue.
- `simulation_scope=enemy_attack_intents_only` does not claim to resolve end-of-turn card, power, relic, orb, or room-script hooks.

`POST /v2/decision/preview` accepts the current `decision_id` and one `action_id`. It never mutates the run. The response declares `complete`, `incomplete`, `source`, `coverage`, and `limitations`; the current runtime does not expose a transactional combat clone/rollback API, so unsupported downstream hooks remain explicit rather than guessed.

### Ordered Action Trace

`GET /v2/trace/actions?after_sequence=N` returns an ordered ring-buffer view of engine action lifecycle events. It covers every queued `GameAction` plus `GenericHookGameAction` hook ids, with `started`, `completed`, `failed`, or `cancelled` phases. `take_action` embeds the slice that occurred after its trace cursor in `action_trace` and in `result_delta.trigger_events`. Arbitrary callbacks that do not enqueue an action are outside the declared coverage.

### Unified Trigger Progress

Relics and powers share `trigger_progress.schema_version=1` with:

- `primary.current`, optional `threshold`, `remaining`, and `next_trigger`;
- named `counters`, `thresholds`, `parameters`, and boolean `statuses`;
- `metrics[]` carrying each value's runtime source member.

For counter relics, the game's `ShowCounter`/`DisplayAmount` is authoritative. Missing values are reported as unknown and are never inferred from localized descriptions.

---

## MCP-Side Run Logging

The v2 HTTP API accepts `client_note` on `act`, but persistent review logs should be handled by the MCP server.

Recommended MCP behavior:

- Create or reuse one public markdown decision log under `agent_knowledge/run_logs/` per run.
- Append a compact table row after each `take_action` call with `action_id`, the `client_note` exactly as received, and result status. Do not translate or rewrite agent-provided text.
- Optionally create an internal JSONL diagnostic log under `agent_knowledge/mcp_logs/` with full structured decision/action metadata for development and debugging.
- Do not block gameplay on logging failures. Return the game action result and include a non-fatal logging warning if needed.
- Add a future MCP helper such as `append_decision_note` only if automatic `take_action` logging is not enough.

---

## MCP Tool Mapping

| MCP Tool | HTTP Endpoint |
| --- | --- |
| `health_check` | `GET /health` and v2 capability metadata |
| `wait_for_decision` | `POST /v2/decision/wait` |
| `get_current_decision` | `GET /v2/decision/current` |
| `preview_action` | `POST /v2/decision/preview` |
| `preview_action_plan` | MCP-local read-only preflight over the current decision |
| `take_action` | `POST /v2/decision/act` |
| `get_action_trace` | `GET /v2/trace/actions` |
| `execute_action_plan` | MCP-local orchestration over successive `POST /v2/decision/act` calls |
| `select_cards` | MCP-local multi-select convenience wrapper over `execute_action_plan` |
| `select_character` | MCP-local exact character/ascension resolver followed by `POST /v2/decision/act` |
| `lookup_game_data` | `POST /v2/data/lookup` |
| `search_game_data` | Debug-only wrapper over `POST /v2/data/search` |
| `list_model_ids` | Debug-only wrapper over `GET /v2/data/ids` |
| `append_decision_note` | MCP-local log append; optional, no HTTP endpoint required |

The MCP profile name should be `ai_safe_v2`.

MCP startup validates the protocol version, minimum state/decision versions, and all required semantic capabilities before serving tools. A mismatched or unreachable Mod fails loudly. `STS2_MCP_ALLOW_INCOMPATIBLE=1` is the only explicit local override; compatibility is never silently assumed.

### Conditional Action Plans

`preview_action_plan` performs a read-only strict-combat preflight before execution.
It resolves stable selectors and folds the known energy/star budget, card-play and
shared limits, direct Block, direct damage, enemy Block/HP, and Vulnerable into a
shadow state. It returns `validation_complete`, `effects_complete`,
`known_infeasible`, `valid_prefix_count`, projected state, per-step limitations, and
the information boundary where preflight stopped. It never calls the game action
endpoint and declares `transactional_engine_dry_run=false`.

`execute_action_plan` reduces agent round trips without weakening v2 stale-decision validation. Before mutating a combat, it runs the same preflight and returns `executed_count=0` for a known hard failure such as insufficient cumulative energy/stars, a known `SLOTH` card cap, a reused card reference, a shared play limit, or an already projected-dead target. Otherwise the MCP server executes one step at a time, consumes each action response's `next_decision`, resolves the next intent against that fresh decision, and stops before any step that is unavailable or ambiguous.

Agents should prefer a strict plan over repeated single actions when they have already
evaluated a deterministic 2-5 step combat line and none of the planned cards draw,
generate, return, transform, or randomly discard cards. Information-revealing plays
and unresolved tactical branches should remain single-step. The manual driver accepts
`batch ACTION_ID ACTION_ID ... | note` and converts current choices to stable-reference
plan steps.

```json
{
  "decision_id": "current-decision-id",
  "mode": "strict",
  "plan_id": "optional-idempotency-key",
  "client_note": "optional default note for every step",
  "steps": [
    {
      "kind": "play_card",
      "card_ref": "card:STRIKE:1234abcd",
      "target_entity_ref": "enemy:QUEEN:5678ef01",
      "note": "step-specific note"
    }
  ]
}
```

Supported combat kinds are `play_card` and `use_potion`, with at most five steps. Supported selection kinds are `select_deck_card` and `confirm_selection`, with at most twelve steps. Combat and selection actions cannot be mixed in one plan. `end_turn` is intentionally unsupported.

Preflight stops at an explicit information boundary after draws, generation/return,
upgrade/transform, random discard, unsupported power changes, X-cost effects, potion
effects, or a projected kill that can change the next decision. An information
boundary means “re-read after this prefix,” not “the prefix is illegal.” Actual
strict combat plans also stop when the phase changes, stable references are missing,
an additional card leaves the hand, an action becomes illegal, or a selector matches
zero or multiple choices. Results use partial-success semantics: completed game
actions are logged individually and cannot be rolled back. The response includes
`executed_count`, `stop_reason`, preflight, per-step results, and the latest
`next_decision`.

`select_cards` accepts the current `decision_id`, a unique list of `card_refs`, and `confirm=true|false`. It constructs the corresponding strict selection plan and confirms only when the fresh decision exposes a legal confirmation choice.

---

## Compatibility

- v1 remains the fallback and debug layer.
- v2 should not call v1 HTTP endpoints from inside the Mod. Shared internal services are fine.
- v2 action ids may internally map to v1 action implementations, but stale-decision validation must run before execution.
- v2 should not expose legacy per-action MCP tools in the AI-safe profile.
