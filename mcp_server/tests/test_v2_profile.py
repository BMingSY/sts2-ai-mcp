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


def _choice(action_id: str, kind: str, **source: object) -> dict:
    return {
        "action_id": action_id,
        "kind": kind,
        "label": action_id,
        "source": source,
    }


class SequentialPlanClient(DummyV2Client):
    def __init__(self, decisions: list[dict]) -> None:
        self.decisions = decisions
        self.position = 0
        self.take_calls: list[dict] = []
        self.current_decision = {"available": True, "decision": decisions[0]}

    def get_current_decision(self, **kwargs) -> dict:
        return {"available": True, "decision": self.decisions[self.position]}

    def wait_for_decision(self, **kwargs) -> dict:
        return {"available": True, "decision": self.decisions[self.position]}

    def take_action(self, **kwargs) -> dict:
        current = self.decisions[self.position]
        self.take_calls.append(kwargs)
        if kwargs["decision_id"] != current["decision_id"]:
            raise AssertionError("plan used a stale decision")
        if kwargs["action_id"] not in {choice["action_id"] for choice in current["choices"]}:
            raise AssertionError("plan used an action outside the current choices")

        self.position += 1
        next_decision = self.decisions[self.position]
        return {
            "action_id": kwargs["action_id"],
            "status": "completed",
            "stable": True,
            "next_decision": next_decision,
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
                    "execute_action_plan",
                    "select_cards",
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
                    "execute_action_plan",
                    "select_cards",
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
                    content = f.read()
                with open(result["logging"]["mcp_log_path"], "r", encoding="utf-8") as f:
                    mcp_row = json.loads(f.readline())

        self.assertEqual(result["status"], "completed")
        self.assertTrue(result["logging"]["ok"])
        self.assertTrue(result["logging"]["path"].endswith("run123.md"))
        self.assertTrue(result["logging"]["mcp_log_path"].endswith("run123.jsonl"))
        self.assertIn("# STS2 Decision Log", content)
        self.assertIn("MCP does not translate or rewrite it", content)
        self.assertIn("| 1 | `combat:end_turn` | forced by test | completed |", content)
        self.assertNotIn("HP 50/70", content)
        self.assertNotIn("End turn", content)
        self.assertEqual(mcp_row["decision_id"], "run:f1:combat:t1:abc123")
        self.assertEqual(mcp_row["action_id"], "combat:end_turn")
        self.assertEqual(mcp_row["selected_label"], "End turn")
        self.assertEqual(mcp_row["client_note"], "forced by test")
        self.assertEqual(mcp_row["summary"], {"floor": 1, "current_hp": 50, "max_hp": 70})

    def test_take_action_avoids_mixing_old_decision_log_format(self) -> None:
        client = DummyV2Client()
        with tempfile.TemporaryDirectory() as tmpdir:
            run_logs = os.path.join(tmpdir, "run_logs")
            os.makedirs(run_logs)
            with open(os.path.join(run_logs, "run123.md"), "w", encoding="utf-8") as f:
                f.write("| Step | Floor/Screen | State Snapshot | Decision | Reason | Result |\n")

            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                get_current = asyncio.run(server.get_tool("get_current_decision"))
                take_action = asyncio.run(server.get_tool("take_action"))

                get_current.fn()
                result = take_action.fn(
                    decision_id="run:f1:combat:t1:abc123",
                    action_id="combat:end_turn",
                    client_note="测试旧格式隔离",
                )

        self.assertTrue(result["logging"]["path"].endswith("run123.decision.md"))

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

    def test_select_cards_executes_each_fresh_decision_then_confirms(self) -> None:
        decisions = [
            {
                "decision_id": "run:f1:combat_selection:t1:one",
                "run_id": "RUN123",
                "phase": "combat_selection",
                "screen": "CARD_SELECTION",
                "summary": {"floor": 1},
                "context": {"selection": {"selected_count": 0}},
                "choices": [
                    _choice("selection:select:0", "select_deck_card", card_ref="card:A:1"),
                    _choice("selection:select:1", "select_deck_card", card_ref="card:B:2"),
                ],
            },
            {
                "decision_id": "run:f1:combat_selection:t1:two",
                "run_id": "RUN123",
                "phase": "combat_selection",
                "screen": "CARD_SELECTION",
                "summary": {"floor": 1},
                "context": {"selection": {"selected_count": 1}},
                "choices": [
                    _choice("selection:select:1", "select_deck_card", card_ref="card:B:2"),
                ],
            },
            {
                "decision_id": "run:f1:combat_selection:t1:three",
                "run_id": "RUN123",
                "phase": "combat_selection",
                "screen": "CARD_SELECTION",
                "summary": {"floor": 1},
                "context": {"selection": {"selected_count": 2}},
                "choices": [
                    _choice("selection:confirm", "confirm_selection"),
                ],
            },
            {
                "decision_id": "run:f1:combat:t1:four",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1},
                "context": {"combat": {"hand": []}},
                "choices": [_choice("combat:end_turn", "end_turn")],
            },
        ]
        client = SequentialPlanClient(decisions)
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                select_cards = asyncio.run(server.get_tool("select_cards"))
                result = select_cards.fn(
                    decision_id=decisions[0]["decision_id"],
                    card_refs=["card:A:1", "card:B:2"],
                    confirm=True,
                    client_note="discard two cards",
                    plan_id="selection-plan-1",
                )

        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["executed_count"], 3)
        self.assertEqual(len(client.take_calls), 3)
        self.assertEqual(result["next_decision"]["phase"], "combat")

    def test_combat_plan_stops_when_a_card_is_drawn(self) -> None:
        decisions = [
            {
                "decision_id": "run:f1:combat:t1:one",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1},
                "context": {
                    "combat": {
                        "hand": [
                            {"card_ref": "card:A:1"},
                            {"card_ref": "card:B:2"},
                        ]
                    }
                },
                "choices": [
                    _choice(
                        "combat:play:0:enemy:0",
                        "play_card",
                        card_ref="card:A:1",
                        target_entity_ref="enemy:X:1",
                    ),
                    _choice(
                        "combat:play:1:enemy:0",
                        "play_card",
                        card_ref="card:B:2",
                        target_entity_ref="enemy:X:1",
                    ),
                ],
            },
            {
                "decision_id": "run:f1:combat:t1:two",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1},
                "context": {
                    "combat": {
                        "hand": [
                            {"card_ref": "card:B:2"},
                            {"card_ref": "card:C:3"},
                        ]
                    }
                },
                "choices": [
                    _choice(
                        "combat:play:0:enemy:0",
                        "play_card",
                        card_ref="card:B:2",
                        target_entity_ref="enemy:X:1",
                    )
                ],
            },
        ]
        client = SequentialPlanClient(decisions)
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                execute_plan = asyncio.run(server.get_tool("execute_action_plan"))
                result = execute_plan.fn(
                    decision_id=decisions[0]["decision_id"],
                    steps=[
                        {
                            "kind": "play_card",
                            "card_ref": "card:A:1",
                            "target_entity_ref": "enemy:X:1",
                        },
                        {
                            "kind": "play_card",
                            "card_ref": "card:B:2",
                            "target_entity_ref": "enemy:X:1",
                        },
                    ],
                    mode="strict",
                    client_note="two-card line",
                    plan_id="combat-plan-1",
                )

        self.assertEqual(result["status"], "stopped")
        self.assertEqual(result["stop_reason"], "combat_hand_gained_or_returned_cards")
        self.assertEqual(result["executed_count"], 1)
        self.assertEqual(len(client.take_calls), 1)

    def test_combat_plan_continues_when_only_the_played_card_leaves_hand(self) -> None:
        decisions = [
            {
                "decision_id": "run:f1:combat:t1:one",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1},
                "context": {
                    "combat": {
                        "hand": [
                            {"card_ref": "card:A:1"},
                            {"card_ref": "card:B:2"},
                        ]
                    }
                },
                "choices": [
                    _choice("combat:play:0", "play_card", card_ref="card:A:1"),
                    _choice("combat:play:1", "play_card", card_ref="card:B:2"),
                ],
            },
            {
                "decision_id": "run:f1:combat:t1:two",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1},
                "context": {"combat": {"hand": [{"card_ref": "card:B:2"}]}},
                "choices": [_choice("combat:play:0", "play_card", card_ref="card:B:2")],
            },
            {
                "decision_id": "run:f1:combat:t1:three",
                "run_id": "RUN123",
                "phase": "combat",
                "screen": "COMBAT",
                "summary": {"floor": 1},
                "context": {"combat": {"hand": []}},
                "choices": [_choice("combat:end_turn", "end_turn")],
            },
        ]
        client = SequentialPlanClient(decisions)
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                execute_plan = asyncio.run(server.get_tool("execute_action_plan"))
                result = execute_plan.fn(
                    decision_id=decisions[0]["decision_id"],
                    steps=[
                        {"kind": "play_card", "card_ref": "card:A:1"},
                        {"kind": "play_card", "card_ref": "card:B:2"},
                    ],
                    mode="strict",
                    client_note="deterministic two-card line",
                    plan_id="combat-plan-success",
                )

        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["executed_count"], 2)
        self.assertEqual(len(client.take_calls), 2)
        self.assertEqual(result["next_decision"]["decision_id"], decisions[2]["decision_id"])

    def test_plan_id_replay_does_not_execute_actions_twice(self) -> None:
        decisions = [
            {
                "decision_id": "run:f1:combat_selection:t1:one",
                "run_id": "RUN123",
                "phase": "combat_selection",
                "screen": "CARD_SELECTION",
                "summary": {"floor": 1},
                "context": {"selection": {}},
                "choices": [_choice("selection:select:0", "select_deck_card", card_ref="card:A:1")],
            },
            {
                "decision_id": "run:f1:combat_selection:t1:two",
                "run_id": "RUN123",
                "phase": "combat_selection",
                "screen": "CARD_SELECTION",
                "summary": {"floor": 1},
                "context": {"selection": {}},
                "choices": [_choice("selection:confirm", "confirm_selection")],
            },
        ]
        client = SequentialPlanClient(decisions)
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                execute_plan = asyncio.run(server.get_tool("execute_action_plan"))
                kwargs = {
                    "decision_id": decisions[0]["decision_id"],
                    "steps": [{"kind": "select_deck_card", "card_ref": "card:A:1"}],
                    "mode": "strict",
                    "client_note": "select one",
                    "plan_id": "idempotent-plan",
                }
                first = execute_plan.fn(**kwargs)
                second = execute_plan.fn(**kwargs)

        self.assertEqual(first["status"], "completed")
        self.assertTrue(second["idempotent_replay"])
        self.assertEqual(len(client.take_calls), 1)


if __name__ == "__main__":
    unittest.main()
