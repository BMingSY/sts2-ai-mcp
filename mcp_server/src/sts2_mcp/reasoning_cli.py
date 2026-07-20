"""One-shot JSON CLI for the bounded STS2 reasoning calculators."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

from .reasoning import (
    DEFAULT_MAX_STATES,
    DEFAULT_TIME_BUDGET_MS,
    ReasoningInputError,
    decision_from_payload,
    evaluate_combat_horizon,
    evaluate_run_decision,
    reject_hidden_order_input,
)
from .server import _preview_combat_action_plan


MAX_INPUT_BYTES = 2 * 1024 * 1024


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="sts2-reasoning",
        description=(
            "Bounded, read-only STS2 run and combat calculations. "
            "The commands never connect to or act on the game."
        ),
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Emit JSON (accepted for CLI-harness compatibility; output is always JSON).",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    for name, help_text in (
        ("run-evaluator", "Calculate public deck metrics and visible candidate deltas."),
        ("combat-horizon", "Check several supplied combat lines without executing them."),
    ):
        command = subparsers.add_parser(name, help=help_text)
        command.add_argument(
            "--input",
            type=Path,
            default=None,
            help="JSON request file; omit or use '-' to read stdin.",
        )
        command.add_argument(
            "--time-budget-ms",
            type=int,
            default=None,
            help="Requested cooperative time budget; hard-capped at 500ms.",
        )
        command.add_argument(
            "--max-states",
            type=int,
            default=None,
            help="Requested work-state budget; hard-capped at 4096.",
        )
    return parser


def _read_bytes(path: Path | None) -> bytes:
    if path is None or str(path) == "-":
        raw = sys.stdin.buffer.read(MAX_INPUT_BYTES + 1)
    else:
        if path.stat().st_size > MAX_INPUT_BYTES:
            raise ReasoningInputError(
                f"input exceeds {MAX_INPUT_BYTES} byte limit"
            )
        raw = path.read_bytes()
    if len(raw) > MAX_INPUT_BYTES:
        raise ReasoningInputError(f"input exceeds {MAX_INPUT_BYTES} byte limit")
    return raw


def _load_payload(path: Path | None) -> dict[str, Any]:
    raw = _read_bytes(path)
    try:
        payload = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReasoningInputError(f"input must be valid UTF-8 JSON: {exc}") from exc
    if not isinstance(payload, dict):
        raise ReasoningInputError("top-level JSON input must be an object")
    reject_hidden_order_input(payload)
    return payload


def _override(payload: dict[str, Any], key: str, value: int | None, default: int) -> int:
    if value is not None:
        return value
    raw = payload.get(key, default)
    try:
        return int(raw)
    except (TypeError, ValueError, OverflowError) as exc:
        raise ReasoningInputError(f"{key} must be an integer") from exc


def _run(args: argparse.Namespace) -> dict[str, Any]:
    payload = _load_payload(args.input)
    decision = decision_from_payload(payload)
    time_budget_ms = _override(
        payload, "time_budget_ms", args.time_budget_ms, DEFAULT_TIME_BUDGET_MS
    )
    max_states = _override(payload, "max_states", args.max_states, DEFAULT_MAX_STATES)

    if args.command == "run-evaluator":
        candidates = payload.get("candidate_card_ids", [])
        horizons = payload.get("horizons", [5, 10, 15])
        if not isinstance(candidates, list):
            raise ReasoningInputError("candidate_card_ids must be a list")
        if not isinstance(horizons, list):
            raise ReasoningInputError("horizons must be a list")
        return evaluate_run_decision(
            decision,
            candidate_card_ids=candidates,
            horizons=horizons,
            time_budget_ms=time_budget_ms,
            max_states=max_states,
        )

    lines = payload.get("lines")
    if not isinstance(lines, list):
        raise ReasoningInputError("lines must be a list")
    return evaluate_combat_horizon(
        decision,
        lines=lines,
        previewer=_preview_combat_action_plan,
        time_budget_ms=time_budget_ms,
        max_states=max_states,
    )


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    try:
        result = _run(args)
    except (ReasoningInputError, OSError) as exc:
        print(
            json.dumps(
                {
                    "schema_version": 1,
                    "status": "rejected",
                    "mutation_performed": False,
                    "error": {"type": exc.__class__.__name__, "message": str(exc)},
                },
                ensure_ascii=False,
                sort_keys=True,
            )
        )
        return 2
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":  # pragma: no cover - subprocess entry point
    raise SystemExit(main())
