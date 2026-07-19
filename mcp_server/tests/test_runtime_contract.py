from __future__ import annotations

import unittest
from unittest.mock import patch

from sts2_mcp.client import (
    MIN_DECISION_VERSION,
    MIN_STATE_VERSION,
    REQUIRED_CAPABILITIES,
    REQUIRED_PROTOCOL_VERSION,
    Sts2CapabilityError,
    Sts2Client,
    evaluate_runtime_contract,
)


def compatible_health() -> dict:
    return {
        "protocol_version": REQUIRED_PROTOCOL_VERSION,
        "state_version": MIN_STATE_VERSION,
        "decision_version": MIN_DECISION_VERSION,
        "capabilities": {name: True for name in REQUIRED_CAPABILITIES},
    }


class RuntimeContractTests(unittest.TestCase):
    def test_compatible_contract_passes(self) -> None:
        result = evaluate_runtime_contract(compatible_health())

        self.assertTrue(result["compatible"])
        self.assertEqual(result["missing"], [])

    def test_missing_capability_fails_loudly(self) -> None:
        health = compatible_health()
        health["capabilities"]["action_trace"] = False
        client = Sts2Client()

        with patch.object(client, "get_health", return_value=health):
            with self.assertRaises(Sts2CapabilityError) as raised:
                client.require_runtime_contract()

        self.assertIn("capability:action_trace", raised.exception.missing)

    def test_boss_identity_is_a_required_runtime_capability(self) -> None:
        health = compatible_health()
        health["capabilities"]["map_boss_identity"] = False

        result = evaluate_runtime_contract(health)

        self.assertFalse(result["compatible"])
        self.assertIn("capability:map_boss_identity", result["missing"])

    def test_preview_and_trace_client_routes(self) -> None:
        client = Sts2Client()
        with patch.object(client, "_request", return_value={"ok": True}) as request:
            client.preview_action(decision_id="d1", action_id="a1")
            preview_call = request.call_args
            client.get_action_trace(after_sequence=42)
            trace_call = request.call_args

        self.assertEqual(preview_call.args[:2], ("POST", "/v2/decision/preview"))
        self.assertEqual(preview_call.kwargs["payload"], {"decision_id": "d1", "action_id": "a1"})
        self.assertEqual(trace_call.args[:2], ("GET", "/v2/trace/actions?after_sequence=42"))

    def test_model_discovery_client_routes(self) -> None:
        client = Sts2Client()
        with patch.object(client, "_request", return_value={"ok": True}) as request:
            client.search_game_data(query="queen", collections=["monsters", "encounters"], limit=12)
            search_call = request.call_args
            client.list_model_ids(collection="enchantments", query="ad", offset=2, limit=40)
            ids_call = request.call_args

        self.assertEqual(search_call.args[:2], ("POST", "/v2/data/search"))
        self.assertEqual(
            search_call.kwargs["payload"],
            {"query": "queen", "collections": ["monsters", "encounters"], "limit": 12},
        )
        self.assertEqual(
            ids_call.args[:2],
            ("GET", "/v2/data/ids?collection=enchantments&offset=2&limit=40&query=ad"),
        )


if __name__ == "__main__":
    unittest.main()
