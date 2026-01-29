// ★ v0.2.22: Unified Decision Engine - Response Curves
using System;
using UnityEngine;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// Non-linear utility functions for diminishing returns and urgency scaling.
    /// Based on RT v3.5.7 response curve patterns.
    /// </summary>
    public static class ResponseCurves
    {
        #region HP-Based Curves

        /// <summary>
        /// Heal urgency based on HP percentage.
        /// Returns higher values at lower HP with exponential increase below critical threshold.
        /// Range: 0.0 to 3.0+
        /// </summary>
        /// <param name="hpPercent">Current HP percentage (0-100)</param>
        public static float HealUrgency(float hpPercent)
        {
            if (hpPercent >= 80f)
                return 0.1f;  // Almost full, minimal urgency

            if (hpPercent >= 60f)
                return 0.3f + (80f - hpPercent) * 0.01f;  // Mild urgency

            if (hpPercent >= 40f)
                return 0.5f + (60f - hpPercent) * 0.02f;  // Moderate urgency

            if (hpPercent >= 25f)
                return 1.0f + (40f - hpPercent) * 0.04f;  // High urgency

            // Critical: Exponential increase below 25%
            float criticalFactor = (25f - hpPercent) / 25f;
            return 1.6f + Mathf.Pow(criticalFactor, 1.5f) * 2f;
        }

        /// <summary>
        /// Target priority based on HP percentage.
        /// Lower HP = higher priority for finishing off.
        /// Range: 0.0 to 2.0
        /// </summary>
        public static float TargetHPPriority(float hpPercent)
        {
            if (hpPercent <= 10f)
                return 2.0f;  // Nearly dead, finish them

            if (hpPercent <= 25f)
                return 1.5f + (25f - hpPercent) * 0.02f;

            if (hpPercent <= 50f)
                return 1.0f + (50f - hpPercent) * 0.02f;

            // High HP targets are lower priority
            return 1.0f - (hpPercent - 50f) * 0.01f;
        }

        /// <summary>
        /// Kill bonus based on damage-to-HP ratio.
        /// Significant bonus when damage would kill or nearly kill.
        /// Range: 0.0 to 50.0+
        /// </summary>
        /// <param name="damageRatio">Estimated damage / target HP (0.0 to 1.0+)</param>
        public static float KillBonus(float damageRatio)
        {
            if (damageRatio < 0.5f)
                return 0f;  // Not close to killing

            if (damageRatio < 0.75f)
                return (damageRatio - 0.5f) * 40f;  // 0-10 bonus

            if (damageRatio < 1.0f)
            {
                // Exponential increase approaching kill
                float killProximity = (damageRatio - 0.75f) / 0.25f;
                return 10f + Mathf.Pow(killProximity, 2) * 30f;
            }

            // Overkill: slightly less valuable than exact kill
            return 40f + (damageRatio - 1.0f) * 5f;
        }

        #endregion

        #region Resource Curves

        /// <summary>
        /// Resource value multiplier based on remaining uses.
        /// Scarcer resources are more valuable to conserve.
        /// Range: 0.5 to 2.0
        /// </summary>
        /// <param name="remainingCasts">Number of uses left</param>
        /// <param name="maxCasts">Maximum uses. -1 for unlimited.</param>
        public static float ResourceValue(int remainingCasts, int maxCasts)
        {
            // Unlimited resource
            if (maxCasts <= 0 || remainingCasts < 0)
                return 0.5f;  // Low conservation value

            float ratio = (float)remainingCasts / maxCasts;

            // Last cast is very valuable
            if (remainingCasts == 1)
                return 2.0f;

            // Few casts left
            if (ratio <= 0.25f)
                return 1.5f;

            // Half remaining
            if (ratio <= 0.5f)
                return 1.0f;

            // Plenty left
            return 0.5f;
        }

        /// <summary>
        /// Spell level penalty for resource conservation.
        /// Higher level spells get bigger penalty when conserving.
        /// Range: 0.0 to 45.0
        /// </summary>
        /// <param name="spellLevel">Spell level (0-9)</param>
        /// <param name="conservationFactor">How much to conserve (0.0 to 1.0)</param>
        public static float SpellLevelPenalty(int spellLevel, float conservationFactor)
        {
            if (spellLevel <= 0)
                return 0f;  // Cantrips have no penalty

            // Penalty = level * 5 * conservation
            // Level 9 with full conservation = 45 penalty
            return spellLevel * 5f * conservationFactor;
        }

        #endregion

        #region Distance Curves

        /// <summary>
        /// Distance penalty for ranged actions.
        /// Quadratic penalty - distance matters more at longer ranges.
        /// Range: 0.0 to 30.0+
        /// </summary>
        /// <param name="distance">Distance in game units (meters)</param>
        /// <param name="maxRange">Maximum effective range</param>
        public static float DistancePenalty(float distance, float maxRange)
        {
            if (distance <= 0 || maxRange <= 0)
                return 0f;

            float ratio = distance / maxRange;

            // Within optimal range
            if (ratio <= 0.5f)
                return 0f;

            // Quadratic penalty beyond optimal
            float excess = ratio - 0.5f;
            return excess * excess * 120f;  // 30 penalty at max range
        }

        /// <summary>
        /// Melee approach bonus - closer is better for melee.
        /// Range: -10.0 to 15.0
        /// </summary>
        /// <param name="distance">Distance to target in meters</param>
        public static float MeleeDistanceBonus(float distance)
        {
            if (distance <= 1.5f)
                return 15f;  // Optimal melee range

            if (distance <= 3f)
                return 10f - (distance - 1.5f) * 3f;

            if (distance <= 6f)
                return 5f - (distance - 3f) * 2.5f;

            // Far from melee range
            return -5f - (distance - 6f) * 0.5f;
        }

        /// <summary>
        /// Ranged optimal distance bonus.
        /// Not too close (danger), not too far (accuracy).
        /// Range: -10.0 to 10.0
        /// </summary>
        /// <param name="distance">Distance to target in meters</param>
        public static float RangedDistanceBonus(float distance)
        {
            // Too close - danger!
            if (distance < 3f)
                return -10f + distance * 2f;

            // Optimal ranged distance: 6-15m
            if (distance >= 6f && distance <= 15f)
                return 10f;

            // Getting close to optimal
            if (distance >= 3f && distance < 6f)
                return 5f + (distance - 3f) * 1.67f;

            // Beyond optimal but still ok
            if (distance > 15f && distance <= 25f)
                return 10f - (distance - 15f) * 0.5f;

            // Too far
            return 5f - (distance - 25f) * 0.5f;
        }

        #endregion

        #region Buff/Debuff Curves

        /// <summary>
        /// Buff value based on existing buff count.
        /// Diminishing returns - first few buffs are most valuable.
        /// Range: 0.3 to 1.5
        /// </summary>
        public static float BuffStackValue(int currentBuffCount)
        {
            if (currentBuffCount == 0)
                return 1.5f;  // First buff very valuable

            if (currentBuffCount == 1)
                return 1.0f;

            if (currentBuffCount == 2)
                return 0.7f;

            if (currentBuffCount == 3)
                return 0.5f;

            return 0.3f;  // Diminishing returns
        }

        /// <summary>
        /// CC value based on target threat level.
        /// Higher threat = more valuable to CC.
        /// Range: 0.5 to 2.0
        /// </summary>
        /// <param name="threatLevel">Threat score (0.0 to 1.0)</param>
        public static float CCTargetValue(float threatLevel)
        {
            // Low threat - less valuable to CC
            if (threatLevel < 0.3f)
                return 0.5f;

            // Medium threat
            if (threatLevel < 0.6f)
                return 0.5f + (threatLevel - 0.3f) * 2.5f;  // 0.5 to 1.25

            // High threat - valuable to CC
            if (threatLevel < 0.8f)
                return 1.25f + (threatLevel - 0.6f) * 2.5f;  // 1.25 to 1.75

            // Very high threat - critical to CC
            return 1.75f + (threatLevel - 0.8f) * 1.25f;  // up to 2.0
        }

        #endregion

        #region AoE Curves

        /// <summary>
        /// AoE value based on number of targets hit.
        /// Significant bonus for hitting multiple enemies.
        /// Range: 0.0 to 60.0+
        /// </summary>
        public static float AoETargetCountBonus(int enemyCount, int allyCount = 0)
        {
            // Hitting allies is very bad
            if (allyCount > 0)
                return -1000f;  // Never hit allies

            if (enemyCount <= 1)
                return 0f;  // Single target, no AoE bonus

            // Each additional enemy is valuable
            // 2 enemies: +15, 3 enemies: +32, 4 enemies: +51, etc.
            // Slightly diminishing returns
            float bonus = 0f;
            for (int i = 2; i <= enemyCount; i++)
            {
                bonus += 15f + (i - 2) * 2f;
            }

            return bonus;
        }

        #endregion

        #region Sigmoid Utility

        /// <summary>
        /// General sigmoid function for smooth transitions.
        /// Maps input to 0-1 range with smooth S-curve.
        /// </summary>
        /// <param name="x">Input value</param>
        /// <param name="midpoint">Value where output is 0.5</param>
        /// <param name="steepness">How steep the transition is</param>
        public static float Sigmoid(float x, float midpoint = 0f, float steepness = 1f)
        {
            return 1f / (1f + Mathf.Exp(-steepness * (x - midpoint)));
        }

        /// <summary>
        /// Inverse sigmoid - high at low values, low at high values.
        /// Useful for urgency calculations.
        /// </summary>
        public static float InverseSigmoid(float x, float midpoint = 50f, float steepness = 0.1f)
        {
            return 1f - Sigmoid(x, midpoint, steepness);
        }

        #endregion
    }
}
