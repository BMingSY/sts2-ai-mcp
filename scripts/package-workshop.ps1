param(
    [string]$Configuration = "Release",
    [string]$ProjectRoot = "",
    [string]$GameRoot = "",
    [string]$DataDir = "",
    [string]$GodotExe = "",
    [string]$OutputRoot = "",
    [string]$PreviewFile = "",
    [string]$PublishedFileId = "",
    [ValidateSet("private", "friends", "public", "unlisted", "0", "1", "2", "3")]
    [string]$Visibility = "private",
    [string]$Title = "STS2 AI MCP",
    [string]$Description = "",
    [string]$ChangeNote = "",
    [int]$AppId = 2868840,
    [switch]$SkipBuild,
    [switch]$AllowGodotVersionMismatch
)

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
$modName = "STS2AIMCP"

function Resolve-ProjectRoot {
    param([string]$InputRoot)

    if ([string]::IsNullOrWhiteSpace($InputRoot)) {
        return (Resolve-Path (Join-Path $scriptRoot "..")).ProviderPath
    }

    return (Resolve-Path $InputRoot).ProviderPath
}

function First-ExistingDirectory {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate -PathType Container)) {
            return (Resolve-Path $candidate).ProviderPath
        }
    }

    return ""
}

function Resolve-GameRoot {
    param([string]$InputRoot)

    if (-not [string]::IsNullOrWhiteSpace($InputRoot)) {
        return (Resolve-Path $InputRoot).ProviderPath
    }

    if (-not [string]::IsNullOrWhiteSpace($env:STS2_GAME_ROOT)) {
        return (Resolve-Path $env:STS2_GAME_ROOT).ProviderPath
    }

    $detected = First-ExistingDirectory -Candidates @(
        "D:/steam/steamapps/common/Slay the Spire 2",
        "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2",
        "C:/Program Files/Steam/steamapps/common/Slay the Spire 2"
    )

    if ([string]::IsNullOrWhiteSpace($detected)) {
        throw "Slay the Spire 2 game root not found. Pass -GameRoot or set STS2_GAME_ROOT."
    }

    return $detected
}

function Resolve-DataDir {
    param(
        [string]$InputDataDir,
        [string]$ResolvedGameRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($InputDataDir)) {
        return (Resolve-Path $InputDataDir).ProviderPath
    }

    if (-not [string]::IsNullOrWhiteSpace($env:STS2_DATA_DIR)) {
        return (Resolve-Path $env:STS2_DATA_DIR).ProviderPath
    }

    $detected = First-ExistingDirectory -Candidates @(
        (Join-Path $ResolvedGameRoot "data_sts2_windows_x86_64"),
        (Join-Path $ResolvedGameRoot "data_sts2_linuxbsd_x86_64"),
        (Join-Path $ResolvedGameRoot "data_sts2_osx_arm64"),
        (Join-Path $ResolvedGameRoot "data_sts2_osx_x86_64"),
        (Join-Path $ResolvedGameRoot "data_sts2_macos_arm64"),
        (Join-Path $ResolvedGameRoot "data_sts2_macos_x86_64")
    )

    if ([string]::IsNullOrWhiteSpace($detected)) {
        throw "STS2 data directory not found under: $ResolvedGameRoot"
    }

    return $detected
}

function Get-GameVersion {
    param([string]$ResolvedGameRoot)

    $releaseInfoPath = Join-Path $ResolvedGameRoot "release_info.json"
    if (-not (Test-Path $releaseInfoPath)) {
        return "unknown"
    }

    try {
        $releaseInfo = Get-Content -Raw $releaseInfoPath | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($releaseInfo.version)) {
            return [string]$releaseInfo.version
        }
    }
    catch {
        Write-Warning "Could not read game release_info.json: $_"
    }

    return "unknown"
}

function Get-ModVersion {
    param([string]$ModJsonPath)

    try {
        $modJson = Get-Content -Raw $ModJsonPath | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($modJson.version)) {
            return [string]$modJson.version
        }
    }
    catch {
        Write-Warning "Could not read mod version from ${ModJsonPath}: $_"
    }

    return "unknown"
}

function Convert-Visibility {
    param([string]$InputVisibility)

    switch ($InputVisibility) {
        "public" { return "0" }
        "friends" { return "1" }
        "private" { return "2" }
        "unlisted" { return "3" }
        default { return $InputVisibility }
    }
}

