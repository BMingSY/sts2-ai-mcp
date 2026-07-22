---
name: sts2-player
description: Play, continue, test, or debug Slay the Spire 2 runs through the STS2 AI MCP v2 server or STS2AIMCP mod HTTP API. Use when Codex is asked to bootstrap or operate the Windows game from WSL/Windows, start or check a local MCP server from an sts2-ai-mcp checkout, drive ai_safe_v2 decision choices or available_actions safely, investigate missing play_card/end_turn/action-window issues, run an AI/autoplay attempt to a boss, or maintain STS2 gameplay automation scripts.
---

# STS2 Player

## Overview

Drive Slay the Spire 2 through STS2 AI MCP as a state-driven agent. Prefer the v2 decision API and `ai_safe_v2` MCP profile for gameplay. Treat `decision.choices[]` as the hard action boundary when using v2, or `state.available_actions` when using raw HTTP. Read state before every action, and stop with diagnostics when the game exposes no meaningful legal action.

When Codex runs in WSL, prefer this topology: Windows owns Steam, Slay the Spire 2, and the STS2AIMCP mod HTTP API; WSL owns Codex, the MCP wrapper, and helper scripts from the repository checkout. Run MCP on Windows only when the client cannot use WSL stdio/network MCP or WSL cannot reach the mod API.

For full-run strategy, read `references/play-experience.md` before user-facing play sessions. It defines the default NOSL A10 objective as maximizing the probability of defeating both Act 3 bosses, not maximizing current HP. Read `references/encounter-notes.md` only for unfamiliar or decision-changing encounters, and still verify their current mechanics through live state/data. Read `references/run-retrospectives.md` for post-run review or strategy maintenance, not routine play. Maintain these references by replacing superseded claims, separating current mechanics from single-run observations, and extracting only generalizable lessons from raw logs. Never write run IDs, seeds, reconstructable per-turn action histories, or other log identifiers into skill references; keep those details in raw logs and store only anonymized decision patterns in the skill.

## Safety Rules

- Prefer v2 MCP tools: `wait_for_decision`, `get_current_decision`, `preview_action`, `preview_action_plan`, `run_evaluator`, `combat_horizon`, `take_action`, `execute_action_plan`, `select_cards`, `lookup_game_data`, and `append_decision_note`. Use raw `/state` and `/action` only for diagnostics or when v2 is missing a legal action that raw state exposes.
- In v2, execute only a current `decision.choices[].action_id` with the matching `decision_id`. If the state changes or an action returns stale-decision diagnostics, fetch a fresh decision before continuing.
- Use only actions currently present in the latest `state.available_actions`.
- Prefer strict `execute_action_plan` over repeated single actions when the agent has already evaluated a deterministic 2-5 step combat line and every planned card is information-stable: it does not draw, generate, return, transform, or randomly discard cards, and no intermediate damage/power result can change the unresolved tactical choice. Typical batchable lines are several basic attacks/blocks or a fixed power-plus-block sequence. Otherwise execute one action, then read/wait for a fresh state before deciding again. Use `select_cards` for one multi-card selection overlay. Plans must revalidate every step against the returned fresh decision and stop on hand mutations, phase changes, ambiguity, or unavailable actions. Never include `end_turn` in a plan. After a stopped or completed plan, inspect its `next_decision` before deciding again. Do not reuse old hand indices or target indices; use stable `card_ref` and `target_entity_ref` selectors.
- Treat `save_and_quit` and `discard_potion` as passive actions; they should not make a state "actionable" for normal play.
- For NOSL runs, never use save/reload, rollback, debug actions, or hidden-state inspection to retry or condition a decision on an outcome the player could not yet know. A normal pause is allowed only when it does not replay known randomness.
- Do not use `resolve_rewards` during user-facing play. It auto-resolves card rewards and, without an explicit index, can pick the first card by default. Claim rewards one item at a time with `claim_reward`, then use `choose_reward_card` or `skip_reward_cards` deliberately.
- In combat, do not call `end_turn` when `hand` is empty and `cards_played_this_turn == 0`; report this as an action-window bug.
- If combat has only `end_turn` and every hand card is unplayable, call it a forced-end state. Ask before continuing manually, or pass `--allow-forced-end` to the script.
- Prefer event/action notifications when available: MCP `wait_until_actionable`, `/events/stream`, or an `available_actions_changed` event. Polling is acceptable only as a fallback.
- For user-facing play, think on each actionable state yourself. Use the bundled script for smoke tests, short tactical automation, or reproduction logs, not as a substitute for turn-by-turn decisions.

## Decision Ownership

When the user asks Codex to play or continue a run, Codex should make each meaningful gameplay decision itself whenever possible. Use scripts only to gather state, wait for action windows, execute a single chosen action, or reproduce a bug. Do not hand off a whole combat, route, event chain, or run to `scripts/sts2_autoplay.py` unless the user explicitly asks for automated play, smoke testing, or regression testing.

