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
from typing_extensions import NotRequired, TypedDict

from fastmcp import FastMCP

from .client import Sts2ApiError, Sts2CapabilityError, Sts2Client, evaluate_runtime_contract
from .game_data import GameDataSnapshot, GameDataVersionError, VersionedGameDataStore
from .handoff import Sts2HandoffService
from .knowledge import Sts2KnowledgeBase
from .reasoning import (
    DEFAULT_MAX_STATES,
    DEFAULT_TIME_BUDGET_MS,
    ReasoningInputError,
    evaluate_combat_horizon,
    evaluate_run_decision,
)

ToolHandler = Callable[..., dict[str, Any]]


class ActionPlanStep(TypedDict):
    kind: str
    action_id: NotRequired[str]
    card_ref: NotRequired[str]
    card_id: NotRequired[str]
    card_name: NotRequired[str]
    target_ref: NotRequired[str]
    target_entity_ref: NotRequired[str]
    potion_id: NotRequired[str]
    option_index: NotRequired[int]
    note: NotRequired[str]


class CombatHorizonLine(TypedDict):
    label: NotRequired[str]
    steps: list[ActionPlanStep]

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

_PLAN_ALLOWED_KINDS = {
    "play_card",
    "use_potion",
    "select_deck_card",
    "confirm_selection",
}
_PLAN_COMBAT_KINDS = {"play_card", "use_potion"}
_PLAN_SELECTION_KINDS = {"select_deck_card", "confirm_selection"}
_PLAN_SELECTOR_KEYS = (
    "card_ref",
    "card_id",
    "card_name",
    "target_ref",
    "target_entity_ref",
    "potion_id",
    "option_index",
)
_MAX_COMBAT_PLAN_STEPS = 5
_MAX_SELECTION_PLAN_STEPS = 12
_MAX_CACHED_PLAN_RESULTS = 128
_DEFAULT_ACTION_HANDOFF_TIMEOUT_SECONDS = 120.0
_ACTION_HANDOFF_WAIT_SLICE_MS = 20_000

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
            "state_machine",
            "numeric_parameters",
            "damage_values",
            "block_values",
            "mechanics",
            "planning_notes",
            "data_completeness",
            "data_source_notes",
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

_RELEVANT_ID_COLLECTIONS = {
    "card_id": "cards",
    "enemy_id": "monsters",
    "monster_id": "monsters",
    "power_id": "powers",
    "relic_id": "relics",
    "potion_id": "potions",
    "event_id": "events",
}

_CORE_GLOSSARY = {
    "Strength": "Adds to attack damage.",
    "Weak": "Attack damage dealt is reduced by 25%.",
    "Vulnerable": "Attack damage received is increased by 50%.",
    "Dexterity": "Adds to Block gained from cards before multiplicative modifiers.",
    "Frail": "Block gained from cards is reduced by 25%.",
    "Block": "Block absorbs damage before HP loss.",
    "Eternal": "Card cannot be removed from the deck.",
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
    ActionToolSpec("select_character", "character_ascension", "Select a character and exact unlocked ascension level in one action."),
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


def enforce_startup_contract(client: Sts2Client) -> dict[str, Any]:
    try:
        return client.require_runtime_contract()
    except (Sts2ApiError, Sts2CapabilityError) as exc:
        if _env_flag("STS2_MCP_ALLOW_INCOMPATIBLE"):
            logger.warning("Starting with explicit incompatible-contract override: %s", exc)
            return {
                "compatible": False,
                "override": True,
                "error": str(exc),
            }
        raise RuntimeError(f"STS2 MCP startup contract check failed: {exc}") from exc


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


def _match_plan_choice(
    decision: dict[str, Any],
    step: dict[str, Any],
) -> tuple[dict[str, Any] | None, str | None, int]:
    choices = decision.get("choices")
    if not isinstance(choices, list):
        return None, "decision_has_no_choices", 0

    action_id = str(step.get("action_id") or "").strip()
    if action_id:
        choice = _find_choice(decision, action_id)
        return (choice, None, 1) if choice is not None else (None, "action_id_not_available", 0)

    kind = str(step.get("kind") or "").strip()
    if not kind:
        return None, "step_requires_kind_or_action_id", 0

    candidates = [choice for choice in choices if isinstance(choice, dict) and choice.get("kind") == kind]
    for key in _PLAN_SELECTOR_KEYS:
        if key not in step or step.get(key) is None:
            continue
        expected = step.get(key)
        candidates = [
            choice
            for choice in candidates
            if isinstance(choice.get("source"), dict) and choice["source"].get(key) == expected
        ]

    if len(candidates) == 1:
        return candidates[0], None, 1
    if not candidates:
        return None, "planned_action_not_available", 0
    return None, "planned_action_is_ambiguous", len(candidates)


def _combat_hand_refs(decision: dict[str, Any]) -> set[str] | None:
    context = decision.get("context")
    combat = context.get("combat") if isinstance(context, dict) else None
    hand = combat.get("hand") if isinstance(combat, dict) else None
    if not isinstance(hand, list):
        return None

    refs: set[str] = set()
    for card in hand:
        card_ref = card.get("card_ref") if isinstance(card, dict) else None
        if not isinstance(card_ref, str) or not card_ref:
            return None
        refs.add(card_ref)
    return refs


def _strict_plan_transition_error(
    before: dict[str, Any],
    after: dict[str, Any],
    choice: dict[str, Any],
    next_step: dict[str, Any],
) -> str | None:
    before_phase = before.get("phase")
    after_phase = after.get("phase")
    if before_phase != after_phase:
        return "decision_phase_changed"

    kind = str(choice.get("kind") or "")
    if kind not in _PLAN_COMBAT_KINDS:
        return None

    before_refs = _combat_hand_refs(before)
    after_refs = _combat_hand_refs(after)
    if before_refs is None or after_refs is None:
        return "combat_hand_refs_unavailable"

    if kind == "use_potion":
        if before_refs != after_refs:
            return "combat_hand_changed_after_potion"
        return None

    source = choice.get("source")
    played_ref = source.get("card_ref") if isinstance(source, dict) else None
    if not isinstance(played_ref, str) or not played_ref:
        return "played_card_ref_unavailable"

    expected_after = before_refs - {played_ref}
    if after_refs - expected_after:
        return "combat_hand_gained_or_returned_cards"
    if expected_after - after_refs:
        return "combat_hand_lost_additional_cards"

    next_kind = str(next_step.get("kind") or "")
    if next_kind and next_kind not in _PLAN_COMBAT_KINDS:
        return "combat_plan_crosses_action_category"
    return None


def _optional_int(value: Any) -> int | None:
    if isinstance(value, bool) or value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError):
        return None


def _plan_combat_context(decision: dict[str, Any]) -> dict[str, Any]:
    context = decision.get("context")
    if not isinstance(context, dict):
        return {}
    combat = context.get("combat")
    return combat if isinstance(combat, dict) else {}


def _plan_player_state(decision: dict[str, Any]) -> dict[str, Any]:
    combat = _plan_combat_context(decision)
    player = combat.get("player")
    return player if isinstance(player, dict) else {}


