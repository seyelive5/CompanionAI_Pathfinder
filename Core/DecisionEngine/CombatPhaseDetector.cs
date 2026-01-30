// ★ v0.2.22: Unified Decision Engine - Combat Phase Detector
// ★ v0.2.59: 실시간 전투 라운드 추정 구현
using System;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Scoring;
using TurnBased.Controllers;

namespace CompanionAI_Pathfinder.Core.DecisionEngine
{
    /// <summary>
    /// Detects the current combat phase based on battle situation.
    /// Used to adjust AI decision weights dynamically.
    /// </summary>
    public class CombatPhaseDetector
    {
        #region Thresholds

        // HP thresholds
        private const float DESPERATE_HP_SELF = 25f;
        private const float DESPERATE_HP_ALLY_AVG = 35f;
        private const float DESPERATE_ENGAGEMENT_COUNT = 3;

        // Cleanup thresholds
        private const int CLEANUP_MAX_ENEMIES = 2;
        private const float CLEANUP_ENEMY_HP_AVG = 40f;
        private const float CLEANUP_SELF_HP_MIN = 50f;

        // Opening thresholds
        private const int OPENING_MAX_ROUND = 2;
        private const float OPENING_MIN_HP = 80f;

        // ★ v0.2.59: 실시간 전투 라운드 추정
        private const float SECONDS_PER_ROUND = 6f;  // D&D: 1 라운드 = 6초
        private static float _realtimeCombatStartTime = 0f;
        private static bool _wasInCombat = false;

        #endregion

        #region Phase Detection

        /// <summary>
        /// Detect the current combat phase based on situation analysis
        /// </summary>
        public CombatPhase DetectPhase(Situation situation)
        {
            if (situation == null)
                return CombatPhase.Midgame;

            try
            {
                // Check Desperate first (highest priority)
                if (IsDesperatePhase(situation))
                {
                    Main.Verbose($"[PhaseDetector] Phase=Desperate (HP={situation.HPPercent:F0}%, Engaged={situation.EngagedByCount})");
                    return CombatPhase.Desperate;
                }

                // Check Cleanup (low enemy count, situation is favorable)
                if (IsCleanupPhase(situation))
                {
                    Main.Verbose($"[PhaseDetector] Phase=Cleanup (Enemies={situation.Enemies?.Count ?? 0})");
                    return CombatPhase.Cleanup;
                }

                // Check Opening (early combat, not yet engaged)
                if (IsOpeningPhase(situation))
                {
                    Main.Verbose($"[PhaseDetector] Phase=Opening (Round={GetCombatRound()})");
                    return CombatPhase.Opening;
                }

                // Default to Midgame
                Main.Verbose($"[PhaseDetector] Phase=Midgame");
                return CombatPhase.Midgame;
            }
            catch (Exception ex)
            {
                Main.Error($"[PhaseDetector] Error: {ex.Message}");
                return CombatPhase.Midgame;
            }
        }

        /// <summary>
        /// Check if we're in Desperate phase (survival priority)
        /// </summary>
        private bool IsDesperatePhase(Situation situation)
        {
            // Self HP critical
            if (situation.HPPercent < DESPERATE_HP_SELF)
                return true;

            // Heavily engaged (3+ enemies on us)
            if (situation.EngagedByCount >= DESPERATE_ENGAGEMENT_COUNT && situation.HPPercent < 50f)
                return true;

            // Team average HP critical
            float allyAvgHP = GetAllyAverageHP(situation);
            if (allyAvgHP > 0 && allyAvgHP < DESPERATE_HP_ALLY_AVG)
                return true;

            // Outnumbered significantly
            int allyCount = (situation.Allies?.Count ?? 0) + 1;  // +1 for self
            int enemyCount = situation.Enemies?.Count ?? 0;
            if (enemyCount >= allyCount * 2 && situation.HPPercent < 60f)
                return true;

            return false;
        }

        /// <summary>
        /// Check if we're in Cleanup phase (finishing off enemies)
        /// </summary>
        private bool IsCleanupPhase(Situation situation)
        {
            // Need to be in decent shape ourselves
            if (situation.HPPercent < CLEANUP_SELF_HP_MIN)
                return false;

            // Few enemies remaining
            int liveEnemies = situation.Enemies?
                .Count(e => e != null && !e.Descriptor?.State?.IsDead == true) ?? 0;

            if (liveEnemies > CLEANUP_MAX_ENEMIES)
                return false;

            if (liveEnemies == 0)
                return true;  // Combat ending

            // Enemies should be weakened
            float avgEnemyHP = GetAverageEnemyHP(situation);
            if (avgEnemyHP > 0 && avgEnemyHP < CLEANUP_ENEMY_HP_AVG)
                return true;

            return false;
        }