For every actionable state, inspect the latest state and decide directly:

- In combat, evaluate enemy intents, lethal lines, incoming damage, potions, energy, hand, draw/selection overlays, and current HP before choosing one action.
- After evaluating the full current hand, actively look for a safe deterministic 2-5 action prefix and batch it. Do not default to repeated `take_action` merely because it is simpler to call. Stop the batch before the first draw/generate/return/transform/random-discard effect, uncertain kill trigger, target-dependent branch, selection overlay, or `end_turn`.
- On rewards, events, shops, rests, and map nodes, read the exposed structured options and choose based on the current run context, not the script's default heuristic.
- Use `run_evaluator` after proposing the visible reward/shop/removal candidates when access probabilities or deck-shape deltas materially affect the choice. Treat its output as arithmetic evidence only: it preserves candidate order and deliberately provides no score, ranking, or recommendation.
- Use `combat_horizon` when at least two plausible current-turn lines need the same resource/damage/Block/end-turn arithmetic. Supply only lines you have already proposed; the tool does not search the hand. Treat any stopped information boundary or incomplete preview as a requirement to re-read after the valid prefix, not as proof that the remaining line is safe.
- After executing one action or one previously evaluated strict short plan, inspect the returned fresh decision before deciding again. Do not use a plan to delegate unresolved tactical choices.
- If using a script during user-facing play, prefer `--max-steps 0` for state probes or `--until actionable` for a one-step handoff. Avoid long unattended runs.
- Use Codex-mounted MCP tools directly for normal user-facing play. Do not put a persistent terminal REPL, PTY polling loop, or a separately managed network MCP server between Codex and MCP merely to compensate for missing tools in the current session. If tools were configured after the session started, verify the stdio MCP configuration and tell the user that a session reload is required. Use a one-shot process that exits after exactly one MCP response only for bounded diagnostics when a reload is impractical. Direct MCP `take_action` remains the writer for the public markdown decision log under `agent_knowledge/run_logs/`; write concise `client_note` text in the user's language.

## Gameplay Information Sources

Use live game state and exported game data for card, monster, power, relic, potion, and event effects. Do not rely on memory when an effect matters to survival, reward choice, boss planning, or route risk.

Primary sources:

- Latest v2 decision window: MCP `get_current_decision` / `wait_for_decision`, or `/v2/decision/current?include_relevant_game_data=true`. Read `choices[]`, `choice.preview`, `context`, `summary`, `knowledge.glossary`, and `knowledge.relevant`.
- Bounded MCP calculators: call `run_evaluator` or `combat_horizon` only with the just-read cached `decision_id`. They perform no game request or action, default to 100ms, hard-cap work at 500ms/4096 states, and return a structured partial result instead of waiting indefinitely. Do not increase their limits to turn them into a search solver.
- V2 data lookup: MCP `lookup_game_data` or HTTP `/v2/data/lookup` for `cards`, `monsters`, `encounters`, `enchantments`, `powers`, `relics`, `potions`, and `events`. Prefer live v2 lookup. If MCP reports `data_source=checked_in_cache`, treat it as a fallback and verify high-risk choices against current state. During GM/debug work with `STS2_ENABLE_DEBUG_ACTIONS=1`, use `search_game_data` or `list_model_ids` instead of guessing collection names or IDs.
- Latest `/state` or MCP `get_game_state`: combat hand cards expose `card_id`, costs, `rules_text`, `resolved_rules_text`, dynamic values, target legality, playability, and unplayable reasons. Combat enemies expose `enemy_id`, HP/block, `powers[]`, `intent`/`move_id`, and structured `intents[]` including damage, hits, total damage, and status card count.
- Legacy MCP data tools in non-v2 profiles: use `get_game_data_item`, `get_game_data_items`, or `get_relevant_game_data` only when the current MCP profile exposes them. The backing exports live under `<repo>/mcp_server/data/eng/`.
- Experience notes: use `references/play-experience.md` for stable-run strategy, `references/encounter-notes.md` for routed encounter hazards, and `references/run-retrospectives.md` plus `agent_knowledge/` for evidence and review. They supplement live state and exported data; they do not replace them.

Required lookups during user-facing play:

- Query card data before choosing unfamiliar reward cards, buying cards, removing cards, upgrading a card, or planning around non-obvious text.
- Query monster data for unfamiliar enemies, elites, and bosses, or when `move_id` / `intent` text is not enough to understand future risk.
- Query power data whenever a player or enemy power/debuff can affect lethal, draw, energy, max playable cards, self-damage, block, or damage scaling. In combat, also read v2 `choice.preview` for common Weak, Vulnerable, Strength, Dexterity, Frail, block, and end-turn lethal estimates, but treat it as an estimate rather than a complete engine simulation.
- Query relic, potion, and event data before high-impact shops, potion use, event choices, or any option that can change HP, max HP, deck consistency, or boss readiness.
- If an effect is still unclear after live state plus data lookup, stop and report the uncertainty instead of guessing through a lethal or irreversible choice.

