"""Bounded, read-only calculations for model-owned STS2 reasoning.

These helpers deliberately do not choose an action and do not talk to the game.
They consume a public decision snapshot, calculate factual deltas or candidate-line
outcomes, and return in input order so the calling model remains responsible for
the final judgment.
"""

from __future__ import annotations

import math
import time
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any


REASONING_SCHEMA_VERSION = 1

DEFAULT_TIME_BUDGET_MS = 100
HARD_MAX_TIME_BUDGET_MS = 500
DEFAULT_MAX_STATES = 512
HARD_MAX_STATES = 4096

MAX_DECK_CARDS = 300
MAX_CANDIDATES = 16
MAX_HORIZONS = 4
MAX_COMBAT_LINES = 8
MAX_COMBAT_STEPS = 5
MAX_COMBAT_ENEMIES = 16

_HIDDEN_ORDER_KEYS = {
    "draw_order",
    "draw_pile_order",
    "draw_pile_cards_in_order",
    "known_draw_order",
    "rng_state",
    "seed_override",
}


class ReasoningInputError(ValueError):
    """Raised for invalid or unsafe calculator input."""


@dataclass(slots=True)
class WorkBudget:
    """Cooperative wall-clock and state budget with hard upper caps."""

    requested_time_ms: int = DEFAULT_TIME_BUDGET_MS
    requested_max_states: int = DEFAULT_MAX_STATES
    clock: Callable[[], float] = time.monotonic
    effective_time_ms: int = field(init=False)
    effective_max_states: int = field(init=False)
    deadline: float = field(init=False)
    states_used: int = 0
    stop_reason: str | None = None

    def __post_init__(self) -> None:
        try:
            requested_time = int(self.requested_time_ms)
            requested_states = int(self.requested_max_states)
        except (TypeError, ValueError, OverflowError) as exc:
            raise ReasoningInputError("time_budget_ms and max_states must be integers") from exc
        self.effective_time_ms = min(max(1, requested_time), HARD_MAX_TIME_BUDGET_MS)
        self.effective_max_states = min(max(1, requested_states), HARD_MAX_STATES)
        self.deadline = self.clock() + self.effective_time_ms / 1000.0

    def consume(self, count: int = 1) -> bool:
        if self.stop_reason is not None:
            return False
        if self.clock() >= self.deadline:
            self.stop_reason = "time_budget_exhausted"
            return False
        count = max(0, int(count))
        if self.states_used + count > self.effective_max_states:
            self.stop_reason = "state_budget_exhausted"
            return False
        self.states_used += count
        return True

    def metadata(self) -> dict[str, Any]:
        return {
            "requested_time_ms": self.requested_time_ms,
            "effective_time_ms": self.effective_time_ms,
            "hard_max_time_ms": HARD_MAX_TIME_BUDGET_MS,
            "requested_max_states": self.requested_max_states,
            "effective_max_states": self.effective_max_states,
            "hard_max_states": HARD_MAX_STATES,
            "states_used": self.states_used,
        }


def probability_at_least(
    population: int,
    successes: int,
    draws: int,
    minimum_successes: int = 1,
) -> float:
    """Exact hypergeometric P(X >= minimum_successes), rounded for JSON."""

    population = max(0, int(population))
    successes = min(population, max(0, int(successes)))
    draws = min(population, max(0, int(draws)))
    minimum_successes = max(0, int(minimum_successes))
    if minimum_successes == 0:
        return 1.0
    if population == 0 or successes == 0 or draws == 0:
        return 0.0
    if minimum_successes > min(successes, draws):
        return 0.0

    denominator = math.comb(population, draws)
    lower = max(0, draws - (population - successes))
    upper = min(successes, draws)
    favorable = sum(
        math.comb(successes, hits) * math.comb(population - successes, draws - hits)
        for hits in range(max(lower, minimum_successes), upper + 1)
    )
    return round(favorable / denominator, 6)


