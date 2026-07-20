from __future__ import annotations

import json
import shutil
import subprocess
import sys
import time
from pathlib import Path


def _resolve_cli() -> list[str]:
    installed = shutil.which("sts2-reasoning")
    if installed:
        return [installed]
    return [sys.executable, "-m", "sts2_mcp.reasoning_cli"]


CLI = _resolve_cli()


def _run(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(CLI + args, capture_output=True, text=True, timeout=3, check=False)


def _run_payload() -> dict:
    return {
        "decision": {
            "decision_id": "run:reward:1",
            "context": {
                "run": {"deck": [{"card_id": "DEFEND", "card_type": "Skill"}]}
            },
            "knowledge": {
                "relevant": {
                    "cards": {
                        "DEFEND": {
                            "id": "DEFEND",
                            "name": "Defend",
                            "type": "Skill",
                            "cost": 1,
                            "block": 5,
                        },
                        "SHRUG": {
                            "id": "SHRUG",
                            "name": "Shrug",
                            "type": "Skill",
                            "cost": 1,
                            "block": 8,
                            "description": "Draw 1 card.",
                        },
                    }
                }
            },
        },
        "candidate_card_ids": ["SHRUG"],
        "horizons": [1, 2],
    }


def _combat_payload() -> dict:
    return {
        "decision": {
            "decision_id": "run:combat:1",
            "summary": {"current_hp": 10, "block": 0, "energy": 1},
            "context": {
                "combat": {
                    "cards_played_this_turn": 0,
                    "player": {
                        "current_hp": 10,
                        "block": 0,
                        "energy": 1,
                        "stars": 0,
                        "powers": [],
                    },
                    "hand": [
                        {
                            "card_ref": "card:DEFEND:1",
                            "card_id": "DEFEND",
                            "card_type": "Skill",
                            "energy_cost": 1,
                            "star_cost": 0,
                            "rules_text": "Gain 5 Block.",
                            "dynamic_vars": {},
                        }
                    ],
                    "enemies": [
                        {
                            "index": 0,
                            "enemy_ref": "enemy:A:1",
                            "enemy_id": "A",
                            "current_hp": 10,
                            "block": 0,
                            "is_alive": True,
                            "powers": [],
                            "intents": [
                                {"intent_type": "Attack", "total_damage": 8}
                            ],
                        }
                    ],
                }
            },
            "choices": [
                {
                    "action_id": "combat:defend",
                    "kind": "play_card",
                    "source": {"card_ref": "card:DEFEND:1"},
                    "preview": {
                        "preview_complete": True,
                        "energy_cost": 1,
                        "star_cost": 0,
                        "block": {"estimated_gain": 5},
                        "powers_applied": [],
                    },
                }
            ],
        },
        "lines": [
            {
                "label": "defend",
                "steps": [{"kind": "play_card", "card_ref": "card:DEFEND:1"}],
            }
        ],
    }


def test_cli_help() -> None:
    result = _run(["--help"])

    assert result.returncode == 0
    assert "run-evaluator" in result.stdout
    assert "combat-horizon" in result.stdout


def test_run_evaluator_cli_json(tmp_path: Path) -> None:
    path = tmp_path / "run.json"
    path.write_text(json.dumps(_run_payload()), encoding="utf-8")

    result = _run(["--json", "run-evaluator", "--input", str(path)])
    data = json.loads(result.stdout)

    assert result.returncode == 0
    assert data["status"] == "complete"
    assert data["candidates"][0]["card_id"] == "SHRUG"
    assert data["mutation_performed"] is False


def test_combat_horizon_cli_json(tmp_path: Path) -> None:
    path = tmp_path / "combat.json"
    path.write_text(json.dumps(_combat_payload()), encoding="utf-8")

    result = _run(["combat-horizon", "--input", str(path)])
    data = json.loads(result.stdout)

    assert result.returncode == 0
    assert data["status"] == "complete"
    assert data["lines"][0]["end_turn"]["hp_after"] == 7
    assert data["mutation_performed"] is False


def test_oversized_request_is_rejected_quickly(tmp_path: Path) -> None:
    payload = _combat_payload()
    payload["lines"] = [{"steps": []}] * 9
    path = tmp_path / "oversized.json"
    path.write_text(json.dumps(payload), encoding="utf-8")

    started = time.monotonic()
    result = _run(["combat-horizon", "--input", str(path)])
    elapsed = time.monotonic() - started
    data = json.loads(result.stdout)

    assert result.returncode == 2
    assert elapsed < 2
    assert data["status"] == "rejected"
    assert data["mutation_performed"] is False
