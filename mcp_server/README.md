# STS2 AI MCP Server

FastMCP wrapper for the local STS2 AI MCP mod HTTP API.

The default tool profile is `ai_safe_v2`. It exposes only the v2 decision-window workflow:

- `health_check`
- `wait_for_decision`
- `get_current_decision`
- `preview_action`
- `preview_action_plan`
- `run_evaluator`
- `combat_horizon`
- `take_action`
- `get_action_trace`
- `execute_action_plan`
- `select_cards`
- `select_character`
- `lookup_game_data`
- `append_decision_note`

Legacy profiles still exist in code for migration/testing, but normal AI play should use `ai_safe_v2`.
Screen-specific actions such as `continue_run` are returned as current decision choices and executed through `take_action`; the compact v2 profile does not expose a separate tool for every game action.

## Run

```bash
uv sync
uv run sts2-mcp-server
```

The stdio server may be started before the game. It keeps the MCP transport
available while the Mod API is unreachable and becomes usable when the game is
launched. A reachable but incompatible Mod contract still fails closed. This
allows MCP clients such as Codex to own the server lifecycle without a separate
network server or terminal driver.

Network MCP:

```bash
uv run sts2-network-mcp-server --host 127.0.0.1 --port 8765 --path /mcp
```

Unlike the client-owned stdio transport, network MCP startup expects the Mod API to be reachable.

## Game Data

`lookup_game_data` uses a versioned local snapshot under `mcp_server/data/versions/<game_version>/`.

On the first lookup for a new game version, MCP calls the running Mod's `/v2/data/export` endpoint once, saves all static collections, and serves subsequent lookups locally. Decision tools request live state without game-side knowledge expansion and hydrate relevant cards, monsters, powers, relics, potions, and events from the same snapshot.

To pre-generate a version explicitly:

```bash
python3 ../scripts/sync-game-data.py
```

Use `STS2_GAME_DATA_DIR` to override the snapshot root. Generated game data is not checked in by default.

## Read-only reasoning helpers

`run_evaluator(decision_id, candidate_card_ids, horizons)` calculates public deck
shape, exact without-replacement access probabilities, and one-copy candidate deltas.
It preserves candidate input order and does not rank or choose cards.

`combat_horizon(decision_id, lines)` checks up to eight model-proposed combat lines
of at most five steps using the deterministic plan preview, then calculates the
current exposed attack-intent outcome after projected direct kills. It does not
search for lines or execute them.

Both tools require a decision already cached by `get_current_decision` or
`wait_for_decision`, so calculator calls perform no game/network wait. Their default
work budget is 100ms; the non-configurable hard ceiling is 500ms and 4096 states.
Budget exhaustion returns `status: "partial"` with the completed prefix.

The same calculators are available as an offline JSON CLI that never connects to
the game:

```bash
uv run sts2-reasoning --json run-evaluator --input run-request.json
uv run sts2-reasoning --json combat-horizon --input combat-request.json
```

## Tests

```bash
uv run pytest -q
```

If `uv` is unavailable but a virtualenv exists:

```bash
.venv/bin/python -m pytest -q
```