def reject_hidden_order_input(value: Any, path: str = "$") -> None:
    """Reject explicit hidden-order/RNG fields; unordered public piles are fine."""

    if isinstance(value, Mapping):
        for raw_key, nested in value.items():
            key = str(raw_key).strip().lower()
            if key in _HIDDEN_ORDER_KEYS:
                raise ReasoningInputError(
                    f"hidden-order field is not allowed: {path}.{raw_key}"
                )
            reject_hidden_order_input(nested, f"{path}.{raw_key}")
    elif isinstance(value, list):
        for index, nested in enumerate(value):
            reject_hidden_order_input(nested, f"{path}[{index}]")


def _as_int(value: Any) -> int | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError):
        return None


def _text(card: Mapping[str, Any]) -> str:
    parts: list[str] = []
    for key in ("rules_text", "resolved_rules_text", "description"):
        value = card.get(key)
        if isinstance(value, str):
            parts.append(value)
    for key in ("keywords", "tags", "mods"):
        value = card.get(key)
        if isinstance(value, list):
            parts.extend(str(item) for item in value)
    return " ".join(parts).casefold()


def _var_int(card: Mapping[str, Any], *names: str) -> int | None:
    variables = card.get("vars")
    if not isinstance(variables, Mapping):
        variables = card.get("dynamic_vars")
    if not isinstance(variables, Mapping):
        return None
    wanted = {name.casefold() for name in names}
    for name, raw in variables.items():
        if str(name).casefold() not in wanted:
            continue
        if isinstance(raw, Mapping):
            for key in ("preview_value", "enchanted_value", "base_value"):
                parsed = _as_int(raw.get(key))
                if parsed is not None:
                    return parsed
        parsed = _as_int(raw)
        if parsed is not None:
            return parsed
    return None


def _card_features(card: Mapping[str, Any]) -> dict[str, Any]:
    card_type = str(card.get("card_type") or card.get("type") or "Unknown")
    normalized_type = card_type.casefold()
    target = str(card.get("target_type") or card.get("target") or "")
    normalized_target = target.casefold()
    text = _text(card)
    costs_x = bool(card.get("costs_x", card.get("is_x_cost", False)))
    energy_cost = _as_int(card.get("energy_cost"))
    if energy_cost is None:
        energy_cost = _as_int(card.get("cost"))
    block = _as_int(card.get("block")) or _var_int(card, "Block", "CalculatedBlock") or 0
    draw = _as_int(card.get("cards_draw")) or _var_int(card, "Cards", "Draw") or 0
    energy_gain = _as_int(card.get("energy_gain")) or _var_int(card, "Energy") or 0
    hp_loss = _as_int(card.get("hp_loss")) or _var_int(card, "HpLoss", "HealthLoss") or 0

    is_attack = normalized_type == "attack"
    is_defense = block > 0 or "block" in text or "格挡" in text
    is_draw = draw > 0 or "draw" in text or "抽" in text
    is_energy = energy_gain > 0 or (
        ("gain" in text or "获得" in text)
        and ("energy" in text or "能量" in text or "energyicons" in text)
    )
    is_weak = "weak" in text or "虚弱" in text
    is_vulnerable = "vulnerable" in text or "易伤" in text
    is_dead_draw = normalized_type in {"curse", "status"} or "unplayable" in text or "无法打出" in text
    is_scaling = normalized_type == "power" or any(
        term in text
        for term in ("strength", "dexterity", "focus", "力量", "敏捷", "集中")
    )
    is_targeted_attack = is_attack and any(
        term in normalized_target for term in ("enemy", "anyenemy", "single")
    ) and "all" not in normalized_target and "random" not in normalized_target
    is_aoe = is_attack and "all" in normalized_target
    is_self_damage = hp_loss > 0 or (
        ("lose" in text or "失去" in text)
        and ("hp" in text or "health" in text or "生命" in text)
    )

    return {
        "card_id": str(card.get("card_id") or card.get("id") or ""),
        "name": str(card.get("name") or card.get("card_name") or card.get("card_id") or card.get("id") or ""),
        "type": card_type,
        "costs_x": costs_x,
        "energy_cost": energy_cost,
        "roles": {
            "defense": is_defense,
            "draw": is_draw,
            "energy_generation": is_energy,
            "weak": is_weak,
            "vulnerable": is_vulnerable,
            "scaling": is_scaling,
            "targeted_attack": is_targeted_attack,
            "aoe": is_aoe,
            "self_damage": is_self_damage,
            "dead_draw": is_dead_draw,
        },
    }