## Quick Start

Check the mod API from Windows PowerShell:

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:8080/healthz -TimeoutSec 5 | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri http://127.0.0.1:8080/state -TimeoutSec 5 | ConvertTo-Json -Depth 8
```

Check the network MCP server:

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:8765/healthz -TimeoutSec 5 | ConvertTo-Json -Depth 5
```

Run the minimal autoplay harness from WSL:

```bash
NO_PROXY=127.0.0.1,localhost python3 ~/.codex/skills/sts2-player/scripts/sts2_autoplay.py \
  --api http://127.0.0.1:8080 \
  --until boss \
  --max-steps 500
```

Use `--max-steps 0` for a non-mutating state probe. Use `--allow-forced-end` only when the user agrees that forced skip turns are acceptable.

Probe the v2 decision API directly:

```bash
curl --noproxy '*' -fsS \
  'http://127.0.0.1:8080/v2/decision/current?profile=ai_safe&include_relevant_game_data=true'

curl --noproxy '*' -fsS -X POST 'http://127.0.0.1:8080/v2/data/lookup' \
  -H 'Content-Type: application/json' \
  --data '{"items":[{"collection":"cards","id":"BASH"}],"fields":["id","name","description","damage","block","powers_applied","vars"]}'
```

## Startup Workflow

When the user says "play STS2" or asks to continue a run, bootstrap the environment before making gameplay decisions:

1. Resolve the repo root. Use the current workspace if it contains `mcp_server/`; otherwise require `STS2_AI_MCP_REPO` to point at a checkout. If neither resolves, report the missing checkout instead of defaulting to the legacy fork.
   - Resolve the game root from `STS2_GAME_ROOT` or Steam library metadata. Common WSL locations include `/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2` and `/mnt/d/steam/steamapps/common/Slay the Spire 2`.
   - The Windows data directory is `<game-root>/data_sts2_windows_x86_64`.
   - Current game engine is Godot/MegaDot `4.5.1.m.12`; do not pack `STS2AIMCP.pck` with Godot `4.6.x`, because the game rejects newer PCKs. Use Godot `4.5.1` for `scripts/build-mod.sh`.
2. Probe the mod API before starting anything new:

```bash
api="${STS2_API_BASE_URL:-http://127.0.0.1:8080}"
curl -fsS "$api/healthz" || curl -fsS "$api/health"
curl -fsS "$api/state"
```

If WSL has local proxy variables such as `HTTP_PROXY`/`HTTPS_PROXY`, do not rely on broad patterns like `NO_PROXY=127.*`; some clients may still send `127.0.0.1` traffic through the proxy and return `502`. Prefer `localhost` for local probes, or set exact bypasses before probing:

```bash
export NO_PROXY="127.0.0.1,localhost"
export no_proxy="127.0.0.1,localhost"
curl --noproxy '*' -fsS http://127.0.0.1:8080/health
```

3. If the mod API is down, launch the Windows game through Steam from WSL and poll the API until it is ready:

```bash
powershell.exe -NoProfile -Command "Start-Process 'steam://rungameid/2868840'"
```

If `127.0.0.1:8080` is unreachable from WSL after the game is running, retry via the Windows host IP and export it for MCP/scripts:

```bash
win_host="$(ip route | awk '/default/ {print $3; exit}')"
export STS2_API_BASE_URL="http://$win_host:8080"
```

4. Start MCP in WSL only if an MCP endpoint is required and one is not already running. For Codex running in WSL, prefer stdio MCP configured with working directory `<repo>/mcp_server`, command `uv run sts2-mcp-server`, and `STS2_API_BASE_URL` set to the working mod API URL. For an HTTP MCP endpoint:

```bash
cd "$repo/mcp_server"
uv sync
STS2_API_BASE_URL="${STS2_API_BASE_URL:-http://127.0.0.1:8080}" \
  uv run sts2-network-mcp-server --host 127.0.0.1 --port 8765 --path /mcp --tool-profile ai_safe_v2
```

When running the WSL network MCP server under a local proxy environment, clear proxy variables for that process so its internal calls to the game Mod API stay local:

```bash
env -u HTTP_PROXY -u HTTPS_PROXY -u ALL_PROXY -u http_proxy -u https_proxy -u all_proxy \
  NO_PROXY=127.0.0.1,localhost no_proxy=127.0.0.1,localhost \
  STS2_API_BASE_URL="${STS2_API_BASE_URL:-http://127.0.0.1:8080}" \
  uv run sts2-network-mcp-server --host 127.0.0.1 --port 8765 --path /mcp --tool-profile ai_safe_v2
```

