from __future__ import annotations

import importlib.util
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[2] / "scripts" / "sts2_mcp_manual_driver.py"
SPEC = importlib.util.spec_from_file_location("sts2_mcp_manual_driver", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
DRIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DRIVER)


def _decision() -> dict:
    return {
        "choices": [
            {
                "action_id": "combat:play:0:enemy:0",
                "kind": "play_card",
                "source": {
                    "card_ref": "card:STRIKE:1",
                    "target_entity_ref": "enemy:SLIME:1",
                    "target_ref": "enemy:0",
                },
            },
            {
                "action_id": "combat:play:1",
                "kind": "play_card",
                "source": {"card_ref": "card:DEFEND:2"},
            },
            {
                "action_id": "combat:end_turn",
                "kind": "end_turn",
                "source": {"screen": "COMBAT"},
            },
        ]
    }


def test_batch_converts_current_action_ids_to_stable_plan_steps() -> None:
    steps, error = DRIVER._batch_steps_from_action_ids(
        _decision(),
        ["combat:play:0:enemy:0", "combat:play:1"],
    )

    assert error is None
    assert steps == [
        {
            "kind": "play_card",
            "card_ref": "card:STRIKE:1",
            "target_ref": "enemy:0",
            "target_entity_ref": "enemy:SLIME:1",
        },
        {"kind": "play_card", "card_ref": "card:DEFEND:2"},
    ]


def test_batch_rejects_non_plan_safe_action() -> None:
    steps, error = DRIVER._batch_steps_from_action_ids(
        _decision(),
        ["combat:play:1", "combat:end_turn"],
    )

    assert steps == []
    assert error == "action is not combat-plan safe: combat:end_turn (end_turn)"


def test_batch_requires_multiple_unique_actions() -> None:
    steps, error = DRIVER._batch_steps_from_action_ids(_decision(), ["combat:play:1"])
    assert steps == []
    assert error == "batch requires at least two action ids"

    steps, error = DRIVER._batch_steps_from_action_ids(
        _decision(),
        ["combat:play:1", "combat:play:1"],
    )
    assert steps == []
    assert error == "batch action ids must be unique"