_ROLES = (
    "defense",
    "draw",
    "energy_generation",
    "weak",
    "vulnerable",
    "scaling",
    "targeted_attack",
    "aoe",
    "self_damage",
    "dead_draw",
)


def _empty_aggregate() -> dict[str, Any]:
    return {
        "deck_size": 0,
        "type_counts": {},
        "role_counts": {role: 0 for role in _ROLES},
        "cost_curve": {"zero": 0, "one": 0, "two": 0, "three_plus": 0, "x": 0, "unknown": 0},
        "fixed_cost_sum": 0,
        "fixed_cost_count": 0,
    }


def _add_features(aggregate: dict[str, Any], features: Mapping[str, Any]) -> None:
    aggregate["deck_size"] += 1
    card_type = str(features.get("type") or "Unknown")
    aggregate["type_counts"][card_type] = aggregate["type_counts"].get(card_type, 0) + 1
    roles = features.get("roles") if isinstance(features.get("roles"), Mapping) else {}
    for role in _ROLES:
        if roles.get(role) is True:
            aggregate["role_counts"][role] += 1

    if features.get("costs_x"):
        aggregate["cost_curve"]["x"] += 1
        return
    cost = _as_int(features.get("energy_cost"))
    if cost is None or cost < 0:
        aggregate["cost_curve"]["unknown"] += 1
        return
    bucket = "zero" if cost == 0 else "one" if cost == 1 else "two" if cost == 2 else "three_plus"
    aggregate["cost_curve"][bucket] += 1
    aggregate["fixed_cost_sum"] += cost
    aggregate["fixed_cost_count"] += 1


def _copy_aggregate(aggregate: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "deck_size": int(aggregate["deck_size"]),
        "type_counts": dict(aggregate["type_counts"]),
        "role_counts": dict(aggregate["role_counts"]),
        "cost_curve": dict(aggregate["cost_curve"]),
        "fixed_cost_sum": int(aggregate["fixed_cost_sum"]),
        "fixed_cost_count": int(aggregate["fixed_cost_count"]),
    }


def _aggregate_metrics(aggregate: Mapping[str, Any], horizons: Sequence[int]) -> dict[str, Any]:
    deck_size = int(aggregate["deck_size"])
    fixed_count = int(aggregate["fixed_cost_count"])
    role_counts = dict(aggregate["role_counts"])
    access: dict[str, Any] = {}
    for horizon in horizons:
        draws = min(deck_size, int(horizon))
        access[str(horizon)] = {
            "cards_seen": draws,
            "any_defense": probability_at_least(deck_size, role_counts["defense"], draws),
            "two_or_more_defense": probability_at_least(deck_size, role_counts["defense"], draws, 2),
            "any_draw": probability_at_least(deck_size, role_counts["draw"], draws),
            "any_energy_generation": probability_at_least(
                deck_size, role_counts["energy_generation"], draws
            ),
            "any_scaling": probability_at_least(deck_size, role_counts["scaling"], draws),
            "any_targeted_attack": probability_at_least(
                deck_size, role_counts["targeted_attack"], draws
            ),
            "any_dead_draw": probability_at_least(deck_size, role_counts["dead_draw"], draws),
        }
    return {
        "deck_size": deck_size,
        "type_counts": dict(sorted(aggregate["type_counts"].items())),
        "role_counts": role_counts,
        "cost_curve": {
            **aggregate["cost_curve"],
            "average_non_x_known": (
                round(int(aggregate["fixed_cost_sum"]) / fixed_count, 4) if fixed_count else None
            ),
        },
        "natural_access": access,
    }