Then verify:

```bash
curl -fsS http://127.0.0.1:8765/healthz
```

5. Do not start duplicate game or MCP processes when the health checks already pass. If `uv` is missing, report it as an environment blocker and fall back to direct mod HTTP probes if possible.
6. Starting or configuring an MCP process does not hot-add tools to an already running Codex session. Configure the stdio server so Codex owns its lifecycle, then reload the Codex session. Do not compensate by starting a persistent terminal driver or separately managed network MCP server. Use direct Mod HTTP only for bounded diagnostics before the reload.

## Play Workflow

1. For user-facing runs, call the Codex-mounted `wait_for_decision` or `get_current_decision` tool directly with relevant game data, then call direct MCP action tools. Summarize `screen`, `floor`, `hp`, `turn`, `energy`, `hand`, enemies, potions, and available v2 choices. Prefer `execute_action_plan` for an already-evaluated information-stable 2-5 step combat prefix and `take_action` for information-revealing or unresolved steps. Send concise reasons as `client_note` in the user's language; MCP action tools automatically write them to the public markdown decision log. If direct MCP tools are absent, stop normal gameplay and request a Codex session reload after verifying the stdio configuration. If v2 is unavailable after reload, use raw state only for diagnostics.
2. If no non-passive action is available, wait for an actionable event/state.
3. Branch by screen:
   - `COMBAT`: account for enemy intents, pending powers, self-damage, thorns/reflection, consumables, lethal/phase transitions, deterministic survival, draw/energy conversion, and future damage before ending the turn. Use potions while they still turn a dangerous distribution into a stable line; do not reserve them for a future boss when the current room can end the run. Use `choice.preview` to compare expected damage, remaining enemy HP, block gain, and end-turn lethal risk; verify complex relic, boss, cap, thorns, random, and generated-card effects against live data.
   - `REWARD` or card reward selection: evaluate material rewards before proceeding, then choose a card with `choose_reward_card` or skip with `skip_reward_cards`; never use `resolve_rewards` for manual play. A claimable potion reward means the game has confirmed an open potion slot. Look up unfamiliar potions before evaluating them and normally claim one when an empty slot makes doing so free, but skipping or later discarding it remains valid when the potion has no practical value for the run. Skipping is also a normal outcome when all card rewards worsen the deck.
   - `MAP`: inspect the known boss identities and trace each candidate through the next reliable recovery or meaningful pivot, including forced combats/elites, combat-capable unknowns, shops that may fail to repair the run, and forced rooms immediately after that checkpoint; then compare the next 2-3 nodes in detail. There is no fixed room priority: normal combats provide predictable growth, events add variance, and elites provide necessary scaling when the deck, potions, and post-fight route pass a preparedness check. Choose the route that maximizes whole-run completion probability and preserves useful pivots; do not let an early shop or other attractive room hide an overlong locked risk segment.
   - `EVENT`: avoid locked/lethal options; inspect event text keys. Do not blindly choose "full heal" options that add severe draw-lock or sleep effects before forced combats.
   - `REST`: compare the marginal completion-probability gain from healing, upgrading, removing, or other options. Do not use a fixed HP percentage; rest when healing crosses a real survival threshold and upgrade when strength/consistency is the larger failure mode.
   - `SHOP`: buy clearly useful, affordable cards/relics; otherwise proceed.
4. After every action, return to step 1.
5. Stop immediately on game over, unknown screens, repeated retryable action failures, or forced-end-only combat unless explicitly allowed.

## Known STS2 AI MCP Edges

- `end_turn` can appear slightly before the UI button is ready. A retry after a short wait is acceptable; repeated failures should be reported with the latest state.
- Combat transition can briefly show no hand. This must not expose actionable `end_turn`; if it does, treat it as a code issue.
- Some event penalties can create stable turns where the hand contains only an unplayable card such as `POOR_SLEEP`. That is a forced-end state, not a missing `play_card` action.
- V2 combat previews cover common damage, block, Weak, Vulnerable, Strength, Dexterity, Frail, applied powers, and end-turn incoming damage. They are conservative decision aids, not a full simulation of every relic, monster power, boss rule, damage cap, thorns/reflection, self-damage, target redirection, random effect, or generated-card chain.

## Bundled Script

`scripts/sts2_autoplay.py` is a conservative HTTP harness for the mod API. It prints each state/action, exits `0` on reaching the requested goal, exits `2` on a gameplay/MCP blocker, and exits `1` on unexpected script errors.

Patch the script rather than rewriting it when improving strategy. Keep its default behavior conservative: stop and report suspicious states instead of silently skipping turns.
