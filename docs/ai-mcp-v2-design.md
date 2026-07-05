# STS2 AI MCP v2 Design

Status: draft for review

Protocol goal: expose Slay the Spire 2 to an AI agent as stable decision windows, not as a raw set of UI buttons.

---

## Problem Statement

The current v1 API/MCP stack is usable, but it is not AI-native:

- The agent can read transient states while the game is drawing cards, resolving animations, draining rewards, or changing screens.
- `available_actions` can be interpreted as "safe to decide", even when the state is only briefly actionable.
- Many actions are index-based. A stale `card_index`, `target_index`, or `option_index` can point at a different object after the state changes.
- High-level automation actions such as `resolve_rewards` and `collect_rewards_and_proceed` are convenient for smoke tests but unsafe for user-facing play.
- Static game knowledge is available, but it is separated from the state payload, so the agent can forget to look up unfamiliar card, monster, power, relic, potion, or event effects.

v2 should make the safe path the default path.

---

## Design Goals

1. Only expose stable decision points to the AI-facing MCP profile.
2. Replace raw indexes with server-issued `action_id` values wherever possible.
3. Bind every action to the `decision_id` that produced it.
4. Reject stale decisions instead of trying to recover from old indexes.
5. Hide dangerous automation from the AI-safe profile.
6. Include relevant game data inside the decision package when it can affect the choice.
7. Log normal play decisions from the MCP layer without blocking gameplay.
8. Keep v1 available for debugging, reproduction, and direct low-level inspection.

---

## Non-Goals

- v2 does not need to preserve the v1 tool surface.
- v2 does not need to support unattended heuristic autoplay.
- v2 does not replace v1 debug endpoints such as raw state, direct action execution, or debug console tools.
- v2 does not decide strategy. It defines what is legal, stable, current, and known.

---

## Architecture

```text
Windows game process
  STS2AIMCP Mod
    v1 services remain available for debug
    DecisionWindowService
      observes game state, queues, UI screens, and transition stability
      publishes stable decision windows
    ActionRegistry
      maps decision choices to stable action ids
      validates decision freshness before execution
    GameDataHydrator
      attaches relevant card/monster/power/relic/potion/event data

WSL / client side
  MCP v2 profile
    health_check
    wait_for_decision
    get_current_decision
    take_action
    lookup_game_data
```

The MCP server should remain a thin wrapper. The Mod is the source of truth for stability, legal choices, and action execution.

---

## Decision Window Model

A decision window is the only state shape intended for normal AI play.

Every decision window must answer:

- Is the game stable enough for a decision?
- What phase is the player in?
- Which choices are legal right now?
- What stable identifiers should the AI use to choose?
- Which game data is needed to understand the choice?
- What risks should be visible before ending a turn or taking an irreversible action?

### Decision Phases

Initial phases:

| Phase | Meaning |
| --- | --- |
| `main_menu` | Main menu and run setup decisions |
| `character_select` | Character, ascension, embark, timeline/lobby setup |
| `combat` | Player combat action window |
| `combat_selection` | Combat-created card selection, upgrade selection, or curse choice |
| `map` | Route node choice |
| `reward` | Reward claim, card reward choice, skip reward |
| `event` | Event option choice or event proceed |
| `rest` | Rest site option or follow-up card selection |
| `shop` | Shop buy/remove/open/close decisions |
| `chest` | Chest open or relic selection |
| `modal` | Blocking modal confirmation/dismissal |
| `game_over` | Game-over resolution |
| `unknown` | Mod cannot safely classify the current state |

`unknown` must not expose normal gameplay choices. It should expose diagnostics and safe passive choices only.

---

## Stability Contract

The Mod may poll internally if the game lacks direct hooks, but v2 must not publish a decision until the stability contract passes.

Minimum combat stability:

- Active screen is a valid combat room or combat selection screen.
- Local player is alive and has control.
- Combat room mode is active.
- Hand UI is not in card play animation unless the phase is `combat_selection`.
- Game action executor has no running action.
- Game action queue has no ready action.
- Hand, energy, stars, turn number, and selected screen signature remain unchanged for a configured delay.
- Current choices can be rebuilt twice with the same `choice_signature`.

