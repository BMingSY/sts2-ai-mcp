from __future__ import annotations

import unittest
from unittest.mock import patch

from sts2_mcp.client import (
    MIN_DECISION_VERSION,
    MIN_STATE_VERSION,
    REQUIRED_CAPABILITIES,
    REQUIRED_PROTOCOL_VERSION,
    Sts2ApiError,
    Sts2CapabilityError,
    Sts2Client,
    evaluate_runtime_contract,
)
from sts2_mcp.server import enforce_startup_contract


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

    def test_stdio_startup_can_defer_when_game_is_not_running(self) -> None:
        client = Sts2Client()
        unavailable = Sts2ApiError(
            status_code=0,
            code="connection_error",
            message="game is not running",
            retryable=True,
        )

        with patch.object(client, "require_runtime_contract", side_effect=unavailable):
            result = enforce_startup_contract(client, allow_unreachable=True)

        self.assertFalse(result["compatible"])
        self.assertTrue(result["deferred"])

    def test_default_startup_still_rejects_an_unreachable_game(self) -> None:
        client = Sts2Client()
        unavailable = Sts2ApiError(
            status_code=0,
            code="connection_error",
            message="game is not running",
            retryable=True,
        )

        with patch.object(client, "require_runtime_contract", side_effect=unavailable):
            with self.assertRaisesRegex(RuntimeError, "startup contract check failed"):
                enforce_startup_contract(client)

    def test_deferred_startup_does_not_hide_an_incompatible_mod(self) -> None:
        client = Sts2Client()
        incompatible = Sts2CapabilityError(
            missing=["capability:decision_v2"],
            health=compatible_health(),
        )

        with patch.object(client, "require_runtime_contract", side_effect=incompatible):
            with self.assertRaisesRegex(RuntimeError, "startup contract check failed"):
                enforce_startup_contract(client, allow_unreachable=True)

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
