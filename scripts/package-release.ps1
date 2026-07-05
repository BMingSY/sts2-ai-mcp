param(
    [string]$Configuration = "Release",
    [string]$ProjectRoot = "",
    [string]$GameRoot = "",
    [string]$GodotExe = "",
    [string]$OutputRoot = "",
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

function Get-JsonValue {
    param(
        [string]$Path,
        [string]$PropertyName
    )

    $data = Get-Content -Raw $Path | ConvertFrom-Json
    return [string]$data.$PropertyName
}

function Get-GameVersion {
    param([string]$ResolvedGameRoot)

    if ([string]::IsNullOrWhiteSpace($ResolvedGameRoot)) {
        return "unknown"
    }

    $releaseInfoPath = Join-Path $ResolvedGameRoot "release_info.json"
    if (-not (Test-Path $releaseInfoPath)) {
        Write-Warning "release_info.json not found under game root: $ResolvedGameRoot"
        return "unknown"
    }

    try {
        $version = Get-JsonValue -Path $releaseInfoPath -PropertyName "version"
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            return $version
        }
    }
    catch {
        Write-Warning "Could not read game release_info.json: $_"
    }

    return "unknown"
}

function Sanitize-FilePart {
    param([string]$Value)

    return ($Value -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
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
if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = (Resolve-Path $GameRoot).ProviderPath
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot "dist/release"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path $OutputRoot).ProviderPath

if (-not $SkipBuild) {
    $buildScript = Join-Path $scriptRoot "build-mod.ps1"
    $buildArgs = @(
        "-ProjectRoot", $ProjectRoot,
        "-Configuration", $Configuration
    )

    if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
        $buildArgs += @("-GameRoot", $GameRoot)
    }

    if (-not [string]::IsNullOrWhiteSpace($GodotExe)) {
        $buildArgs += @("-GodotExe", $GodotExe)
    }

    if ($AllowGodotVersionMismatch) {
        $buildArgs += "-AllowGodotVersionMismatch"
    }

    Write-Host "[release] Building mod..."
    & $buildScript @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "build-mod.ps1 failed with exit code $LASTEXITCODE"
    }
}

$modJsonSource = Join-Path $ProjectRoot "$modName/$modName.json"
$stagingModDir = Join-Path $ProjectRoot "build/mods/$modName"
$modVersion = Get-JsonValue -Path $modJsonSource -PropertyName "version"
$gameVersion = Get-GameVersion -ResolvedGameRoot $GameRoot

foreach ($artifact in @(
    (Join-Path $stagingModDir "$modName.dll"),
    (Join-Path $stagingModDir "$modName.pck"),
    (Join-Path $stagingModDir "$modName.json")
)) {
    if (-not (Test-Path $artifact)) {
        throw "Required build artifact not found: $artifact"
    }
}

$artifactName = "sts2-ai-mcp-v$modVersion-sts2-$(Sanitize-FilePart $gameVersion)-godot-4.5.1"
$stageDir = Join-Path $OutputRoot "stage/$artifactName"
$zipPath = Join-Path $OutputRoot "$artifactName.zip"
$checksumPath = "$zipPath.sha256"
$stageModDir = Join-Path $stageDir $modName

if (Test-Path $stageDir) {
    Remove-Item -Recurse -Force $stageDir
}

New-Item -ItemType Directory -Force -Path $stageModDir | Out-Null
Copy-Item -Force (Join-Path $stagingModDir "$modName.dll") (Join-Path $stageModDir "$modName.dll")
Copy-Item -Force (Join-Path $stagingModDir "$modName.pck") (Join-Path $stageModDir "$modName.pck")
Copy-Item -Force (Join-Path $stagingModDir "$modName.json") (Join-Path $stageModDir "$modName.json")

$readmeText = @"
STS2 AI MCP v$modVersion

This release ZIP contains only the Slay the Spire 2 game-side mod:

- $modName/$modName.dll
- $modName/$modName.pck
- $modName/$modName.json

The MCP server is distributed separately from the GitHub repository.

Tested game version: $gameVersion
Expected Godot/MegaDot version: 4.5.1.m.12
V2 protocol version: 2026-07-04-v2-draft

Manual install:

1. Close Slay the Spire 2.
2. Remove or disable older STS2AIAgent files from the game's mods directory.
3. Copy the $modName folder from this ZIP into the game's mods directory.
4. Start the game and verify http://127.0.0.1:8080/health.

Do not assume this build is compatible with newer Slay the Spire 2 updates until it is retested.
"@

Write-Utf8NoBom -Path (Join-Path $stageDir "README-release.txt") -Content $readmeText

$metadata = [ordered]@{
    service = "sts2-ai-mcp"
    mod_id = $modName
    mod_version = $modVersion
    v2_protocol_version = "2026-07-04-v2-draft"
    tested_game_version = $gameVersion
    expected_godot_version = "4.5.1.m.12"
    packaged_at_utc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    contents = @(
        "$modName/$modName.dll",
        "$modName/$modName.pck",
        "$modName/$modName.json"
    )
}

Write-Utf8NoBom -Path (Join-Path $stageDir "release-metadata.json") -Content ($metadata | ConvertTo-Json -Depth 8)

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

if (Test-Path $checksumPath) {
    Remove-Item -Force $checksumPath
}

Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath
$hash = Get-FileHash -Algorithm SHA256 $zipPath
Write-Utf8NoBom -Path $checksumPath -Content "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $zipPath)`n"

Write-Host "[release] ZIP written:"
Write-Host "  $zipPath"
Write-Host "[release] SHA256 written:"
Write-Host "  $checksumPath"
Write-Host "[release] Staged contents:"
Get-ChildItem -Recurse -File $stageDir | ForEach-Object {
    Write-Host "  $($_.FullName.Substring($stageDir.Length + 1))"
}