def _numeric_delta(before: Any, after: Any) -> Any:
    if isinstance(before, Mapping) and isinstance(after, Mapping):
        result: dict[str, Any] = {}
        for key in after:
            if key in before:
                nested = _numeric_delta(before[key], after[key])
                if nested not in ({}, None):
                    result[str(key)] = nested
        return result
    if isinstance(before, (int, float)) and not isinstance(before, bool) and isinstance(after, (int, float)) and not isinstance(after, bool):
        difference = round(float(after) - float(before), 6)
        return int(difference) if difference.is_integer() else difference
    return None


def _decision_catalog(decision: Mapping[str, Any]) -> dict[str, dict[str, Any]]:
    knowledge = decision.get("knowledge")
    relevant = knowledge.get("relevant") if isinstance(knowledge, Mapping) else None
    cards = relevant.get("cards") if isinstance(relevant, Mapping) else None
    if not isinstance(cards, Mapping):
        return {}
    result: dict[str, dict[str, Any]] = {}
    for raw_id, value in cards.items():
        if isinstance(value, Mapping):
            result[str(raw_id).casefold()] = dict(value)
    return result


def _merge_card(card: Mapping[str, Any], catalog: Mapping[str, Mapping[str, Any]]) -> dict[str, Any]:
    card_id = str(card.get("card_id") or card.get("id") or "")
    static = catalog.get(card_id.casefold(), {})
    return {**static, **card, "card_id": card_id or static.get("id", "")}