Minimum non-combat stability:

- Screen classification remains unchanged for a configured delay.
- Blocking modal, reward, event, shop, rest, chest, map, and selection payloads remain internally consistent.
- Choice count and choice ids remain unchanged for a configured delay.
- No action response is still pending for the local player.

Recommended defaults:

```text
combat stable delay: 200 ms
non-combat stable delay: 150 ms
wait poll interval fallback: 50-120 ms
```

These values are implementation defaults, not protocol guarantees.

---

## Decision Identity

Every decision must include:

| Field | Purpose |
| --- | --- |
| `decision_id` | Opaque id bound to this exact decision window |
| `state_version` | Incrementing state model version |
| `decision_version` | Incrementing protocol version |
| `run_id` | Current seed/run identifier when available |
| `phase` | Decision phase |
| `screen` | Current screen classification |
| `choice_signature` | Stable hash of choices and key state inputs |
| `created_at_utc` | Timestamp for logs/debugging |

`decision_id` should include enough entropy to avoid accidental reuse, but clients must treat it as opaque.

Suggested internal components:

```text
run_id + floor + screen + phase + turn + local_player_id + monotonic counter + choice_signature hash
```

---

## Choice Model

Every legal AI action is represented as a choice.

```json
{
  "action_id": "combat:play:card-instance-123:enemy-0",
  "kind": "play_card",
  "label": "Play Strike on Cultist",
  "summary": "Deal 6 damage.",
  "requires_target": false,
  "params_schema": {},
  "risk_tags": [],
  "source": {
    "screen": "COMBAT",
    "card_instance_id": "card-instance-123",
    "target_ref": "enemy:0"
  }
}
```

The AI should select `action_id`; it should not reconstruct low-level indexes.

### Choice Kinds

Initial action kinds:

| Kind | Notes |
| --- | --- |
| `play_card` | Prefer one choice per legal card-target pair |
| `end_turn` | Include incoming damage and lethal risk tags |
| `use_potion` | Prefer one choice per potion-target pair |
| `discard_potion` | Expose when the game exposes it; mark as irreversible |
| `choose_map_node` | One choice per available node |
| `claim_reward` | One choice per non-card reward |
| `choose_reward_card` | One choice per card option |
| `skip_reward_cards` | Explicit choice, never implicit |
| `choose_event_option` | One choice per unlocked event option |
| `choose_rest_option` | One choice per rest option |
| `select_deck_card` | One choice per legal card in the current selection |
| `open_shop_inventory` | Shop room to inventory transition |
| `close_shop_inventory` | Inventory to shop room transition |
| `buy_card` | One choice per affordable/visible card |
| `buy_relic` | One choice per affordable/visible relic |
| `buy_potion` | One choice per affordable/visible potion |
| `remove_card_at_shop` | Open remove selection or select a card when already selecting |
| `open_chest` | Chest open transition |
| `choose_treasure_relic` | One choice per relic option |
| `proceed` | Only when it cannot skip a meaningful unresolved reward, card choice, relic choice, event branch, or selection |
| `confirm_modal` | Blocking modal confirmation |
| `dismiss_modal` | Blocking modal dismissal |
| `return_to_main_menu` | Only on game-over or explicit safe screens |

Dangerous automation choices such as `resolve_rewards` and `collect_rewards_and_proceed` should not be emitted by the v2 AI-safe profile.

---

## Action Freshness Rules

`take_action` must receive both `decision_id` and `action_id`.

This is mandatory in v2. The client should not be able to execute an AI-facing action with only a raw action name or index.

Default policy: the Mod must reject an action when:

- `decision_id` is not the current active decision.
- `choice_signature` has changed.
- The action id is unknown for this decision.
- The source card, target, reward, option, or screen is no longer valid.
- The action kind is not allowed in the current profile.

Strict stale-decision rejection is the safest default. If this proves too strict during implementation, only a debug profile should get a recovery mode; normal AI play should call `wait_for_decision` again.

