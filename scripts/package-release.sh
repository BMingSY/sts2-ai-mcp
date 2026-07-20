#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

configuration="${CONFIGURATION:-Release}"
repo_root_input="${REPO_ROOT:-}"
game_root_input="${STS2_GAME_ROOT:-}"
godot_exe_input="${GODOT_BIN:-}"
output_root_input=""
skip_build="0"
allow_godot_version_mismatch="0"

usage() {
  cat <<'EOF'
Usage: package-release.sh [--configuration Release] [--repo-root PATH] [--game-root PATH] [--godot-exe PATH] [--output-root PATH] [--skip-build] [--allow-godot-version-mismatch]

Creates a GitHub Release-ready ZIP containing only the game-side STS2AIMCP mod.
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
    --godot-exe)
      godot_exe_input="${2:-}"
      shift 2
      ;;
    --output-root)
      output_root_input="${2:-}"
      shift 2
      ;;
    --skip-build)
      skip_build="1"
      shift
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

resolve_dir() {
  cd -- "$1" && pwd
}

repo_root="$repo_root_input"
if [[ -z "$repo_root" ]]; then
  repo_root="$(resolve_dir "$script_dir/..")"
else
  repo_root="$(resolve_dir "$repo_root")"
fi

mod_name="STS2AIMCP"
mod_json_source="$repo_root/$mod_name/$mod_name.json"
staging_mod_dir="$repo_root/build/mods/$mod_name"
release_output_root="$output_root_input"
if [[ -z "$release_output_root" ]]; then
  release_output_root="$repo_root/dist/release"
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required to read release metadata." >&2
  exit 1
fi

read_json_value() {
  local path="$1"
  local key="$2"
  python3 - "$path" "$key" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as fh:
    data = json.load(fh)
value = data
for part in sys.argv[2].split("."):
    value = value.get(part, "")
print(value if value is not None else "")
PY
}

sanitize_file_part() {
  printf '%s' "$1" | tr -cs 'A-Za-z0-9._-' '-'
}

game_version="unknown"
if [[ -n "$game_root_input" && -f "$game_root_input/release_info.json" ]]; then
  game_version="$(read_json_value "$game_root_input/release_info.json" "version")"
elif [[ -n "$game_root_input" ]]; then
  echo "[release] WARNING: release_info.json not found under game root: $game_root_input" >&2
fi
if [[ -z "$game_version" ]]; then
  game_version="unknown"
fi

mod_version="$(read_json_value "$mod_json_source" "version")"
if [[ -z "$mod_version" ]]; then
  echo "Could not read mod version from: $mod_json_source" >&2
  exit 1
fi

if [[ "$skip_build" != "1" ]]; then
  build_args=(
    --configuration "$configuration"
    --repo-root "$repo_root"
  )

  if [[ -n "$game_root_input" ]]; then
    build_args+=(--game-root "$game_root_input")
  fi

  if [[ -n "$godot_exe_input" ]]; then
    build_args+=(--godot-exe "$godot_exe_input")
  fi

  if [[ "$allow_godot_version_mismatch" == "1" ]]; then
    build_args+=(--allow-godot-version-mismatch)
  fi

  echo "[release] Building mod..."
  "$script_dir/build-mod.sh" "${build_args[@]}"
fi

for artifact in "$staging_mod_dir/$mod_name.dll" "$staging_mod_dir/$mod_name.pck" "$staging_mod_dir/$mod_name.json"; do
  if [[ ! -f "$artifact" ]]; then
    echo "Required build artifact not found: $artifact" >&2
    exit 1
  fi
done

game_version_part="$(sanitize_file_part "$game_version")"
artifact_name="sts2-ai-mcp-v${mod_version}-sts2-${game_version_part}-godot-4.5.1"
stage_dir="$release_output_root/stage/$artifact_name"
zip_path="$release_output_root/$artifact_name.zip"
checksum_path="$zip_path.sha256"

rm -rf "$stage_dir"
mkdir -p "$stage_dir/$mod_name" "$release_output_root"
cp -f "$staging_mod_dir/$mod_name.dll" "$stage_dir/$mod_name/$mod_name.dll"
cp -f "$staging_mod_dir/$mod_name.pck" "$stage_dir/$mod_name/$mod_name.pck"
cp -f "$staging_mod_dir/$mod_name.json" "$stage_dir/$mod_name/$mod_name.json"

cat >"$stage_dir/README-release.txt" <<EOF
STS2 AI MCP v$mod_version

This release ZIP contains only the Slay the Spire 2 game-side mod:

- $mod_name/$mod_name.dll
- $mod_name/$mod_name.pck
- $mod_name/$mod_name.json

The MCP server is distributed separately from the GitHub repository.

Tested game version: $game_version
Expected Godot/MegaDot version: 4.5.1.m.12
V2 protocol version: 2026-07-18-v2-draft
State version: 13
Decision version: 6

Manual install:

1. Close Slay the Spire 2.
2. Remove or disable older STS2AIAgent files from the game's mods directory.
3. Copy the $mod_name folder from this ZIP into the game's mods directory.
4. Start the game and verify http://127.0.0.1:8080/health.

Do not assume this build is compatible with newer Slay the Spire 2 updates until it is retested.
EOF

python3 - "$stage_dir/release-metadata.json" "$mod_version" "$game_version" <<'PY'
import json
import sys
from datetime import datetime, timezone

path, mod_version, game_version = sys.argv[1:4]
metadata = {
    "service": "sts2-ai-mcp",
    "mod_id": "STS2AIMCP",
    "mod_version": mod_version,
    "v2_protocol_version": "2026-07-18-v2-draft",
    "state_version": 13,
    "decision_version": 6,
    "tested_game_version": game_version,
    "expected_godot_version": "4.5.1.m.12",
    "packaged_at_utc": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
    "contents": [
        "STS2AIMCP/STS2AIMCP.dll",
        "STS2AIMCP/STS2AIMCP.pck",
        "STS2AIMCP/STS2AIMCP.json",
    ],
}
with open(path, "w", encoding="utf-8") as fh:
    json.dump(metadata, fh, indent=2)
    fh.write("\n")
PY

rm -f "$zip_path" "$checksum_path"
if command -v zip >/dev/null 2>&1; then
  (cd "$stage_dir" && zip -qr "$zip_path" .)
else
  python3 - "$stage_dir" "$zip_path" <<'PY'
import os
import sys
import zipfile

stage_dir, zip_path = sys.argv[1:3]
with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for root, _, files in os.walk(stage_dir):
        for name in files:
            path = os.path.join(root, name)
            zf.write(path, os.path.relpath(path, stage_dir))
PY
fi

(cd "$(dirname "$zip_path")" && sha256sum "$(basename "$zip_path")") >"$checksum_path"

echo "[release] ZIP written:"
echo "  $zip_path"
echo "[release] SHA256 written:"
echo "  $checksum_path"
echo "[release] Staged contents:"
find "$stage_dir" -maxdepth 2 -type f -printf '  %P\n' | sort
