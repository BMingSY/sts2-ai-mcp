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
        self.search_calls: list[dict] = []
        self.list_id_calls: list[dict] = []
        self.take_calls: list[dict] = []
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
        self.take_calls.append(kwargs)
        return {
            "action_id": kwargs["action_id"],
            "status": "completed",
            "stable": True,
        }

    def search_game_data(self, **kwargs) -> dict:
        self.search_calls.append(kwargs)
        return {"query": kwargs["query"], "matches": []}

    def list_model_ids(self, **kwargs) -> dict:
        self.list_id_calls.append(kwargs)
        return {"collection": kwargs["collection"], "items": []}


class VersionedDataClient(DummyV2Client):
    def __init__(self) -> None:
        super().__init__()
        self.export_calls = 0
        self.current_kwargs: dict = {}

    def get_health(self) -> dict:
        return {"game_version": "v-test.1", "mod_version": "0.1.0"}

    def export_game_data(self) -> dict:
        self.export_calls += 1
        return {
            "collections": {
                "cards": {
                    "BASH": {"id": "BASH", "name": "Bash", "description": "Apply Vulnerable.", "cost": 2}
                },
                "monsters": {},
                "powers": {},
                "relics": {},
                "potions": {},
                "events": {},
            },
            "metadata": {
                "game_version": "v-test.1",
                "mod_version": "0.1.0",
                "exported_at_utc": "2026-07-11T00:00:00Z",
            },
        }

    def get_current_decision(self, **kwargs) -> dict:
        self.current_kwargs = kwargs
        return {
            "available": True,
            "decision": {
                **self.current_decision["decision"],
                "context": {"combat": {"hand": [{"card_id": "BASH"}]}},
            },
        }


