from __future__ import annotations

import json
import os
import re
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from fastmcp import FastMCP

from .client import Sts2ApiError, Sts2Client
from .handoff import Sts2HandoffService
from .knowledge import Sts2KnowledgeBase

ToolHandler = Callable[..., dict[str, Any]]

JSON_FILE_EXTENSION = ".json"
JSON_FILE_EXTENSION_LENGTH = len(JSON_FILE_EXTENSION)
GAME_DATA_RELATIVE_PATH = ("..", "..", "data", "eng")
KNOWN_ITEM_ID_KEYS = ("id", "ID", "Id")
ITEM_IDS_SEPARATOR = ","

SCENE_MENU = "menu"
SCENE_COMBAT = "combat"
SCENE_SHOP = "shop"
SCENE_EVENT = "event"

COMBAT_SCREEN_KEYWORDS = ("combat",)
COMBAT_SCREEN_NAMES = {"combat_reward", "combat_victory"}
SHOP_SCREEN_KEYWORDS = ("shop", "merchant")
EVENT_SCREEN_KEYWORDS = ("event",)
EVENT_SCREEN_NAMES = {"event_room", "ancient_event"}

_GAME_DATA_CACHE: dict[str, Any] | None = None
_GAME_DATA_INDEXES: dict[str, dict[str, Any]] = {}
_GAME_DATA_CACHE_LOCK = threading.Lock()
_GAME_DATA_INDEXES_LOCK = threading.Lock()

_SCENE_FIELD_SETS: dict[str, dict[str, list[str]]] = {
    SCENE_COMBAT: {
        "cards": [
            "id",
            "name",
            "description",
            "type",
            "rarity",
            "target",
            "cost",
            "is_x_cost",
            "star_cost",
            "is_x_star_cost",
            "damage",
            "block",
            "keywords",
            "tags",
            "vars",
            "upgrade",
        ],
        "monsters": [
            "id",
            "name",
            "type",
            "min_hp",
            "max_hp",
            "moves",
            "damage_values",
            "block_values",
        ],
        "powers": [
            "id",
            "name",
            "description",
            "type",
            "stack_type",
        ],
    },
    SCENE_SHOP: {
        "cards": [
            "id",
            "name",
            "description",
            "type",
            "rarity",
            "cost",
        ],
        "relics": [
            "id",
            "name",
            "description",
            "rarity",
            "pool",
        ],
        "potions": [
            "id",
            "name",
            "description",
            "rarity",
        ],
    },
    SCENE_EVENT: {
        "events": [
            "id",
            "name",
            "description",
            "options",
        ],
    },
}


@dataclass(frozen=True, slots=True)
class ActionToolSpec:
    name: str
    kind: str
    description: str


