#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import shlex
import sys
from datetime import datetime
from pathlib import Path
from typing import Any

from fastmcp import Client


REPO = Path(__file__).resolve().parents[1]
DEFAULT_MCP_URL = os.environ.get("STS2_MCP_URL", "http://127.0.0.1:8765/mcp")


def _tool_data(result: Any) -> dict[str, Any]:
    data = getattr(result, "data", None)
    if isinstance(data, dict):
        return data
    structured = getattr(result, "structured_content", None)
    if isinstance(structured, dict):
        return structured
    content = getattr(result, "content", [])
    if content:
        text = getattr(content[0], "text", "")
        try:
            parsed = json.loads(text)
            if isinstance(parsed, dict):
                return parsed
        except json.JSONDecodeError:
            return {"text": text}
    return {}


def _short_card(card: dict[str, Any]) -> str:
    name = card.get("name") or card.get("id") or card.get("card_id") or "?"
    cost = card.get("energy_cost", card.get("cost", ""))
    playable = card.get("playable")
    index = card.get("index")
    parts = []
    if index is not None:
        parts.append(f"#{index}")
    card_ref = card.get("card_ref")
    if card_ref:
        parts.append(f"ref={card_ref}")
    parts.append(str(name))
    if cost != "":
        parts.append(f"费{cost}")
    if playable is False:
        parts.append("不可打")
    return " ".join(parts)


def _short_stack(stack: dict[str, Any]) -> str:
    name = stack.get("name") or stack.get("card_id") or "?"
    count = stack.get("count")
    card_type = stack.get("card_type")
    upgraded = "+" if stack.get("upgraded") and not str(name).endswith("+") else ""
    prefix = f"{name}{upgraded}"
    if count is not None:
        prefix = f"{prefix}*{count}"
    return f"{prefix}({card_type})" if card_type else prefix


def _md_cell(value: Any) -> str:
    return str(value if value is not None else "").replace("|", "\\|").replace("\n", " ")


