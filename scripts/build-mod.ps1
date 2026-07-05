param(
    [string]$Configuration = "Debug",
    [string]$ProjectRoot = "",
    [string]$GameRoot = "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2",
    [string]$GodotExe = "",
    [switch]$AllowGodotVersionMismatch
)

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot

function Resolve-ProjectRoot {
    param([string]$InputRoot)

    if ([string]::IsNullOrWhiteSpace($InputRoot)) {
        return (Resolve-Path (Join-Path $scriptRoot "..")).Path
    }

    return (Resolve-Path $InputRoot).Path
}

$ProjectRoot = Resolve-ProjectRoot -InputRoot $ProjectRoot

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

$modName = "STS2AIAgent"
$modProject = Join-Path $ProjectRoot "STS2AIAgent/STS2AIAgent.csproj"
$buildOutputDir = Join-Path $ProjectRoot "STS2AIAgent/bin/$Configuration/net9.0"
$stagingDir = Join-Path $ProjectRoot "build/mods/$modName"
$modsDir = Join-Path $GameRoot "mods"
$manifestSource = Join-Path $ProjectRoot "STS2AIAgent/mod_manifest.json"
$dllSource = Join-Path $buildOutputDir "$modName.dll"
$pckOutput = Join-Path $stagingDir "$modName.pck"
$dllTarget = Join-Path $stagingDir "$modName.dll"
$builderProjectDir = Join-Path $ProjectRoot "tools/pck_builder"
$builderScript = Join-Path $builderProjectDir "build_pck.gd"

Write-Host "[build-mod] Building C# mod project..."
dotnet build $modProject -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $dllSource)) {
    throw "Built DLL not found: $dllSource"
}

New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
Copy-Item -Force $dllSource $dllTarget

if (-not (Test-Path $manifestSource)) {
    throw "Manifest not found: $manifestSource"
}

Write-Host "[build-mod] Packing mod_manifest.json into PCK..."
& $GodotExe --headless --path $builderProjectDir --script $builderScript -- $manifestSource $pckOutput | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Godot PCK build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $pckOutput)) {
    throw "PCK output not found: $pckOutput"
}

Write-Host "[build-mod] Preparing game mods directory..."
New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
Copy-Item -Force $dllTarget (Join-Path $modsDir "$modName.dll")
Copy-Item -Force $pckOutput (Join-Path $modsDir "$modName.pck")

Write-Host "[build-mod] Done."
Write-Host "[build-mod] Installed files:"
Write-Host "  $(Join-Path $modsDir "$modName.dll")"
Write-Host "  $(Join-Path $modsDir "$modName.pck")"
