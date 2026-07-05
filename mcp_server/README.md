# STS2 AI MCP Server

FastMCP wrapper for the local STS2 AI MCP mod HTTP API.

The default tool profile is `ai_safe_v2`. It exposes only the v2 decision-window workflow:

- `health_check`
- `wait_for_decision`
- `get_current_decision`
- `take_action`
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

`lookup_game_data` prefers the running game's `/v2/data/lookup` endpoint.

Checked-in `mcp_server/data/eng` cache data is intentionally not required in this public repository. If you generate or add a local cache, keep its game version and content hash tied to the running game build.

## Tests

```bash
uv run pytest -q
```

If `uv` is unavailable but a virtualenv exists:

```bash
.venv/bin/python -m pytest -q
```