_LEGACY_ACTION_TOOLS: tuple[ActionToolSpec, ...] = (
    ActionToolSpec("end_turn", "no_args", "End the player's turn during combat."),
    ActionToolSpec("play_card", "card_target", "Play a card from the current hand."),
    ActionToolSpec("choose_map_node", "option_index", "Travel to a map node."),
    ActionToolSpec("collect_rewards_and_proceed", "no_args", "Auto-collect rewards and advance."),
    ActionToolSpec("skip_rewards_and_proceed", "no_args", "Skip remaining reward items and advance."),
    ActionToolSpec("claim_reward", "option_index", "Claim a single reward item."),
    ActionToolSpec("choose_reward_card", "option_index", "Pick a card from a reward screen."),
    ActionToolSpec("skip_reward_cards", "no_args", "Skip the current card reward."),
    ActionToolSpec("select_deck_card", "option_index", "Select a card on a deck selection screen."),
    ActionToolSpec("confirm_selection", "no_args", "Confirm the current manual card-selection overlay."),
    ActionToolSpec("open_chest", "no_args", "Open the treasure chest in the current room."),
    ActionToolSpec("choose_treasure_relic", "option_index", "Choose a relic from an opened chest."),
    ActionToolSpec("choose_event_option", "option_index", "Choose an option in the current event room."),
    ActionToolSpec("choose_rest_option", "option_index", "Choose a rest-site option."),
    ActionToolSpec("open_shop_inventory", "no_args", "Open the merchant inventory."),
    ActionToolSpec("close_shop_inventory", "no_args", "Close the merchant inventory."),
    ActionToolSpec("buy_card", "option_index", "Buy a card from the open merchant inventory."),
    ActionToolSpec("buy_relic", "option_index", "Buy a relic from the open merchant inventory."),
    ActionToolSpec("buy_potion", "option_index", "Buy a potion from the open merchant inventory."),
    ActionToolSpec("remove_card_at_shop", "no_args", "Use the merchant card-removal service."),
    ActionToolSpec("continue_run", "no_args", "Continue the current run from the main menu."),
    ActionToolSpec("abandon_run", "no_args", "Open the abandon-run confirmation from the main menu."),
    ActionToolSpec("open_character_select", "no_args", "Open the character select screen."),
    ActionToolSpec("open_timeline", "no_args", "Open the timeline screen."),
    ActionToolSpec("close_main_menu_submenu", "no_args", "Close the current main-menu submenu."),
    ActionToolSpec("choose_timeline_epoch", "option_index", "Choose a visible epoch on the timeline screen."),
    ActionToolSpec("confirm_timeline_overlay", "no_args", "Confirm the current timeline inspect or unlock overlay."),
    ActionToolSpec("select_character", "option_index", "Pick a character on the character select screen."),
    ActionToolSpec("embark", "no_args", "Start the run from character select."),
    ActionToolSpec("unready", "no_args", "Cancel local ready status in a multiplayer character-select lobby."),
    ActionToolSpec("increase_ascension", "no_args", "Increase the lobby ascension level when the local player is allowed to change it."),
    ActionToolSpec("decrease_ascension", "no_args", "Decrease the lobby ascension level when the local player is allowed to change it."),
    ActionToolSpec("use_potion", "option_target", "Use a potion from the player's belt."),
    ActionToolSpec("discard_potion", "option_index", "Discard a potion from the player's belt."),
    ActionToolSpec("confirm_modal", "no_args", "Confirm the currently open modal."),
    ActionToolSpec("dismiss_modal", "no_args", "Dismiss or cancel the currently open modal."),
    ActionToolSpec("return_to_main_menu", "no_args", "Leave the game over screen and return to the main menu."),
    ActionToolSpec("proceed", "no_args", "Click the current Proceed or Continue button."),
)


def _env_flag(name: str, default: bool = False) -> bool:
    value = os.getenv(name, "")
    if not value:
        return default

    return value.strip().lower() in {"1", "true", "yes", "on"}


def _normalize_tool_profile(tool_profile: str | None) -> str:
    value = (tool_profile or os.getenv("STS2_MCP_TOOL_PROFILE") or "ai_safe_v2").strip().lower()
    if value in {"ai_safe_v2", "aisafe_v2", "v2", "decision_v2"}:
        return "ai_safe_v2"
    if value in {"full", "legacy"}:
        return "full"
    if value in {"layered", "planner", "multi-agent"}:
        return "layered"

    return "guided"


def _debug_tools_enabled() -> bool:
    return _env_flag("STS2_ENABLE_DEBUG_ACTIONS")


def _get_game_data_dir() -> str:
    here = os.path.dirname(__file__)
    return os.path.abspath(os.path.join(here, *GAME_DATA_RELATIVE_PATH))


def _load_game_data() -> dict[str, Any]:
    global _GAME_DATA_CACHE
    if _GAME_DATA_CACHE is not None:
        return _GAME_DATA_CACHE

    with _GAME_DATA_CACHE_LOCK:
        if _GAME_DATA_CACHE is not None:
            return _GAME_DATA_CACHE

        data_dir = _get_game_data_dir()
        if not os.path.isdir(data_dir):
            raise RuntimeError(f"Game data directory not found: {data_dir!r}.")

        data: dict[str, Any] = {}
        for filename in sorted(os.listdir(data_dir)):
            path = os.path.join(data_dir, filename)
            if os.path.isdir(path) or not filename.lower().endswith(JSON_FILE_EXTENSION):
                continue

            key = filename[:-JSON_FILE_EXTENSION_LENGTH]
            try:
                with open(path, "r", encoding="utf-8") as f:
                    data[key] = json.load(f)
            except Exception as exc:
                raise RuntimeError(f"Failed to load game data file {path!r}: {exc}") from exc

        _GAME_DATA_CACHE = data
        return data


def _add_case_insensitive_item_id(index: dict[str, Any], item_id: str, item: Any) -> None:
    normalized = item_id.strip()
    if not normalized:
        return

    index[normalized] = item
    index[normalized.upper()] = item
    index[normalized.lower()] = item


