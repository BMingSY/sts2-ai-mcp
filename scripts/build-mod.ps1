param(
    [string]$Configuration = "Debug",
    [string]$ProjectRoot = "",
    [string]$GameRoot = "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2",
    [string]$DataDir = "",
    [string]$ModsDir = "",
    [string]$GodotExe = "",
    [switch]$AllowGodotVersionMismatch
)

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot

function Resolve-ProjectRoot {
    param([string]$InputRoot)

    if ([string]::IsNullOrWhiteSpace($InputRoot)) {
        return (Resolve-Path (Join-Path $scriptRoot "..")).ProviderPath
    }

    return (Resolve-Path $InputRoot).ProviderPath
}

$ProjectRoot = Resolve-ProjectRoot -InputRoot $ProjectRoot

function Resolve-OptionalExistingDirectory {
    param(
        [string]$InputPath,
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($InputPath)) {
        return ""
    }

    if (-not (Test-Path $InputPath -PathType Container)) {
        throw "$Label not found: $InputPath"
    }

    return (Resolve-Path $InputPath).ProviderPath
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

$GameRoot = Resolve-OptionalExistingDirectory -InputPath $GameRoot -Label "Game root"

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = $env:STS2_DATA_DIR
}

if ([string]::IsNullOrWhiteSpace($DataDir) -and -not [string]::IsNullOrWhiteSpace($GameRoot)) {
    $DataDir = First-ExistingDirectory -Candidates @(
        (Join-Path $GameRoot "data_sts2_windows_x86_64"),
        (Join-Path $GameRoot "data_sts2_linuxbsd_x86_64"),
        (Join-Path $GameRoot "data_sts2_osx_arm64"),
        (Join-Path $GameRoot "data_sts2_osx_x86_64"),
        (Join-Path $GameRoot "data_sts2_macos_arm64"),
        (Join-Path $GameRoot "data_sts2_macos_x86_64")
    )
}

$DataDir = Resolve-OptionalExistingDirectory -InputPath $DataDir -Label "STS2 data directory"

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    throw "STS2 data directory not found. Pass -DataDir, set STS2_DATA_DIR, or provide a GameRoot containing data_sts2_*."
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    $ModsDir = $env:STS2_MODS_DIR
}

if ([string]::IsNullOrWhiteSpace($ModsDir) -and -not [string]::IsNullOrWhiteSpace($GameRoot)) {
    $ModsDir = Join-Path $GameRoot "mods"
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    throw "STS2 mods directory not found. Pass -ModsDir, set STS2_MODS_DIR, or provide -GameRoot."
}

if ([string]::IsNullOrWhiteSpace($GodotExe)) {
    $GodotExe = $env:GODOT_BIN
}

