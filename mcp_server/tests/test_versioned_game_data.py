from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from sts2_mcp.game_data import GameDataVersionError, VersionedGameDataStore


def _export(game_version: str = "v-test.1") -> dict:
    return {
        "collections": {
            "cards": {
                "BASH": {
                    "id": "BASH",
                    "name": "Bash",
                    "description": "Deal damage and apply Vulnerable.",
                    "cost": 2,
                }
            },
            "monsters": {},
            "powers": {},
            "relics": {},
            "potions": {},
            "events": {},
        },
        "metadata": {
            "game_version": game_version,
            "mod_version": "0.1.0",
            "exported_at_utc": "2026-07-11T00:00:00Z",
        },
    }


class VersionedGameDataStoreTests(unittest.TestCase):
    def test_save_load_and_case_insensitive_lookup(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            store = VersionedGameDataStore(Path(tmpdir))
            snapshot = store.save_export(_export())

            result = snapshot.lookup(
                [{"collection": "cards", "id": "bash"}],
                fields=["id", "name"],
            )

            self.assertEqual(result["items"]["cards:bash"], {"id": "BASH", "name": "Bash"})
            self.assertEqual(result["metadata"]["game_version"], "v-test.1")
            self.assertEqual(result["metadata"]["data_source"], "mcp_versioned_cache")
            self.assertTrue(result["metadata"]["content_hash"].startswith("sha256:"))
            self.assertEqual(store.available_versions(), ["v-test.1"])

            manifest = json.loads((Path(tmpdir) / "v-test.1" / "manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["collections"]["cards"]["count"], 1)

    def test_load_rejects_content_changes(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            root = Path(tmpdir)
            VersionedGameDataStore(root).save_export(_export())
            cards_path = root / "v-test.1" / "cards.json"
            cards_path.write_text("{}\n", encoding="utf-8")

            with self.assertRaisesRegex(GameDataVersionError, "content hash mismatch"):
                VersionedGameDataStore(root).load("v-test.1")

    def test_each_game_version_has_an_independent_directory(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            store = VersionedGameDataStore(Path(tmpdir))
            store.save_export(_export("v-test.1"))
            store.save_export(_export("v-test.2"))

            self.assertEqual(store.available_versions(), ["v-test.1", "v-test.2"])
            self.assertEqual(store.load("v-test.1").game_version, "v-test.1")
            self.assertEqual(store.load("v-test.2").game_version, "v-test.2")

    def test_load_applies_curated_queen_override_after_hash_validation(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            export = _export()
            export["collections"]["monsters"] = {
                "QUEEN": {
                    "id": "QUEEN",
                    "name": "Queen",
                    "description": "",
                    "moves": [],
                }
            }
            store = VersionedGameDataStore(Path(tmpdir))
            snapshot = store.save_export(export)

            result = snapshot.lookup([{"collection": "monsters", "id": "QUEEN"}])
            queen = result["items"]["monsters:QUEEN"]

            self.assertEqual(queen["data_completeness"], "curated_partial")
            self.assertEqual(queen["type"], "Boss")
            self.assertEqual(queen["moves"][0]["id"], "ENRAGE_MOVE")
            self.assertIn("monsters:QUEEN", snapshot.manifest["curated_overrides"])
            self.assertIn("monsters:QUEEN", result["metadata"]["curated_overrides"])

    def test_curated_queen_notes_do_not_replace_runtime_state_machine(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            export = _export()
            runtime_moves = [{"id": "EXECUTION", "is_move": True, "intents": []}]
            runtime_machine = {"initial_state_id": "EXECUTION", "states": runtime_moves}
            export["collections"]["monsters"] = {
                "QUEEN": {
                    "id": "QUEEN",
                    "name": "Queen",
                    "description": "runtime",
                    "moves": runtime_moves,
                    "state_machine": runtime_machine,
                    "data_completeness": "runtime_state_machine",
                    "data_source_notes": "runtime export",
                }
            }

            snapshot = VersionedGameDataStore(Path(tmpdir)).save_export(export)
            queen = snapshot.lookup([{"collection": "monsters", "id": "QUEEN"}])["items"]["monsters:QUEEN"]

            self.assertEqual(queen["moves"], runtime_moves)
            self.assertEqual(queen["state_machine"], runtime_machine)
            self.assertEqual(queen["data_completeness"], "runtime_state_machine")
            self.assertEqual(queen["data_source_notes"], "runtime export")
            self.assertTrue(queen["mechanics"])