def evaluate_run(
    *,
    deck: Sequence[Mapping[str, Any]],
    catalog: Mapping[str, Mapping[str, Any]] | None = None,
    candidate_card_ids: Sequence[str] | None = None,
    horizons: Sequence[int] = (5, 10, 15),
    decision_id: str | None = None,
    time_budget_ms: int = DEFAULT_TIME_BUDGET_MS,
    max_states: int = DEFAULT_MAX_STATES,
    clock: Callable[[], float] = time.monotonic,
) -> dict[str, Any]:
    """Evaluate public deck facts and candidate-before/after deltas."""

    if len(deck) > MAX_DECK_CARDS:
        raise ReasoningInputError(f"deck has {len(deck)} cards; maximum is {MAX_DECK_CARDS}")
    candidates = [str(value).strip() for value in (candidate_card_ids or []) if str(value).strip()]
    if len(candidates) > MAX_CANDIDATES:
        raise ReasoningInputError(
            f"candidate_card_ids has {len(candidates)} entries; maximum is {MAX_CANDIDATES}"
        )
    normalized_horizons = sorted({int(value) for value in horizons if int(value) > 0})
    if not normalized_horizons:
        raise ReasoningInputError("horizons must contain at least one positive integer")
    if len(normalized_horizons) > MAX_HORIZONS:
        raise ReasoningInputError(
            f"horizons has {len(normalized_horizons)} entries; maximum is {MAX_HORIZONS}"
        )

    normalized_catalog = {
        str(key).casefold(): value
        for key, value in (catalog or {}).items()
        if isinstance(value, Mapping)
    }
    budget = WorkBudget(time_budget_ms, max_states, clock=clock)
    base = {
        "schema_version": REASONING_SCHEMA_VERSION,
        "tool": "run_evaluator",
        "decision_id": decision_id,
        "mutation_performed": False,
        "selection_performed": False,
        "input_order_preserved": True,
        "assumptions": [
            "deck composition is public and unordered",
            "natural-access probabilities sample without replacement and ignore draw, retain, and shuffle effects",
            "candidate deltas add exactly one copy and do not model upgrade text unless supplied in the input",
        ],
        "limitations": [
            "role detection combines structured fields with localized rules text",
            "results are factual inputs for model reasoning, not a card ranking or recommendation",
        ],
    }

    if not budget.consume(len(deck)):
        return {
            **base,
            "status": "partial",
            "stop_reason": budget.stop_reason,
            "baseline": None,
            "candidates": [],
            "completed_candidate_count": 0,
            "limits": budget.metadata(),
        }

    aggregate = _empty_aggregate()
    for card in deck:
        if not isinstance(card, Mapping):
            raise ReasoningInputError("every deck entry must be an object")
        _add_features(aggregate, _card_features(_merge_card(card, normalized_catalog)))
    baseline = _aggregate_metrics(aggregate, normalized_horizons)

    candidate_results: list[dict[str, Any]] = []
    unresolved: list[str] = []
    for candidate_id in candidates:
        if not budget.consume():
            break
        candidate = normalized_catalog.get(candidate_id.casefold())
        if candidate is None:
            unresolved.append(candidate_id)
            candidate_results.append(
                {
                    "card_id": candidate_id,
                    "resolved": False,
                    "reason": "candidate_not_present_in_public_catalog",
                }
            )
            continue
        merged = {**candidate, "card_id": str(candidate.get("id") or candidate_id)}
        features = _card_features(merged)
        after_aggregate = _copy_aggregate(aggregate)
        _add_features(after_aggregate, features)
        after = _aggregate_metrics(after_aggregate, normalized_horizons)
        candidate_results.append(
            {
                "card_id": candidate_id,
                "name": features["name"],
                "resolved": True,
                "added_card_facts": features,
                "after": after,
                "delta": _numeric_delta(baseline, after),
            }
        )

    status = "partial" if budget.stop_reason else "complete"
    return {
        **base,
        "status": status,
        "stop_reason": budget.stop_reason,
        "baseline": baseline,
        "candidates": candidate_results,
        "unresolved_candidate_ids": unresolved,
        "completed_candidate_count": len(candidate_results),
        "limits": budget.metadata(),
    }


def evaluate_run_decision(
    decision: Mapping[str, Any],
    *,
    candidate_card_ids: Sequence[str] | None = None,
    horizons: Sequence[int] = (5, 10, 15),
    time_budget_ms: int = DEFAULT_TIME_BUDGET_MS,
    max_states: int = DEFAULT_MAX_STATES,
    clock: Callable[[], float] = time.monotonic,
) -> dict[str, Any]:
    context = decision.get("context")
    run = context.get("run") if isinstance(context, Mapping) else None
    deck = run.get("deck") if isinstance(run, Mapping) else None
    if not isinstance(deck, list):
        raise ReasoningInputError("decision has no public context.run.deck")
    return evaluate_run(
        deck=deck,
        catalog=_decision_catalog(decision),
        candidate_card_ids=candidate_card_ids,
        horizons=horizons,
        decision_id=str(decision.get("decision_id") or "") or None,
        time_budget_ms=time_budget_ms,
        max_states=max_states,
        clock=clock,
    )


def _enemy_key(enemy: Mapping[str, Any]) -> str:
    ref = enemy.get("enemy_ref")
    if isinstance(ref, str) and ref:
        return ref
    index = _as_int(enemy.get("index"))
    return f"enemy:{index}" if index is not None else str(enemy.get("enemy_id") or enemy.get("name") or "unknown")


