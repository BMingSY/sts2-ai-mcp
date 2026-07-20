# STS2 AI MCP

AI-safe MCP and local HTTP control surface for Slay the Spire 2.

This repository is the v2-focused successor to the older STS2-Agent fork. The goal is to expose stable decision windows to agents instead of raw UI button calls.

## What Is Included

- `STS2AIMCP/`: the in-game mod HTTP API.
- `mcp_server/`: FastMCP wrapper with `ai_safe_v2` as the default profile.
- `scripts/`: build and MCP startup helpers.
- `docs/`: v2 protocol, compatibility, build notes, and the repository-maintained `sts2-player` Codex skill.

Reusable gameplay guidance required by `sts2-player` is versioned under `docs/skills/sts2-player/`. Run-specific logs and private local notes should still live under ignored paths such as `agent_knowledge/`, `run_logs/`, or an external path such as `~/.local/share/sts2-ai-mcp/knowledge`.

## Current Status

The v2 protocol is experimental but usable for local testing:

- `GET /v2/decision/current`
- `POST /v2/decision/wait`
- `POST /v2/decision/preview`
- `POST /v2/decision/act`
- `GET /v2/trace/actions`
- `POST /v2/data/lookup`
- `POST /v2/data/search`
- `GET /v2/data/ids`
- `POST /v2/data/export`

Normal MCP play should use only:

- `health_check`
- `wait_for_decision`
- `get_current_decision`
- `preview_action`
- `preview_action_plan`
- `run_evaluator`
- `combat_horizon`
- `take_action`
- `get_action_trace`
- `execute_action_plan`
- `select_cards`
- `select_character`
- `lookup_game_data`
- `append_decision_note`

When `STS2_ENABLE_DEBUG_ACTIONS=1`, MCP additionally exposes the read-only `search_game_data` and `list_model_ids` discovery tools alongside `run_console_command`. They search live ModelDb data and remain absent from the normal tool surface.

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

The stdio MCP server may start before the game. It keeps the client-owned transport available while the Mod API is unreachable and becomes usable after the game starts, so normal Codex play does not need a separately managed network server or terminal driver. A reachable but incompatible Mod still fails the v2 protocol/state/decision capability contract. The network MCP startup continues to require a reachable Mod API. Use `STS2_MCP_ALLOW_INCOMPATIBLE=1` only for an explicit local compatibility experiment.

On the character-select screen, the preferred `ai_safe_v2`, guided, and full action is a single call with both values: `select_character(character_id="IRONCLAD", ascension=10)`. The Mod validates the exact level against the currently unlocked range; callers no longer need to select a character and then issue repeated ascension increments.

The AI-safe v2 MCP also exposes `preview_action(decision_id, action_id)`, read-only
`preview_action_plan(decision_id, steps)`, `run_evaluator`, `combat_horizon`, and
`get_action_trace(after_sequence)`.
Plan preview checks a deterministic combat prefix against the current energy, stars,
known card-play limits, stable references, and sequential direct damage/Block without
mutating the game. `run_evaluator` reports public deck metrics and visible-candidate
before/after deltas; `combat_horizon` checks several model-proposed lines under the
same preview semantics and adds current-intent survival arithmetic. Neither tool
chooses or executes an action. Both use cached decisions only, default to a 100ms
work budget, have a 500ms hard ceiling, and return structured partial results when a
work limit is reached. Monster lookups carry the generated engine move state machine,
while relics and powers expose the same structured `trigger_progress` schema.

Every run-backed decision now includes factual `context.run_analysis` deck-shape,
role-density, cost-curve, and natural-access metrics. Special card selections expose
live resolved text/dynamic values plus recognized consequence and deck-interaction
previews, so callers do not need to substitute a static model value for a runtime
curse amount.

The current Act's rolled boss is available throughout the run as
`context.run.boss_encounter` with both its stable `encounter_id` and localized
`name`. Ascension 10+ runs also expose `second_boss_encounter`. On map decisions,
the same identities are repeated under `context.map.boss_info` alongside their map
nodes, allowing reward, shop, and route choices to account for the known boss before
the boss room is reached.

Static card, monster, encounter, enchantment, power, relic, potion, and event metadata is cached once per game version under `mcp_server/data/versions/`. Pre-generate the running version with:

```bash
python3 scripts/sync-game-data.py
```

## Repository-Maintained Codex Skill

The complete `sts2-player` skill lives under [`docs/skills/sts2-player/`](docs/skills/sts2-player/). It includes the direct MCP workflow, NOSL A10 strategy references, encounter/retrospective notes, UI metadata, and the bounded autoplay harness.

To install or refresh the personal Codex copy:

```bash
mkdir -p ~/.codex/skills/sts2-player
rsync -a docs/skills/sts2-player/ ~/.codex/skills/sts2-player/
```

Treat the repository copy as the maintained source and exclude generated `__pycache__` files.

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
