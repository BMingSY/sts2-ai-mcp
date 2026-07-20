from __future__ import annotations

from collections.abc import Iterator

import pytest

from sts2_mcp.reasoning import (
    HARD_MAX_TIME_BUDGET_MS,
    ReasoningInputError,
    WorkBudget,
    evaluate_combat_horizon,
    evaluate_run,
    evaluate_run_decision,
    probability_at_least,
    reject_hidden_order_input,
)
from sts2_mcp.server import _preview_combat_action_plan


def _catalog() -> dict[str, dict]:
    return {
        "STRIKE": {
            "id": "STRIKE",
            "name": "Strike",
            "type": "Attack",
            "target": "AnyEnemy",
            "cost": 1,
            "damage": 6,
        },
        "DEFEND": {
            "id": "DEFEND",
            "name": "Defend",
            "type": "Skill",
            "target": "Self",
            "cost": 1,
            "block": 5,
            "keywords": ["Block"],
        },
        "SHRUG": {
            "id": "SHRUG",
            "name": "Shrug It Off",
            "type": "Skill",
            "target": "Self",
            "cost": 1,
            "block": 8,
            "description": "Gain Block. Draw 1 card.",
            "vars": {"Cards": 1},
        },
        "CURSE": {
            "id": "CURSE",
            "name": "Curse",
            "type": "Curse",
            "target": "None",
            "cost": -1,
            "description": "Unplayable.",
        },
    }


def _run_decision() -> dict:
    catalog = _catalog()
    return {
        "decision_id": "run:f1:reward:1",
        "context": {
            "run": {
                "deck": [
                    {"card_id": "STRIKE", "card_type": "Attack"},
                    {"card_id": "STRIKE", "card_type": "Attack"},
                    {"card_id": "DEFEND", "card_type": "Skill"},
                    {"card_id": "DEFEND", "card_type": "Skill"},
                ]
            }
        },
        "knowledge": {"relevant": {"cards": catalog}},
    }


def _combat_decision() -> dict:
    return {
        "decision_id": "run:f10:combat:t2:1",
        "summary": {
            "current_hp": 12,
            "block": 0,
            "energy": 2,
            "cards_played_this_turn": 0,
        },
        "context": {
            "combat": {
                "cards_played_this_turn": 0,
                "player": {
                    "current_hp": 12,
                    "block": 0,
                    "energy": 2,
                    "stars": 0,
                    "powers": [],
                },
                "hand": [
                    {
                        "card_ref": "card:KILL:1",
                        "card_id": "KILL",
                        "card_type": "Attack",
                        "energy_cost": 1,
                        "star_cost": 0,
                        "rules_text": "Deal 10 damage.",
                        "dynamic_vars": {},
                    },
                    {
                        "card_ref": "card:DEFEND:2",
                        "card_id": "DEFEND",
                        "card_type": "Skill",
                        "energy_cost": 1,
                        "star_cost": 0,
                        "rules_text": "Gain 5 Block.",
                        "dynamic_vars": {},
                    },
                ],
                "enemies": [
                    {
                        "index": 0,
                        "enemy_ref": "enemy:A:1",
                        "enemy_id": "A",
                        "name": "A",
                        "current_hp": 8,
                        "block": 0,
                        "is_alive": True,
                        "powers": [],
                        "intents": [
                            {"intent_type": "Attack", "damage": 10, "hits": 1, "total_damage": 10}
                        ],
                    },
                    {
                        "index": 1,
                        "enemy_ref": "enemy:B:2",
                        "enemy_id": "B",
                        "name": "B",
                        "current_hp": 20,
                        "block": 0,
                        "is_alive": True,
                        "powers": [],
                        "intents": [
                            {"intent_type": "Attack", "damage": 7, "hits": 1, "total_damage": 7}
                        ],
                    },
                ],
            }
        },
        "choices": [
            {
                "action_id": "combat:kill-a",
                "kind": "play_card",
                "source": {
                    "card_ref": "card:KILL:1",
                    "target_ref": "enemy:0",
                    "target_entity_ref": "enemy:A:1",
                },
                "preview": {
                    "preview_complete": True,
                    "energy_cost": 1,
                    "star_cost": 0,
                    "damage": {
                        "targets": [
                            {"target_index": 0, "pre_target_per_hit": 10, "hit_count": 1}
                        ]
                    },
                    "powers_applied": [],
                },
            },
            {
                "action_id": "combat:defend",
                "kind": "play_card",
                "source": {"card_ref": "card:DEFEND:2"},
                "preview": {
                    "preview_complete": True,
                    "energy_cost": 1,
                    "star_cost": 0,
                    "block": {"estimated_gain": 5},
                    "powers_applied": [],
                },
            },
        ],
    }


def _ticks(values: list[float]) -> Iterator[float]:
    yield from values
    while True:
        yield values[-1]


def test_probability_at_least_handles_exact_boundaries() -> None:
    assert probability_at_least(10, 0, 5) == 0
    assert probability_at_least(10, 10, 1) == 1
    assert probability_at_least(10, 2, 5) == pytest.approx(7 / 9, abs=1e-6)
    assert probability_at_least(10, 2, 5, 2) == pytest.approx(2 / 9, abs=1e-6)