class ManualDriver:
    def __init__(
        self,
        *,
        log_path: Path | None,
        title: str,
        character: str,
        ascension: str,
        goal: str,
        interface: str,
    ) -> None:
        env_log_path = os.environ.get("STS2_RUN_LOG_PATH")
        self.log_path = (
            log_path
            if log_path is not None
            else Path(env_log_path)
            if env_log_path
            else None
        )
        if self.log_path is not None:
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self.title = title
        self.character = character
        self.ascension = ascension
        self.goal = goal
        self.interface = interface
        self.public_log_path: str | None = None
        self.internal_mcp_log_path: str | None = None
        self.last_decision: dict[str, Any] | None = None
        self.last_raw: dict[str, Any] | None = None
        self.step = 0

    def init_log(self) -> None:
        if self.log_path is None:
            return
        if self.log_path.exists() and self.log_path.stat().st_size > 0:
            self.step = self._read_last_step()
            return
        self.log_path.write_text(
            "\n".join(
                [
                    f"# {self.title}",
                    "",
                    f"- 开始时间: {datetime.now().isoformat(timespec='seconds')}",
                    f"- 角色/进阶: {self.character}{(' / ' + self.ascension) if self.ascension else ''}",
                    f"- 目标: {self.goal}",
                    f"- 接口: {self.interface}",
                    "- 最终结果: 进行中。",
                    "",
                    "| Step | Floor/Screen | State Snapshot | Decision | Reason | Result |",
                    "| --- | --- | --- | --- | --- | --- |",
                    "",
                ]
            ),
            encoding="utf-8",
        )

    def _read_last_step(self) -> int:
        if self.log_path is None:
            return 0
        last_step = 0
        for line in self.log_path.read_text(encoding="utf-8").splitlines():
            match = re.match(r"^\|\s*(\d+)\s*\|", line)
            if match:
                last_step = max(last_step, int(match.group(1)))
        return last_step

    def print_decision(self, payload: dict[str, Any]) -> None:
        data = payload.get("data") if isinstance(payload.get("data"), dict) else payload
        decision = data.get("decision") if isinstance(data, dict) else None
        if not isinstance(decision, dict):
            print("NO_DECISION", json.dumps(payload, ensure_ascii=False)[:2000], flush=True)
            return

        self.last_decision = decision
        raw = data.get("raw_state") or decision.get("diagnostics", {}).get("raw_state")
        self.last_raw = raw if isinstance(raw, dict) else None
        summary = decision.get("summary") or {}

        print("\n=== DECISION ===", flush=True)
        print("id:", decision.get("decision_id"), flush=True)
        print(
            "phase/screen:",
            decision.get("phase"),
            decision.get("screen"),
            "stable=",
            decision.get("stable"),
            flush=True,
        )
        print(
            "floor/turn/char/A:",
            summary.get("floor"),
            summary.get("turn"),
            summary.get("character_id"),
            summary.get("ascension"),
            flush=True,
        )
        print(
            "hp/block/energy/gold/incoming:",
            f"{summary.get('current_hp')}/{summary.get('max_hp')}",
            "block",
            summary.get("block"),
            "energy",
            summary.get("energy"),
            "gold",
            summary.get("gold"),
            "incoming",
            summary.get("incoming_damage"),
            flush=True,
        )

        if self.last_raw:
            run = self.last_raw.get("run") or {}
            if run:
                relics = [r.get("name") or r.get("id") for r in run.get("relics") or []]
                potions = [p.get("name") or p.get("id") for p in run.get("potions") or []]
                print(
                    "seed:",
                    run.get("seed"),
                    "deckN=",
                    len(run.get("deck") or []),
                    "relics=",
                    relics,
                    "potions=",
                    potions,
                    flush=True,
                )
            for key, limit in (
                ("character_select", 3500),
                ("map", 5500),
                ("event", 5500),
                ("reward", 5500),
                ("selection", 5500),
                ("rest", 3500),
                ("shop", 6500),
                ("chest", 3500),
                ("modal", 3000),
                ("game_over", 3500),
            ):
                value = self.last_raw.get(key)
                if isinstance(value, dict):
                    print(f"{key}:", json.dumps(value, ensure_ascii=False)[:limit], flush=True)
            combat = self.last_raw.get("combat")
            if isinstance(combat, dict):
                player = combat.get("player") or {}
                print(
                    "combat player:",
                    {
                        k: player.get(k)
                        for k in ("hp", "max_hp", "block", "energy", "cards_played_this_turn")
                    },
                    flush=True,
                )
                for enemy in combat.get("enemies") or []:
                    powers = [
                        (p.get("name") or p.get("id"), p.get("amount"))
                        for p in enemy.get("powers") or []
                    ]
                    print(
                        "enemy:",
                        enemy.get("index"),
                        enemy.get("name") or enemy.get("id"),
                        f"{enemy.get('hp')}/{enemy.get('max_hp')}",
                        "block",
                        enemy.get("block"),
                        "intent",
                        enemy.get("intents") or enemy.get("intent"),
                        "powers",
                        powers,
                        flush=True,
                    )
                print(
                    "hand:",
                    " | ".join(_short_card(c) for c in combat.get("hand") or []),
                    flush=True,
                )
                piles = combat.get("piles")
                if isinstance(piles, dict):
                    for pile_name in ("draw", "discard", "exhaust"):
                        pile = piles.get(pile_name)
                        if not isinstance(pile, dict):
                            continue
                        stacks = list(pile.get("stacks") or [])
                        print(
                            f"{pile_name}:",
                            {
                                "count": pile.get("count"),
                                "non_attack": pile.get("non_attack_count"),
                                "defensive": pile.get("defensive_out_count"),
                                "non_attack_defensive": pile.get("non_attack_defensive_out_count"),
                                "truncated": pile.get("truncated"),
                            },
                            "stacks=",
                            " | ".join(_short_stack(stack) for stack in stacks[:18]),
                            flush=True,
                        )

        print("choices:", flush=True)
        for index, choice in enumerate(decision.get("choices") or []):
            line = (
                f"  [{index}] {choice.get('action_id')} :: {choice.get('label')} "
                f"att={choice.get('attention')} risk={choice.get('risk_tags') or []}"
            )
            if choice.get("summary"):
                line += f" summary={choice.get('summary')}"
            source = choice.get("source")
            if isinstance(source, dict):
                refs = {
                    key: source.get(key)
                    for key in ("card_ref", "target_entity_ref", "target_ref", "potion_id")
                    if source.get(key) is not None
                }
                if refs:
                    line += f" refs={json.dumps(refs, ensure_ascii=False)}"
            print(line, flush=True)
            preview = choice.get("preview")
            if preview:
                print(
                    "      preview:",
                    json.dumps(preview, ensure_ascii=False)[:1400],
                    flush=True,
                )
        if self.public_log_path:
            print("public_log_path:", self.public_log_path, flush=True)
            if self.internal_mcp_log_path:
                print("internal_mcp_log_path:", self.internal_mcp_log_path, flush=True)
        elif self.log_path is not None:
            print("local_log_path:", self.log_path, flush=True)
        else:
            print("public_log_path: will be returned by take_action logging", flush=True)

    def append_log(self, action_id: str, note: str, result: dict[str, Any]) -> None:
        logging_result = result.get("logging")
        if isinstance(logging_result, dict) and logging_result.get("path"):
            self.public_log_path = str(logging_result["path"])
            print("PUBLIC_LOG_PATH:", self.public_log_path, flush=True)
            if logging_result.get("mcp_log_path"):
                self.internal_mcp_log_path = str(logging_result["mcp_log_path"])
                print("INTERNAL_MCP_LOG_PATH:", self.internal_mcp_log_path, flush=True)

        if self.log_path is None:
            return

        self.step += 1
        decision = self.last_decision or {}
        summary = decision.get("summary") or {}
        ok = result.get("ok", True)
        row = (
            f"| {self.step} | {_md_cell(summary.get('floor'))} / {_md_cell(decision.get('screen'))} | "
            f"HP {_md_cell(summary.get('current_hp'))}/{_md_cell(summary.get('max_hp'))} 格挡 {_md_cell(summary.get('block'))} "
            f"能量 {_md_cell(summary.get('energy'))} 进伤 {_md_cell(summary.get('incoming_damage'))} 金币 {_md_cell(summary.get('gold'))} | "
            f"{_md_cell(action_id)} | {_md_cell(note)} | {'ok' if ok else 'fail'} |\n"
        )
        with self.log_path.open("a", encoding="utf-8") as handle:
            handle.write(row)

    def finish_log(self, text: str) -> None:
        if self.log_path is None:
            print("FINISH_NOT_WRITTEN_LOCALLY: MCP 自动日志没有最终结果字段；请用 note 记录最终结果。", flush=True)
            return
        content = self.log_path.read_text(encoding="utf-8")
        content = content.replace("- 最终结果: 进行中。", f"- 最终结果: {text}")
        self.log_path.write_text(content, encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Interactive STS2 ai_safe_v2 MCP driver. MCP take_action writes the public decision log."
    )
    parser.add_argument("--mcp-url", default=DEFAULT_MCP_URL, help="Network MCP URL.")
    parser.add_argument("--log-path", type=Path, default=None, help="Optional extra local markdown log path. MCP take_action logs automatically by default.")
    parser.add_argument("--title", default="STS2 MCP v2 中文决策日志", help="Markdown log title.")
    parser.add_argument("--character", default="未知角色", help="Character/run label written to the log header.")
    parser.add_argument("--ascension", default="", help="Ascension label written to the log header, for example A0.")
    parser.add_argument("--goal", default="通关三层 Boss 或死亡。", help="Run goal written to the log header.")
    parser.add_argument(
        "--interface",
        default="本地网络 MCP `ai_safe_v2`，工具 `wait_for_decision` / `take_action` / `execute_action_plan` / `select_cards` / `lookup_game_data`。",
        help="Interface description written to the log header.",
    )
    return parser.parse_args()


