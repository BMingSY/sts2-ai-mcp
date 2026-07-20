# Release Packaging

Use GitHub Releases for versioned downloads and rollback. Use Steam Workshop for subscription updates. The two channels can point to the same tested build, but they should stay operationally separate.

The release ZIP contains only the game-side mod:

- `STS2AIMCP/STS2AIMCP.dll`
- `STS2AIMCP/STS2AIMCP.pck`
- `STS2AIMCP/STS2AIMCP.json`
- `README-release.txt`
- `release-metadata.json`

The MCP server is installed from the repository separately.
The repository tag also contains the maintained `docs/skills/sts2-player/` Codex skill; it is source content and is not duplicated inside the game-side ZIP.

## Build A ZIP

From WSL or Linux:

```bash
./scripts/package-release.sh \
  --game-root "/mnt/d/steam/steamapps/common/Slay the Spire 2" \
  --godot-exe "/mnt/d/steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe"
```

From Windows PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\package-release.ps1" `
  -GameRoot "D:\steam\steamapps\common\Slay the Spire 2" `
  -GodotExe "D:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"
```

The output is written under `dist/release/` by default:

```text
sts2-ai-mcp-v<mod-version>-sts2-<game-version>-godot-4.5.1.zip
sts2-ai-mcp-v<mod-version>-sts2-<game-version>-godot-4.5.1.zip.sha256
```

If the repository is stored under WSL, prefer the Bash script. Windows PowerShell can package an existing build with `-SkipBuild`, but building .NET projects from a WSL UNC path can be unreliable.

## Release Checklist

Before publishing a GitHub Release:

- build with the game-bundled Godot/MegaDot `4.5.1.m.12` executable
- launch the game with only `STS2AIMCP` enabled
- verify `http://127.0.0.1:8080/health`
- verify `/state`, `/v2/decision/current`, and `/v2/data/lookup`
- run MCP server tests
- validate `docs/skills/sts2-player/` with the Codex skill validator
- verify packaged metadata reports the current protocol, state, and decision versions
- name the tag `v<mod-version>` unless the release needs a compatibility suffix
- include the tested Slay the Spire 2 version in the release notes

Do not publish a release as compatible with a newer Slay the Spire 2 build until it has been retested.