def test_run_evaluator_reports_structure_and_candidate_delta_without_ranking() -> None:
    result = evaluate_run_decision(
        _run_decision(), candidate_card_ids=["SHRUG", "CURSE"], horizons=[2, 4]
    )

    assert result["status"] == "complete"
    assert result["mutation_performed"] is False
    assert result["baseline"]["deck_size"] == 4
    assert result["baseline"]["role_counts"]["defense"] == 2
    assert [item["card_id"] for item in result["candidates"]] == ["SHRUG", "CURSE"]
    assert result["candidates"][0]["delta"]["role_counts"]["defense"] == 1
    assert result["candidates"][0]["delta"]["role_counts"]["draw"] == 1
    assert result["candidates"][1]["delta"]["role_counts"]["dead_draw"] == 1
    assert "ranking" not in result
    assert "recommendation" not in result


def test_run_evaluator_reports_unresolved_candidate_instead_of_guessing() -> None:
    result = evaluate_run_decision(_run_decision(), candidate_card_ids=["MISSING"])

    assert result["status"] == "complete"
    assert result["unresolved_candidate_ids"] == ["MISSING"]
    assert result["candidates"][0]["resolved"] is False


def test_run_evaluator_state_budget_returns_partial_without_partial_metrics() -> None:
    result = evaluate_run(
        deck=[{"card_id": "STRIKE"}, {"card_id": "DEFEND"}],
        catalog=_catalog(),
        max_states=1,
    )

    assert result["status"] == "partial"
    assert result["stop_reason"] == "state_budget_exhausted"
    assert result["baseline"] is None


def test_work_budget_clamps_time_and_stops_on_elapsed_clock() -> None:
    clock_values = _ticks([0.0, 0.0, 1.0])
    budget = WorkBudget(99_999, 10, clock=lambda: next(clock_values))

    assert budget.effective_time_ms == HARD_MAX_TIME_BUDGET_MS
    assert budget.consume() is True
    assert budget.consume() is False
    assert budget.stop_reason == "time_budget_exhausted"


def test_hidden_draw_order_fields_are_rejected() -> None:
    with pytest.raises(ReasoningInputError, match="hidden-order field"):
        reject_hidden_order_input({"decision": {}, "draw_pile_order": ["A", "B"]})

    reject_hidden_order_input({"combat": {"piles": {"draw": {"order": "hidden", "count": 5}}}})


def test_run_evaluator_rejects_large_inputs_before_work() -> None:
    with pytest.raises(ReasoningInputError, match="maximum"):
        evaluate_run(deck=[{"card_id": "X"}] * 301)

    with pytest.raises(ReasoningInputError, match="maximum"):
        evaluate_run(deck=[], candidate_card_ids=[str(index) for index in range(17)])


def test_combat_horizon_excludes_killed_attacker_and_keeps_input_order() -> None:
    decision = _combat_decision()
    result = evaluate_combat_horizon(
        decision,
        lines=[
            {
                "label": "defend only",
                "steps": [{"kind": "play_card", "card_ref": "card:DEFEND:2"}],
            },
            {
                "label": "kill (preview stops at decision boundary)",
                "steps": [
                    {
                        "kind": "play_card",
                        "card_ref": "card:KILL:1",
                        "target_entity_ref": "enemy:A:1",
                    },
                    {"kind": "play_card", "card_ref": "card:DEFEND:2"},
                ],
            },
        ],
        previewer=_preview_combat_action_plan,
    )

    assert result["status"] == "complete"
    assert [line["label"] for line in result["lines"]] == [
        "defend only",
        "kill (preview stops at decision boundary)",
    ]
    assert result["lines"][0]["end_turn"]["incoming_damage"] == 17
    assert result["lines"][0]["end_turn"]["hp_after"] == 0
    assert result["lines"][1]["end_turn"]["incoming_damage"] == 7
    assert result["lines"][1]["preview"]["stop_reason"] == "information_boundary_after_step"
    assert result["lines"][1]["end_turn"]["hp_after"] == 5
    assert result["lines"][1]["end_turn"]["excluded_killed_attackers"] == ["enemy:A:1"]
    assert result["mutation_performed"] is False


def test_combat_horizon_marks_incomplete_preview_as_not_proven() -> None:
    decision = _combat_decision()
    decision["choices"][1]["preview"]["preview_complete"] = False
    result = evaluate_combat_horizon(
        decision,
        lines=[
            {
                "label": "incomplete",
                "steps": [{"kind": "play_card", "card_ref": "card:DEFEND:2"}],
            }
        ],
        previewer=_preview_combat_action_plan,
    )

    end_turn = result["lines"][0]["end_turn"]
    assert end_turn["complete"] is False
    assert end_turn["survival_proven_within_scope"] is False


def test_combat_horizon_returns_completed_prefix_on_state_budget() -> None:
    decision = _combat_decision()
    lines = [
        {"label": "one", "steps": [{"kind": "play_card", "card_ref": "card:DEFEND:2"}]},
        {"label": "two", "steps": [{"kind": "play_card", "card_ref": "card:DEFEND:2"}]},
    ]
    result = evaluate_combat_horizon(
        decision,
        lines=lines,
        previewer=_preview_combat_action_plan,
        max_states=4,
    )

    assert result["status"] == "partial"
    assert result["stop_reason"] == "state_budget_exhausted"
    assert result["completed_line_count"] == 1


def test_combat_horizon_rejects_excessive_lines_and_steps() -> None:
    decision = _combat_decision()
    with pytest.raises(ReasoningInputError, match="maximum"):
        evaluate_combat_horizon(
            decision,
            lines=[{"steps": []}] * 9,
            previewer=_preview_combat_action_plan,
        )
    with pytest.raises(ReasoningInputError, match="maximum"):
        evaluate_combat_horizon(
            decision,
            lines=[{"steps": [{}] * 6}],
            previewer=_preview_combat_action_plan,
        )
