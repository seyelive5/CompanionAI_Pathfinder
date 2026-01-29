using Kingmaker;
using TurnBased.Controllers;

namespace CompanionAI_Pathfinder.Abstraction
{
    /// <summary>
    /// Pathfinder의 전투 상태를 ICombatState 인터페이스로 래핑
    /// </summary>
    public class PathfinderCombatStateAdapter : ICombatState
    {
        // === 전투 모드 ===
        public CombatMode CurrentMode
        {
            get
            {
                if (!IsInCombat)
                    return CombatMode.None;

                return CombatController.IsInTurnBasedCombat()
                    ? CombatMode.TurnBased
                    : CombatMode.RealTime;
            }
        }

        public bool IsTurnBased => CurrentMode == CombatMode.TurnBased;
        public bool IsRealTime => CurrentMode == CombatMode.RealTime;

        // === 턴 정보 ===
        public IGameUnit CurrentTurnUnit
        {
            get
            {
                var combatController = Game.Instance?.TurnBasedCombatController;
                var turnUnit = combatController?.CurrentTurn?.Rider;
                return turnUnit != null ? new PathfinderUnitAdapter(turnUnit) : null;
            }
        }

        public int CurrentRound
        {
            get
            {
                var combatController = Game.Instance?.TurnBasedCombatController;
                return combatController?.RoundNumber ?? 0;
            }
        }

        public float TimeInRound
        {
            get
            {
                var combatController = Game.Instance?.TurnBasedCombatController;
                return combatController?.TimeToNextRound ?? 0f;
            }
        }

        // === 전투 상태 ===
        public bool IsInCombat
        {
            get
            {
                var player = Game.Instance?.Player;
                return player?.IsInCombat ?? false;
            }
        }

        public int EnemyCount
        {
            get
            {
                int count = 0;
                var units = Game.Instance?.State?.Units;
                if (units == null) return 0;

                foreach (var unit in units)
                {
                    if (unit.IsInCombat && !unit.IsPlayerFaction && unit.HPLeft > 0)
                        count++;
                }
                return count;
            }
        }

        public int AllyCount
        {
            get
            {
                int count = 0;
                var units = Game.Instance?.State?.Units;
                if (units == null) return 0;

                foreach (var unit in units)
                {
                    if (unit.IsPlayerFaction && unit.HPLeft > 0)
                        count++;
                }
                return count;
            }
        }

        // === 싱글톤 ===
        private static PathfinderCombatStateAdapter _instance;
        public static PathfinderCombatStateAdapter Instance => _instance ??= new PathfinderCombatStateAdapter();
    }
}