class PendingActionClient(DummyV2Client):
    def __init__(self) -> None:
        super().__init__()
        self.wait_calls: list[dict] = []
        self.next_decision = {
            **self.current_decision["decision"],
            "decision_id": "run:f1:combat:t2:def456",
            "summary": {"floor": 1, "current_hp": 44, "max_hp": 70},
        }

    def take_action(self, **kwargs) -> dict:
        return {
            "action_id": kwargs["action_id"],
            "status": "pending",
            "stable": False,
            "previous_decision_id": kwargs["decision_id"],
            "next_decision": None,
        }

    def wait_for_decision(self, **kwargs) -> dict:
        self.wait_calls.append(kwargs)
        return {"available": True, "decision": self.next_decision}


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
                "preview_action",
                "preview_action_plan",
                "run_evaluator",
                "combat_horizon",
                "take_action",
                "get_action_trace",
                "execute_action_plan",
                "select_cards",
                "select_character",
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
                "preview_action",
                "preview_action_plan",
                "run_evaluator",
                "combat_horizon",
                "take_action",
                "get_action_trace",
                "execute_action_plan",
                "select_cards",
                "select_character",
                "lookup_game_data",
                "append_decision_note",
            },
        )
        self.assertNotIn("act", tool_names)
        self.assertNotIn("get_game_state", tool_names)

    def test_model_discovery_tools_are_debug_gated(self) -> None:
        client = DummyV2Client()
        with patch.dict(os.environ, {"STS2_ENABLE_DEBUG_ACTIONS": "1"}):
            server = create_server(client=client, tool_profile="ai_safe_v2")
            tool_names = {tool.name for tool in asyncio.run(server.list_tools())}
            search = asyncio.run(server.get_tool("search_game_data"))
            list_ids = asyncio.run(server.get_tool("list_model_ids"))

            search_result = search.fn(query="queen", collections="monsters, encounters", limit=10)
            ids_result = list_ids.fn(collection="enchantments", query="ad", offset=1, limit=20)

        self.assertIn("search_game_data", tool_names)
        self.assertIn("list_model_ids", tool_names)
        self.assertEqual(search_result["query"], "queen")
        self.assertEqual(client.search_calls[-1]["collections"], ["monsters", "encounters"])
        self.assertEqual(ids_result["collection"], "enchantments")
        self.assertEqual(client.list_id_calls[-1]["query"], "ad")

    def test_reasoning_tools_use_cached_decision_without_actions(self) -> None:
        client = DummyV2Client()
        client.current_decision["decision"] = {
            **client.current_decision["decision"],
            "summary": {
                "floor": 1,
                "current_hp": 10,
                "max_hp": 70,
                "block": 0,
                "energy": 1,
                "cards_played_this_turn": 0,
            },
            "context": {
                "run": {
                    "deck": [
                        {"card_id": "DEFEND", "card_type": "Skill", "keywords": ["Block"]}
                    ]
                },
                "combat": {
                    "cards_played_this_turn": 0,
                    "player": {
                        "current_hp": 10,
                        "block": 0,
                        "energy": 1,
                        "stars": 0,
                        "powers": [],
                    },
                    "hand": [],
                    "enemies": [],
                },
            },
        }
        server = create_server(client=client, tool_profile="ai_safe_v2")
        current_tool = asyncio.run(server.get_tool("get_current_decision"))
        run_tool = asyncio.run(server.get_tool("run_evaluator"))
        combat_tool = asyncio.run(server.get_tool("combat_horizon"))

        current_tool.fn()
        run_result = run_tool.fn(decision_id="run:f1:combat:t1:abc123")
        combat_result = combat_tool.fn(
            decision_id="run:f1:combat:t1:abc123",
            lines=[{"label": "invalid but read-only", "steps": [{"kind": "play_card"}]}],
        )

        self.assertEqual(run_result["status"], "complete")
        self.assertEqual(combat_result["status"], "complete")
        self.assertFalse(run_result["mutation_performed"])
        self.assertFalse(combat_result["mutation_performed"])
        self.assertEqual(client.take_calls, [])

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

    def test_take_action_waits_through_pending_until_next_decision(self) -> None:
        client = PendingActionClient()
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_AGENT_KNOWLEDGE_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                get_current = asyncio.run(server.get_tool("get_current_decision"))
                take_action = asyncio.run(server.get_tool("take_action"))

                get_current.fn()
                result = take_action.fn(
                    decision_id="run:f1:combat:t1:abc123",
                    action_id="combat:end_turn",
                    client_note="wait for enemy turn",
                )

        self.assertEqual(result["status"], "completed")
        self.assertTrue(result["stable"])
        self.assertEqual(result["next_decision"]["decision_id"], "run:f1:combat:t2:def456")
        self.assertEqual(len(client.wait_calls), 1)
        self.assertEqual(client.wait_calls[0]["after_decision_id"], "run:f1:combat:t1:abc123")

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

    def test_lookup_game_data_exports_once_then_uses_versioned_local_snapshot(self) -> None:
        client = VersionedDataClient()
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_GAME_DATA_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                lookup = asyncio.run(server.get_tool("lookup_game_data"))
                first = lookup.fn(items=[{"collection": "cards", "id": "BASH"}], fields=["id", "name"])
                second = lookup.fn(items=[{"collection": "cards", "id": "bash"}], fields=["cost"])

        self.assertEqual(first["items"]["cards:BASH"], {"id": "BASH", "name": "Bash"})
        self.assertEqual(second["items"]["cards:bash"], {"cost": 2})
        self.assertEqual(first["metadata"]["data_source"], "mcp_versioned_cache")
        self.assertEqual(client.export_calls, 1)

    def test_current_decision_uses_local_knowledge_without_live_hydration(self) -> None:
        client = VersionedDataClient()
        with tempfile.TemporaryDirectory() as tmpdir:
            with patch.dict(os.environ, {"STS2_GAME_DATA_DIR": tmpdir}):
                server = create_server(client=client, tool_profile="ai_safe_v2")
                current = asyncio.run(server.get_tool("get_current_decision"))
                result = current.fn(include_relevant_game_data=True)

        self.assertFalse(client.current_kwargs["include_relevant_game_data"])
        self.assertEqual(result["decision"]["knowledge"]["metadata"]["data_source"], "mcp_versioned_cache")
        self.assertEqual(result["decision"]["knowledge"]["relevant"]["cards"]["BASH"]["name"], "Bash")

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

    def test_select_cards_can_confirm_an_empty_selection(self) -> None:
        decisions = [
            {
                "decision_id": "run:f1:combat_selection:t1:empty",
                "run_id": "RUN123",
                "phase": "combat_selection",
                "screen": "CARD_SELECTION",
                "summary": {"floor": 1},
                "context": {
                    "selection": {
                        "min_select": 0,
                        "max_select": 2,
                        "selected_count": 0,
                        "can_confirm": True,
                    }
                },
                "choices": [_choice("selection:confirm", "confirm_selection")],
            },
            {
                "decision_id": "run:f1:combat:t1:done",
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
                    card_refs=[],
                    confirm=True,
                    client_note="return no cards",
                    plan_id="empty-selection-plan",
                )

        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["executed_count"], 1)
        self.assertEqual(len(client.take_calls), 1)
        self.assertEqual(client.take_calls[0]["action_id"], "selection:confirm")
        self.assertEqual(result["next_decision"]["phase"], "combat")

    def test_select_cards_rejects_an_empty_no_op(self) -> None:
        server = create_server(client=DummyV2Client(), tool_profile="ai_safe_v2")
        select_cards = asyncio.run(server.get_tool("select_cards"))

        result = select_cards.fn(
            decision_id="run:f1:combat:t1:abc123",
            card_refs=[],
            confirm=False,
        )

        self.assertEqual(result["status"], "rejected")
        self.assertEqual(result["stop_reason"], "selection_plan_requires_cards_or_confirmation")

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

    def test_preview_action_plan_folds_direct_damage_through_shadow_state(self) -> None:
        attack_a = _choice(
            "combat:play:0:enemy:0",
            "play_card",
            card_ref="card:A:1",
            target_entity_ref="enemy:X:1",
        )
        attack_a["preview"] = {
            "preview_complete": True,
            "energy_cost": 1,
            "star_cost": 0,
            "damage": {
                "targets": [
                    {"target_index": 0, "pre_target_per_hit": 8, "final_per_hit": 8, "hit_count": 1}
                ]
            },
            "powers_applied": [],
            "unmodeled_effects": [],
        }
        attack_b = _choice(
            "combat:play:1:enemy:0",
            "play_card",
            card_ref="card:B:2",
            target_entity_ref="enemy:X:1",
        )
        attack_b["preview"] = attack_a["preview"]
        decision = {
            "decision_id": "run:f1:combat:t1:preview",
            "run_id": "RUN123",
            "phase": "combat",
            "screen": "COMBAT",
            "summary": {"floor": 1, "energy": 2, "cards_played_this_turn": 0},
            "context": {
                "combat": {
                    "player": {"energy": 2, "stars": 0, "block": 0, "current_hp": 50, "powers": []},
                    "cards_played_this_turn": 0,
                    "hand": [
                        {"card_ref": "card:A:1", "energy_cost": 1, "star_cost": 0, "rules_text": "Deal 8 damage."},
                        {"card_ref": "card:B:2", "energy_cost": 1, "star_cost": 0, "rules_text": "Deal 8 damage."},
                    ],
                    "enemies": [
                        {
                            "index": 0,
                            "enemy_ref": "enemy:X:1",
                            "enemy_id": "X",
                            "name": "Target",
                            "current_hp": 30,
                            "block": 5,
                            "is_alive": True,
                            "powers": [],
                        }
                    ],
                }
            },
            "choices": [attack_a, attack_b],
        }
        client = SequentialPlanClient([decision])
        server = create_server(client=client, tool_profile="ai_safe_v2")
        preview_plan = asyncio.run(server.get_tool("preview_action_plan"))

        result = preview_plan.fn(
            decision_id=decision["decision_id"],
            steps=[
                {"kind": "play_card", "card_ref": "card:A:1", "target_entity_ref": "enemy:X:1"},
                {"kind": "play_card", "card_ref": "card:B:2", "target_entity_ref": "enemy:X:1"},
            ],
        )

        self.assertTrue(result["executable_all"])
        self.assertTrue(result["effects_complete"])
        self.assertEqual(result["projected"]["energy"], 0)
        self.assertEqual(result["projected"]["enemies"][0]["block"], 0)
        self.assertEqual(result["projected"]["enemies"][0]["hp"], 19)
        self.assertEqual(result["aggregate"]["hp_damage_by_target"]["enemy:X:1"], 11)

    def test_execute_action_plan_preflight_stops_before_spending_energy(self) -> None:
        choices = []
        hand = []
        for index, (card_ref, cost, block) in enumerate(
            [("card:FLAME_BARRIER:1", 2, 16), ("card:DEFEND:2", 1, 8), ("card:BODY_SLAM:3", 1, 0)]
        ):
            choice = _choice(f"combat:play:{index}", "play_card", card_ref=card_ref)
            choice["preview"] = {
                "preview_complete": True,
                "energy_cost": cost,
                "star_cost": 0,
                "block": {"estimated_gain": block} if block else None,
                "powers_applied": [],
                "unmodeled_effects": [],
            }
            choices.append(choice)
            hand.append(
                {
                    "card_ref": card_ref,
                    "energy_cost": cost,
                    "star_cost": 0,
                    "rules_text": "Gain Block." if block else "Deal damage equal to Block.",
                }
            )
        decision = {
            "decision_id": "run:f1:combat:t1:energy",
            "run_id": "RUN123",
            "phase": "combat",
            "screen": "COMBAT",
            "summary": {"floor": 1, "energy": 3, "cards_played_this_turn": 0},
            "context": {
                "combat": {
                    "player": {"energy": 3, "stars": 0, "block": 0, "current_hp": 50, "powers": []},
                    "cards_played_this_turn": 0,
                    "hand": hand,
                    "enemies": [],
                }
            },
            "choices": choices,
        }
        client = SequentialPlanClient([decision])
        server = create_server(client=client, tool_profile="ai_safe_v2")
        execute_plan = asyncio.run(server.get_tool("execute_action_plan"))

        result = execute_plan.fn(
            decision_id=decision["decision_id"],
            steps=[
                {"kind": "play_card", "card_ref": "card:FLAME_BARRIER:1"},
                {"kind": "play_card", "card_ref": "card:DEFEND:2"},
                {"kind": "play_card", "card_ref": "card:BODY_SLAM:3"},
            ],
            mode="strict",
            client_note="invalid four-energy line",
        )

        self.assertEqual(result["status"], "stopped")
        self.assertEqual(result["stop_reason"], "not_enough_energy")
        self.assertEqual(result["executed_count"], 0)
        self.assertEqual(result["preflight"]["valid_prefix_count"], 2)
        self.assertEqual(client.take_calls, [])

    def test_preview_action_plan_folds_simple_energy_gain_and_hp_loss(self) -> None:
        bloodletting = _choice("combat:play:0", "play_card", card_ref="card:BLOODLETTING:1")
        bloodletting["preview"] = {
            "preview_complete": True,
            "card_id": "BLOODLETTING",
            "energy_cost": 0,
            "star_cost": 0,
            "powers_applied": [],
            "unmodeled_effects": [],
        }
        attack = _choice(
            "combat:play:1:enemy:0",
            "play_card",
            card_ref="card:EXPENSIVE:2",
            target_entity_ref="enemy:X:1",
        )
        attack["preview"] = {
            "preview_complete": True,
            "card_id": "EXPENSIVE",
            "energy_cost": 4,
            "star_cost": 0,
            "damage": {"targets": [{"target_index": 0, "pre_target_per_hit": 5, "hit_count": 1}]},
            "powers_applied": [],
            "unmodeled_effects": [],
        }
        decision = {
            "decision_id": "run:f1:combat:t1:resources",
            "run_id": "RUN123",
            "phase": "combat",
            "screen": "COMBAT",
            "summary": {"floor": 1, "energy": 3, "cards_played_this_turn": 0},
            "context": {
                "combat": {
                    "player": {"energy": 3, "stars": 0, "block": 0, "current_hp": 50, "powers": []},
                    "cards_played_this_turn": 0,
                    "hand": [
                        {
                            "card_ref": "card:BLOODLETTING:1",
                            "card_id": "BLOODLETTING",
                            "energy_cost": 0,
                            "star_cost": 0,
                            "rules_text": "Lose 3 HP. Gain 2 Energy.",
                            "dynamic_vars": {
                                "Energy": {"preview_value": 2},
                                "HpLoss": {"preview_value": 3},
                            },
                        },
                        {
                            "card_ref": "card:EXPENSIVE:2",
                            "card_id": "EXPENSIVE",
                            "energy_cost": 4,
                            "star_cost": 0,
                            "rules_text": "Deal 5 damage.",
                        },
                    ],
                    "enemies": [
                        {
                            "index": 0,
                            "enemy_ref": "enemy:X:1",
                            "current_hp": 20,
                            "block": 0,
                            "is_alive": True,
                            "powers": [],
                        }
                    ],
                }
            },
            "choices": [bloodletting, attack],
        }
        server = create_server(client=SequentialPlanClient([decision]), tool_profile="ai_safe_v2")
        preview_plan = asyncio.run(server.get_tool("preview_action_plan"))

        result = preview_plan.fn(
            decision_id=decision["decision_id"],
            steps=[
                {"kind": "play_card", "card_ref": "card:BLOODLETTING:1"},
                {
                    "kind": "play_card",
                    "card_ref": "card:EXPENSIVE:2",
                    "target_entity_ref": "enemy:X:1",
                },
            ],
        )

        self.assertTrue(result["executable_all"])
        self.assertEqual(result["projected"]["energy"], 1)
        self.assertEqual(result["projected"]["player_hp"], 47)
        self.assertEqual(result["steps"][0]["energy_gain"], 2)
        self.assertEqual(result["steps"][0]["hp_loss"], 3)

    def test_preview_action_plan_recomputes_body_slam_from_projected_block(self) -> None:
        defend = _choice("combat:play:0", "play_card", card_ref="card:DEFEND:1")
        defend["preview"] = {
            "preview_complete": True,
            "card_id": "DEFEND_IRONCLAD",
            "energy_cost": 1,
            "star_cost": 0,
            "block": {"estimated_gain": 8},
            "powers_applied": [],
            "unmodeled_effects": [],
        }
        body_slam = _choice(
            "combat:play:1:enemy:0",
            "play_card",
            card_ref="card:BODY_SLAM:2",
            target_entity_ref="enemy:X:1",
        )
        body_slam["preview"] = {
            "preview_complete": True,
            "card_id": "BODY_SLAM",
            "energy_cost": 1,
            "star_cost": 0,
            "damage": {"targets": [{"target_index": 0, "pre_target_per_hit": 0, "hit_count": 1}]},
            "powers_applied": [],
            "unmodeled_effects": [],
        }
        decision = {
            "decision_id": "run:f1:combat:t1:body-slam",
            "run_id": "RUN123",
            "phase": "combat",
            "screen": "COMBAT",
            "summary": {"floor": 1, "energy": 2, "cards_played_this_turn": 0},
            "context": {
                "combat": {
                    "player": {"energy": 2, "stars": 0, "block": 0, "current_hp": 50, "powers": []},
                    "cards_played_this_turn": 0,
                    "hand": [
                        {
                            "card_ref": "card:DEFEND:1",
                            "card_id": "DEFEND_IRONCLAD",
                            "energy_cost": 1,
                            "star_cost": 0,
                            "rules_text": "Gain 8 Block.",
                        },
                        {
                            "card_ref": "card:BODY_SLAM:2",
                            "card_id": "BODY_SLAM",
                            "energy_cost": 1,
                            "star_cost": 0,
                            "rules_text": "Deal damage equal to your current Block.",
                        },
                    ],
                    "enemies": [
                        {
                            "index": 0,
                            "enemy_ref": "enemy:X:1",
                            "current_hp": 20,
                            "block": 0,
                            "is_alive": True,
                            "powers": [],
                        }
                    ],
                }
            },
            "choices": [defend, body_slam],
        }
        server = create_server(client=SequentialPlanClient([decision]), tool_profile="ai_safe_v2")
        preview_plan = asyncio.run(server.get_tool("preview_action_plan"))

        result = preview_plan.fn(
            decision_id=decision["decision_id"],
            steps=[
                {"kind": "play_card", "card_ref": "card:DEFEND:1"},
                {
                    "kind": "play_card",
                    "card_ref": "card:BODY_SLAM:2",
                    "target_entity_ref": "enemy:X:1",
                },
            ],
        )

        self.assertTrue(result["effects_complete"])
        self.assertEqual(result["projected"]["player_block"], 8)
        self.assertEqual(result["projected"]["enemies"][0]["hp"], 12)
        self.assertEqual(result["steps"][1]["hp_damage_by_target"]["enemy:X:1"], 8)

    def test_preview_action_plan_applies_sloth_card_limit(self) -> None:
        choices = []
        hand = []
        for index in range(3):
            card_ref = f"card:ZERO:{index}"
            choice = _choice(f"combat:play:{index}", "play_card", card_ref=card_ref)
            choice["preview"] = {
                "preview_complete": True,
                "energy_cost": 0,
                "star_cost": 0,
                "powers_applied": [],
                "unmodeled_effects": [],
            }
            choices.append(choice)
            hand.append({"card_ref": card_ref, "energy_cost": 0, "star_cost": 0, "rules_text": ""})
        decision = {
            "decision_id": "run:f1:combat:t1:sloth",
            "run_id": "RUN123",
            "phase": "combat",
            "screen": "COMBAT",
            "summary": {"floor": 1, "energy": 3, "cards_played_this_turn": 1},
            "context": {
                "combat": {
                    "player": {
                        "energy": 3,
                        "stars": 0,
                        "block": 0,
                        "current_hp": 50,
                        "powers": [{"power_id": "SLOTH_POWER", "amount": 3}],
                    },
                    "cards_played_this_turn": 1,
                    "hand": hand,
                    "enemies": [],
                }
            },
            "choices": choices,
        }
        client = SequentialPlanClient([decision])
        server = create_server(client=client, tool_profile="ai_safe_v2")
        preview_plan = asyncio.run(server.get_tool("preview_action_plan"))

        result = preview_plan.fn(
            decision_id=decision["decision_id"],
            steps=[{"kind": "play_card", "card_ref": card["card_ref"]} for card in hand],
        )

        self.assertTrue(result["known_infeasible"])
        self.assertEqual(result["stop_reason"], "card_play_limit_reached")
        self.assertEqual(result["stopped_before_step"], 2)
        self.assertEqual(result["valid_prefix_count"], 2)

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