def _ensure_game_data_index(collection: str) -> dict[str, Any]:
    if collection in _GAME_DATA_INDEXES:
        return _GAME_DATA_INDEXES[collection]

    with _GAME_DATA_INDEXES_LOCK:
        if collection in _GAME_DATA_INDEXES:
            return _GAME_DATA_INDEXES[collection]

        data = _load_game_data()
        if collection not in data:
            raise KeyError(f"Unknown game data collection: {collection}")

        items = data[collection]
        index: dict[str, Any] = {}
        if isinstance(items, dict):
            for raw_id, item in items.items():
                _add_case_insensitive_item_id(index=index, item_id=str(raw_id), item=item)
        elif isinstance(items, list):
            for item in items:
                if not isinstance(item, dict):
                    continue
                item_id = ""
                for key in KNOWN_ITEM_ID_KEYS:
                    candidate = item.get(key)
                    if candidate:
                        item_id = str(candidate).strip()
                        break
                if item_id:
                    _add_case_insensitive_item_id(index=index, item_id=item_id, item=item)
        else:
            raise TypeError(f"Unsupported data type for collection {collection!r}: {type(items)}")

        _GAME_DATA_INDEXES[collection] = index
        return index


def _lookup_game_data_item(index: dict[str, Any], item_id: str) -> Any:
    return index.get(item_id) or index.get(item_id.upper()) or index.get(item_id.lower())


def _build_game_data_tool_error(collection: str, exc: Exception) -> dict[str, Any]:
    if isinstance(exc, KeyError):
        available_collections = sorted(_GAME_DATA_CACHE.keys()) if _GAME_DATA_CACHE else []
        return {
            "error": {
                "type": "unknown_collection",
                "collection": collection,
                "message": str(exc),
                "available_collections": available_collections,
            }
        }

    if isinstance(exc, RuntimeError):
        return {
            "error": {
                "type": "game_data_unavailable",
                "collection": collection,
                "message": str(exc),
            }
        }

    return {
        "error": {
            "type": "invalid_game_data",
            "collection": collection,
            "message": str(exc),
        }
    }


def get_game_data_items_fields(collection: str, item_ids: str, fields: str | None) -> dict[str, Any]:
    if not item_ids:
        return {}

    index = _ensure_game_data_index(collection)
    ids = [s.strip() for s in item_ids.split(ITEM_IDS_SEPARATOR) if s.strip()]
    requested_fields = [s.strip() for s in fields.split(ITEM_IDS_SEPARATOR) if s.strip()] if fields else []

    result: dict[str, Any] = {}
    for item_id in ids:
        item = _lookup_game_data_item(index=index, item_id=item_id)
        if item is None:
            result[item_id] = None
            continue

        if not requested_fields or not isinstance(item, dict):
            result[item_id] = item
            continue

        result[item_id] = {key: item[key] for key in requested_fields if key in item}

    return result


def _detect_scene_from_screen(screen: str) -> str:
    normalized = (screen or "").lower()
    if any(keyword in normalized for keyword in COMBAT_SCREEN_KEYWORDS) or normalized in COMBAT_SCREEN_NAMES:
        return SCENE_COMBAT
    if any(keyword in normalized for keyword in SHOP_SCREEN_KEYWORDS):
        return SCENE_SHOP
    if any(keyword in normalized for keyword in EVENT_SCREEN_KEYWORDS) or normalized in EVENT_SCREEN_NAMES:
        return SCENE_EVENT
    return SCENE_MENU


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


def _sanitize_log_part(value: str | None, fallback: str) -> str:
    raw = (value or "").strip().lower()
    if not raw:
        return fallback
    cleaned = re.sub(r"[^a-z0-9._+-]+", "_", raw).strip("._+-")
    return cleaned or fallback


def _knowledge_root() -> Path:
    configured = os.getenv("STS2_AGENT_KNOWLEDGE_DIR", "").strip()
    return Path(configured).expanduser().resolve() if configured else _repo_root() / "agent_knowledge"


def _run_logs_dir() -> Path:
    return _knowledge_root() / "run_logs"


def _mcp_logs_dir() -> Path:
    return _knowledge_root() / "mcp_logs"


def _utc_timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _md_cell(value: Any) -> str:
    return str(value if value is not None else "").replace("|", "\\|").replace("\n", " ")


def _log_step_count(path: Path) -> int:
    if not path.exists():
        return 0
    last_step = 0
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^\|\s*(\d+)\s*\|", line)
        if match:
            last_step = max(last_step, int(match.group(1)))
    return last_step