function Escape-VdfValue {
    param([string]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return ($Value -replace '"', '\"' -replace "`r?`n", '\n')
}

function Add-VdfLine {
    param(
        [System.Text.StringBuilder]$Builder,
        [string]$Key,
        [string]$Value
    )

    [void]$Builder.AppendLine(("    `"{0}`" `"{1}`"" -f $Key, (Escape-VdfValue $Value)))
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

$ProjectRoot = Resolve-ProjectRoot -InputRoot $ProjectRoot
$GameRoot = Resolve-GameRoot -InputRoot $GameRoot
$DataDir = Resolve-DataDir -InputDataDir $DataDir -ResolvedGameRoot $GameRoot

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    if ($ProjectRoot.StartsWith("\\")) {
        $OutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) "sts2-ai-mcp-workshop"
    }
    else {
        $OutputRoot = Join-Path $ProjectRoot "build/workshop"
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).ProviderPath
$contentRoot = Join-Path $OutputRoot "content"
$itemModDir = Join-Path $contentRoot $modName
$vdfPath = Join-Path $OutputRoot "sts2-ai-mcp-workshop.vdf"
$stagingModDir = Join-Path $ProjectRoot "build/mods/$modName"
$modJsonSource = Join-Path $ProjectRoot "STS2AIMCP/$modName.json"
$dllSource = Join-Path $stagingModDir "$modName.dll"
$pckSource = Join-Path $stagingModDir "$modName.pck"
$jsonSource = Join-Path $stagingModDir "$modName.json"
$gameVersion = Get-GameVersion -ResolvedGameRoot $GameRoot
$modVersion = Get-ModVersion -ModJsonPath $modJsonSource

if (-not $SkipBuild) {
    $buildScript = Join-Path $scriptRoot "build-mod.ps1"
    $buildArgs = @(
        "-ProjectRoot", $ProjectRoot,
        "-GameRoot", $GameRoot,
        "-DataDir", $DataDir,
        "-Configuration", $Configuration
    )

    if (-not [string]::IsNullOrWhiteSpace($GodotExe)) {
        $buildArgs += @("-GodotExe", $GodotExe)
    }

    if ($AllowGodotVersionMismatch) {
        $buildArgs += "-AllowGodotVersionMismatch"
    }

    Write-Host "[workshop] Building mod before packaging..."
    & $buildScript @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "build-mod.ps1 failed with exit code $LASTEXITCODE"
    }
}

foreach ($required in @($dllSource, $pckSource)) {
    if (-not (Test-Path $required)) {
        throw "Required build artifact not found: $required"
    }
}

if (-not (Test-Path $jsonSource)) {
    if (-not (Test-Path $modJsonSource)) {
        throw "Mod JSON manifest not found: $modJsonSource"
    }

    $jsonSource = $modJsonSource
}

if (Test-Path $contentRoot) {
    Remove-Item -Recurse -Force $contentRoot
}

New-Item -ItemType Directory -Force -Path $itemModDir | Out-Null
Copy-Item -Force $dllSource (Join-Path $itemModDir "$modName.dll")
Copy-Item -Force $pckSource (Join-Path $itemModDir "$modName.pck")
Copy-Item -Force $jsonSource (Join-Path $itemModDir "$modName.json")

$readmeText = @"
STS2 AI MCP

This Workshop item contains only the Slay the Spire 2 game-side mod:

- $modName.dll
- $modName.pck
- $modName.json

The MCP server and agent/client configuration are distributed separately from the project repository.

Tested game version: $gameVersion
Expected Godot/MegaDot version: 4.5.1.m.12
Mod version: $modVersion

If Slay the Spire 2 updates and this mod stops loading, use a release tested against your current game version.
"@

Write-Utf8NoBom -Path (Join-Path $itemModDir "README-workshop.txt") -Content $readmeText

if ([string]::IsNullOrWhiteSpace($Description)) {
    $Description = "Game-side bridge for STS2 AI MCP clients. This Workshop item installs the Slay the Spire 2 mod only; install and run the MCP server from GitHub. Tested game version: $gameVersion. Expected Godot/MegaDot: 4.5.1.m.12."
}

if ([string]::IsNullOrWhiteSpace($ChangeNote)) {
    $ChangeNote = "Packaged STS2AIMCP mod artifacts for game version $gameVersion."
}

$visibilityValue = Convert-Visibility -InputVisibility $Visibility
$vdf = New-Object System.Text.StringBuilder
[void]$vdf.AppendLine('"workshopitem"')
[void]$vdf.AppendLine('{')
Add-VdfLine -Builder $vdf -Key "appid" -Value ([string]$AppId)
Add-VdfLine -Builder $vdf -Key "publishedfileid" -Value $(if ([string]::IsNullOrWhiteSpace($PublishedFileId)) { "0" } else { $PublishedFileId })

Add-VdfLine -Builder $vdf -Key "contentfolder" -Value $contentRoot

if (-not [string]::IsNullOrWhiteSpace($PreviewFile)) {
    $resolvedPreview = (Resolve-Path $PreviewFile).ProviderPath
    Add-VdfLine -Builder $vdf -Key "previewfile" -Value $resolvedPreview
}

Add-VdfLine -Builder $vdf -Key "visibility" -Value $visibilityValue
Add-VdfLine -Builder $vdf -Key "title" -Value $Title
Add-VdfLine -Builder $vdf -Key "description" -Value $Description
Add-VdfLine -Builder $vdf -Key "changenote" -Value $ChangeNote
[void]$vdf.AppendLine('}')

Write-Utf8NoBom -Path $vdfPath -Content $vdf.ToString()

Write-Host "[workshop] Workshop content staged:"
Write-Host "  $contentRoot"
Write-Host "[workshop] SteamCMD VDF written:"
Write-Host "  $vdfPath"
Write-Host "[workshop] Upload/update command:"
Write-Host "  steamcmd +login <steam_user> +workshop_build_item `"$vdfPath`" +quit"

if ([string]::IsNullOrWhiteSpace($PublishedFileId)) {
    Write-Warning "PublishedFileId is empty. SteamCMD will create a new Workshop item. Save the generated publishedfileid and pass it next time to update the same item."
}

if ([string]::IsNullOrWhiteSpace($PreviewFile)) {
    Write-Warning "No PreviewFile was provided. Steam may reject a first upload until a preview image is supplied."
}

Write-Host "[workshop] Game version detected: $gameVersion"
