# Reasoning Tools Test Plan

This plan covers the read-only `run_evaluator` and `combat_horizon` helpers.
They calculate facts for an agent but never select or execute a game action.

## Test inventory

- `test_reasoning.py`: about 14 unit tests for probability math, deck deltas,
  candidate resolution, combat-line summaries, budget stops, and validation.
- `test_reasoning_cli.py`: about 4 subprocess tests for help, both JSON commands,
  and a bounded adversarial request.
- `test_v2_profile.py`: existing MCP profile tests extended to verify that both
  read-only tools are exposed and do not invoke an action endpoint.

## Unit test plan

### `sts2_mcp.reasoning`

- Verify hypergeometric probabilities at zero/full/bounded populations.
- Verify deck size, cost curve, role counts, and opening-access probabilities.
- Verify that adding a candidate reports metric deltas without ranking candidates.
- Verify unresolved candidate IDs are reported instead of guessed.
- Verify ordered draw-pile or hidden-order input is rejected by the offline loader.
- Verify elapsed-time and state-count budgets return a partial structured result.
- Verify hard caps reject excessive cards, candidates, horizons, lines, steps, and
  enemies before expensive work starts.
- Verify deterministic combat damage/block/self-damage arithmetic.
- Verify a projected enemy kill removes only that enemy's current attack intent.
- Verify incomplete card previews remain explicitly incomplete.

## End-to-end CLI plan

- Resolve `sts2-reasoning` from PATH, falling back to `python -m
  sts2_mcp.reasoning_cli` for source-tree development.
- Check `--help` exits successfully.
- Run `run-evaluator` on a real JSON file and parse its JSON output.
- Run `combat-horizon` on a decision fixture and candidate-line file.
- Submit an oversized request and verify it fails quickly with a structured
  validation error rather than hanging.

## Realistic workflows

### Reward-card comparison

- Start from a public run deck and current static card catalog.
- Add each visible reward candidate one at a time.
- Verify the output preserves input order and reports only factual before/after
  deltas, leaving the choice to the calling model.

### Current-turn line checking

- Start from one cached combat decision with live card previews and enemy intents.
- Check several model-proposed 1-5 step lines.
- Verify resource feasibility, projected direct damage/block, killed attackers,
  and end-turn survival margins without executing any action.

### Bounded failure

- Submit more work than the state budget permits.
- Verify the completed prefix is returned with `status=partial`, an explicit stop
  reason, and no mutation field set to true.

## Test results

Command:

```text
.venv/bin/python -m pytest tests/test_reasoning.py tests/test_reasoning_cli.py tests/test_v2_profile.py -v --tb=no
```

```text
============================= test session starts ==============================
platform linux -- Python 3.12.3, pytest-9.1.1, pluggy-1.6.0
collected 36 items

tests/test_reasoning.py::test_probability_at_least_handles_exact_boundaries PASSED
tests/test_reasoning.py::test_run_evaluator_reports_structure_and_candidate_delta_without_ranking PASSED
tests/test_reasoning.py::test_run_evaluator_reports_unresolved_candidate_instead_of_guessing PASSED
tests/test_reasoning.py::test_run_evaluator_state_budget_returns_partial_without_partial_metrics PASSED
tests/test_reasoning.py::test_work_budget_clamps_time_and_stops_on_elapsed_clock PASSED
tests/test_reasoning.py::test_hidden_draw_order_fields_are_rejected PASSED
tests/test_reasoning.py::test_run_evaluator_rejects_large_inputs_before_work PASSED
tests/test_reasoning.py::test_combat_horizon_excludes_killed_attacker_and_keeps_input_order PASSED
tests/test_reasoning.py::test_combat_horizon_marks_incomplete_preview_as_not_proven PASSED
tests/test_reasoning.py::test_combat_horizon_returns_completed_prefix_on_state_budget PASSED
tests/test_reasoning.py::test_combat_horizon_rejects_excessive_lines_and_steps PASSED
tests/test_reasoning_cli.py::test_cli_help PASSED
tests/test_reasoning_cli.py::test_run_evaluator_cli_json PASSED
tests/test_reasoning_cli.py::test_combat_horizon_cli_json PASSED
tests/test_reasoning_cli.py::test_oversized_request_is_rejected_quickly PASSED
tests/test_v2_profile.py::V2ProfileTests::test_ai_safe_v2_exposes_only_v2_tools PASSED
tests/test_v2_profile.py::V2ProfileTests::test_combat_plan_continues_when_only_the_played_card_leaves_hand PASSED
tests/test_v2_profile.py::V2ProfileTests::test_combat_plan_stops_when_a_card_is_drawn PASSED
tests/test_v2_profile.py::V2ProfileTests::test_current_decision_uses_local_knowledge_without_live_hydration PASSED
tests/test_v2_profile.py::V2ProfileTests::test_default_profile_is_ai_safe_v2 PASSED
tests/test_v2_profile.py::V2ProfileTests::test_execute_action_plan_preflight_stops_before_spending_energy PASSED
tests/test_v2_profile.py::V2ProfileTests::test_lookup_game_data_exports_once_then_uses_versioned_local_snapshot PASSED
tests/test_v2_profile.py::V2ProfileTests::test_lookup_game_data_filters_fields PASSED
tests/test_v2_profile.py::V2ProfileTests::test_model_discovery_tools_are_debug_gated PASSED
tests/test_v2_profile.py::V2ProfileTests::test_plan_id_replay_does_not_execute_actions_twice PASSED
tests/test_v2_profile.py::V2ProfileTests::test_preview_action_plan_applies_sloth_card_limit PASSED
tests/test_v2_profile.py::V2ProfileTests::test_preview_action_plan_folds_direct_damage_through_shadow_state PASSED
tests/test_v2_profile.py::V2ProfileTests::test_preview_action_plan_folds_simple_energy_gain_and_hp_loss PASSED
tests/test_v2_profile.py::V2ProfileTests::test_preview_action_plan_recomputes_body_slam_from_projected_block PASSED
tests/test_v2_profile.py::V2ProfileTests::test_reasoning_tools_use_cached_decision_without_actions PASSED
tests/test_v2_profile.py::V2ProfileTests::test_select_cards_can_confirm_an_empty_selection PASSED
tests/test_v2_profile.py::V2ProfileTests::test_select_cards_executes_each_fresh_decision_then_confirms PASSED
tests/test_v2_profile.py::V2ProfileTests::test_select_cards_rejects_an_empty_no_op PASSED
tests/test_v2_profile.py::V2ProfileTests::test_take_action_appends_decision_log PASSED
tests/test_v2_profile.py::V2ProfileTests::test_take_action_avoids_mixing_old_decision_log_format PASSED
tests/test_v2_profile.py::V2ProfileTests::test_take_action_waits_through_pending_until_next_decision PASSED

============================== 36 passed in 3.12s ==============================
```

The complete repository suite also passed: `75 passed in 3.68s`.

Coverage notes: the tests exercise deterministic public-state calculations,
validation, hard caps, cooperative budget stops, MCP registration/no-action behavior,
and subprocess JSON contracts. They intentionally do not compare results with hidden
draw order or a live engine dry-run, because both would violate the tools' scope.
