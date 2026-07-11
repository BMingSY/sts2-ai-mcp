#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from urllib import request


REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "mcp_server" / "src"))

from sts2_mcp.game_data import VersionedGameDataStore  # noqa: E402


def _request_json(method: str, url: str, payload: dict | None = None) -> dict:
    body = json.dumps(payload).encode("utf-8") if payload is not None else None
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
    opener = request.build_opener(request.ProxyHandler({}))
    with opener.open(request.Request(url, method=method, data=body, headers=headers), timeout=30) as response:
        parsed = json.loads(response.read().decode("utf-8"))
    if not parsed.get("ok") or not isinstance(parsed.get("data"), dict):
        raise RuntimeError(f"Unexpected response from {url}: {parsed}")
    return parsed["data"]


def main() -> int:
    parser = argparse.ArgumentParser(description="Export one STS2 build's static game data into a versioned MCP cache.")
    parser.add_argument("--api", default="http://127.0.0.1:8080", help="STS2 Mod API base URL.")
    parser.add_argument(
        "--output",
        type=Path,
        default=REPO_ROOT / "mcp_server" / "data" / "versions",
        help="Root directory containing one subdirectory per game version.",
    )
    parser.add_argument("--force", action="store_true", help="Replace an existing snapshot for this game version.")
    args = parser.parse_args()

    api = args.api.rstrip("/")
    health = _request_json("GET", f"{api}/health")
    game_version = str(health.get("game_version", "")).strip()
    if not game_version:
        raise RuntimeError("The running game did not report game_version.")

    store = VersionedGameDataStore(args.output)
    if not args.force:
        try:
            snapshot = store.load(game_version)
            print(f"Snapshot already exists: {store.version_dir(game_version)}")
            print(f"content_hash={snapshot.manifest.get('content_hash')}")
            return 0
        except RuntimeError:
            pass

    exported = _request_json("POST", f"{api}/v2/data/export", {})
    snapshot = store.save_export(exported)
    print(f"Saved STS2 {snapshot.game_version} data: {store.version_dir(snapshot.game_version)}")
    print(f"content_hash={snapshot.manifest.get('content_hash')}")
    for collection, metadata in snapshot.manifest["collections"].items():
        print(f"{collection}: {metadata['count']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
