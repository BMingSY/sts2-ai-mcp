# Optional Game Data Cache

The v2 MCP server prefers live game data from the running mod API:

```text
POST /v2/data/lookup
```

This directory is reserved for an optional local fallback cache. The public repository does not require checked-in `eng/*.json` data.

If you generate a local cache, keep it tied to:

- the Slay the Spire 2 game version
- the export source
- a content hash

Do not commit generated cache files unless their source, license, and game-version compatibility have been explicitly reviewed.