def _intent_damage(enemy: Mapping[str, Any]) -> tuple[int, bool]:
    intents = enemy.get("intents")
    if not isinstance(intents, list):
        intent_label = str(enemy.get("intent") or "").casefold()
        return 0, not any(term in intent_label for term in ("attack", "damage", "攻击", "伤害"))
    if not intents:
        intent_label = str(enemy.get("intent") or "").casefold()
        return 0, not any(term in intent_label for term in ("attack", "damage", "攻击", "伤害"))
    total = 0
    complete = True
    for intent in intents:
        if not isinstance(intent, Mapping):
            complete = False
            continue
        total_damage = _as_int(intent.get("total_damage"))
        damage = _as_int(intent.get("damage"))
        hits = _as_int(intent.get("hits")) or 1
        intent_type = str(intent.get("intent_type") or intent.get("type") or intent.get("label") or "").casefold()
        looks_like_attack = any(
            term in intent_type for term in ("attack", "damage", "攻击", "伤害")
        )
        if total_damage is not None:
            total += max(0, total_damage)
        elif damage is not None:
            total += max(0, damage) * max(1, hits)
        elif looks_like_attack:
            complete = False
    return total, complete


def _combat_context(decision: Mapping[str, Any]) -> Mapping[str, Any]:
    context = decision.get("context")
    combat = context.get("combat") if isinstance(context, Mapping) else None
    if not isinstance(combat, Mapping):
        raise ReasoningInputError("decision has no context.combat")
    return combat


def summarize_combat_preview(
    decision: Mapping[str, Any],
    preview: Mapping[str, Any],
) -> dict[str, Any] | None:
    """Add current-intent survival arithmetic to one deterministic line preview."""

    if preview.get("status") != "previewed":
        return None
    projected = preview.get("projected")
    if not isinstance(projected, Mapping):
        return None
    combat = _combat_context(decision)
    enemies = combat.get("enemies")
    if not isinstance(enemies, list):
        raise ReasoningInputError("context.combat.enemies must be a list")
    if len(enemies) > MAX_COMBAT_ENEMIES:
        raise ReasoningInputError(
            f"combat has {len(enemies)} enemies; maximum is {MAX_COMBAT_ENEMIES}"
        )

    projected_enemies_raw = projected.get("enemies")
    projected_enemies = {
        _enemy_key(enemy): enemy
        for enemy in projected_enemies_raw
        if isinstance(enemy, Mapping)
    } if isinstance(projected_enemies_raw, list) else {}

    incoming = 0
    intent_complete = True
    excluded_killed_attackers: list[str] = []
    attackers: list[dict[str, Any]] = []
    for enemy in enemies:
        if not isinstance(enemy, Mapping) or enemy.get("is_alive", True) is False:
            continue
        key = _enemy_key(enemy)
        damage, complete = _intent_damage(enemy)
        intent_complete = intent_complete and complete
        projected_enemy = projected_enemies.get(key)
        projected_alive = projected_enemy.get("alive", True) if isinstance(projected_enemy, Mapping) else True
        if projected_alive is False:
            if damage > 0:
                excluded_killed_attackers.append(key)
            attackers.append({"enemy_ref": key, "damage": damage, "included": False})
            continue
        incoming += damage
        attackers.append({"enemy_ref": key, "damage": damage, "included": True})

    hp = _as_int(projected.get("player_hp"))
    block = _as_int(projected.get("player_block"))
    if hp is None or block is None:
        return {
            "complete": False,
            "stop_reason": "projected_player_hp_or_block_unavailable",
            "incoming_damage": incoming,
            "attackers": attackers,
        }
    blocked = min(max(0, block), incoming)
    hp_damage = max(0, incoming - blocked)
    hp_after = max(0, hp - hp_damage)
    block_after = max(0, block - incoming)
    effects_complete = preview.get("effects_complete") is True
    complete = effects_complete and intent_complete
    return {
        "scope": "current_exposed_attack_intents_after_projected_direct_kills",
        "complete": complete,
        "intent_damage_complete": intent_complete,
        "line_effects_complete": effects_complete,
        "starting_hp": hp,
        "starting_block": block,
        "incoming_damage": incoming,
        "blocked_damage": blocked,
        "hp_damage": hp_damage,
        "hp_after": hp_after,
        "block_after": block_after,
        "modeled_survives": hp_after > 0,
        "survival_proven_within_scope": complete and hp_after > 0,
        "lethal_proven_within_scope": complete and hp_after <= 0,
        "excluded_killed_attackers": excluded_killed_attackers,
        "attackers": attackers,
        "limitations": [
            "does not model end-turn powers, relics, potions, status triggers, or enemy state changes beyond exposed attack intents",
        ],
    }


