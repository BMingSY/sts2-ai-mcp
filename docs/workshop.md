# Steam Workshop Publishing

Slay the Spire 2 supports Steam Workshop subscriptions for mods. Subscribers receive Workshop item updates through Steam, but game updates can still break a mod build. Treat each Workshop upload as compatible only with the game version it was tested against.

The Workshop package for this project contains only the game-side mod:

- `STS2AIMCP/STS2AIMCP.dll`
- `STS2AIMCP/STS2AIMCP.pck`
- `STS2AIMCP/STS2AIMCP.json`

The MCP server is distributed separately from this repository. Users still need to install and run the MCP server locally.

## Package

From Windows PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\package-workshop.ps1" `
  -GameRoot "D:\steam\steamapps\common\Slay the Spire 2" `
  -Configuration Release `
  -OutputRoot "D:\steam\sts2-ai-mcp-workshop" `
  -PreviewFile "C:\path\to\workshop-preview.png"
```

The script:

- builds the mod with `scripts/build-mod.ps1`
- detects the game version from `release_info.json`
- stages Workshop content under `<OutputRoot>/content`
- writes a SteamCMD VDF at `<OutputRoot>/sts2-ai-mcp-workshop.vdf`

If the repository is under WSL and the script is run through Windows PowerShell, the default staging location is `%TEMP%\sts2-ai-mcp-workshop` so SteamCMD can read normal Windows paths. Pass `-OutputRoot` to use a stable location.

If you are only regenerating the VDF from an existing build, pass `-SkipBuild`.

## First Upload

Keep the first upload private until you have verified the download from Steam:

```powershell
steamcmd +login <steam_user> +workshop_build_item "D:\steam\sts2-ai-mcp-workshop\sts2-ai-mcp-workshop.vdf" +quit
```

SteamCMD may require a preview image and the Steam account may need to accept Workshop terms before the item can be published publicly.

After the first successful upload, SteamCMD should write a `publishedfileid` into the VDF. Save that ID somewhere local and private.

## Updating An Existing Item

Pass the existing Workshop item ID when packaging:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\package-workshop.ps1" `
  -GameRoot "D:\steam\steamapps\common\Slay the Spire 2" `
  -PublishedFileId "1234567890" `
  -Configuration Release `
  -ChangeNote "Rebuilt for STS2 v0.107.1."
```

Then run the SteamCMD command printed by the script. Subscribers will receive the updated Workshop content through Steam.

## Compatibility Checklist

Before making an update public:

- build with a Godot/MegaDot `4.5.1` compatible packer
- verify `scripts/build-mod.ps1` did not warn about a `4.6.x` PCK
- launch the game with the Workshop item enabled
- check the mod health endpoint, for example `http://127.0.0.1:8080/health`
- record the tested Slay the Spire 2 version in the changenote

If the game updates, publish a new build only after retesting. Do not assume an older Workshop item is compatible with a newer game build.
