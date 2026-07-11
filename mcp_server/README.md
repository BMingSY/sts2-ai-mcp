# STS2 AI MCP Server

FastMCP wrapper for the local STS2 AI MCP mod HTTP API.

The default tool profile is `ai_safe_v2`. It exposes only the v2 decision-window workflow:

- `health_check`
- `wait_for_decision`
- `get_current_decision`
- `take_action`
- `execute_action_plan`
- `select_cards`
- `lookup_game_data`
- `append_decision_note`

Legacy profiles still exist in code for migration/testing, but normal AI play should use `ai_safe_v2`.

## Run

```bash
uv sync
uv run sts2-mcp-server
```

Network MCP:

```bash
uv run sts2-network-mcp-server --host 127.0.0.1 --port 8765 --path /mcp
```

## Game Data

`lookup_game_data` uses a versioned local snapshot under `mcp_server/data/versions/<game_version>/`.

On the first lookup for a new game version, MCP calls the running Mod's `/v2/data/export` endpoint once, saves all static collections, and serves subsequent lookups locally. Decision tools request live state without game-side knowledge expansion and hydrate relevant cards, monsters, powers, relics, potions, and events from the same snapshot.

To pre-generate a version explicitly:

```bash
python3 ../scripts/sync-game-data.py
```

Use `STS2_GAME_DATA_DIR` to override the snapshot root. Generated game data is not checked in by default.

## Tests

```bash
uv run pytest -q
```

If `uv` is unavailable but a virtualenv exists:

```bash
.venv/bin/python -m pytest -q
```