def _decision_log_header(decision: dict[str, Any] | None) -> str:
    summary = decision.get("summary") if isinstance(decision, dict) else None
    run_id = decision.get("run_id") if isinstance(decision, dict) else None
    character = summary.get("character_id") if isinstance(summary, dict) else None
    ascension = summary.get("ascension") if isinstance(summary, dict) else None
    character_label = str(character or "未知角色")
    if ascension is not None:
        character_label = f"{character_label} / A{ascension}"

    return "\n".join(
        [
            "# STS2 Decision Log",
            "",
            f"- Started UTC: {_utc_timestamp()}",
            f"- Run ID: {run_id or 'unknown'}",
            f"- Character/Ascension: {character_label}",
            "- Interface: MCP `ai_safe_v2` / automatic `take_action` logging.",
            "- Note: this public log preserves agent-provided `client_note` / `append_decision_note` text as-is. MCP does not translate or rewrite it.",
            "- Result: in progress.",
            "",
            "| Step | Action | Agent note | Result |",
            "| --- | --- | --- | --- |",
            "",
        ]
    )


def _resolve_public_log_path(path: Path) -> Path:
    if not path.exists() or path.stat().st_size == 0:
        return path
    content = path.read_text(encoding="utf-8")
    if "| Step | Action | Agent note | Result |" in content:
        return path
    return path.with_name(f"{path.stem}.decision{path.suffix}")


def _find_choice(decision: dict[str, Any] | None, action_id: str) -> dict[str, Any] | None:
    if not isinstance(decision, dict):
        return None
    choices = decision.get("choices")
    if not isinstance(choices, list):
        return None
    for choice in choices:
        if isinstance(choice, dict) and choice.get("action_id") == action_id:
            return choice
    return None


def _append_decision_log(
    *,
    decision: dict[str, Any] | None,
    action_id: str,
    client_note: str | None,
    result: dict[str, Any] | None,
    note: str | None = None,
) -> dict[str, Any]:
    try:
        log_dir = _run_logs_dir()
        mcp_log_dir = _mcp_logs_dir()
        log_dir.mkdir(parents=True, exist_ok=True)
        mcp_log_dir.mkdir(parents=True, exist_ok=True)

        run_id = None
        if isinstance(decision, dict):
            run_id = decision.get("run_id")
        log_name = _sanitize_log_part(str(run_id) if run_id else None, "run_unknown")
        path = _resolve_public_log_path(log_dir / f"{log_name}.md")
        mcp_log_path = mcp_log_dir / f"{log_name}.jsonl"
        choice = _find_choice(decision, action_id)

        if not path.exists() or path.stat().st_size == 0:
            path.write_text(_decision_log_header(decision), encoding="utf-8")

        step = _log_step_count(path) + 1
        reason = note if note is not None else client_note
        status = result.get("status") if isinstance(result, dict) else ("note" if note else "")
        row = f"| {step} | {_md_cell(f'`{action_id}`' if action_id else 'note')} | {_md_cell(reason)} | {_md_cell(status)} |\n"

        with path.open("a", encoding="utf-8") as f:
            f.write(row)

        summary = decision.get("summary") if isinstance(decision, dict) else None
        entry = {
            "timestamp_utc": _utc_timestamp(),
            "decision_id": decision.get("decision_id") if isinstance(decision, dict) else None,
            "run_id": run_id,
            "action_id": action_id,
            "phase": decision.get("phase") if isinstance(decision, dict) else None,
            "screen": decision.get("screen") if isinstance(decision, dict) else None,
            "summary": summary if isinstance(summary, dict) else None,
            "selected_label": choice.get("label") if isinstance(choice, dict) else None,
            "selected_source": choice.get("source") if isinstance(choice, dict) else None,
            "client_note": client_note,
            "note": note,
            "result_status": result.get("status") if isinstance(result, dict) else None,
            "result_stable": result.get("stable") if isinstance(result, dict) else None,
        }
        with mcp_log_path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False, sort_keys=True) + "\n")

        return {"ok": True, "path": str(path), "mcp_log_path": str(mcp_log_path)}
    except Exception as exc:  # pragma: no cover - best effort logging
        return {"ok": False, "warning": f"{exc.__class__.__name__}: {exc}"}


def _register_no_arg_tool(mcp: FastMCP, name: str, description: str, handler: ToolHandler) -> None:
    def tool() -> dict[str, Any]:
        return handler()

    tool.__name__ = name
    tool.__doc__ = description
    mcp.tool(name=name, description=description)(tool)


