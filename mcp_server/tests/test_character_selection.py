from __future__ import annotations

import asyncio
import unittest
from unittest.mock import patch

from sts2_mcp.client import Sts2Client
from sts2_mcp.server import create_server


class RecordingGuidedClient:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict]] = []

    def get_health(self) -> dict:
        return {"ok": True}

    def get_state(self) -> dict:
        return {"screen": "CHARACTER_SELECT", "available_actions": ["select_character"]}

    def get_available_actions(self) -> list[dict]:
        return []

    def wait_for_event(self, *, event_names=None, timeout=0.0) -> dict | None:
        return None

    def execute_action(self, action: str, **kwargs) -> dict:
        self.calls.append((action, kwargs))
        return {"status": "completed"}


class RecordingV2Client:
    def __init__(self) -> None:
        self.take_calls: list[dict] = []

    def get_health(self) -> dict:
        return {"ok": True}

    def get_current_decision(self, **kwargs) -> dict:
        return {
            "available": True,
            "decision": {
                "decision_id": "run:f0:character_select:t0:test",
                "phase": "character_select",
                "choices": [
                    {
                        "action_id": "character_select:select:IRONCLAD:a7",
                        "kind": "select_character",
                        "source": {"character_id": "IRONCLAD", "ascension": 7},
                    },
                    {
                        "action_id": "character_select:select:SILENT:a3",
                        "kind": "select_character",
                        "source": {"character_id": "SILENT", "ascension": 3},
                    },
                ],
            },
        }

    def take_action(self, **kwargs) -> dict:
        self.take_calls.append(kwargs)
        return {"status": "completed", "stable": True, "action_id": kwargs["action_id"]}


class CharacterSelectionTests(unittest.TestCase):
    def test_client_select_character_sends_character_id_and_exact_ascension(self) -> None:
        client = Sts2Client()
        with patch.object(client, "_request", return_value={"status": "completed"}) as request:
            client.select_character("IRONCLAD", 10)

        payload = request.call_args.kwargs["payload"]
        self.assertEqual(payload["action"], "select_character")
        self.assertEqual(payload["character_id"], "IRONCLAD")
        self.assertEqual(payload["ascension"], 10)
        self.assertIsNone(payload["option_index"])

    def test_guided_act_forwards_character_and_ascension(self) -> None:
        client = RecordingGuidedClient()
        server = create_server(client=client, tool_profile="guided")
        act = asyncio.run(server.get_tool("act"))

        result = act.fn(action="select_character", character_id="IRONCLAD", ascension=10)

        self.assertEqual(result["status"], "completed")
        action, kwargs = client.calls[-1]
        self.assertEqual(action, "select_character")
        self.assertEqual(kwargs["character_id"], "IRONCLAD")
        self.assertEqual(kwargs["ascension"], 10)

    def test_full_select_character_tool_uses_new_signature(self) -> None:
        client = Sts2Client()
        server = create_server(client=client, tool_profile="full")
        tool = asyncio.run(server.get_tool("select_character"))

        with patch.object(client, "_request", return_value={"status": "completed"}) as request:
            tool.fn(character_id="SILENT", ascension=3)

        payload = request.call_args.kwargs["payload"]
        self.assertEqual(payload["character_id"], "SILENT")
        self.assertEqual(payload["ascension"], 3)

    def test_ai_safe_v2_select_character_matches_both_fields(self) -> None:
        client = RecordingV2Client()
        server = create_server(client=client, tool_profile="ai_safe_v2")
        tool = asyncio.run(server.get_tool("select_character"))

        result = tool.fn(character_id="ironclad", ascension=7)

        self.assertEqual(result["status"], "completed")
        self.assertEqual(client.take_calls[-1]["action_id"], "character_select:select:IRONCLAD:a7")

    def test_ai_safe_v2_select_character_rejects_unavailable_pair(self) -> None:
        client = RecordingV2Client()
        server = create_server(client=client, tool_profile="ai_safe_v2")
        tool = asyncio.run(server.get_tool("select_character"))

        result = tool.fn(character_id="IRONCLAD", ascension=10)

        self.assertEqual(result["status"], "rejected")
        self.assertEqual(result["stop_reason"], "character_or_ascension_not_available")
        self.assertEqual(client.take_calls, [])
