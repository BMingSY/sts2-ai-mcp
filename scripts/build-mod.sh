#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

configuration="${CONFIGURATION:-Debug}"
repo_root_input="${REPO_ROOT:-}"
game_root_input="${STS2_GAME_ROOT:-}"
data_dir_input="${STS2_DATA_DIR:-}"
mods_dir_input="${STS2_MODS_DIR:-}"
godot_exe_input="${GODOT_BIN:-}"
allow_godot_version_mismatch="${ALLOW_GODOT_VERSION_MISMATCH:-0}"

usage() {
  cat <<'EOF'
Usage: build-mod.sh [--configuration Debug|Release] [--repo-root PATH] [--game-root PATH] [--data-dir PATH] [--mods-dir PATH] [--godot-exe PATH] [--allow-godot-version-mismatch]
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --configuration)
      configuration="${2:-}"
      shift 2
      ;;
    --repo-root)
      repo_root_input="${2:-}"
      shift 2
      ;;
    --game-root)
      game_root_input="${2:-}"
      shift 2
      ;;
    --data-dir)
      data_dir_input="${2:-}"
      shift 2
      ;;
    --mods-dir)
      mods_dir_input="${2:-}"
      shift 2
      ;;
    --godot-exe)
      godot_exe_input="${2:-}"
      shift 2
      ;;
    --allow-godot-version-mismatch)
      allow_godot_version_mismatch="1"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

resolve_existing_dir() {
  local path="$1"
  cd -- "$path" && pwd
}

candidate_exists() {
  local candidate="$1"
  [[ -n "$candidate" && -d "$candidate" ]]
}