def _register_option_index_tool(mcp: FastMCP, name: str, description: str, handler: ToolHandler) -> None:
    def tool(option_index: int) -> dict[str, Any]:
        return handler(option_index=option_index)

    tool.__name__ = name
    tool.__doc__ = description
    mcp.tool(name=name, description=description)(tool)


def _register_card_target_tool(mcp: FastMCP, name: str, description: str, handler: ToolHandler) -> None:
    def tool(card_index: int, target_index: int | None = None) -> dict[str, Any]:
        return handler(card_index=card_index, target_index=target_index)

    tool.__name__ = name
    tool.__doc__ = description
    mcp.tool(name=name, description=description)(tool)


def _register_option_target_tool(mcp: FastMCP, name: str, description: str, handler: ToolHandler) -> None:
    def tool(option_index: int, target_index: int | None = None) -> dict[str, Any]:
        return handler(option_index=option_index, target_index=target_index)

    tool.__name__ = name
    tool.__doc__ = description
    mcp.tool(name=name, description=description)(tool)


def _register_legacy_action_tools(mcp: FastMCP, sts2: Sts2Client) -> None:
    for spec in _LEGACY_ACTION_TOOLS:
        handler = getattr(sts2, spec.name)
        if spec.kind == "no_args":
            _register_no_arg_tool(mcp, spec.name, spec.description, handler)
            continue

        if spec.kind == "option_index":
            _register_option_index_tool(mcp, spec.name, spec.description, handler)
            continue

        if spec.kind == "card_target":
            _register_card_target_tool(mcp, spec.name, spec.description, handler)
            continue

        if spec.kind == "option_target":
            _register_option_target_tool(mcp, spec.name, spec.description, handler)
            continue

        raise RuntimeError(f"Unsupported action tool kind: {spec.kind}")


