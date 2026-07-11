# STS2 AI MCP

AI-safe MCP and local HTTP control surface for Slay the Spire 2.

This repository is the v2-focused successor to the older STS2-Agent fork. The goal is to expose stable decision windows to agents instead of raw UI button calls.

## What Is Included

- `STS2AIMCP/`: the in-game mod HTTP API.
- `mcp_server/`: FastMCP wrapper with `ai_safe_v2` as the default profile.
- `scripts/`: build and MCP startup helpers.
- `docs/`: v2 protocol, compatibility, and build notes.

Private gameplay knowledge is intentionally not part of this repository. Runtime logs and local strategy notes should live under `agent_knowledge/`, `run_logs/`, or an external path such as `~/.local/share/sts2-ai-mcp/knowledge`.

## Current Status

The v2 protocol is experimental but usable for local testing:

- `GET /v2/decision/current`
- `POST /v2/decision/wait`
- `POST /v2/decision/act`
- `POST /v2/data/lookup`
- `POST /v2/data/export`

Normal MCP play should use only:

- `health_check`
- `wait_for_decision`
- `get_current_decision`
- `take_action`
- `execute_action_plan`
- `select_cards`
- `lookup_game_data`
- `append_decision_note`

## Compatibility

Slay the Spire 2 is still changing, so a mod build may not remain compatible across game updates. Release artifacts should always identify the tested game version and Godot/MegaDot version.

Known engine constraint:

- Current target: Godot/MegaDot `4.5.1.m.12`.
- Do not pack `STS2AIMCP.pck` with Godot `4.6.x`; the game may reject it.

See [docs/compatibility.md](docs/compatibility.md) for the compatibility policy.

## Build

Windows PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\build-mod.ps1" `
  -GameRoot "D:\steam\steamapps\common\Slay the Spire 2" `
  -GodotExe "D:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe" `
  -Configuration Release
```

macOS/Linux:

```bash
./scripts/build-mod.sh \
  --game-root "/path/to/Slay the Spire 2" \
  --godot-exe "/path/to/game-or-godot-4.5.1" \
  --configuration Release
```

See [docs/building.md](docs/building.md) for details.

If you are migrating from the older fork, remove or disable old `STS2AIAgent` files under the game's `mods` directory before testing `STS2AIMCP`.

## Steam Workshop

The Workshop item should contain the game-side mod only. The MCP server remains a separate local install from this repository.

Package the Workshop content and SteamCMD VDF:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\package-workshop.ps1" `
  -GameRoot "D:\steam\steamapps\common\Slay the Spire 2" `
  -Configuration Release `
  -OutputRoot "D:\steam\sts2-ai-mcp-workshop" `
  -PreviewFile "C:\path\to\workshop-preview.png"
```

Then upload or update with SteamCMD:

```powershell
steamcmd +login <steam_user> +workshop_build_item "D:\steam\sts2-ai-mcp-workshop\sts2-ai-mcp-workshop.vdf" +quit
```

Use `-PublishedFileId <id>` after the first upload to update the same Workshop item. See [docs/workshop.md](docs/workshop.md).

## Release ZIP

For GitHub Releases, package a manual-install ZIP:

```bash
./scripts/package-release.sh \
  --game-root "/mnt/d/steam/steamapps/common/Slay the Spire 2" \
  --godot-exe "/mnt/d/steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe"
```

The artifact is written under `dist/release/` and contains only the game-side `STS2AIMCP` mod. See [docs/release.md](docs/release.md).

## Run MCP

Stdio MCP:

```bash
./scripts/start-mcp-stdio.sh
```

HTTP MCP:

```bash
./scripts/start-mcp-network.sh --host 127.0.0.1 --port 8765 --path /mcp
```

Static card, monster, power, relic, potion, and event metadata is cached once per game version under `mcp_server/data/versions/`. Pre-generate the running version with:

```bash
python3 scripts/sync-game-data.py
```

## Validation

```bash
dotnet build STS2AIMCP/STS2AIMCP.csproj -c Release \
  "/p:Sts2DataDir=/path/to/Slay the Spire 2/data_sts2_windows_x86_64"
cd mcp_server
.venv/bin/python -m pytest -q
```

If `uv` is installed:

```bash
cd mcp_server
uv sync
uv run pytest -q
```

## License

AGPL-3.0-only. See [LICENSE](LICENSE).