Preferred error:

```json
{
  "ok": false,
  "error": {
    "code": "stale_decision",
    "message": "Decision window changed. Call wait_for_decision again.",
    "retryable": true,
    "details": {
      "expected_decision_id": "run:f12:combat:t3:7",
      "actual_decision_id": "run:f12:combat:t3:8"
    }
  }
}
```

---

## Relevant Game Data

Each decision may include `knowledge.relevant`:

- Hand cards and reward cards: card text, cost, type, rarity, upgrade preview, dynamic values.
- Current enemies: monster metadata, known moves, damage/block values.
- Current player/enemy powers: power description, type, stack behavior.
- Relics and potions relevant to current choices.
- Event text and option metadata.
- Keyword glossary for terms appearing in the visible text.

This does not replace full lookup tools. It reduces accidental blind decisions during high-pressure turns.

Because STS2 can update and card/monster/power effects can change, v2 must treat game data as versioned runtime data:

- Prefer data exported from the currently loaded game process over checked-in cached JSON.
- Include `game_version`, `mod_version`, `data_source`, and, when practical, a content hash or export timestamp in `knowledge.metadata`.
- Treat cached MCP data as a fallback. If the loaded game version differs from the cache version, mark the cached entry as stale or lower-confidence.
- Keep hydrated data compact by default; the AI can call `lookup_game_data` for full details.

---

## MCP v2 Profile

The AI-safe MCP profile should expose only:

- `health_check`
- `wait_for_decision`
- `get_current_decision`
- `take_action`
- `lookup_game_data`
- `append_decision_note` or automatic decision logging through `take_action`

Debug/full profiles may continue to expose v1 tools.

The skill and autoplay scripts should treat v2 as the default once it passes validation.

---

## MCP Decision Logging

Decision logging should live primarily in the MCP layer because MCP runs in WSL with direct access to `agent_knowledge/run_logs/`, while the Mod runs inside the Windows game process.

Default behavior for user-facing play:

- `wait_for_decision` returns log-friendly summary fields.
- `take_action` accepts an optional `client_note` and appends a compact row to the active run log.
- Logging is best-effort and non-blocking. A filesystem error must not prevent a legal game action.
- The MCP server creates one public markdown decision log per run under `agent_knowledge/run_logs/`. This log is for review and sharing, so it records the selected `action_id`, the agent-provided note exactly as received, and the result status. MCP must not translate or rewrite the note.
- The MCP server may also create an internal JSONL log under `agent_knowledge/mcp_logs/` for development diagnostics. That log may include `decision_id`, phase, screen, summary, selected label, source, result status, and other structured fields.

The Mod may expose an action history for diagnostics, but normal review logs should be produced by MCP.

---

## Logging and Review Payloads

Decision windows should be easy to paste into run logs. Include stable summary fields:

- seed/run id
- character and ascension
- floor/screen/phase
- HP/block/energy/stars
- enemies and incoming damage
- combat pile summaries: draw/discard/exhaust counts, type counts, non-attack/defensive outs, and compact stacks; default ai-safe payloads must not expose draw order or the exact next card
- choices considered
- selected action id and label
- result status

The Mod should return enough post-action state to log whether the action completed or whether the client must wait again.

---

## Open Questions

- Can the game provide direct signals for draw completion, action queue drain, reward drain, and screen transition completion, or must v2 keep an internal sampler?
- Are stable card instance ids available across a combat turn, or do we need to synthesize ids from card object identity plus per-decision indexes?
- Should strict stale-decision rejection ever have a non-debug recovery mode?
- Is MCP-side best-effort logging enough, or should the Mod also persist an action journal for non-MCP clients?
- Which risk tags and summary fields are enough for lethal or high-damage `end_turn` choices? Current draft keeps normal gameplay choices visible and avoids human confirmation gates.
- Should v2 support multiplayer local-player-only control in the first version, or defer multiplayer until singleplayer is stable?
- How much game data should be hydrated by default before token size becomes counterproductive?