def _plan_summary_int(decision: dict[str, Any], key: str) -> int | None:
    summary = decision.get("summary")
    return _optional_int(summary.get(key)) if isinstance(summary, dict) else None


def _plan_power_id(power: dict[str, Any]) -> str:
    return str(power.get("power_id") or power.get("id") or power.get("name") or "").strip().upper()


def _plan_card_play_limit(decision: dict[str, Any]) -> tuple[int | None, str | None]:
    powers = _plan_player_state(decision).get("powers")
    if not isinstance(powers, list):
        return None, None
    for power in powers:
        if not isinstance(power, dict):
            continue
        power_id = _plan_power_id(power)
        if power_id in {"SLOTH", "SLOTH_POWER"}:
            amount = _optional_int(power.get("amount"))
            if amount is not None and amount >= 0:
                return amount, power_id
    return None, None


def _plan_hand_cards(decision: dict[str, Any]) -> dict[str, dict[str, Any]]:
    hand = _plan_combat_context(decision).get("hand")
    if not isinstance(hand, list):
        return {}
    return {
        str(card.get("card_ref")): card
        for card in hand
        if isinstance(card, dict) and isinstance(card.get("card_ref"), str) and card.get("card_ref")
    }


def _plan_enemy_key(enemy: dict[str, Any]) -> str:
    enemy_ref = enemy.get("enemy_ref")
    if isinstance(enemy_ref, str) and enemy_ref:
        return enemy_ref
    index = _optional_int(enemy.get("index"))
    return f"enemy:{index}" if index is not None else str(enemy.get("enemy_id") or "unknown")


def _plan_enemy_states(decision: dict[str, Any]) -> dict[str, dict[str, Any]]:
    enemies = _plan_combat_context(decision).get("enemies")
    result: dict[str, dict[str, Any]] = {}
    if not isinstance(enemies, list):
        return result
    for enemy in enemies:
        if not isinstance(enemy, dict):
            continue
        powers = enemy.get("powers") if isinstance(enemy.get("powers"), list) else []
        state = {
            "enemy_ref": enemy.get("enemy_ref"),
            "enemy_id": enemy.get("enemy_id"),
            "name": enemy.get("name"),
            "index": _optional_int(enemy.get("index")),
            "hp": _optional_int(enemy.get("current_hp")),
            "block": _optional_int(enemy.get("block")),
            "alive": bool(enemy.get("is_alive", True)),
            "vulnerable": any(
                isinstance(power, dict) and _plan_power_id(power) in {"VULNERABLE", "VULNERABLE_POWER"}
                for power in powers
            ),
        }
        result[_plan_enemy_key(enemy)] = state
        if state["index"] is not None:
            result.setdefault(f"enemy:{state['index']}", state)
    return result