async def main() -> None:
    args = parse_args()
    driver = ManualDriver(
        log_path=args.log_path,
        title=args.title,
        character=args.character,
        ascension=args.ascension,
        goal=args.goal,
        interface=args.interface,
    )
    driver.init_log()
    print("READY log_path:", driver.log_path, flush=True)

    async with Client(args.mcp_url) as client:
        while True:
            print("cmd> ", end="", flush=True)
            line = sys.stdin.readline()
            if not line:
                return
            line = line.strip()
            if not line:
                continue
            if line in {"q", "quit", "exit"}:
                return
            try:
                if line in {"state", "wait"}:
                    result = await client.call_tool(
                        "wait_for_decision",
                        {
                            "timeout_ms": 20000,
                            "include_raw_state": True,
                            "include_relevant_game_data": True,
                        },
                    )
                    driver.print_decision(_tool_data(result))
                elif line == "current":
                    result = await client.call_tool(
                        "get_current_decision",
                        {
                            "include_raw_state": True,
                            "include_relevant_game_data": True,
                        },
                    )
                    driver.print_decision(_tool_data(result))
                elif line.startswith("act "):
                    text = line[4:]
                    if "|" in text:
                        action_id, note = [part.strip() for part in text.split("|", 1)]
                    else:
                        parts = text.split(maxsplit=1)
                        action_id = parts[0]
                        note = parts[1] if len(parts) > 1 else ""
                    decision = driver.last_decision
                    if not decision:
                        print("ERR no decision; run state first", flush=True)
                        continue
                    valid = [choice.get("action_id") for choice in decision.get("choices") or []]
                    if action_id not in valid:
                        print("ERR action not in current choices", action_id, "valid=", valid, flush=True)
                        continue
                    result = await client.call_tool(
                        "take_action",
                        {
                            "decision_id": decision.get("decision_id"),
                            "action_id": action_id,
                            "client_note": note,
                        },
                    )
                    data = _tool_data(result)
                    print("ACTION_RESULT:", json.dumps(data, ensure_ascii=False)[:4500], flush=True)
                    driver.append_log(action_id, note, data)
                    next_decision = data.get("next_decision")
                    if isinstance(next_decision, dict):
                        driver.last_decision = next_decision
                elif line.startswith("plan "):
                    text = line[5:]
                    if "|" in text:
                        steps_text, note = [part.strip() for part in text.split("|", 1)]
                    else:
                        steps_text, note = text.strip(), ""
                    steps = json.loads(steps_text)
                    if not isinstance(steps, list):
                        print("ERR plan must be a JSON array", flush=True)
                        continue
                    decision = driver.last_decision
                    if not decision:
                        print("ERR no decision; run state first", flush=True)
                        continue
                    result = await client.call_tool(
                        "execute_action_plan",
                        {
                            "decision_id": decision.get("decision_id"),
                            "steps": steps,
                            "mode": "strict",
                            "client_note": note,
                        },
                    )
                    data = _tool_data(result)
                    print("PLAN_RESULT:", json.dumps(data, ensure_ascii=False)[:10000], flush=True)
                    next_decision = data.get("next_decision")
                    if isinstance(next_decision, dict):
                        driver.last_decision = next_decision
                elif line.startswith("select "):
                    text = line[7:]
                    if "|" in text:
                        refs_text, note = [part.strip() for part in text.split("|", 1)]
                    else:
                        refs_text, note = text.strip(), ""
                    card_refs = shlex.split(refs_text)
                    decision = driver.last_decision
                    if not decision:
                        print("ERR no decision; run state first", flush=True)
                        continue
                    result = await client.call_tool(
                        "select_cards",
                        {
                            "decision_id": decision.get("decision_id"),
                            "card_refs": card_refs,
                            "confirm": True,
                            "client_note": note,
                        },
                    )
                    data = _tool_data(result)
                    print("SELECT_RESULT:", json.dumps(data, ensure_ascii=False)[:10000], flush=True)
                    next_decision = data.get("next_decision")
                    if isinstance(next_decision, dict):
                        driver.last_decision = next_decision
                elif line.startswith("lookup "):
                    items: list[dict[str, str]] = []
                    fields: list[str] = []
                    for token in shlex.split(line[7:]):
                        if token.startswith("fields="):
                            fields = [item for item in token.removeprefix("fields=").split(",") if item]
                        elif ":" in token:
                            collection, item_id = token.split(":", 1)
                            items.append({"collection": collection, "id": item_id})
                    result = await client.call_tool(
                        "lookup_game_data",
                        {"items": items, "fields": fields},
                    )
                    print(
                        "LOOKUP:",
                        json.dumps(_tool_data(result), ensure_ascii=False, indent=2)[:10000],
                        flush=True,
                    )
                elif line.startswith("note "):
                    note = line[5:].strip()
                    decision = driver.last_decision or {}
                    result = await client.call_tool(
                        "append_decision_note",
                        {
                            "decision_id": decision.get("decision_id", ""),
                            "action_id": "",
                            "note": note,
                        },
                    )
                    data = _tool_data(result)
                    print("NOTE_RESULT:", json.dumps(data, ensure_ascii=False)[:2500], flush=True)
                    logging_result = data if data.get("path") else data.get("logging")
                    if isinstance(logging_result, dict) and logging_result.get("path"):
                        driver.public_log_path = str(logging_result["path"])
                        print("PUBLIC_LOG_PATH:", driver.public_log_path, flush=True)
                        if logging_result.get("mcp_log_path"):
                            driver.internal_mcp_log_path = str(logging_result["mcp_log_path"])
                            print("INTERNAL_MCP_LOG_PATH:", driver.internal_mcp_log_path, flush=True)
                    if driver.log_path is not None:
                        with driver.log_path.open("a", encoding="utf-8") as handle:
                            handle.write(f"\n备注: {note}\n")
                elif line.startswith("finish "):
                    driver.finish_log(line[7:].strip())
                    print("FINISHED_LOG", driver.log_path, flush=True)
                else:
                    print(
                        "commands: state|current|act ACTION_ID | 中文理由|plan JSON_ARRAY | 中文理由|select CARD_REF... | 中文理由|lookup collection:id fields=a,b|note TEXT|finish TEXT|quit",
                        flush=True,
                    )
            except Exception as exc:
                print("EXC", type(exc).__name__, str(exc), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
