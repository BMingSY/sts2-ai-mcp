#!/usr/bin/env python3
"""Conservative Slay the Spire 2 autoplay harness for STS2 AI MCP.

This script talks to the mod HTTP API, not directly to the MCP transport. It is
intended as a minimal, reproducible driver that another Codex agent can run or
patch while following the sts2-player skill.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any


PASSIVE_ACTIONS = {"save_and_quit", "discard_potion"}


class ApiError(RuntimeError):
    def __init__(self, message: str, *, code: str | None = None, retryable: bool = False, body: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.retryable = retryable
        self.body = body


class Blocker(RuntimeError):
    def __init__(self, message: str, state: dict[str, Any] | None = None) -> None:
        super().__init__(message)
        self.state = state


@dataclass
class Config:
    api: str
    until: str
    max_steps: int
    wait_timeout: float
    allow_forced_end: bool
    sleep: float


class Sts2Client:
    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")
        # Ignore host proxy settings for localhost. WSL environments often have
        # proxies that turn localhost requests into HTTP 502s.
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
        *,
        timeout: float = 30.0,
    ) -> Any:
        data = None
        headers = {"Accept": "application/json"}
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"

        req = urllib.request.Request(self.base_url + path, data=data, method=method, headers=headers)
        try:
            with self.opener.open(req, timeout=timeout) as response:
                obj = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            text = exc.read().decode("utf-8", "replace")
            raise self._api_error_from_text(text, fallback=f"HTTP {exc.code}: {text}") from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            raise ApiError(str(exc), retryable=True) from exc

        if not obj.get("ok"):
            err = obj.get("error") or {}
            raise ApiError(
                err.get("message", "API request failed"),
                code=err.get("code"),
                retryable=bool(err.get("retryable")),
                body=obj,
            )
        return obj.get("data")

    @staticmethod
    def _api_error_from_text(text: str, *, fallback: str) -> ApiError:
        try:
            obj = json.loads(text)
        except json.JSONDecodeError:
            return ApiError(fallback)
        err = obj.get("error") or {}
        return ApiError(
            err.get("message", fallback),
            code=err.get("code"),
            retryable=bool(err.get("retryable")),
            body=obj,
        )

    def state(self) -> dict[str, Any]:
        return self.request("GET", "/state", timeout=8)

    def act(
        self,
        action: str,
        *,
        card_index: int | None = None,
        target_index: int | None = None,
        option_index: int | None = None,
    ) -> Any:
        payload = {
            "action": action,
            "card_index": card_index,
            "target_index": target_index,
            "option_index": option_index,
            "client_context": {"source": "sts2-player", "tool_name": "sts2_autoplay.py"},
        }
        return self.request("POST", "/action", payload, timeout=35)


def actions(state: dict[str, Any]) -> list[str]:
    return [str(action) for action in state.get("available_actions") or []]


def real_actions(state: dict[str, Any]) -> list[str]:
    return [action for action in actions(state) if action not in PASSIVE_ACTIONS]


def combat(state: dict[str, Any]) -> dict[str, Any]:
    return state.get("combat") or {}


def player(state: dict[str, Any]) -> dict[str, Any]:
    return combat(state).get("player") or {}


def run(state: dict[str, Any]) -> dict[str, Any]:
    return state.get("run") or {}


def hand(state: dict[str, Any]) -> list[dict[str, Any]]:
    return list(combat(state).get("hand") or [])


def enemies(state: dict[str, Any]) -> list[dict[str, Any]]:
    return [
        enemy
        for enemy in combat(state).get("enemies") or []
        if enemy.get("is_alive", True) and enemy.get("is_hittable", True)
    ]


def hp_pair(state: dict[str, Any]) -> tuple[int, int]:
    r = run(state)
    p = player(state)
    if isinstance(r.get("current_hp"), int) and isinstance(r.get("max_hp"), int):
        return int(r["current_hp"]), int(r["max_hp"])
    if isinstance(r.get("hp"), str) and "/" in r["hp"]:
        left, right = r["hp"].split("/", 1)
        return int(left), int(right)
    return int(p.get("current_hp") or 0), int(p.get("max_hp") or 1)


def hp_ratio(state: dict[str, Any]) -> float:
    current, maximum = hp_pair(state)
    return current / max(1, maximum)


def dyn(card: dict[str, Any], name: str) -> int:
    total = 0
    needle = name.lower()
    for value in card.get("dynamic_values") or []:
        if needle in str(value.get("name", "")).lower():
            total += int(value.get("current_value") or 0)
    return total


def enemy_incoming(enemy: dict[str, Any]) -> int:
    total = 0
    for intent in enemy.get("intents") or []:
        total += int(intent.get("total_damage") or 0)
    return total


def incoming_damage(state: dict[str, Any]) -> int:
    return sum(enemy_incoming(enemy) for enemy in enemies(state))


def lethal_risks(state: dict[str, Any]) -> list[dict[str, Any]]:
    return list(combat(state).get("lethal_risks") or [])


def end_turn_will_kill_player(state: dict[str, Any]) -> bool:
    c = combat(state)
    return bool(c.get("end_turn_will_kill_player")) or any(
        bool(risk.get("will_kill_player")) for risk in lethal_risks(state)
    )


def effective_hp(enemy: dict[str, Any]) -> int:
    return int(enemy.get("current_hp") or 0) + int(enemy.get("block") or 0)


def summarize(state: dict[str, Any]) -> dict[str, Any]:
    p = player(state)
    current, maximum = hp_pair(state)
    return {
        "screen": state.get("screen"),
        "floor": run(state).get("floor"),
        "hp": f"{current}/{maximum}",
        "turn": state.get("turn"),
        "energy": p.get("energy"),
        "block": p.get("block"),
        "incoming": incoming_damage(state) if state.get("screen") == "COMBAT" else None,
        "end_turn_will_kill_player": end_turn_will_kill_player(state) if state.get("screen") == "COMBAT" else None,
        "lethal_risks": lethal_risks(state) if state.get("screen") == "COMBAT" else None,
        "actions": actions(state),
        "hand": [
            {
                "i": card.get("index"),
                "id": card.get("card_id"),
                "playable": card.get("playable"),
                "cost": card.get("energy_cost"),
                "damage": dyn(card, "damage"),
                "block": dyn(card, "block"),
                "reason": card.get("unplayable_reason"),
            }
            for card in hand(state)
        ],
        "enemies": [
            {
                "i": enemy.get("index"),
                "id": enemy.get("enemy_id"),
                "hp": f"{enemy.get('current_hp')}/{enemy.get('max_hp')}",
                "block": enemy.get("block"),
                "intent": enemy.get("intent"),
                "incoming": enemy_incoming(enemy),
            }
            for enemy in enemies(state)
        ],
    }


def print_state(prefix: str, state: dict[str, Any]) -> None:
    print(prefix, json.dumps(summarize(state), ensure_ascii=False, sort_keys=True), flush=True)


def execute(client: Sts2Client, action: str, **kwargs: int | None) -> None:
    state = client.state()
    if action not in actions(state):
        raise Blocker(f"action {action!r} not in latest available_actions", state)
    if action == "end_turn" and end_turn_will_kill_player(state):
        raise Blocker("end_turn_will_kill_player is true; refusing to end turn", state)

    printed = {key: value for key, value in kwargs.items() if value is not None}
    print("ACT", action, json.dumps(printed, sort_keys=True), flush=True)
    last_error: ApiError | None = None
    for attempt in range(8):
        try:
            client.act(
                action,
                card_index=kwargs.get("card_index"),
                target_index=kwargs.get("target_index"),
                option_index=kwargs.get("option_index"),
            )
            time.sleep(0.35)
            return
        except ApiError as exc:
            last_error = exc
            if not exc.retryable:
                raise
            print(f"RETRY {action} {exc.code or ''} {exc}", flush=True)
            time.sleep(0.5 + attempt * 0.25)
    raise Blocker(f"action {action!r} kept returning retryable errors: {last_error}", client.state())


def wait_actionable(client: Sts2Client, cfg: Config) -> dict[str, Any]:
    deadline = time.monotonic() + cfg.wait_timeout
    last: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        state = client.state()
        last = state
        if state.get("screen") == "COMBAT":
            p = player(state)
            if "end_turn" in real_actions(state) and "play_card" not in real_actions(state):
                if not hand(state) and int(p.get("cards_played_this_turn") or 0) == 0:
                    raise Blocker("combat exposes end_turn before the local turn is ready", state)
        if real_actions(state):
            return state
        time.sleep(cfg.sleep)
    raise Blocker("timed out waiting for actionable state", last)


def choose_target(state: dict[str, Any], damage: int = 0) -> int | None:
    candidates = enemies(state)
    if not candidates:
        return None
    killable = [enemy for enemy in candidates if damage > 0 and damage >= effective_hp(enemy)]
    if killable:
        return max(killable, key=lambda enemy: (enemy_incoming(enemy), effective_hp(enemy))).get("index")
    attackers = [enemy for enemy in candidates if enemy_incoming(enemy) > 0]
    if attackers:
        return max(attackers, key=lambda enemy: (enemy_incoming(enemy), -effective_hp(enemy))).get("index")
    return min(candidates, key=effective_hp).get("index")


def playable_cards(state: dict[str, Any]) -> list[dict[str, Any]]:
    energy = int(player(state).get("energy") or 0)
    return [
        card
        for card in hand(state)
        if card.get("playable", True) and int(card.get("energy_cost") or 0) <= energy
    ]


def use_potion_if_needed(client: Sts2Client, state: dict[str, Any]) -> bool:
    if "use_potion" not in actions(state):
        return False
    current_hp, _ = hp_pair(state)
    incoming = incoming_damage(state)
    if incoming < 18 and current_hp > 30:
        return False

    usable = [potion for potion in run(state).get("potions") or [] if potion.get("can_use")]
    if not usable:
        return False

    potion = next((p for p in usable if "WEAK" in str(p.get("potion_id"))), usable[0])
    target = choose_target(state, 0) if potion.get("requires_target") or potion.get("valid_target_indices") else None
    execute(client, "use_potion", option_index=int(potion["index"]), target_index=target)
    return True


def handle_selection(client: Sts2Client, state: dict[str, Any]) -> bool:
    if "select_deck_card" not in actions(state):
        if "confirm_selection" in actions(state) and (state.get("selection") or {}).get("can_confirm"):
            execute(client, "confirm_selection")
            return True
        return False

    cards = list((state.get("selection") or {}).get("cards") or [])
    if not cards:
        raise Blocker("selection action exists but no selection cards are exposed", state)

    need_block = incoming_damage(state) > int(player(state).get("block") or 0)
    priority = {
        "INFLAME": 100,
        "BASH": 90,
        "POMMEL_STRIKE": 85,
        "SHRUG_IT_OFF": 80,
        "ANGER": 70,
        "STRIKE_IRONCLAD": 50,
        "DEFEND_IRONCLAD": 40,
    }
    if need_block:
        choice = max(cards, key=lambda card: (dyn(card, "block"), priority.get(str(card.get("card_id")), 0)))
    else:
        choice = max(cards, key=lambda card: (priority.get(str(card.get("card_id")), 0), dyn(card, "damage"), dyn(card, "block")))
    execute(client, "select_deck_card", option_index=int(choice["index"]))
    return True


def handle_combat(client: Sts2Client, cfg: Config, state: dict[str, Any]) -> None:
    if use_potion_if_needed(client, state):
        return

    if "play_card" not in actions(state):
        if "end_turn" not in actions(state):
            raise Blocker("combat has no play_card or end_turn action", state)
        playable = [card for card in hand(state) if card.get("playable")]
        if not playable and hand(state) and not cfg.allow_forced_end:
            raise Blocker("forced-end-only combat state: hand has no playable cards", state)
        execute(client, "end_turn")
        return

    cards = playable_cards(state)
    if not cards:
        if "end_turn" in actions(state):
            if hand(state) and not cfg.allow_forced_end:
                raise Blocker("forced-end-only combat state: no playable affordable cards", state)
            execute(client, "end_turn")
            return
        raise Blocker("play_card is available but no playable affordable card is exposed", state)

    # Powers are valuable when they do not leave the player exposed.
    safe_deficit = incoming_damage(state) - int(player(state).get("block") or 0)
    power = next((card for card in cards if card.get("card_id") == "INFLAME" and safe_deficit <= 12), None)
    if power:
        execute(client, "play_card", card_index=int(power["index"]))
        return

    free_attacks = [card for card in cards if card.get("requires_target") and int(card.get("energy_cost") or 0) == 0]
    if free_attacks:
        card = max(free_attacks, key=lambda c: dyn(c, "damage"))
        execute(client, "play_card", card_index=int(card["index"]), target_index=choose_target(state, dyn(card, "damage")))
        return

    # Kill attacking enemies before blocking when one card can remove damage.
    kill_options: list[tuple[int, int, dict[str, Any], dict[str, Any]]] = []
    for card in cards:
        if not card.get("requires_target"):
            continue
        damage = dyn(card, "damage")
        for enemy in enemies(state):
            if enemy_incoming(enemy) > 0 and damage >= effective_hp(enemy):
                kill_options.append((enemy_incoming(enemy), damage, card, enemy))
    if kill_options:
        _, _, card, enemy = max(kill_options, key=lambda item: (item[0], item[1]))
        execute(client, "play_card", card_index=int(card["index"]), target_index=int(enemy["index"]))
        return

    deficit = incoming_damage(state) - int(player(state).get("block") or 0)
    if deficit > 0:
        armaments = next((card for card in cards if card.get("card_id") == "ARMAMENTS"), None)
        if armaments:
            execute(client, "play_card", card_index=int(armaments["index"]))
            fresh = client.state()
            if handle_selection(client, fresh):
                return

        blocks = [card for card in cards if not card.get("requires_target") and dyn(card, "block") > 0]
        if blocks:
            card = max(blocks, key=lambda c: (dyn(c, "block") / max(1, int(c.get("energy_cost") or 1)), dyn(c, "block")))
            execute(client, "play_card", card_index=int(card["index"]))
            return

    attacks = [card for card in cards if card.get("requires_target")]
    if attacks:
        min_enemy_hp = min([effective_hp(enemy) for enemy in enemies(state)] or [999])
        card = max(
            attacks,
            key=lambda c: (
                100 if dyn(c, "damage") >= min_enemy_hp else 0,
                20 if c.get("card_id") == "BASH" else 0,
                dyn(c, "damage"),
            ),
        )
        execute(client, "play_card", card_index=int(card["index"]), target_index=choose_target(state, dyn(card, "damage")))
        return

    utility = [
        card
        for card in cards
        if not card.get("requires_target") and card.get("card_id") in {"SHRUG_IT_OFF", "ARMAMENTS"}
    ]
    if utility:
        card = max(utility, key=lambda c: dyn(c, "block"))
        execute(client, "play_card", card_index=int(card["index"]))
        fresh = client.state()
        if card.get("card_id") == "ARMAMENTS":
            handle_selection(client, fresh)
        return

    if "end_turn" in actions(state):
        execute(client, "end_turn")
        return
    raise Blocker("unhandled combat state", state)


def reward_card_score(card: dict[str, Any]) -> float:
    card_id = str(card.get("card_id") or "")
    card_type = str(card.get("card_type") or "")
    score = 0.0
    if "Attack" in card_type:
        score += 18 + dyn(card, "damage") * 1.7
    elif "Skill" in card_type:
        score += 15 + dyn(card, "block") * 1.5
    elif "Power" in card_type:
        score += 35
    else:
        score += 5

    bonuses = {
        "INFLAME": 70,
        "SHRUG_IT_OFF": 55,
        "POMMEL_STRIKE": 50,
        "ANGER": 45,
        "BASH": 35,
        "DISARM": 65,
        "SHOCKWAVE": 70,
    }
    for key, bonus in bonuses.items():
        if key in card_id:
            score += bonus
    if card_id in {"BLOODLETTING"}:
        score -= 40
    if "CURSE" in card_type.upper() or "STATUS" in card_type.upper():
        score -= 100
    return score


def handle_reward(client: Sts2Client, state: dict[str, Any]) -> None:
    reward = state.get("reward") or {}
    claims = [entry for entry in reward.get("rewards") or [] if entry.get("claimable")]
    if "claim_reward" in actions(state) and claims:
        claims.sort(key=lambda entry: (str(entry.get("reward_type")) == "Card", int(entry.get("index") or 0)))
        execute(client, "claim_reward", option_index=int(claims[0]["index"]))
        return

    cards = list(reward.get("card_options") or [])
    if "choose_reward_card" in actions(state) and cards:
        scored = sorted(((reward_card_score(card), card) for card in cards), key=lambda item: item[0], reverse=True)
        best_score, best = scored[0]
        print("REWARD_CARDS", json.dumps([(round(s, 1), c.get("index"), c.get("card_id")) for s, c in scored]), flush=True)
        if "skip_reward_cards" in actions(state) and best_score < 12:
            execute(client, "skip_reward_cards")
        else:
            execute(client, "choose_reward_card", option_index=int(best["index"]))
        return

    for action in ("collect_rewards_and_proceed", "proceed", "skip_reward_cards"):
        if action in actions(state):
            execute(client, action)
            return
    if "resolve_rewards" in actions(state):
        execute(client, "resolve_rewards", option_index=-1)
        return
    raise Blocker("unhandled reward state", state)


def node_score(node: dict[str, Any], state: dict[str, Any]) -> float:
    node_type = str(node.get("node_type") or "").lower()
    score = 0.0
    if "boss" in node_type:
        score = 10000
    elif "rest" in node_type or "camp" in node_type:
        score = 80 if hp_ratio(state) < 0.75 else 35
    elif "unknown" in node_type or "event" in node_type or "question" in node_type:
        score = 55 if hp_ratio(state) < 0.55 else 38
    elif "treasure" in node_type or "chest" in node_type:
        score = 50
    elif "shop" in node_type or "merchant" in node_type:
        score = 42
    elif "monster" in node_type:
        score = 35 if hp_ratio(state) > 0.45 else 15
    elif "elite" in node_type:
        score = 25 if hp_ratio(state) > 0.85 else -100
    else:
        score = 10
    return score - abs(int(node.get("col") or 0) - 3) * 0.5


def handle_map(client: Sts2Client, state: dict[str, Any]) -> None:
    nodes = list((state.get("map") or {}).get("available_nodes") or [])
    if not nodes:
        raise Blocker("choose_map_node is available but no map nodes are exposed", state)
    scored = sorted(((node_score(node, state), node) for node in nodes), key=lambda item: item[0], reverse=True)
    print("MAP_OPTIONS", json.dumps([(round(s, 1), n.get("index"), n.get("node_type")) for s, n in scored]), flush=True)
    execute(client, "choose_map_node", option_index=int(scored[0][1]["index"]))


def handle_event(client: Sts2Client, state: dict[str, Any]) -> None:
    event = state.get("event") or {}
    options = [
        option
        for option in event.get("options") or []
        if not option.get("is_locked") and not option.get("will_kill_player")
    ]
    if not options:
        raise Blocker("event has no safe unlocked option", state)

    if event.get("is_finished"):
        choice = next((option for option in options if option.get("is_proceed")), options[0])
        execute(client, "choose_event_option", option_index=int(choice["index"]))
        return

    # Avoid Unrest Site full-rest unless the run is in serious danger; it can add
    # POOR_SLEEP and cause forced skip turns in later combats.
    event_id = str(event.get("event_id") or "")
    if event_id == "UNREST_SITE" and hp_ratio(state) > 0.35:
        non_rest = [option for option in options if "REST" not in str(option.get("text_key") or "")]
        if non_rest:
            execute(client, "choose_event_option", option_index=int(non_rest[0]["index"]))
            return

    relic_options = [option for option in options if option.get("has_relic_preview")]
    non_proceed = [option for option in options if not option.get("is_proceed")]
    choice = (relic_options or non_proceed or options)[0]
    execute(client, "choose_event_option", option_index=int(choice["index"]))


def handle_rest(client: Sts2Client, state: dict[str, Any]) -> None:
    options = [option for option in (state.get("rest") or {}).get("options") or [] if option.get("is_enabled")]
    if not options:
        raise Blocker("rest screen has no enabled options", state)

    def text(option: dict[str, Any]) -> str:
        return f"{option.get('option_id', '')} {option.get('title', '')}".lower()

    choice = None
    if hp_ratio(state) < 0.72:
        choice = next((option for option in options if any(word in text(option) for word in ("rest", "heal", "sleep"))), None)
    if choice is None:
        choice = next((option for option in options if any(word in text(option) for word in ("smith", "upgrade"))), None)
    if choice is None:
        choice = options[0]

    kwargs: dict[str, int | None] = {"option_index": int(choice["index"])}
    targets = choice.get("valid_target_indices") or []
    if choice.get("requires_target") and targets:
        kwargs["target_index"] = int(targets[0])
    execute(client, "choose_rest_option", **kwargs)


def handle_misc(client: Sts2Client, state: dict[str, Any]) -> bool:
    for action in ("confirm_modal", "dismiss_modal", "open_chest"):
        if action in actions(state):
            execute(client, action)
            return True
    if "choose_treasure_relic" in actions(state):
        relics = (state.get("chest") or {}).get("relic_options") or []
        index = int((relics[0] if relics else {"index": 0}).get("index"))
        execute(client, "choose_treasure_relic", option_index=index)
        return True
    if "choose_capstone_option" in actions(state):
        execute(client, "choose_capstone_option", option_index=0)
        return True
    if "choose_bundle" in actions(state):
        execute(client, "choose_bundle", option_index=0)
        return True
    if "confirm_bundle" in actions(state):
        execute(client, "confirm_bundle")
        return True
    if "proceed" in actions(state):
        execute(client, "proceed")
        return True
    return False


def is_boss_combat(state: dict[str, Any]) -> bool:
    boss_id = str(run(state).get("boss_id") or "").upper()
    for enemy in enemies(state):
        enemy_id = str(enemy.get("enemy_id") or "").upper()
        if "BOSS" in enemy_id or (boss_id and boss_id in enemy_id):
            return True
    return False


def run_loop(client: Sts2Client, cfg: Config) -> int:
    state = client.state()
    print_state("START", state)
    if cfg.max_steps <= 0:
        return 0

    saw_boss = is_boss_combat(state)
    for step in range(1, cfg.max_steps + 1):
        state = wait_actionable(client, cfg)
        print_state(f"STATE {step}", state)

        screen = str(state.get("screen") or "")
        if saw_boss and screen != "COMBAT" and cfg.until == "boss":
            print("DONE boss combat ended", flush=True)
            return 0
        if screen == "GAME_OVER":
            raise Blocker("run is game over before requested goal", state)
        if screen == "COMBAT":
            if is_boss_combat(state):
                saw_boss = True
            handle_combat(client, cfg, state)
        elif "choose_map_node" in actions(state):
            handle_map(client, state)
        elif screen == "REWARD" or any(
            action in actions(state)
            for action in ("choose_reward_card", "claim_reward", "collect_rewards_and_proceed", "resolve_rewards")
        ):
            handle_reward(client, state)
        elif screen == "EVENT" or "choose_event_option" in actions(state):
            handle_event(client, state)
        elif screen == "REST" or "choose_rest_option" in actions(state):
            handle_rest(client, state)
        elif handle_selection(client, state):
            pass
        elif handle_misc(client, state):
            pass
        else:
            raise Blocker("unknown actionable state", state)

        if cfg.until == "actionable":
            return 0

    raise Blocker("step limit reached before requested goal", client.state())


def parse_args(argv: list[str]) -> Config:
    parser = argparse.ArgumentParser(description="Conservative STS2 AI MCP autoplay harness.")
    parser.add_argument("--api", default="http://127.0.0.1:8080", help="STS2 mod HTTP API base URL.")
    parser.add_argument("--until", choices=["boss", "actionable"], default="boss", help="Goal condition.")
    parser.add_argument("--max-steps", type=int, default=250, help="Maximum action steps. Use 0 for state probe.")
    parser.add_argument("--wait-timeout", type=float, default=120.0, help="Seconds to wait for actionable state.")
    parser.add_argument("--sleep", type=float, default=0.35, help="Polling delay in seconds.")
    parser.add_argument(
        "--allow-forced-end",
        action="store_true",
        help="Allow ending turns when the hand has only unplayable cards.",
    )
    args = parser.parse_args(argv)
    return Config(
        api=args.api,
        until=args.until,
        max_steps=args.max_steps,
        wait_timeout=args.wait_timeout,
        allow_forced_end=args.allow_forced_end,
        sleep=args.sleep,
    )


def main(argv: list[str]) -> int:
    cfg = parse_args(argv)
    client = Sts2Client(cfg.api)
    try:
        return run_loop(client, cfg)
    except Blocker as exc:
        print(f"BLOCKER {exc}", flush=True)
        if exc.state:
            print_state("BLOCKER_STATE", exc.state)
        return 2
    except ApiError as exc:
        print(f"API_ERROR {exc.code or ''} {exc}", flush=True)
        if exc.body is not None:
            print(json.dumps(exc.body, ensure_ascii=False), flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