if ([string]::IsNullOrWhiteSpace($GodotExe)) {
    $candidateGodotExePaths = @(
        (Join-Path $GameRoot "Slay the Spire 2.exe"),
        (Join-Path $GameRoot "SlayTheSpire2.exe")
    )

    foreach ($candidate in $candidateGodotExePaths) {
        if (Test-Path $candidate) {
            $GodotExe = $candidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($GodotExe)) {
    throw "Godot executable not found. Pass -GodotExe, set GODOT_BIN, or provide a GameRoot containing the STS2 executable."
}

if (-not (Test-Path $GodotExe)) {
    throw "Godot executable not found: $GodotExe"
}

function Test-GodotVersion {
    param(
        [string]$Executable,
        [bool]$AllowMismatch
    )

    $versionOutput = ""
    try {
        $versionOutput = (& $Executable --version 2>&1 | Select-Object -First 1)
    }
    catch {
        $versionOutput = ""
    }

    if ([string]::IsNullOrWhiteSpace($versionOutput)) {
        Write-Warning "Could not detect Godot/MegaDot version from: $Executable"
        Write-Warning "Expected a 4.5.1-compatible packer; do not use Godot 4.6.x for release builds."
        return
    }

    Write-Host "[build-mod] Detected Godot/MegaDot version: $versionOutput"
    if ($versionOutput -match '(^|[^0-9])4\.6([^0-9]|$)' -and -not $AllowMismatch) {
        throw "Refusing to build PCK with Godot 4.6.x. STS2 currently expects Godot/MegaDot 4.5.1-compatible PCKs. Pass -AllowGodotVersionMismatch only for local compatibility experiments."
    }

    if ($versionOutput -notmatch '(^|[^0-9])4\.5\.1([^0-9]|$)') {
        Write-Warning "Expected Godot/MegaDot 4.5.1-compatible packer. Current detected version may be untested: $versionOutput"
    }
}

Test-GodotVersion -Executable $GodotExe -AllowMismatch:$AllowGodotVersionMismatch.IsPresent

$modName = "STS2AIMCP"
$legacyModName = "STS2AIAgent"
$modProject = Join-Path $ProjectRoot "STS2AIMCP/STS2AIMCP.csproj"
$buildOutputDir = Join-Path $ProjectRoot "STS2AIMCP/bin/$Configuration/net9.0"
$stagingDir = Join-Path $ProjectRoot "build/mods/$modName"
$installedModDir = Join-Path $ModsDir $modName
$pckManifestSource = Join-Path $ProjectRoot "STS2AIMCP/mod_manifest.json"
$modJsonSource = Join-Path $ProjectRoot "STS2AIMCP/$modName.json"
$dllSource = Join-Path $buildOutputDir "$modName.dll"
$pckOutput = Join-Path $stagingDir "$modName.pck"
$dllTarget = Join-Path $stagingDir "$modName.dll"
$modJsonTarget = Join-Path $stagingDir "$modName.json"
$builderProjectDir = Join-Path $ProjectRoot "tools/pck_builder"
$builderScript = Join-Path $builderProjectDir "build_pck.gd"

Write-Host "[build-mod] Building C# mod project..."
dotnet build $modProject -c $Configuration "/p:Sts2DataDir=$DataDir" | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $dllSource)) {
    throw "Built DLL not found: $dllSource"
}

New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
Copy-Item -Force $dllSource $dllTarget

if (-not (Test-Path $pckManifestSource)) {
    throw "PCK manifest not found: $pckManifestSource"
}

if (-not (Test-Path $modJsonSource)) {
    throw "Mod JSON manifest not found: $modJsonSource"
}

Copy-Item -Force $modJsonSource $modJsonTarget

Write-Host "[build-mod] Packing mod_manifest.json into PCK..."
& $GodotExe --headless --path $builderProjectDir --script $builderScript -- $pckManifestSource $pckOutput | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Godot PCK build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $pckOutput)) {
    throw "PCK output not found: $pckOutput"
}

Write-Host "[build-mod] Preparing game mods directory..."
New-Item -ItemType Directory -Force -Path $installedModDir | Out-Null
Copy-Item -Force $dllTarget (Join-Path $installedModDir "$modName.dll")
Copy-Item -Force $pckOutput (Join-Path $installedModDir "$modName.pck")
Copy-Item -Force $modJsonTarget (Join-Path $installedModDir "$modName.json")

$legacyRootFiles = @(
    (Join-Path $ModsDir "$modName.dll"),
    (Join-Path $ModsDir "$modName.pck"),
    (Join-Path $ModsDir "$legacyModName.dll"),
    (Join-Path $ModsDir "$legacyModName.pck"),
    (Join-Path $ModsDir "mod_id.json")
) | Where-Object { Test-Path $_ }

$legacyFolders = @(
    (Join-Path $ModsDir $legacyModName)
) | Where-Object { Test-Path $_ }

if ($legacyRootFiles.Count -gt 0 -or $legacyFolders.Count -gt 0) {
    Write-Warning "Legacy STS2AIAgent mod files were found in the mods directory. Back them up and remove them before testing STS2AIMCP to avoid duplicate or stale mod loads:"
    foreach ($legacyFile in $legacyRootFiles) {
        Write-Warning "  $legacyFile"
    }
    foreach ($legacyFolder in $legacyFolders) {
        Write-Warning "  $legacyFolder"
    }
}

Write-Host "[build-mod] Done."
Write-Host "[build-mod] Using data dir: $DataDir"
Write-Host "[build-mod] Using mods dir: $ModsDir"
Write-Host "[build-mod] Installed files:"
Write-Host "  $(Join-Path $installedModDir "$modName.dll")"
Write-Host "  $(Join-Path $installedModDir "$modName.pck")"
Write-Host "  $(Join-Path $installedModDir "$modName.json")"