        /// <summary>
        /// Check if we're in Opening phase (combat just started)
        /// </summary>
        private bool IsOpeningPhase(Situation situation)
        {
            // Must be early in combat
            int round = GetCombatRound();
            if (round > OPENING_MAX_ROUND)
                return false;

            // Must be healthy
            if (situation.HPPercent < OPENING_MIN_HP)
                return false;

            // Not heavily engaged yet
            if (situation.IsEngaged && situation.EngagedByCount >= 2)
                return false;

            // Haven't attacked yet this turn
            if (situation.HasAttackedThisTurn)
                return false;

            return true;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get current combat round (1-based)
        /// ★ v0.2.59: 실시간 전투 라운드 추정 구현
        /// </summary>
        private int GetCombatRound()
        {
            try
            {
                // 턴제 모드
                if (CombatController.IsInTurnBasedCombat())
                {
                    var tbController = Game.Instance.TurnBasedCombatController;
                    return tbController?.RoundNumber ?? 1;
                }

                // 실시간 모드: 전투 시간 기반 라운드 추정
                bool isInCombat = Game.Instance?.Player?.IsInCombat ?? false;

                if (isInCombat && !_wasInCombat)
                {
                    // 전투 시작 감지
                    _realtimeCombatStartTime = UnityEngine.Time.time;
                    _wasInCombat = true;
                    Main.Verbose($"[PhaseDetector] Real-time combat started at {_realtimeCombatStartTime:F1}s");
                }
                else if (!isInCombat && _wasInCombat)
                {
                    // 전투 종료 감지
                    _wasInCombat = false;
                    _realtimeCombatStartTime = 0f;
                }

                if (!isInCombat || _realtimeCombatStartTime <= 0f)
                    return 1;

                // 경과 시간으로 라운드 계산 (6초 = 1라운드)
                float elapsed = UnityEngine.Time.time - _realtimeCombatStartTime;
                int round = Math.Max(1, (int)(elapsed / SECONDS_PER_ROUND) + 1);

                return round;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// ★ v0.2.59: 전투 종료 시 상태 초기화 (외부 호출용)
        /// </summary>
        public static void ResetCombatTracking()
        {
            _realtimeCombatStartTime = 0f;
            _wasInCombat = false;
        }

        /// <summary>
        /// Get average HP percentage of allies
        /// </summary>
        private float GetAllyAverageHP(Situation situation)
        {
            if (situation.Allies == null || situation.Allies.Count == 0)
                return 100f;  // No allies to worry about

            try
            {
                var validAllies = situation.Allies
                    .Where(a => a != null && !a.Descriptor?.State?.IsDead == true)
                    .ToList();

                if (validAllies.Count == 0)
                    return 100f;

                return validAllies.Average(a => GetHPPercent(a));
            }
            catch
            {
                return 100f;
            }
        }

        /// <summary>
        /// Get average HP percentage of enemies
        /// </summary>
        private float GetAverageEnemyHP(Situation situation)
        {
            if (situation.Enemies == null || situation.Enemies.Count == 0)
                return 0f;  // No enemies

            try
            {
                var validEnemies = situation.Enemies
                    .Where(e => e != null && !e.Descriptor?.State?.IsDead == true)
                    .ToList();

                if (validEnemies.Count == 0)
                    return 0f;

                return validEnemies.Average(e => GetHPPercent(e));
            }
            catch
            {
                return 50f;  // Assume mid-HP on error
            }
        }

        /// <summary>
        /// Get HP percentage for a unit
        /// </summary>
        private float GetHPPercent(UnitEntityData unit)
        {
            try
            {
                if (unit?.Stats?.HitPoints == null)
                    return 100f;

                float current = unit.Stats.HitPoints.ModifiedValue;
                float max = unit.Stats.HitPoints.BaseValue;

                if (max <= 0)
                    return 100f;

                return (current / max) * 100f;
            }
            catch
            {
                return 100f;
            }
        }

        #endregion

        #region Phase Information

        /// <summary>
        /// Get a description of the phase for logging
        /// </summary>
        public static string GetPhaseDescription(CombatPhase phase)
        {
            switch (phase)
            {
                case CombatPhase.Opening:
                    return "Opening (buffs valuable)";
                case CombatPhase.Midgame:
                    return "Midgame (balanced)";
                case CombatPhase.Cleanup:
                    return "Cleanup (finish enemies)";
                case CombatPhase.Desperate:
                    return "Desperate (survival)";
                default:
                    return phase.ToString();
            }
        }

        #endregion
    }
}
