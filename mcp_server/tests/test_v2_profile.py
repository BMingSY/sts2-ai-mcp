from __future__ import annotations

import asyncio
import json
import os
import tempfile
import unittest
from unittest.mock import patch

from sts2_mcp.server import create_server


class DummyV2Client:
    def __init__(self) -> None:
        self.current_decision = {
            "available": True,
            "decision": {
                "decision_id": "run:f1:combat:t1:abc123",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1, "current_hp": 50, "max_hp": 70},
                "choices": [
                    {
                        "action_id": "combat:end_turn",
                        "kind": "end_turn",
                        "label": "End turn",
                    }
                ],
            },
        }

    def get_health(self) -> dict:
        return {"ok": True}

    def get_current_decision(self, **kwargs) -> dict:
        return self.current_decision

    def wait_for_decision(self, **kwargs) -> dict:
        return self.current_decision

    def take_action(self, **kwargs) -> dict:
        return {
            "action_id": kwargs["action_id"],
            "status": "completed",
            "stable": True,
        }


class V2ProfileTests(unittest.TestCase):
    def test_default_profile_is_ai_safe_v2(self) -> None:
        server = create_server(client=DummyV2Client())

        tools = asyncio.run(server.list_tools())
        tool_names = {tool.name for tool in tools}

        self.assertEqual(
            tool_names,
            {
                "health_check",
                "get_current_decision",
                "wait_for_decision",
                "take_action",
                "lookup_game_data",
                "append_decision_note",
            },
        )

    def test_ai_safe_v2_exposes_only_v2_tools(self) -> None:
        server = create_server(client=DummyV2Client(), tool_profile="ai_safe_v2")

        tools = asyncio.run(server.list_tools())
        tool_names = {tool.name for tool in tools}

        self.assertEqual(
            tool_names,
            {
                "health_check",
                "get_current_decision",
                "wait_for_decision",
                "take_action",
                "lookup_game_data",
                "append_decision_note",
            },
        )
        self.assertNotIn("act", tool_names)
        self.assertNotIn("get_game_state", tool_names)

    def test_take_action_appends_decision_log(self) -> None:
        client = DummyV2Client()
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                get_current = asyncio.run(server.get_tool("get_current_decision"))
                take_action = asyncio.run(server.get_tool("take_action"))

                get_current.fn()
                result = take_action.fn(
                    decision_id="run:f1:combat:t1:abc123",
                    action_id="combat:end_turn",
                    client_note="forced by test",
                )
                with open(result["logging"]["path"], "r", encoding="utf-8") as f:
                    row = json.loads(f.readline())

        self.assertEqual(result["status"], "completed")
        self.assertTrue(result["logging"]["ok"])
        self.assertEqual(row["decision_id"], "run:f1:combat:t1:abc123")
        self.assertEqual(row["action_id"], "combat:end_turn")
        self.assertEqual(row["selected_label"], "End turn")
        self.assertEqual(row["client_note"], "forced by test")

    def test_lookup_game_data_filters_fields(self) -> None:
        server = create_server(client=DummyV2Client(), tool_profile="ai_safe_v2")
        tool = asyncio.run(server.get_tool("lookup_game_data"))

        with patch(
            "sts2_mcp.server._ensure_game_data_index",
            return_value={"ABRASIVE": {"id": "ABRASIVE", "name": "Abrasive", "cost": 2}},
        ):
            result = tool.fn(
                items=[{"collection": "cards", "id": "abrasive"}],
                fields=["id", "name"],
            )

        self.assertEqual(result["items"]["cards:abrasive"], {"id": "ABRASIVE", "name": "Abrasive"})


if __name__ == "__main__":
    unittest.main()
