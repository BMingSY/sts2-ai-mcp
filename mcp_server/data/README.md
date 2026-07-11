# Versioned Game Data Cache

Static game metadata is stored one snapshot per Slay the Spire 2 version:

```text
mcp_server/data/versions/
└── v0.107.1/
    ├── manifest.json
    ├── cards.json
    ├── monsters.json
    ├── powers.json
    ├── relics.json
    ├── potions.json
    └── events.json
```

The MCP server checks the running game's `game_version`. If that version has no local snapshot, it calls `POST /v2/data/export` once and writes the snapshot. All later `lookup_game_data` calls and decision knowledge hydration use the local files instead of the game thread.

Generate or refresh explicitly:

```bash
python3 scripts/sync-game-data.py
python3 scripts/sync-game-data.py --force
```

Set `STS2_GAME_DATA_DIR` to keep snapshots outside the repository.

Each `manifest.json` records:

- the Slay the Spire 2 game version
- the MCP Mod version
- a content hash
- collection file names and item counts

Generated snapshots are ignored by Git by default. Do not publish or commit them unless their source, license, and game-version compatibility have been explicitly reviewed.