first_existing_dir() {
  local candidate
  for candidate in "$@"; do
    if candidate_exists "$candidate"; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

resolve_repo_root() {
  local input_root="$1"
  if [[ -z "$input_root" ]]; then
    cd -- "$script_dir/.." && pwd
    return
  fi

  resolve_existing_dir "$input_root"
}

detect_game_root() {
  first_existing_dir \
    "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2" \
    "$HOME/.steam/steam/steamapps/common/Slay the Spire 2"
}

detect_godot_exe() {
  local candidate=""

  if [[ -n "$godot_exe_input" ]]; then
    printf '%s\n' "$godot_exe_input"
    return 0
  fi

  # Prefer the game-bundled runtime so generated PCK version matches the game engine.
  if [[ -n "${app_bundle:-}" ]]; then
    for candidate in \
      "$app_bundle/Contents/MacOS/Slay the Spire 2" \
      "$app_bundle/Contents/MacOS/SlayTheSpire2"; do
      if [[ -x "$candidate" ]]; then
        printf '%s\n' "$candidate"
        return 0
      fi
    done
  fi

  if [[ -n "${game_root:-}" ]]; then
    for candidate in \
      "$game_root/Slay the Spire 2.exe" \
      "$game_root/SlayTheSpire2.exe" \
      "$game_root/Slay the Spire 2" \
      "$game_root/SlayTheSpire2"; do
      if [[ -x "$candidate" ]]; then
        printf '%s\n' "$candidate"
        return 0
      fi
    done
  fi

  for candidate in godot godot4 Godot; do
    if command -v "$candidate" >/dev/null 2>&1; then
      command -v "$candidate"
      return 0
    fi
  done

  for candidate in \
    "/Applications/Godot.app/Contents/MacOS/Godot" \
    "$HOME/Applications/Godot.app/Contents/MacOS/Godot"; do
    if [[ -x "$candidate" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  return 1
}

check_godot_version() {
  local godot_exe="$1"
  local version_output=""

  version_output="$("$godot_exe" --version 2>&1 | head -n 1 || true)"
  if [[ -z "$version_output" ]]; then
    echo "[build-mod] WARNING: Could not detect Godot/MegaDot version from: $godot_exe" >&2
    echo "[build-mod] Expected a 4.5.1-compatible packer; do not use Godot 4.6.x for release builds." >&2
    return 0
  fi

  echo "[build-mod] Detected Godot/MegaDot version: $version_output"
  if [[ "$version_output" =~ (^|[^0-9])4\.6([^0-9]|$) && "$allow_godot_version_mismatch" != "1" ]]; then
    echo "[build-mod] Refusing to build PCK with Godot 4.6.x." >&2
    echo "[build-mod] The current STS2 runtime expects Godot/MegaDot 4.5.1-compatible PCKs." >&2
    echo "[build-mod] Pass --allow-godot-version-mismatch only for local compatibility experiments." >&2
    exit 1
  fi

  if [[ ! "$version_output" =~ (^|[^0-9])4\.5\.1([^0-9]|$) ]]; then
    echo "[build-mod] WARNING: Expected Godot/MegaDot 4.5.1-compatible packer." >&2
    echo "[build-mod] Current detected version may be untested: $version_output" >&2
  fi
}

repo_root="$(resolve_repo_root "$repo_root_input")"
mod_name="STS2AIMCP"
legacy_mod_name="STS2AIAgent"
mod_project="$repo_root/STS2AIMCP/STS2AIMCP.csproj"
build_output_dir="$repo_root/STS2AIMCP/bin/$configuration/net9.0"
staging_dir="$repo_root/build/mods/$mod_name"
pck_manifest_source="$repo_root/STS2AIMCP/mod_manifest.json"
mod_json_source="$repo_root/STS2AIMCP/$mod_name.json"
dll_source="$build_output_dir/$mod_name.dll"
pck_output="$staging_dir/$mod_name.pck"
dll_target="$staging_dir/$mod_name.dll"
mod_json_target="$staging_dir/$mod_name.json"
builder_project_dir="$repo_root/tools/pck_builder"
builder_script="$builder_project_dir/build_pck.gd"

if [[ ! -f "$mod_project" ]]; then
  echo "Mod project not found: $mod_project" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not installed or not available in PATH." >&2
  echo "On macOS, install it with: brew install dotnet" >&2
  exit 1
fi

game_root="$game_root_input"
if [[ -z "$game_root" ]]; then
  game_root="$(detect_game_root || true)"
fi

if [[ -n "$game_root" ]]; then
  game_root="$(resolve_existing_dir "$game_root")"
fi

app_bundle=""
if [[ -n "$game_root" ]]; then
  if [[ "$game_root" == *.app ]]; then
    app_bundle="$game_root"
  elif [[ -d "$game_root/Slay the Spire 2.app" ]]; then
    app_bundle="$game_root/Slay the Spire 2.app"
  elif [[ -d "$game_root/SlayTheSpire2.app" ]]; then
    app_bundle="$game_root/SlayTheSpire2.app"
  fi
fi

godot_exe="$(detect_godot_exe || true)"
if [[ -z "$godot_exe" ]]; then
  echo "Could not find a Godot executable." >&2
  echo "Pass --godot-exe /path/to/Godot or set GODOT_BIN." >&2
  exit 1
fi

check_godot_version "$godot_exe"

path_for_godot() {
  local path="$1"

  if [[ "$godot_exe" == *.exe && -n "${WSL_DISTRO_NAME:-}" ]] && command -v wslpath >/dev/null 2>&1; then
    wslpath -w "$path"
    return
  fi

  printf '%s\n' "$path"
}

data_dir="$data_dir_input"
if [[ -z "$data_dir" && -n "$game_root" ]]; then
  data_dir="$(first_existing_dir \
    "$game_root/data_sts2_windows_x86_64" \
    "$game_root/data_sts2_osx_arm64" \
    "$game_root/data_sts2_osx_x86_64" \
    "$game_root/data_sts2_macos" \
    "$game_root/data_sts2_macos_arm64" \
    "$game_root/data_sts2_macos_x86_64" \
    "$app_bundle/Contents/Resources/data_sts2_osx_arm64" \
    "$app_bundle/Contents/Resources/data_sts2_osx_x86_64" \
    "$app_bundle/Contents/Resources/data_sts2_macos" \
    "$app_bundle/Contents/Resources/data_sts2_macos_arm64" \
    "$app_bundle/Contents/Resources/data_sts2_macos_x86_64" \
    "$app_bundle/Contents/MacOS/data_sts2_osx_arm64" \
    "$app_bundle/Contents/MacOS/data_sts2_osx_x86_64" \
    "$app_bundle/Contents/MacOS/data_sts2_macos" \
    "$app_bundle/Contents/MacOS/data_sts2_macos_arm64" \
    "$app_bundle/Contents/MacOS/data_sts2_macos_x86_64" \
  || true)"
fi

if [[ -z "$data_dir" ]]; then
  echo "Could not determine the game's data directory." >&2
  echo "Pass --data-dir /path/to/data_sts2_* or set STS2_DATA_DIR." >&2
  exit 1
fi

data_dir="$(resolve_existing_dir "$data_dir")"

mods_dir="$mods_dir_input"
if [[ -z "$mods_dir" && -n "$app_bundle" ]]; then
  mods_dir="$app_bundle/Contents/MacOS/mods"
fi
if [[ -z "$mods_dir" && -n "$game_root" ]]; then
  mods_dir="$game_root/mods"
fi

if [[ -z "$mods_dir" ]]; then
  echo "Could not determine the mods directory." >&2
  echo "Pass --mods-dir /path/to/mods or set STS2_MODS_DIR." >&2
  exit 1
fi

mkdir -p "$staging_dir"
mkdir -p "$mods_dir"

echo "[build-mod] Building C# mod project..."
dotnet build "$mod_project" -c "$configuration" /p:Sts2DataDir="$data_dir"

if [[ ! -f "$dll_source" ]]; then
  echo "Built DLL not found: $dll_source" >&2
  exit 1
fi

cp -f "$dll_source" "$dll_target"

if [[ ! -f "$pck_manifest_source" ]]; then
  echo "PCK manifest not found: $pck_manifest_source" >&2
  exit 1
fi

if [[ ! -f "$mod_json_source" ]]; then
  echo "Mod JSON manifest not found: $mod_json_source" >&2
  exit 1
fi

cp -f "$mod_json_source" "$mod_json_target"

echo "[build-mod] Packing mod_manifest.json into PCK..."
"$godot_exe" \
  --headless \
  --path "$(path_for_godot "$builder_project_dir")" \
  --script "$(path_for_godot "$builder_script")" \
  -- "$(path_for_godot "$pck_manifest_source")" "$(path_for_godot "$pck_output")"

if [[ ! -f "$pck_output" ]]; then
  echo "PCK output not found: $pck_output" >&2
  exit 1
fi

echo "[build-mod] Preparing game mods directory..."
installed_mod_dir="$mods_dir/$mod_name"
mkdir -p "$installed_mod_dir"
cp -f "$dll_target" "$installed_mod_dir/$mod_name.dll"
cp -f "$pck_output" "$installed_mod_dir/$mod_name.pck"
cp -f "$mod_json_target" "$installed_mod_dir/$mod_name.json"

legacy_root_files=()
for legacy_file in "$mods_dir/$mod_name.dll" "$mods_dir/$mod_name.pck" "$mods_dir/$legacy_mod_name.dll" "$mods_dir/$legacy_mod_name.pck" "$mods_dir/mod_id.json"; do
  if [[ -e "$legacy_file" ]]; then
    legacy_root_files+=("$legacy_file")
  fi
done

legacy_folders=()
for legacy_folder in "$mods_dir/$legacy_mod_name"; do
  if [[ -e "$legacy_folder" ]]; then
    legacy_folders+=("$legacy_folder")
  fi
done

if [[ ${#legacy_root_files[@]} -gt 0 || ${#legacy_folders[@]} -gt 0 ]]; then
  echo "[build-mod] WARNING: Legacy STS2AIAgent mod files were found in the mods directory." >&2
  echo "[build-mod] Back them up and remove them before testing STS2AIMCP to avoid duplicate or stale mod loads:" >&2
  for legacy_file in "${legacy_root_files[@]}"; do
    echo "[build-mod]   $legacy_file" >&2
  done
  for legacy_folder in "${legacy_folders[@]}"; do
    echo "[build-mod]   $legacy_folder" >&2
  done
fi

echo "[build-mod] Done."
echo "[build-mod] Using data dir: $data_dir"
echo "[build-mod] Using mods dir: $mods_dir"
echo "[build-mod] Using Godot: $godot_exe"
echo "[build-mod] Installed files:"
echo "  $installed_mod_dir/$mod_name.dll"
echo "  $installed_mod_dir/$mod_name.pck"
echo "  $installed_mod_dir/$mod_name.json"
