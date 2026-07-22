from __future__ import annotations

import unittest
from pathlib import Path


class ModCardPreviewContractTests(unittest.TestCase):
    def test_damage_preview_uses_the_games_target_aware_hook_chain(self) -> None:
        repo_root = Path(__file__).resolve().parents[2]
        state_source = (repo_root / "STS2AIMCP" / "Game" / "GameStateService.cs").read_text(
            encoding="utf-8"
        )
        decision_source = (
            repo_root / "STS2AIMCP" / "Game" / "DecisionWindowService.cs"
        ).read_text(
            encoding="utf-8"
        )

        self.assertIn("BuildCardTargetDynamicVarPayloads(combatState, card)", state_source)
        self.assertIn(
            "card.UpdateDynamicVarPreview(CardPreviewMode.Normal, entry.enemy, card.DynamicVars)",
            state_source,
        )
        self.assertIn(
            "card.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, card.DynamicVars)",
            state_source,
        )
        self.assertIn("target_dynamic_vars = targetDynamicVars", state_source)
        self.assertIn("ResolveReferencedTargetDynamicVar(", decision_source)
        self.assertNotIn("EstimateTargetDependentDamagePerHit(", decision_source)
        self.assertNotIn('card.card_id, "BULLY"', decision_source)
        self.assertNotIn("adjustedPerHit * 1.5m", decision_source)


if __name__ == "__main__":
    unittest.main()