def create_server(client: Sts2Client | None = None, tool_profile: str | None = None) -> FastMCP:
    sts2 = client or Sts2Client()
    knowledge = Sts2KnowledgeBase()
    handoff = Sts2HandoffService(knowledge)
    profile = _normalize_tool_profile(tool_profile)
    mcp = FastMCP("STS2 AI MCP")
    decision_cache: dict[str, dict[str, Any]] = {}

    def _agent_state() -> dict[str, Any]:
        state = sts2.get_state()
        agent_view = state.get("agent_view")
        if isinstance(agent_view, dict):
            if "available_actions" not in agent_view and isinstance(agent_view.get("actions"), list):
                return {
                    **agent_view,
                    "available_actions": agent_view["actions"],
                }
            return agent_view
        return state

    def _is_actionable_state(state: dict[str, Any]) -> bool:
        actions = state.get("available_actions")
        if not isinstance(actions, list):
            actions = state.get("actions")
        return isinstance(actions, list) and len(actions) > 0

    def _wait_until_actionable_impl(
        timeout_seconds: float,
        *,
        monotonic: Callable[[], float] = time.monotonic,
        sleep: Callable[[float], None] = time.sleep,
    ) -> dict[str, Any]:
        timeout = max(0.1, float(timeout_seconds))
        actionable_events = {
            "player_action_window_opened",
            "route_decision_required",
            "reward_decision_required",
            "available_actions_changed",
            "screen_changed",
        }

        state = sts2.get_state()
        if _is_actionable_state(state):
            return {
                "matched": False,
                "event": None,
                "state": state,
                "actions": sts2.get_available_actions(),
                "timeout_seconds": timeout,
                "source": "state",
            }

        started_at = monotonic()
        event: dict[str, Any] | None = None
        source = "events"

        try:
            event = sts2.wait_for_event(event_names=actionable_events, timeout=timeout)
        except Exception:
            event = None
            source = "polling"

        remaining = max(0.0, timeout - (monotonic() - started_at))
        state = sts2.get_state()

        if event is None and not _is_actionable_state(state) and remaining > 0:
            source = "polling"
            interval = max(0.05, float(os.getenv("STS2_MCP_FALLBACK_POLL_SECONDS", "0.25")))
            deadline = monotonic() + remaining
            baseline_signature = "|".join(sorted(str(name) for name in (state.get("available_actions") or [])))

            while monotonic() < deadline:
                sleep(interval)
                state = sts2.get_state()
                if _is_actionable_state(state):
                    break

                signature = "|".join(sorted(str(name) for name in (state.get("available_actions") or [])))
                if signature != baseline_signature:
                    break

        return {
            "matched": event is not None,
            "event": event,
            "state": state,
            "actions": sts2.get_available_actions(),
            "timeout_seconds": timeout,
            "source": source,
        }

    def _cache_decision(payload: dict[str, Any]) -> dict[str, Any]:
        decision = payload.get("decision")
        if isinstance(decision, dict) and isinstance(decision.get("decision_id"), str):
            decision_cache[decision["decision_id"]] = decision
        return payload

    def _lookup_game_data(items: list[dict[str, Any]], fields: list[str] | None = None) -> dict[str, Any]:
        requested_fields = [field.strip() for field in (fields or []) if field and field.strip()]
        result: dict[str, Any] = {}
        for item in items:
            collection = str(item.get("collection", "")).strip()
            item_id = str(item.get("id", "")).strip()
            key = f"{collection}:{item_id}"
            if not collection or not item_id:
                result[key] = None
                continue

            index = _ensure_game_data_index(collection)
            value = _lookup_game_data_item(index=index, item_id=item_id)
            if value is None or not requested_fields or not isinstance(value, dict):
                result[key] = value
                continue

            result[key] = {field: value[field] for field in requested_fields if field in value}
        return result

    @mcp.tool
    def health_check() -> dict[str, Any]:
        """Check whether the STS2 AI MCP Mod is loaded and reachable."""
        health = sts2.get_health()
        return {
            **health,
            "mcp_tool_profile": profile,
            "mcp_capabilities": {
                "ai_safe_v2": profile == "ai_safe_v2",
                "tools": [
                    "health_check",
                    "wait_for_decision",
                    "get_current_decision",
                    "take_action",
                    "lookup_game_data",
                    "append_decision_note",
                ] if profile == "ai_safe_v2" else None,
            },
        }

    if profile == "ai_safe_v2":
        @mcp.tool
        def get_current_decision(
            include_raw_state: bool = False,
            include_relevant_game_data: bool = True,
        ) -> dict[str, Any]:
            """Return the current stable v2 decision window if one is available."""
            return _cache_decision(
                sts2.get_current_decision(
                    profile="ai_safe",
                    include_raw_state=include_raw_state,
                    include_relevant_game_data=include_relevant_game_data,
                )
            )

        @mcp.tool
        def wait_for_decision(
            timeout_ms: int = 20_000,
            include_raw_state: bool = False,
            include_relevant_game_data: bool = True,
            after_decision_id: str | None = None,
        ) -> dict[str, Any]:
            """Wait for a stable v2 decision window and return it."""
            return _cache_decision(
                sts2.wait_for_decision(
                    timeout_ms=timeout_ms,
                    profile="ai_safe",
                    include_raw_state=include_raw_state,
                    include_relevant_game_data=include_relevant_game_data,
                    after_decision_id=after_decision_id,
                )
            )

        @mcp.tool
        def take_action(
            decision_id: str,
            action_id: str,
            client_note: str | None = None,
        ) -> dict[str, Any]:
            """Execute one action from the current v2 decision window."""
            decision = decision_cache.get(decision_id)
            result = sts2.take_action(
                decision_id=decision_id,
                action_id=action_id,
                params={},
                client_note=client_note,
            )
            logging_result = _append_decision_log(
                decision=decision,
                action_id=action_id,
                client_note=client_note,
                result=result,
            )
            if logging_result.get("ok") is False:
                return {**result, "logging_warning": logging_result.get("warning")}
            return {**result, "logging": logging_result}

        @mcp.tool
        def lookup_game_data(items: list[dict[str, Any]], fields: list[str] | None = None) -> dict[str, Any]:
            """Lookup game metadata by collection/id pairs, preferring the live v2 Mod API."""
            try:
                return sts2.lookup_game_data(items=items, fields=fields)
            except (AttributeError, Sts2ApiError, KeyError, RuntimeError, TypeError) as exc:
                try:
                    return {
                        "items": _lookup_game_data(items=items, fields=fields),
                        "metadata": {
                            "data_source": "checked_in_cache",
                            "exported_at_utc": None,
                            "fallback_reason": str(exc),
                        },
                    }
                except (KeyError, RuntimeError, TypeError) as fallback_exc:
                    collection = ""
                    if items and isinstance(items[0], dict):
                        collection = str(items[0].get("collection", ""))
                    return _build_game_data_tool_error(collection=collection, exc=fallback_exc)
            except Exception as exc:
                collection = ""
                if items and isinstance(items[0], dict):
                    collection = str(items[0].get("collection", ""))
                return _build_game_data_tool_error(collection=collection, exc=exc)

        @mcp.tool
        def append_decision_note(decision_id: str, action_id: str = "", note: str = "") -> dict[str, Any]:
            """Append a local note to the active v2 run log."""
            return _append_decision_log(
                decision=decision_cache.get(decision_id),
                action_id=action_id,
                client_note=None,
                result=None,
                note=note,
            )

        return mcp

    @mcp.tool
    def get_game_state() -> dict[str, Any]:
        """Read a full snapshot of the current game state."""
        return sts2.get_state()

    @mcp.tool
    def get_available_actions() -> list[dict[str, Any]]:
        """List currently executable actions with `requires_index` and `requires_target` hints."""
        return sts2.get_available_actions()

    if profile in {"full", "layered"}:
        @mcp.tool
        def get_agent_view() -> dict[str, Any]:
            """Read the compact agent-facing game state snapshot."""
            return _agent_state()

        @mcp.tool
        def get_planner_context(planner_note: str | None = None) -> dict[str, Any]:
            """Build a planner-focused snapshot with route branches and linked event knowledge."""
            return knowledge.build_planner_context(sts2.get_state(), planner_note=planner_note)

        @mcp.tool
        def create_planner_handoff(
            planning_focus: str | None = None,
            previous_combat_summary: str | None = None,
        ) -> dict[str, Any]:
            """Build a clean planner-agent packet for route, reward, event, and shop decisions."""
            return handoff.create_planner_handoff(
                sts2.get_state(),
                planning_focus=planning_focus,
                previous_combat_summary=previous_combat_summary,
            )

        @mcp.tool
        def get_combat_context(
            planner_note: str | None = None,
            include_knowledge: bool = True,
        ) -> dict[str, Any]:
            """Build a combat-focused snapshot and link it to the canonical combat knowledge entry."""
            return knowledge.build_combat_context(
                sts2.get_state(),
                planner_note=planner_note,
                include_knowledge=include_knowledge,
            )

        @mcp.tool
        def create_combat_handoff(
            planner_message: str | None = None,
            combat_objective: str | None = None,
        ) -> dict[str, Any]:
            """Build a clean combat-agent packet with linked combat knowledge and planner guidance."""
            return handoff.create_combat_handoff(
                sts2.get_state(),
                planner_message=planner_message,
                combat_objective=combat_objective,
            )

        @mcp.tool
        def complete_combat_handoff(
            combat_key: str,
            summary: str,
            planner_message: str | None = None,
            pattern_note: str | None = None,
            trait_note: str | None = None,
            tactical_note: str | None = None,
        ) -> dict[str, Any]:
            """Persist a combat-agent summary and optional enemy-pattern notes, then return a planner-facing brief."""
            return handoff.complete_combat_handoff(
                combat_key=combat_key,
                summary=summary,
                planner_message=planner_message,
                pattern_note=pattern_note,
                trait_note=trait_note,
                tactical_note=tactical_note,
            )

        @mcp.tool
        def append_combat_knowledge(note: str, section: str = "observations") -> dict[str, Any]:
            """Append a note to the active combat knowledge file."""
            return knowledge.append_combat_note(
                sts2.get_state(),
                note=note,
                section=section,
            )

        @mcp.tool
        def append_event_knowledge(
            note: str,
            section: str = "observations",
            option_index: int | None = None,
        ) -> dict[str, Any]:
            """Append a note to the active event knowledge file."""
            return knowledge.append_event_note(
                sts2.get_state(),
                note=note,
                section=section,
                option_index=option_index,
            )

        @mcp.tool
        def complete_event_handoff(
            event_id: str,
            summary: str,
            option_index: int | None = None,
            planning_note: str | None = None,
            outcome_note: str | None = None,
        ) -> dict[str, Any]:
            """Persist an event outcome summary and optional event notes, then return a planner-facing brief."""
            return handoff.complete_event_handoff(
                event_id=event_id,
                summary=summary,
                option_index=option_index,
                planning_note=planning_note,
                outcome_note=outcome_note,
            )

    @mcp.tool
    def get_game_data_item(collection: str, item_id: str) -> dict[str, Any] | None:
        """Return a single item from a game metadata collection by id."""
        if not item_id:
            return None

        try:
            index = _ensure_game_data_index(collection)
            return _lookup_game_data_item(index=index, item_id=item_id)
        except (KeyError, RuntimeError, TypeError) as exc:
            return _build_game_data_tool_error(collection=collection, exc=exc)

    @mcp.tool
    def get_game_data_items(collection: str, item_ids: str) -> dict[str, Any]:
        """Return multiple items from a collection by comma-separated ids."""
        if not item_ids:
            return {}

        try:
            index = _ensure_game_data_index(collection)
            ids = [s.strip() for s in item_ids.split(ITEM_IDS_SEPARATOR) if s.strip()]
            return {item_id: _lookup_game_data_item(index=index, item_id=item_id) for item_id in ids}
        except (KeyError, RuntimeError, TypeError) as exc:
            return _build_game_data_tool_error(collection=collection, exc=exc)

    @mcp.tool
    def get_relevant_game_data(collection: str, item_ids: str) -> dict[str, Any]:
        """Return items with a compact field set for the current game context."""
        state = sts2.get_state()
        screen = str(state.get("screen", ""))
        scene = _detect_scene_from_screen(screen)
        try:
            suggested_fields = _SCENE_FIELD_SETS.get(scene, {}).get(collection)
            if not suggested_fields:
                return get_game_data_items(collection=collection, item_ids=item_ids)

            return get_game_data_items_fields(
                collection=collection,
                item_ids=item_ids,
                fields=",".join(suggested_fields),
            )
        except (KeyError, RuntimeError, TypeError) as exc:
            return _build_game_data_tool_error(collection=collection, exc=exc)

    @mcp.tool
    def wait_for_event(event_names: str = "", timeout_seconds: float = 20.0) -> dict[str, Any]:
        """Wait for one matching game event from `/events/stream`."""
        timeout = max(0.1, float(timeout_seconds))
        target_names = [name.strip() for name in event_names.split(",") if name.strip()]
        event = sts2.wait_for_event(
            event_names=target_names or None,
            timeout=timeout,
        )
        if event is None:
            return {
                "matched": False,
                "event": None,
                "event_names": target_names,
                "timeout_seconds": timeout,
            }

        return {
            "matched": True,
            "event": event,
            "event_names": target_names,
            "timeout_seconds": timeout,
        }

    @mcp.tool
    def wait_until_actionable(timeout_seconds: float = 20.0) -> dict[str, Any]:
        """Wait until a new actionable phase is reported, then return fresh state."""
        return _wait_until_actionable_impl(timeout_seconds)

    @mcp.tool
    def act(
        action: str,
        card_index: int | None = None,
        target_index: int | None = None,
        option_index: int | None = None,
    ) -> dict[str, Any]:
        """Execute one currently available game action through the compact tool surface.

        Usage loop:
            1. Call `get_game_state()` or `get_available_actions()`.
            2. Branch on `state.session.mode` and `state.session.phase`.
            3. Pick an action that is currently available.
            4. Pass only the indices required by that action from the latest state.
            5. Read state again after the action completes.

        Compact-tool rules:
            - Guided mode intentionally keeps the tool surface small: use this
              single `act` tool for both singleplayer and multiplayer actions.
            - Multiplayer never changes the control scope; you only control the
              local player exposed by the latest state.
            - Never guess actions from screen names alone. Only call names that
              are present in `state.available_actions`.

        Notes:
            - Use `card_index` for `play_card`.
            - Use `option_index` for map, reward, shop, event, rest, selection,
              and multiplayer-lobby actions.
            - Use `target_index` only when the latest state marks a card or potion as `requires_target=true`.
            - Read `target_index_space` and `valid_target_indices` from state to know whether `target_index`
              refers to `combat.enemies[]` or `combat.players[]`.
            - `run_console_command` is intentionally excluded from this compact tool.
        """
        normalized = action.strip().lower()
        if normalized == "run_console_command":
            raise RuntimeError("run_console_command is gated separately and must use its own tool when enabled.")

        return sts2.execute_action(
            normalized,
            card_index=card_index,
            target_index=target_index,
            option_index=option_index,
            client_context={
                "source": "mcp",
                "tool_name": "act",
                "tool_profile": profile,
            },
        )

    if profile == "full":
        _register_legacy_action_tools(mcp, sts2)

    if _debug_tools_enabled():
        @mcp.tool
        def run_console_command(command: str) -> dict[str, Any]:
            """Run a game dev-console command for local validation or debugging."""
            return sts2.run_console_command(command=command)

    return mcp


def main() -> None:
    create_server().run(transport="stdio", show_banner=False)


if __name__ == "__main__":
    main()