Previewer = Callable[[dict[str, Any], list[dict[str, Any]], str], dict[str, Any]]


def evaluate_combat_horizon(
    decision: Mapping[str, Any],
    *,
    lines: Sequence[Mapping[str, Any]],
    previewer: Previewer,
    time_budget_ms: int = DEFAULT_TIME_BUDGET_MS,
    max_states: int = DEFAULT_MAX_STATES,
    clock: Callable[[], float] = time.monotonic,
) -> dict[str, Any]:
    """Check several model-proposed lines without ranking or executing them."""

    if not lines:
        raise ReasoningInputError("lines must contain at least one candidate line")
    if len(lines) > MAX_COMBAT_LINES:
        raise ReasoningInputError(
            f"lines has {len(lines)} entries; maximum is {MAX_COMBAT_LINES}"
        )
    combat = _combat_context(decision)
    enemies = combat.get("enemies")
    enemy_count = len(enemies) if isinstance(enemies, list) else 0
    if enemy_count > MAX_COMBAT_ENEMIES:
        raise ReasoningInputError(
            f"combat has {enemy_count} enemies; maximum is {MAX_COMBAT_ENEMIES}"
        )

    budget = WorkBudget(time_budget_ms, max_states, clock=clock)
    results: list[dict[str, Any]] = []
    for index, line in enumerate(lines):
        if not isinstance(line, Mapping):
            raise ReasoningInputError("every combat line must be an object")
        steps = line.get("steps")
        if not isinstance(steps, list):
            raise ReasoningInputError(f"line {index} steps must be a list")
        if len(steps) > MAX_COMBAT_STEPS:
            raise ReasoningInputError(
                f"line {index} has {len(steps)} steps; maximum is {MAX_COMBAT_STEPS}"
            )
        if not all(isinstance(step, dict) for step in steps):
            raise ReasoningInputError(f"line {index} steps must contain objects")
        if not budget.consume(1 + len(steps) + enemy_count):
            break
        preview = previewer(dict(decision), list(steps), "strict")
        end_turn = summarize_combat_preview(decision, preview)
        results.append(
            {
                "index": index,
                "label": str(line.get("label") or f"line-{index + 1}"),
                "preview": preview,
                "end_turn": end_turn,
            }
        )

    return {
        "schema_version": REASONING_SCHEMA_VERSION,
        "tool": "combat_horizon",
        "decision_id": decision.get("decision_id"),
        "status": "partial" if budget.stop_reason else "complete",
        "stop_reason": budget.stop_reason,
        "mutation_performed": False,
        "selection_performed": False,
        "input_order_preserved": True,
        "requested_line_count": len(lines),
        "completed_line_count": len(results),
        "lines": results,
        "limits": budget.metadata(),
        "limitations": [
            "only model-proposed lines are checked; no line search or optimization is performed",
            "each line uses the existing deterministic direct-effect preview and stops at its information boundaries",
            "results are evidence for model reasoning, not an instruction to execute a line",
        ],
    }


def decision_from_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    """Accept a raw decision or common MCP response envelopes."""

    candidate: Any = payload
    data = payload.get("data")
    if isinstance(data, Mapping):
        candidate = data
    if isinstance(candidate, Mapping) and isinstance(candidate.get("decision"), Mapping):
        candidate = candidate["decision"]
    if not isinstance(candidate, Mapping) or not isinstance(candidate.get("context"), Mapping):
        raise ReasoningInputError("input does not contain a decision with context")
    return dict(candidate)
