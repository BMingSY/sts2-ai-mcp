from __future__ import annotations

import hashlib
import json
import os
import re
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any


SNAPSHOT_SCHEMA_VERSION = 1
DEFAULT_COLLECTIONS = ("cards", "monsters", "powers", "relics", "potions", "events")


class GameDataVersionError(RuntimeError):
    pass


def _safe_version(value: str) -> str:
    normalized = re.sub(r"[^A-Za-z0-9._+-]+", "_", value.strip()).strip("._")
    return normalized or "unknown"


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _content_hash(collections: dict[str, Any]) -> str:
    return f"sha256:{hashlib.sha256(_canonical_json(collections)).hexdigest()}"


def default_versioned_data_root() -> Path:
    configured = os.getenv("STS2_GAME_DATA_DIR", "").strip()
    if configured:
        return Path(configured).expanduser().resolve()
    return Path(__file__).resolve().parents[2] / "data" / "versions"


@dataclass(frozen=True, slots=True)
class GameDataSnapshot:
    game_version: str
    manifest: dict[str, Any]
    collections: dict[str, dict[str, Any]]
    indexes: dict[str, dict[str, Any]]

    def lookup(self, items: list[dict[str, Any]], fields: list[str] | None = None) -> dict[str, Any]:
        requested_fields = [str(field).strip() for field in (fields or []) if str(field).strip()]
        result: dict[str, Any] = {}

        for requested in items:
            collection = str(requested.get("collection", "")).strip()
            item_id = str(requested.get("id", "")).strip()
            key = f"{collection}:{item_id}"
            index = self.indexes.get(collection)
            value = index.get(item_id.lower()) if index is not None and item_id else None
            if value is None or not requested_fields or not isinstance(value, dict):
                result[key] = value
            else:
                result[key] = {field: value[field] for field in requested_fields if field in value}

        return {
            "items": result,
            "metadata": {
                "schema_version": self.manifest.get("schema_version"),
                "game_version": self.game_version,
                "mod_version": self.manifest.get("mod_version"),
                "data_source": "mcp_versioned_cache",
                "exported_at_utc": self.manifest.get("exported_at_utc"),
                "content_hash": self.manifest.get("content_hash"),
            },
        }


class VersionedGameDataStore:
    def __init__(self, root: Path | None = None) -> None:
        self.root = (root or default_versioned_data_root()).resolve()
        self._cache: dict[str, GameDataSnapshot] = {}
        self._lock = threading.Lock()

    def version_dir(self, game_version: str) -> Path:
        return self.root / _safe_version(game_version)

    def available_versions(self) -> list[str]:
        if not self.root.is_dir():
            return []
        return sorted(
            path.name
            for path in self.root.iterdir()
            if path.is_dir() and (path / "manifest.json").is_file()
        )

    def load(self, game_version: str) -> GameDataSnapshot:
        cache_key = _safe_version(game_version)
        cached = self._cache.get(cache_key)
        if cached is not None:
            return cached

        with self._lock:
            cached = self._cache.get(cache_key)
            if cached is not None:
                return cached

            version_dir = self.version_dir(game_version)
            manifest_path = version_dir / "manifest.json"
            if not manifest_path.is_file():
                raise GameDataVersionError(f"No game-data snapshot for version {game_version!r}.")

            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            except Exception as exc:
                raise GameDataVersionError(f"Failed to read {manifest_path}: {exc}") from exc

            if manifest.get("schema_version") != SNAPSHOT_SCHEMA_VERSION:
                raise GameDataVersionError(
                    f"Unsupported snapshot schema {manifest.get('schema_version')!r} for {game_version!r}."
                )
            if str(manifest.get("game_version", "")) != game_version:
                raise GameDataVersionError(
                    f"Snapshot version mismatch: expected {game_version!r}, found {manifest.get('game_version')!r}."
                )

            collection_metadata = manifest.get("collections")
            if not isinstance(collection_metadata, dict):
                raise GameDataVersionError(f"Snapshot manifest has no collections: {manifest_path}")

            collections: dict[str, dict[str, Any]] = {}
            indexes: dict[str, dict[str, Any]] = {}
            for collection, metadata in collection_metadata.items():
                if not isinstance(metadata, dict):
                    continue
                filename = str(metadata.get("file", f"{collection}.json"))
                collection_path = version_dir / filename
                try:
                    payload = json.loads(collection_path.read_text(encoding="utf-8"))
                except Exception as exc:
                    raise GameDataVersionError(f"Failed to read {collection_path}: {exc}") from exc
                if not isinstance(payload, dict):
                    raise GameDataVersionError(f"Collection {collection!r} must be a JSON object.")

                collections[collection] = payload
                index: dict[str, Any] = {}
                for raw_id, item in payload.items():
                    item_id = str(item.get("id", raw_id)) if isinstance(item, dict) else str(raw_id)
                    index[item_id.lower()] = item
                indexes[collection] = index

            expected_hash = str(manifest.get("content_hash", ""))
            actual_hash = _content_hash(collections)
            if expected_hash and expected_hash != actual_hash:
                raise GameDataVersionError(
                    f"Snapshot content hash mismatch for {game_version!r}: expected {expected_hash}, found {actual_hash}."
                )

            snapshot = GameDataSnapshot(
                game_version=game_version,
                manifest=manifest,
                collections=collections,
                indexes=indexes,
            )
            self._cache[cache_key] = snapshot
            return snapshot

    def save_export(self, export: dict[str, Any]) -> GameDataSnapshot:
        collections_value = export.get("collections")
        metadata_value = export.get("metadata")
        if not isinstance(collections_value, dict) or not isinstance(metadata_value, dict):
            raise GameDataVersionError("Game-data export must contain collections and metadata objects.")

        game_version = str(metadata_value.get("game_version", "")).strip()
        if not game_version:
            raise GameDataVersionError("Game-data export metadata is missing game_version.")

        collections: dict[str, dict[str, Any]] = {}
        for collection in DEFAULT_COLLECTIONS:
            value = collections_value.get(collection, {})
            if not isinstance(value, dict):
                raise GameDataVersionError(f"Export collection {collection!r} must be an object.")
            collections[collection] = value

        version_dir = self.version_dir(game_version)
        version_dir.mkdir(parents=True, exist_ok=True)
        collection_manifest: dict[str, dict[str, Any]] = {}
        for collection, payload in collections.items():
            filename = f"{collection}.json"
            target = version_dir / filename
            temporary = version_dir / f".{filename}.tmp"
            temporary.write_text(
                json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
                encoding="utf-8",
            )
            os.replace(temporary, target)
            collection_manifest[collection] = {"file": filename, "count": len(payload)}

        manifest = {
            "schema_version": SNAPSHOT_SCHEMA_VERSION,
            "game_version": game_version,
            "mod_version": metadata_value.get("mod_version"),
            "data_source": "loaded_game_model",
            "exported_at_utc": metadata_value.get("exported_at_utc"),
            "content_hash": _content_hash(collections),
            "collections": collection_manifest,
        }
        manifest_target = version_dir / "manifest.json"
        manifest_temporary = version_dir / ".manifest.json.tmp"
        manifest_temporary.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        os.replace(manifest_temporary, manifest_target)

        self._cache.pop(_safe_version(game_version), None)
        return self.load(game_version)
