from __future__ import annotations

import unittest
from pathlib import Path


class ModEndTurnContractTests(unittest.TestCase):
    def test_end_turn_uses_player_turn_number_for_extra_turns(self) -> None:
        repo_root = Path(__file__).resolve().parents[2]
        source = (repo_root / "STS2AIMCP" / "Game" / "GameActionService.cs").read_text(
            encoding="utf-8"
        )

        self.assertIn("var turnNumber = playerCombatState.TurnNumber;", source)
        self.assertIn("new EndPlayerTurnAction(me, turnNumber)", source)
        self.assertIn("me.PlayerCombatState.TurnNumber != previousTurn", source)
        self.assertNotIn("var roundNumber = playerCombatState.RoundNumber;", source)


if __name__ == "__main__":
    unittest.main()