def _plan_choice_card(
    choice: dict[str, Any],
    hand_by_ref: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    source = choice.get("source")
    card_ref = source.get("card_ref") if isinstance(source, dict) else None
    return hand_by_ref.get(str(card_ref), {})


def _plan_dynamic_var_int(card: dict[str, Any], *names: str) -> int | None:
    dynamic_vars = card.get("dynamic_vars")
    if not isinstance(dynamic_vars, dict):
        return None
    requested = {name.casefold() for name in names}
    for name, payload in dynamic_vars.items():
        if str(name).casefold() not in requested:
            continue
        if isinstance(payload, dict):
            for key in ("preview_value", "enchanted_value", "base_value"):
                value = _optional_int(payload.get(key))
                if value is not None:
                    return value
        value = _optional_int(payload)
        if value is not None:
            return value
    return None


def _plan_immediate_resource_effects(card: dict[str, Any]) -> dict[str, Any]:
    text = " ".join(
        str(card.get(key) or "")
        for key in ("rules_text", "raw_rules_text", "resolved_rules_text")
    ).lower()
    card_id = str(card.get("card_id") or "").strip().upper()
    barriers: list[str] = []
    next_turn = any(term in text for term in ("next turn", "下个回合", "下一回合"))
    conditional = any(
        term in text
        for term in (
            " if ",
            "whenever",
            "for each",
            "per ",
            "如果",
            "若",
            "每当",
            "每有",
        )
    )
    gains_energy = any(term in text for term in ("gain", "获得")) and any(
        term in text for term in ("energy", "energyicons", "能量")
    )
    gains_stars = any(term in text for term in ("gain", "获得")) and any(
        term in text for term in ("star", "staricons", "星能")
    )
    loses_hp = any(term in text for term in ("lose", "失去")) and any(
        term in text for term in ("hp", "health", "生命")
    )

    energy_gain = None if next_turn or conditional else _plan_dynamic_var_int(card, "Energy")
    star_gain = None if next_turn or conditional else _plan_dynamic_var_int(card, "Stars")
    hp_loss = None if next_turn or conditional else _plan_dynamic_var_int(card, "HpLoss")
    energy_multiplier = 2 if card_id == "DOUBLE_ENERGY" else None

    if gains_energy and not next_turn and energy_gain is None and energy_multiplier is None:
        barriers.append("immediate_energy_change_not_folded")
    if gains_stars and not next_turn and star_gain is None:
        barriers.append("immediate_star_change_not_folded")
    if loses_hp and not next_turn and hp_loss is None:
        barriers.append("immediate_hp_loss_not_folded")

    return {
        "energy_gain": max(0, energy_gain) if gains_energy and energy_gain is not None else None,
        "energy_multiplier": energy_multiplier,
        "star_gain": max(0, star_gain) if gains_stars and star_gain is not None else None,
        "hp_loss": max(0, hp_loss) if loses_hp and hp_loss is not None else None,
        "barriers": barriers,
    }


def _plan_information_barriers(card: dict[str, Any], preview: dict[str, Any]) -> list[str]:
    text = " ".join(
        str(card.get(key) or "")
        for key in ("rules_text", "raw_rules_text", "resolved_rules_text")
    ).lower()
    barriers: list[str] = []
    if not preview:
        barriers.append("single_action_preview_unavailable")
    patterns = (
        ("draw_or_draw_pile_change", ("draw ", "draw pile", "抽", "抽牌堆")),
        ("generated_or_added_cards", ("add ", "create ", "generate ", "加入你的手牌", "生成")),
        ("returned_or_moved_cards", ("return ", "put ", "放回", "置于", "顶部")),
        ("upgraded_or_transformed_cards", ("upgrade ", "transform ", "升级", "转化")),
        ("discarded_cards", ("discard ", "弃")),
        ("random_effect", ("random", "随机")),
        (
            "following_card_cost_change",
            ("costs 0", "cost 0", "cost less", "cost more", "耗能变为0", "耗能减少", "耗能增加"),
        ),
    )
    for reason, terms in patterns:
        if any(term in text for term in terms):
            barriers.append(reason)

    unmodeled = preview.get("unmodeled_effects")
    if isinstance(unmodeled, list) and unmodeled:
        barriers.append("single_action_unmodeled_effects")
    if preview and preview.get("preview_complete") is False:
        barriers.append("single_action_preview_incomplete")
    return list(dict.fromkeys(barriers))


def _plan_target_state(
    target: dict[str, Any],
    choice: dict[str, Any],
    enemies: dict[str, dict[str, Any]],
) -> dict[str, Any] | None:
    source = choice.get("source")
    candidate_keys: list[str] = []
    if isinstance(source, dict):
        for key in ("target_entity_ref", "target_ref"):
            value = source.get(key)
            if isinstance(value, str) and value:
                candidate_keys.append(value)
    target_index = _optional_int(target.get("target_index"))
    if target_index is not None:
        candidate_keys.append(f"enemy:{target_index}")
    for key in candidate_keys:
        if key in enemies:
            return enemies[key]
    return None


def _preview_combat_action_plan(
    decision: dict[str, Any],
    steps: list[dict[str, Any]],
    mode: str,
) -> dict[str, Any]:
    requested_count = len(steps)
    base = {
        "decision_id": decision.get("decision_id"),
        "mutation_performed": False,
        "requested_count": requested_count,
        "mode": mode,
    }
    if (mode or "strict").strip().lower() != "strict":
        return {**base, "status": "rejected", "stop_reason": "only_strict_mode_is_supported"}
    if not steps:
        return {**base, "status": "rejected", "stop_reason": "plan_requires_at_least_one_step"}
    if not all(isinstance(step, dict) for step in steps):
        return {**base, "status": "rejected", "stop_reason": "each_plan_step_must_be_an_object"}
    if len(steps) > _MAX_COMBAT_PLAN_STEPS:
        return {
            **base,
            "status": "rejected",
            "stop_reason": "plan_too_long",
            "max_steps": _MAX_COMBAT_PLAN_STEPS,
        }
    requested_kinds = {str(step.get("kind") or "").strip() for step in steps}
    if not requested_kinds or "" in requested_kinds:
        return {**base, "status": "rejected", "stop_reason": "each_plan_step_requires_kind"}
    unsupported = requested_kinds - _PLAN_COMBAT_KINDS
    if unsupported:
        return {
            **base,
            "status": "rejected",
            "stop_reason": "preview_action_plan_supports_combat_actions_only",
            "unsupported_kinds": sorted(unsupported),
        }

    combat = _plan_combat_context(decision)
    player = _plan_player_state(decision)
    hand_by_ref = _plan_hand_cards(decision)
    enemies = _plan_enemy_states(decision)
    energy = _optional_int(player.get("energy"))
    if energy is None:
        energy = _plan_summary_int(decision, "energy")
    stars = _optional_int(player.get("stars"))
    if stars is None:
        stars = _plan_summary_int(decision, "stars")
    cards_played = _optional_int(combat.get("cards_played_this_turn"))
    if cards_played is None:
        cards_played = _plan_summary_int(decision, "cards_played_this_turn")
    block = _optional_int(player.get("block"))
    if block is None:
        block = _plan_summary_int(decision, "block")
    hp = _optional_int(player.get("current_hp"))
    if hp is None:
        hp = _plan_summary_int(decision, "current_hp")
    max_cards, max_cards_source = _plan_card_play_limit(decision)

    initial = {
        "energy": energy,
        "stars": stars,
        "cards_played_this_turn": cards_played,
        "max_cards_per_turn": max_cards,
        "max_cards_source": max_cards_source,
        "player_block": block,
        "player_hp": hp,
    }
    projected_steps: list[dict[str, Any]] = []
    limitations: list[str] = []
    consumed_card_refs: set[str] = set()
    shared_limit_counts: dict[str, int] = {}
    aggregate_block_gain = 0
    aggregate_damage: dict[str, int] = {}
    validation_complete = True
    effects_complete = True
    all_steps_preflight_passed = True
    known_infeasible = False
    stop_reason: str | None = None
    stopped_before_step: int | None = None
    stopped_after_step: int | None = None

    for index, step in enumerate(steps):
        choice, match_error, match_count = _match_plan_choice(decision, step)
        if choice is None:
            all_steps_preflight_passed = False
            known_infeasible = True
            stop_reason = match_error
            stopped_before_step = index
            limitations.append(f"step {index}: match_count={match_count}")
            break

        kind = str(choice.get("kind") or "")
        source = choice.get("source") if isinstance(choice.get("source"), dict) else {}
        card_ref = str(source.get("card_ref") or "")
        card = _plan_choice_card(choice, hand_by_ref)
        preview = choice.get("preview") if isinstance(choice.get("preview"), dict) else {}
        energy_before = energy
        stars_before = stars
        cards_before = cards_played
        block_before = block
        step_limitations: list[str] = []
        step_damage: dict[str, int] = {}
        step_block_gain = 0
        step_energy_gain = 0
        step_star_gain = 0
        step_hp_loss = 0
        newly_projected_kill = False

        if kind == "play_card":
            if card_ref and card_ref in consumed_card_refs:
                all_steps_preflight_passed = False
                known_infeasible = True
                stop_reason = "card_ref_reused_in_plan"
                stopped_before_step = index
                break

            costs_x = bool(card.get("costs_x"))
            star_costs_x = bool(card.get("star_costs_x"))
            energy_cost = _optional_int(preview.get("energy_cost"))
            if energy_cost is None:
                energy_cost = _optional_int(card.get("energy_cost"))
            star_cost = _optional_int(preview.get("star_cost"))
            if star_cost is None:
                star_cost = _optional_int(card.get("star_cost"))

            if costs_x:
                if energy is None:
                    validation_complete = False
                    step_limitations.append("x_energy_cost_unknown")
                else:
                    energy_cost = energy
            elif energy_cost is not None:
                energy_cost = max(0, energy_cost)
                if energy is None:
                    validation_complete = False
                    step_limitations.append("energy_budget_unknown")
                elif energy < energy_cost:
                    all_steps_preflight_passed = False
                    known_infeasible = True
                    stop_reason = "not_enough_energy"
                    stopped_before_step = index
                    break

            if star_costs_x:
                if stars is None:
                    validation_complete = False
                    step_limitations.append("x_star_cost_unknown")
                else:
                    star_cost = stars
            elif star_cost is not None:
                star_cost = max(0, star_cost)
                if stars is None:
                    validation_complete = False
                    step_limitations.append("star_budget_unknown")
                elif stars < star_cost:
                    all_steps_preflight_passed = False
                    known_infeasible = True
                    stop_reason = "not_enough_stars"
                    stopped_before_step = index
                    break

            if max_cards is not None:
                if cards_played is None:
                    validation_complete = False
                    step_limitations.append("cards_played_count_unknown")
                elif cards_played + 1 > max_cards:
                    all_steps_preflight_passed = False
                    known_infeasible = True
                    stop_reason = "card_play_limit_reached"
                    stopped_before_step = index
                    break

            shared_limit = preview.get("shared_play_limit")
            if isinstance(shared_limit, dict):
                group_id = str(shared_limit.get("group_id") or "")
                group_max = _optional_int(shared_limit.get("max_plays_per_turn"))
                if group_id and group_max is not None:
                    used = shared_limit_counts.get(group_id, 0)
                    if used + 1 > group_max:
                        all_steps_preflight_passed = False
                        known_infeasible = True
                        stop_reason = "shared_play_limit_reached"
                        stopped_before_step = index
                        break
                    shared_limit_counts[group_id] = used + 1

            if energy is not None and energy_cost is not None:
                energy -= energy_cost
            if stars is not None and star_cost is not None:
                stars -= star_cost
            if cards_played is not None:
                cards_played += 1
            if card_ref:
                consumed_card_refs.add(card_ref)

            resource_effects = _plan_immediate_resource_effects(card)
            energy_multiplier = _optional_int(resource_effects.get("energy_multiplier"))
            if energy_multiplier is not None:
                if energy is None:
                    validation_complete = False
                    step_limitations.append("energy_multiplier_budget_unknown")
                else:
                    energy_before_effect = energy
                    energy *= max(0, energy_multiplier)
                    step_energy_gain += energy - energy_before_effect
            energy_gain = _optional_int(resource_effects.get("energy_gain"))
            if energy_gain is not None:
                if energy is None:
                    validation_complete = False
                    step_limitations.append("energy_gain_budget_unknown")
                else:
                    energy += energy_gain
                    step_energy_gain += energy_gain
            star_gain = _optional_int(resource_effects.get("star_gain"))
            if star_gain is not None:
                if stars is None:
                    validation_complete = False
                    step_limitations.append("star_gain_budget_unknown")
                else:
                    stars += star_gain
                    step_star_gain += star_gain
            hp_loss = _optional_int(resource_effects.get("hp_loss"))
            if hp_loss is not None:
                step_hp_loss = hp_loss
                if hp is None:
                    effects_complete = False
                    step_limitations.append("player_hp_unknown_for_hp_loss")
                else:
                    hp = max(0, hp - hp_loss)

            block_preview = preview.get("block")
            block_gain = None
            if isinstance(block_preview, dict):
                block_gain = _optional_int(block_preview.get("estimated_gain"))
            if block_gain is not None:
                step_block_gain = block_gain
                aggregate_block_gain += block_gain
                if block is not None:
                    block += block_gain

            damage_preview = preview.get("damage")
            if isinstance(damage_preview, dict) and isinstance(damage_preview.get("targets"), list):
                for target in damage_preview["targets"]:
                    if not isinstance(target, dict):
                        continue
                    enemy = _plan_target_state(target, choice, enemies)
                    if enemy is None:
                        effects_complete = False
                        step_limitations.append("damage_target_state_unavailable")
                        continue
                    if enemy.get("alive") is False:
                        all_steps_preflight_passed = False
                        known_infeasible = True
                        stop_reason = "projected_target_not_alive"
                        stopped_before_step = index
                        break
                    per_hit = _optional_int(target.get("pre_target_per_hit"))
                    if per_hit is None:
                        per_hit = _optional_int(target.get("final_per_hit"))
                    card_id = str(card.get("card_id") or preview.get("card_id") or "").strip().upper()
                    if card_id == "BODY_SLAM":
                        if block_before is None:
                            effects_complete = False
                            step_limitations.append("body_slam_projected_block_unknown")
                            continue
                        per_hit = max(0, block_before)
                    hit_count = _optional_int(target.get("hit_count")) or 1
                    if per_hit is None:
                        effects_complete = False
                        step_limitations.append("damage_amount_unavailable")
                        continue
                    if enemy.get("vulnerable") and target.get("pre_target_per_hit") is not None:
                        per_hit = int(per_hit * 1.5)
                    total_damage = max(0, per_hit) * max(1, hit_count)
                    enemy_block = enemy.get("block")
                    enemy_hp = enemy.get("hp")
                    if isinstance(enemy_block, int):
                        block_damage = min(enemy_block, total_damage)
                        enemy["block"] = enemy_block - block_damage
                    else:
                        effects_complete = False
                        step_limitations.append("target_block_unknown")
                        continue
                    hp_damage = total_damage - block_damage
                    if isinstance(enemy_hp, int):
                        enemy["hp"] = max(0, enemy_hp - hp_damage)
                        enemy["alive"] = enemy["hp"] > 0
                        newly_projected_kill = newly_projected_kill or (enemy_hp > 0 and enemy["hp"] == 0)
                    else:
                        effects_complete = False
                    enemy_key = str(enemy.get("enemy_ref") or f"enemy:{enemy.get('index')}")
                    aggregate_damage[enemy_key] = aggregate_damage.get(enemy_key, 0) + hp_damage
                    step_damage[enemy_key] = step_damage.get(enemy_key, 0) + hp_damage
                if known_infeasible:
                    break

            powers_applied = preview.get("powers_applied")
            unsupported_power_change = False
            if isinstance(powers_applied, list):
                for power in powers_applied:
                    if not isinstance(power, dict):
                        continue
                    power_name = str(power.get("power") or power.get("power_id") or "").upper()
                    if "VULNERABLE" in power_name:
                        target_state = _plan_target_state({}, choice, enemies)
                        if target_state is not None:
                            target_state["vulnerable"] = True
                    elif power_name:
                        unsupported_power_change = True

            barriers = _plan_information_barriers(card, preview)
            barriers.extend(
                reason
                for reason in resource_effects.get("barriers", [])
                if isinstance(reason, str)
            )
            if not preview:
                effects_complete = False
            if unsupported_power_change:
                barriers.append("unsupported_power_changes_following_preview")
            if (costs_x or star_costs_x) and index < requested_count - 1:
                barriers.append("x_cost_changes_following_budget")
            if newly_projected_kill and index < requested_count - 1:
                barriers.append("projected_kill_may_change_decision")
            if hp == 0 and step_hp_loss > 0:
                barriers.append("projected_player_death_may_change_decision")

            if barriers:
                effects_complete = False
                step_limitations.extend(barriers)
                if index < requested_count - 1:
                    validation_complete = False
                    all_steps_preflight_passed = False
                    stop_reason = "information_boundary_after_step"
                    stopped_after_step = index

        elif kind == "use_potion":
            effects_complete = False
            step_limitations.append("potion_effects_not_folded_by_plan_preview")
            if index < requested_count - 1:
                validation_complete = False
                all_steps_preflight_passed = False
                stop_reason = "information_boundary_after_step"
                stopped_after_step = index

        projected_steps.append(
            {
                "step": index,
                "action_id": choice.get("action_id"),
                "kind": kind,
                "card_ref": card_ref or None,
                "energy_before": energy_before,
                "energy_after": energy,
                "stars_before": stars_before,
                "stars_after": stars,
                "cards_played_before": cards_before,
                "cards_played_after": cards_played,
                "block_before": block_before,
                "block_after": block,
                "block_gain": step_block_gain,
                "energy_gain": step_energy_gain,
                "star_gain": step_star_gain,
                "hp_loss": step_hp_loss,
                "hp_damage_by_target": step_damage,
                "limitations": list(dict.fromkeys(step_limitations)),
            }
        )
        limitations.extend(f"step {index}: {reason}" for reason in step_limitations)
        if stopped_after_step is not None:
            break

    valid_prefix_count = len(projected_steps)
    projected_enemy_values = {
        id(value): value for value in enemies.values()
    }.values()
    return {
        **base,
        "status": "previewed",
        "validation_complete": validation_complete,
        "effects_complete": effects_complete,
        "all_steps_preflight_passed": all_steps_preflight_passed,
        "executable_all": all_steps_preflight_passed and validation_complete,
        "safe_to_execute_strict": all_steps_preflight_passed and validation_complete,
        "known_infeasible": known_infeasible,
        "valid_prefix_count": valid_prefix_count,
        "stop_reason": stop_reason,
        "stopped_before_step": stopped_before_step,
        "stopped_after_step": stopped_after_step,
        "initial": initial,
        "projected": {
            "energy": energy,
            "stars": stars,
            "cards_played_this_turn": cards_played,
            "player_block": block,
            "player_hp": hp,
            "enemies": list(projected_enemy_values),
        },
        "aggregate": {
            "block_gain": aggregate_block_gain,
            "hp_damage_by_target": aggregate_damage,
        },
        "steps": projected_steps,
        "limitations": list(dict.fromkeys(limitations)),
        "coverage": {
            "stable_choice_matching": True,
            "energy_and_star_budget": True,
            "known_card_play_limits": True,
            "sequential_direct_damage_and_block": True,
            "transactional_engine_dry_run": False,
        },
    }


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


def _register_character_ascension_tool(mcp: FastMCP, name: str, description: str, handler: ToolHandler) -> None:
    def tool(character_id: str, ascension: int) -> dict[str, Any]:
        return handler(character_id=character_id, ascension=ascension)

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

        if spec.kind == "character_ascension":
            _register_character_ascension_tool(mcp, spec.name, spec.description, handler)
            continue

        raise RuntimeError(f"Unsupported action tool kind: {spec.kind}")


def _register_debug_tools(mcp: FastMCP, sts2: Sts2Client) -> None:
    @mcp.tool
    def run_console_command(command: str) -> dict[str, Any]:
        """Run a game dev-console command for local validation or debugging."""
        return sts2.run_console_command(command=command)

    @mcp.tool
    def search_game_data(
        query: str,
        collections: str = "",
        limit: int = 25,
    ) -> dict[str, Any]:
        """Search live model IDs, names, descriptions, and model types for debugging."""
        requested_collections = [
            value.strip()
            for value in collections.split(",")
            if value.strip()
        ]
        return sts2.search_game_data(
            query=query,
            collections=requested_collections or None,
            limit=limit,
        )

    @mcp.tool
    def list_model_ids(
        collection: str,
        query: str = "",
        offset: int = 0,
        limit: int = 100,
    ) -> dict[str, Any]:
        """List live model IDs in one collection, optionally filtered by text."""
        return sts2.list_model_ids(
            collection=collection,
            query=query or None,
            offset=offset,
            limit=limit,
        )


def create_server(client: Sts2Client | None = None, tool_profile: str | None = None) -> FastMCP:
    sts2 = client or Sts2Client()
    versioned_game_data = VersionedGameDataStore()
    knowledge = Sts2KnowledgeBase()
    handoff = Sts2HandoffService(knowledge)
    profile = _normalize_tool_profile(tool_profile)
    mcp = FastMCP("STS2 AI MCP")
    decision_cache: dict[str, dict[str, Any]] = {}
    plan_result_cache: dict[str, tuple[str, dict[str, Any]]] = {}
    active_snapshot: GameDataSnapshot | None = None
    snapshot_lock = threading.Lock()

    def _get_versioned_snapshot() -> GameDataSnapshot:
        nonlocal active_snapshot
        health = sts2.get_health()
        game_version = str(health.get("game_version", "")).strip()
        mod_version = str(health.get("mod_version", "")).strip()
        if not game_version:
            raise GameDataVersionError("The running game did not report game_version.")
        if (
            active_snapshot is not None
            and active_snapshot.game_version == game_version
            and (not mod_version or active_snapshot.manifest.get("mod_version") == mod_version)
        ):
            return active_snapshot

        with snapshot_lock:
            if (
                active_snapshot is not None
                and active_snapshot.game_version == game_version
                and (not mod_version or active_snapshot.manifest.get("mod_version") == mod_version)
            ):
                return active_snapshot

            try:
                active_snapshot = versioned_game_data.load(game_version)
            except GameDataVersionError:
                exported = sts2.export_game_data()
                active_snapshot = versioned_game_data.save_export(exported)

            if mod_version and active_snapshot.manifest.get("mod_version") != mod_version:
                exported = sts2.export_game_data()
                active_snapshot = versioned_game_data.save_export(exported)

            if active_snapshot.game_version != game_version:
                raise GameDataVersionError(
                    f"Active snapshot version {active_snapshot.game_version!r} does not match {game_version!r}."
                )
            return active_snapshot

    def _collect_relevant_ids(value: Any, result: dict[str, set[str]]) -> None:
        if isinstance(value, dict):
            for key, nested in value.items():
                collection = _RELEVANT_ID_COLLECTIONS.get(str(key))
                if collection and isinstance(nested, str) and nested.strip():
                    result.setdefault(collection, set()).add(nested.strip())
                _collect_relevant_ids(nested, result)
        elif isinstance(value, list):
            for nested in value:
                _collect_relevant_ids(nested, result)

    def _hydrate_decision_knowledge(decision: dict[str, Any]) -> dict[str, Any]:
        try:
            snapshot = _get_versioned_snapshot()
        except (AttributeError, GameDataVersionError, Sts2ApiError, RuntimeError, TypeError):
            return decision

        requested: dict[str, set[str]] = {}
        _collect_relevant_ids(decision.get("context"), requested)
        _collect_relevant_ids(decision.get("summary"), requested)
        _collect_relevant_ids(decision.get("choices"), requested)
        scene = _detect_scene_from_screen(str(decision.get("screen", "")))
        relevant: dict[str, Any] = {"glossary": _CORE_GLOSSARY}
        for collection, ids in sorted(requested.items()):
            fields = _SCENE_FIELD_SETS.get(scene, {}).get(collection)
            lookup = snapshot.lookup(
                [{"collection": collection, "id": item_id} for item_id in sorted(ids)],
                fields=fields,
            )
            values = {
                key.removeprefix(f"{collection}:"): value
                for key, value in lookup["items"].items()
                if value is not None
            }
            if values:
                relevant[collection] = values

        decision["knowledge"] = {
            "metadata": {
                "schema_version": snapshot.manifest.get("schema_version"),
                "game_version": snapshot.game_version,
                "mod_version": snapshot.manifest.get("mod_version"),
                "data_source": "mcp_versioned_cache",
                "exported_at_utc": snapshot.manifest.get("exported_at_utc"),
                "content_hash": snapshot.manifest.get("content_hash"),
                "curated_overrides": snapshot.manifest.get("curated_overrides", []),
            },
            "relevant": relevant,
        }
        return decision

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

    def _cache_decision(payload: dict[str, Any], *, include_relevant: bool = True) -> dict[str, Any]:
        decision = payload.get("decision")
        if isinstance(decision, dict) and isinstance(decision.get("decision_id"), str):
            if include_relevant:
                _hydrate_decision_knowledge(decision)
            decision_cache[decision["decision_id"]] = decision
        return payload

    def _cache_next_decision(result: dict[str, Any]) -> dict[str, Any] | None:
        decision = result.get("next_decision")
        if isinstance(decision, dict) and isinstance(decision.get("decision_id"), str):
            _hydrate_decision_knowledge(decision)
            decision_cache[decision["decision_id"]] = decision
            return decision
        return None

    def _resolve_plan_start(decision_id: str) -> dict[str, Any] | None:
        decision = decision_cache.get(decision_id)
        if decision is not None:
            return decision

        try:
            payload = _cache_decision(
                sts2.get_current_decision(
                    profile="ai_safe",
                    include_raw_state=False,
                    include_relevant_game_data=False,
                ),
                include_relevant=False,
            )
        except Sts2ApiError:
            return None
        current = payload.get("decision")
        if isinstance(current, dict) and current.get("decision_id") == decision_id:
            return current
        return None

    def _resolve_plan_next(result: dict[str, Any], previous_decision_id: str) -> dict[str, Any] | None:
        next_decision = _cache_next_decision(result)
        if next_decision is not None:
            return next_decision

        try:
            payload = _cache_decision(
                sts2.wait_for_decision(
                    timeout_ms=5_000,
                    profile="ai_safe",
                    include_raw_state=False,
                    include_relevant_game_data=False,
                    after_decision_id=previous_decision_id,
                )
            )
        except Sts2ApiError:
            return None
        decision = payload.get("decision")
        return decision if isinstance(decision, dict) else None

    def _take_logged_action(
        decision: dict[str, Any] | None,
        decision_id: str,
        action_id: str,
        client_note: str | None,
    ) -> dict[str, Any]:
        result = sts2.take_action(
            decision_id=decision_id,
            action_id=action_id,
            params={},
            client_note=client_note,
        )
        if result.get("status") == "pending" or result.get("stable") is False:
            try:
                timeout_seconds = max(
                    0.1,
                    float(
                        os.getenv(
                            "STS2_MCP_ACTION_HANDOFF_TIMEOUT_SECONDS",
                            str(_DEFAULT_ACTION_HANDOFF_TIMEOUT_SECONDS),
                        )
                    ),
                )
            except ValueError:
                timeout_seconds = _DEFAULT_ACTION_HANDOFF_TIMEOUT_SECONDS

            deadline = time.monotonic() + timeout_seconds
            last_error: Sts2ApiError | None = None
            while time.monotonic() < deadline:
                remaining_ms = max(100, int((deadline - time.monotonic()) * 1000))
                wait_ms = min(_ACTION_HANDOFF_WAIT_SLICE_MS, remaining_ms)
                try:
                    payload = _cache_decision(
                        sts2.wait_for_decision(
                            timeout_ms=wait_ms,
                            profile="ai_safe",
                            include_raw_state=False,
                            include_relevant_game_data=False,
                            after_decision_id=decision_id,
                        ),
                        include_relevant=False,
                    )
                except Sts2ApiError as exc:
                    if not exc.retryable:
                        raise
                    last_error = exc
                    remaining_seconds = deadline - time.monotonic()
                    if remaining_seconds > 0:
                        time.sleep(min(0.25, remaining_seconds))
                    continue

                next_decision = payload.get("decision")
                if not isinstance(next_decision, dict):
                    continue
                if next_decision.get("decision_id") == decision_id:
                    continue

                result = {
                    **result,
                    "status": "completed",
                    "stable": True,
                    "message": "Action completed; next decision is ready.",
                    "next_decision": next_decision,
                }
                break
            else:
                raise Sts2ApiError(
                    status_code=0,
                    code="action_transition_timeout",
                    message="Action was accepted, but no new actionable decision became ready before the MCP handoff deadline.",
                    details={
                        "previous_decision_id": decision_id,
                        "timeout_seconds": timeout_seconds,
                        "last_error": str(last_error) if last_error is not None else None,
                    },
                    retryable=True,
                )

        _cache_next_decision(result)
        logging_result = _append_decision_log(
            decision=decision,
            action_id=action_id,
            client_note=client_note,
            result=result,
        )
        if logging_result.get("ok") is False:
            return {**result, "logging_warning": logging_result.get("warning")}
        return {**result, "logging": logging_result}

    def _execute_action_plan_impl(
        decision_id: str,
        steps: list[dict[str, Any]],
        mode: str,
        client_note: str | None,
    ) -> dict[str, Any]:
        normalized_mode = (mode or "strict").strip().lower()
        if normalized_mode != "strict":
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "only_strict_mode_is_supported",
            }
        if not steps:
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": 0,
                "stop_reason": "plan_requires_at_least_one_step",
            }
        if not all(isinstance(step, dict) for step in steps):
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "each_plan_step_must_be_an_object",
            }

        if any(not str(step.get("kind") or "").strip() for step in steps):
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "each_plan_step_requires_kind",
            }

        requested_kinds = {str(step.get("kind") or "").strip() for step in steps}
        unsupported = requested_kinds - _PLAN_ALLOWED_KINDS
        if unsupported:
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "unsupported_action_kind",
                "unsupported_kinds": sorted(unsupported),
            }

        contains_combat = bool(requested_kinds & _PLAN_COMBAT_KINDS)
        contains_selection = bool(requested_kinds & _PLAN_SELECTION_KINDS)
        if contains_combat and contains_selection:
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "plan_cannot_mix_combat_and_selection_actions",
            }

        selected_refs = [
            str(step.get("card_ref") or "").strip()
            for step in steps
            if step.get("kind") == "select_deck_card" and step.get("card_ref")
        ]
        if len(set(selected_refs)) != len(selected_refs):
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "selection_card_refs_must_be_unique",
            }

        max_steps = _MAX_COMBAT_PLAN_STEPS if contains_combat else _MAX_SELECTION_PLAN_STEPS
        if len(steps) > max_steps:
            return {
                "status": "rejected",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "plan_too_long",
                "max_steps": max_steps,
            }

        decision = _resolve_plan_start(decision_id)
        if decision is None:
            return {
                "status": "stopped",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": "starting_decision_is_not_current",
            }

        preflight = _preview_combat_action_plan(decision, steps, normalized_mode) if contains_combat else None
        if isinstance(preflight, dict) and preflight.get("known_infeasible") is True:
            return {
                "status": "stopped",
                "stable": True,
                "executed_count": 0,
                "requested_count": len(steps),
                "stop_reason": preflight.get("stop_reason") or "plan_preflight_failed",
                "stopped_before_step": preflight.get("stopped_before_step"),
                "preflight": preflight,
                "next_decision": decision,
            }

        executed: list[dict[str, Any]] = []
        next_decision: dict[str, Any] | None = decision
        for index, step in enumerate(steps):
            choice, match_error, match_count = _match_plan_choice(decision, step)
            if choice is None:
                return {
                    "status": "stopped",
                    "stable": True,
                    "executed_count": len(executed),
                    "requested_count": len(steps),
                    "stop_reason": match_error,
                    "match_count": match_count,
                    "stopped_before_step": index,
                    "steps": executed,
                    "next_decision": decision,
                }

            kind = str(choice.get("kind") or "")
            if kind not in _PLAN_ALLOWED_KINDS:
                return {
                    "status": "stopped",
                    "stable": True,
                    "executed_count": len(executed),
                    "requested_count": len(steps),
                    "stop_reason": "resolved_action_kind_is_not_plan_safe",
                    "resolved_kind": kind,
                    "stopped_before_step": index,
                    "steps": executed,
                    "next_decision": decision,
                }

            current_decision_id = str(decision.get("decision_id") or "")
            action_id = str(choice.get("action_id") or "")
            step_note = step.get("note") if isinstance(step.get("note"), str) else client_note
            try:
                action_result = _take_logged_action(
                    decision=decision,
                    decision_id=current_decision_id,
                    action_id=action_id,
                    client_note=step_note,
                )
            except Sts2ApiError as exc:
                return {
                    "status": "stopped",
                    "stable": False,
                    "executed_count": len(executed),
                    "requested_count": len(steps),
                    "stop_reason": "action_error",
                    "stopped_at_step": index,
                    "error": {
                        "code": exc.code,
                        "message": exc.message,
                        "retryable": exc.retryable,
                        "details": exc.details,
                    },
                    "steps": executed,
                    "next_decision": decision,
                }

            executed.append(
                {
                    "step": index,
                    "decision_id": current_decision_id,
                    "action_id": action_id,
                    "kind": kind,
                    "status": action_result.get("status"),
                    "stable": action_result.get("stable"),
                    "logging": action_result.get("logging"),
                    "logging_warning": action_result.get("logging_warning"),
                }
            )
            if action_result.get("stable") is not True:
                return {
                    "status": "stopped",
                    "stable": False,
                    "executed_count": len(executed),
                    "requested_count": len(steps),
                    "stop_reason": "action_did_not_reach_stable_state",
                    "stopped_at_step": index,
                    "steps": executed,
                    "next_decision": _cache_next_decision(action_result),
                }

            next_decision = _resolve_plan_next(action_result, current_decision_id)
            if index == len(steps) - 1:
                break
            if next_decision is None:
                return {
                    "status": "stopped",
                    "stable": True,
                    "executed_count": len(executed),
                    "requested_count": len(steps),
                    "stop_reason": "next_decision_unavailable",
                    "stopped_after_step": index,
                    "steps": executed,
                }

            transition_error = _strict_plan_transition_error(
                before=decision,
                after=next_decision,
                choice=choice,
                next_step=steps[index + 1],
            )
            if transition_error:
                return {
                    "status": "stopped",
                    "stable": True,
                    "executed_count": len(executed),
                    "requested_count": len(steps),
                    "stop_reason": transition_error,
                    "stopped_after_step": index,
                    "steps": executed,
                    "next_decision": next_decision,
                }
            decision = next_decision

        return {
            "status": "completed",
            "stable": True,
            "executed_count": len(executed),
            "requested_count": len(steps),
            "stop_reason": None,
            "steps": executed,
            "next_decision": next_decision,
        }

    def _execute_cached_action_plan(
        decision_id: str,
        steps: list[dict[str, Any]],
        mode: str,
        client_note: str | None,
        plan_id: str | None,
    ) -> dict[str, Any]:
        normalized_plan_id = (plan_id or "").strip()
        fingerprint = json.dumps(
            {
                "decision_id": decision_id,
                "steps": steps,
                "mode": mode,
                "client_note": client_note,
            },
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
        if normalized_plan_id and normalized_plan_id in plan_result_cache:
            cached_fingerprint, cached_result = plan_result_cache[normalized_plan_id]
            if cached_fingerprint != fingerprint:
                return {
                    "status": "rejected",
                    "stable": True,
                    "executed_count": 0,
                    "requested_count": len(steps),
                    "stop_reason": "plan_id_reused_with_different_request",
                }
            return {**cached_result, "idempotent_replay": True}

        result = _execute_action_plan_impl(decision_id, steps, mode, client_note)
        if normalized_plan_id:
            if len(plan_result_cache) >= _MAX_CACHED_PLAN_RESULTS:
                plan_result_cache.pop(next(iter(plan_result_cache)))
            plan_result_cache[normalized_plan_id] = (fingerprint, result)
        return result

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
            "runtime_contract": evaluate_runtime_contract(health),
            "mcp_tool_profile": profile,
            "mcp_capabilities": {
                "ai_safe_v2": profile == "ai_safe_v2",
                "tools": [
                    "health_check",
                    "wait_for_decision",
                    "get_current_decision",
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
                    include_relevant_game_data=False,
                ),
                include_relevant=include_relevant_game_data,
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
                    include_relevant_game_data=False,
                    after_decision_id=after_decision_id,
                ),
                include_relevant=include_relevant_game_data,
            )

        @mcp.tool
        def preview_action(decision_id: str, action_id: str) -> dict[str, Any]:
            """Preview one current decision action without mutating game state."""
            return sts2.preview_action(decision_id=decision_id, action_id=action_id)

        @mcp.tool
        def preview_action_plan(
            decision_id: str,
            steps: list[ActionPlanStep],
            mode: str = "strict",
        ) -> dict[str, Any]:
            """Preflight a short combat plan without mutating the game.

            The preview folds known energy, star, card-play-limit, direct damage,
            and direct Block effects through a shadow state. It stops at explicit
            information boundaries such as draws, generated/returned cards,
            upgrades, random effects, unsupported powers, or projected kills.
            This is not a transactional engine dry-run.
            """
            decision = _resolve_plan_start(decision_id)
            if decision is None:
                return {
                    "status": "stopped",
                    "mutation_performed": False,
                    "requested_count": len(steps),
                    "stop_reason": "starting_decision_is_not_current",
                }
            return _preview_combat_action_plan(decision, steps, mode)

        @mcp.tool
        def run_evaluator(
            decision_id: str,
            candidate_card_ids: list[str] | None = None,
            horizons: list[int] | None = None,
            time_budget_ms: int = DEFAULT_TIME_BUDGET_MS,
            max_states: int = DEFAULT_MAX_STATES,
        ) -> dict[str, Any]:
            """Calculate public deck facts and before/after candidate deltas without choosing a card.

            This tool reads only a decision already cached by get_current_decision or
            wait_for_decision. It performs no game request and no action. Results keep
            candidate input order and are bounded by a 500ms/4096-state hard ceiling;
            exhausted budgets return a structured partial result.
            """
            decision = decision_cache.get(decision_id)
            if decision is None:
                return {
                    "schema_version": 1,
                    "tool": "run_evaluator",
                    "status": "rejected",
                    "stop_reason": "decision_not_cached",
                    "mutation_performed": False,
                    "hint": "Call get_current_decision or wait_for_decision first and reuse its decision_id.",
                }
            try:
                return evaluate_run_decision(
                    decision,
                    candidate_card_ids=candidate_card_ids or [],
                    horizons=horizons or [5, 10, 15],
                    time_budget_ms=time_budget_ms,
                    max_states=max_states,
                )
            except ReasoningInputError as exc:
                return {
                    "schema_version": 1,
                    "tool": "run_evaluator",
                    "status": "rejected",
                    "stop_reason": "invalid_input",
                    "mutation_performed": False,
                    "error": str(exc),
                }

        @mcp.tool
        def combat_horizon(
            decision_id: str,
            lines: list[CombatHorizonLine],
            time_budget_ms: int = DEFAULT_TIME_BUDGET_MS,
            max_states: int = DEFAULT_MAX_STATES,
        ) -> dict[str, Any]:
            """Check supplied combat lines and current-intent survival without executing or ranking them.

            The calling model supplies up to eight candidate lines of at most five
            steps. This tool reuses the deterministic action-plan preview, adds
            current exposed-intent arithmetic after projected direct kills, and
            returns lines in input order. It never searches for lines, contacts the
            game, or performs an action. Work is hard-capped at 500ms/4096 states.
            """
            decision = decision_cache.get(decision_id)
            if decision is None:
                return {
                    "schema_version": 1,
                    "tool": "combat_horizon",
                    "status": "rejected",
                    "stop_reason": "decision_not_cached",
                    "mutation_performed": False,
                    "hint": "Call get_current_decision or wait_for_decision first and reuse its decision_id.",
                }
            try:
                return evaluate_combat_horizon(
                    decision,
                    lines=lines,
                    previewer=_preview_combat_action_plan,
                    time_budget_ms=time_budget_ms,
                    max_states=max_states,
                )
            except ReasoningInputError as exc:
                return {
                    "schema_version": 1,
                    "tool": "combat_horizon",
                    "status": "rejected",
                    "stop_reason": "invalid_input",
                    "mutation_performed": False,
                    "error": str(exc),
                }

        @mcp.tool
        def get_action_trace(after_sequence: int = 0) -> dict[str, Any]:
            """Return ordered engine GameAction and GenericHookGameAction events."""
            return sts2.get_action_trace(after_sequence=after_sequence)

        @mcp.tool
        def take_action(
            decision_id: str,
            action_id: str,
            client_note: str | None = None,
        ) -> dict[str, Any]:
            """Execute one action from the current v2 decision window."""
            decision = decision_cache.get(decision_id)
            return _take_logged_action(
                decision=decision,
                decision_id=decision_id,
                action_id=action_id,
                client_note=client_note,
            )

        @mcp.tool
        def execute_action_plan(
            decision_id: str,
            steps: list[ActionPlanStep],
            mode: str = "strict",
            client_note: str | None = None,
            plan_id: str | None = None,
        ) -> dict[str, Any]:
            """Execute a short conditional plan, revalidating every step against each fresh v2 decision.

            Prefer this over repeated take_action calls when the agent has already
            evaluated a deterministic 2-5 step combat line whose cards do not draw,
            generate, return, transform, or randomly discard cards. Typical safe plans
            include several basic attacks/blocks or a fixed power-plus-block sequence.
            Keep using take_action for information-revealing or unresolved choices.

            Supported kinds are play_card/use_potion (up to 5 steps) or
            select_deck_card/confirm_selection (up to 12 steps). Identify cards with
            card_ref and targeted choices with target_entity_ref or target_ref. Strict mode
            stops on phase changes, draws, returned cards, extra discards, ambiguity, or
            any unavailable action. Previously completed steps are not rolled back.
            """
            return _execute_cached_action_plan(
                decision_id=decision_id,
                steps=steps,
                mode=mode,
                client_note=client_note,
                plan_id=plan_id,
            )

        @mcp.tool
        def select_cards(
            decision_id: str,
            card_refs: list[str],
            confirm: bool = True,
            client_note: str | None = None,
            plan_id: str | None = None,
        ) -> dict[str, Any]:
            """Select several cards from one current selection overlay and optionally confirm."""
            normalized_refs = [str(card_ref).strip() for card_ref in card_refs if str(card_ref).strip()]
            if not normalized_refs and not confirm:
                return {
                    "status": "rejected",
                    "stable": True,
                    "executed_count": 0,
                    "requested_count": 0,
                    "stop_reason": "selection_plan_requires_cards_or_confirmation",
                }
            if len(set(normalized_refs)) != len(normalized_refs):
                return {
                    "status": "rejected",
                    "stable": True,
                    "executed_count": 0,
                    "requested_count": len(normalized_refs),
                    "stop_reason": "card_refs_must_be_unique",
                }

            steps: list[dict[str, Any]] = [
                {"kind": "select_deck_card", "card_ref": card_ref}
                for card_ref in normalized_refs
            ]
            if confirm:
                steps.append({"kind": "confirm_selection"})
            return _execute_cached_action_plan(
                decision_id=decision_id,
                steps=steps,
                mode="strict",
                client_note=client_note,
                plan_id=plan_id,
            )

        @mcp.tool
        def select_character(
            character_id: str,
            ascension: int,
            client_note: str | None = None,
        ) -> dict[str, Any]:
            """Select one unlocked character and exact ascension level in a single action."""
            normalized_character = character_id.strip().upper()
            requested_ascension = int(ascension)
            wrapper = _cache_decision(
                sts2.get_current_decision(
                    profile="ai_safe",
                    include_raw_state=False,
                    include_relevant_game_data=False,
                ),
                include_relevant=False,
            )
            decision = wrapper.get("decision") if isinstance(wrapper, dict) else None
            if not isinstance(decision, dict) or decision.get("phase") != "character_select":
                return {
                    "status": "rejected",
                    "stable": True,
                    "stop_reason": "character_selection_not_available",
                    "current_phase": decision.get("phase") if isinstance(decision, dict) else None,
                }

            available_pairs: list[dict[str, Any]] = []
            matched_choice: dict[str, Any] | None = None
            for choice in decision.get("choices", []):
                if not isinstance(choice, dict) or choice.get("kind") != "select_character":
                    continue
                source = choice.get("source")
                if not isinstance(source, dict):
                    continue
                choice_character = str(source.get("character_id") or "").strip().upper()
                try:
                    choice_ascension = int(source.get("ascension"))
                except (TypeError, ValueError):
                    continue
                available_pairs.append(
                    {"character_id": choice_character, "ascension": choice_ascension}
                )
                if choice_character == normalized_character and choice_ascension == requested_ascension:
                    matched_choice = choice

            if matched_choice is None:
                return {
                    "status": "rejected",
                    "stable": True,
                    "stop_reason": "character_or_ascension_not_available",
                    "requested": {
                        "character_id": normalized_character,
                        "ascension": requested_ascension,
                    },
                    "available": available_pairs,
                }

            return _take_logged_action(
                decision=decision,
                decision_id=str(decision.get("decision_id") or ""),
                action_id=str(matched_choice.get("action_id") or ""),
                client_note=client_note,
            )

        @mcp.tool
        def lookup_game_data(items: list[dict[str, Any]], fields: list[str] | None = None) -> dict[str, Any]:
            """Lookup game metadata from the versioned local snapshot for the running game build."""
            try:
                return _get_versioned_snapshot().lookup(items=items, fields=fields)
            except (AttributeError, GameDataVersionError, Sts2ApiError, KeyError, RuntimeError, TypeError) as exc:
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

        if _debug_tools_enabled():
            _register_debug_tools(mcp, sts2)
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
        character_id: str | None = None,
        ascension: int | None = None,
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
            - For `select_character`, pass both `character_id` and exact `ascension`.
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
            character_id=character_id,
            ascension=ascension,
            client_context={
                "source": "mcp",
                "tool_name": "act",
                "tool_profile": profile,
            },
        )

    if profile == "full":
        _register_legacy_action_tools(mcp, sts2)

    if _debug_tools_enabled():
        _register_debug_tools(mcp, sts2)

    return mcp


def main() -> None:
    client = Sts2Client()
    enforce_startup_contract(client)
    create_server(client=client).run(transport="stdio", show_banner=False)


if __name__ == "__main__":
    main()
