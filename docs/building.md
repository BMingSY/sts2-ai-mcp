# Building

## Requirements

- .NET SDK compatible with `net9.0`
- Python 3.11+
- `uv` for MCP dependency management
- Slay the Spire 2 installed locally
- Godot/MegaDot `4.5.1` compatible packer for PCK generation

## Important Engine Constraint

The current game runtime is expected to be Godot/MegaDot `4.5.1.m.12`. Do not build `STS2AIMCP.pck` with Godot `4.6.x`.

When possible, use the game-bundled executable/runtime as the packer. If you use a standalone Godot binary, verify its version first:

```bash
/path/to/godot --version
```

## Windows

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\build-mod.ps1" `
  -Configuration Release `
  -GameRoot "D:\steam\steamapps\common\Slay the Spire 2" `
  -GodotExe "D:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"
```

The script copies `STS2AIMCP.dll` and `STS2AIMCP.pck` into the game's `mods` directory.
It installs them as `mods/STS2AIMCP/STS2AIMCP.dll`, `mods/STS2AIMCP/STS2AIMCP.pck`, and `mods/STS2AIMCP/STS2AIMCP.json`.
If you previously installed the older fork as `mods/STS2AIAgent/`, `mods/STS2AIAgent.dll`, `mods/STS2AIAgent.pck`, or `mods/mod_id.json`, back those files up and remove them before testing `STS2AIMCP`.

## macOS/Linux

```bash
./scripts/build-mod.sh \
  --configuration Release \
  --game-root "/path/to/Slay the Spire 2" \
  --godot-exe "/path/to/game-or-godot-4.5.1"
```

## MCP Server

```bash
cd mcp_server
uv sync
uv run pytest -q
uv run sts2-mcp-server
```

For HTTP MCP:

```bash
uv run sts2-network-mcp-server --host 127.0.0.1 --port 8765 --path /mcp
```

## Local Configuration

Do not commit machine-specific paths. Use:

- `STS2_DATA_DIR`
- `STS2_GAME_ROOT`
- `STS2_MODS_DIR`
- `GODOT_BIN`
- `STS2_API_BASE_URL`

`STS2AIMCP/local.props` is ignored by git; use it only for local MSBuild path overrides.
