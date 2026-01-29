// ★ v0.2.22: Unified Decision Engine - Scoring Weights
using System.Collections.Generic;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// Combat phases for phase-based decision weighting
    /// </summary>
    public enum CombatPhase
    {
        /// <summary>Round 1-2, not engaged yet - buffs are valuable</summary>
        Opening,

        /// <summary>Normal combat - balanced approach</summary>
        Midgame,

        /// <summary>Few enemies left, low HP - finish them, conserve resources</summary>
        Cleanup,

        /// <summary>HP critical or overwhelmed - survival priority</summary>
        Desperate
    }

    /// <summary>
    /// Weight configuration for a specific Phase + Role combination
    /// </summary>
    public class PhaseRoleWeights
    {
        /// <summary>Multiplier for attack actions</summary>
        public float AttackWeight { get; set; } = 1.0f;

        /// <summary>Multiplier for buff actions</summary>
        public float BuffWeight { get; set; } = 1.0f;

        /// <summary>Multiplier for heal actions</summary>
        public float HealWeight { get; set; } = 1.0f;

        /// <summary>Multiplier for debuff/CC actions</summary>
        public float DebuffWeight { get; set; } = 1.0f;

        /// <summary>Multiplier for movement actions</summary>
        public float MoveWeight { get; set; } = 1.0f;

        /// <summary>Bonus multiplier for potential kills</summary>
        public float KillBonusWeight { get; set; } = 1.0f;

        /// <summary>
        /// Resource conservation factor (0 = spend freely, 1 = conserve heavily)
        /// High-level spells get penalized by: SpellLevel * 5 * ResourceConservation
        /// </summary>
        public float ResourceConservation { get; set; } = 0.5f;

        /// <summary>
        /// Minimum HP% to trigger emergency heal consideration
        /// </summary>
        public float EmergencyHealThreshold { get; set; } = 30f;

        /// <summary>
        /// Clone with modifications
        /// </summary>
        public PhaseRoleWeights Clone()
        {
            return new PhaseRoleWeights
            {
                AttackWeight = AttackWeight,
                BuffWeight = BuffWeight,
                HealWeight = HealWeight,
                DebuffWeight = DebuffWeight,
                MoveWeight = MoveWeight,
                KillBonusWeight = KillBonusWeight,
                ResourceConservation = ResourceConservation,
                EmergencyHealThreshold = EmergencyHealThreshold
            };
        }
    }

    /// <summary>
    /// Static weight tables for all Phase + Role combinations.
    /// Based on RT v3.5.7 patterns.
    /// </summary>
    public static class ScoringWeights
    {
        private static readonly Dictionary<(CombatPhase, AIRole), PhaseRoleWeights> _weights;

        static ScoringWeights()
        {
            _weights = new Dictionary<(CombatPhase, AIRole), PhaseRoleWeights>();
            InitializeDPSWeights();
            InitializeTankWeights();
            InitializeSupportWeights();
        }

        #region DPS Weights

        private static void InitializeDPSWeights()
        {
            // DPS Opening: Buff up, then attack
            _weights[(CombatPhase.Opening, AIRole.DPS)] = new PhaseRoleWeights
            {
                AttackWeight = 1.0f,
                BuffWeight = 1.5f,       // Buffs valuable at start
                HealWeight = 0.5f,
                DebuffWeight = 1.2f,     // Opening CC is good
                MoveWeight = 0.8f,
                KillBonusWeight = 1.0f,
                ResourceConservation = 0.3f,  // Can spend resources
                EmergencyHealThreshold = 25f
            };

            // DPS Midgame: Balanced, focus on damage
            _weights[(CombatPhase.Midgame, AIRole.DPS)] = new PhaseRoleWeights
            {
                AttackWeight = 1.2f,     // Emphasize damage
                BuffWeight = 0.6f,       // Less buffing
                HealWeight = 0.8f,
                DebuffWeight = 1.0f,
                MoveWeight = 1.0f,
                KillBonusWeight = 1.5f,  // Finishing enemies is good
                ResourceConservation = 0.5f,
                EmergencyHealThreshold = 30f
            };

            // DPS Cleanup: Finish enemies, save resources
            _weights[(CombatPhase.Cleanup, AIRole.DPS)] = new PhaseRoleWeights
            {
                AttackWeight = 1.5f,     // Heavy attack focus
                BuffWeight = 0.2f,       // Almost no buffing
                HealWeight = 0.3f,
                DebuffWeight = 0.5f,
                MoveWeight = 1.2f,       // Chase down stragglers
                KillBonusWeight = 2.0f,  // Strong kill priority
                ResourceConservation = 0.8f,  // Save for next fight
                EmergencyHealThreshold = 20f
            };

            // DPS Desperate: Survival mode
            _weights[(CombatPhase.Desperate, AIRole.DPS)] = new PhaseRoleWeights
            {
                AttackWeight = 0.8f,
                BuffWeight = 0.3f,
                HealWeight = 2.0f,       // Strong heal priority
                DebuffWeight = 0.4f,
                MoveWeight = 1.5f,       // Escape if needed
                KillBonusWeight = 2.5f,  // Kill to reduce threat
                ResourceConservation = 0.0f,  // Use everything
                EmergencyHealThreshold = 40f
            };
        }

        #endregion

        #region Tank Weights

        private static void InitializeTankWeights()
        {
            // Tank Opening: Buff up, move to engage
            _weights[(CombatPhase.Opening, AIRole.Tank)] = new PhaseRoleWeights
            {
                AttackWeight = 0.8f,
                BuffWeight = 1.8f,       // Defensive buffs critical
                HealWeight = 0.3f,
                DebuffWeight = 1.0f,
                MoveWeight = 1.5f,       // Get into position
                KillBonusWeight = 0.5f,
                ResourceConservation = 0.2f,
                EmergencyHealThreshold = 30f
            };

            // Tank Midgame: Hold aggro, survive
            _weights[(CombatPhase.Midgame, AIRole.Tank)] = new PhaseRoleWeights
            {
                AttackWeight = 1.0f,
                BuffWeight = 1.2f,
                HealWeight = 1.2f,       // Self-sustain important
                DebuffWeight = 1.3f,     // Taunt, CC
                MoveWeight = 0.8f,
                KillBonusWeight = 0.8f,
                ResourceConservation = 0.4f,
                EmergencyHealThreshold = 35f
            };

            // Tank Cleanup: Help finish
            _weights[(CombatPhase.Cleanup, AIRole.Tank)] = new PhaseRoleWeights
            {
                AttackWeight = 1.3f,
                BuffWeight = 0.3f,
                HealWeight = 0.5f,
                DebuffWeight = 0.6f,
                MoveWeight = 1.0f,
                KillBonusWeight = 1.5f,
                ResourceConservation = 0.7f,
                EmergencyHealThreshold = 25f
            };

            // Tank Desperate: Survive at all costs
            _weights[(CombatPhase.Desperate, AIRole.Tank)] = new PhaseRoleWeights
            {
                AttackWeight = 0.5f,
                BuffWeight = 1.5f,       // Defensive buffs
                HealWeight = 2.5f,       // Maximum heal priority
                DebuffWeight = 0.8f,
                MoveWeight = 0.6f,       // Hold position
                KillBonusWeight = 2.0f,
                ResourceConservation = 0.0f,
                EmergencyHealThreshold = 50f
            };
        }

        #endregion

        #region Support Weights

        private static void InitializeSupportWeights()
        {
            // Support Opening: Buff everyone
            _weights[(CombatPhase.Opening, AIRole.Support)] = new PhaseRoleWeights
            {
                AttackWeight = 0.4f,
                BuffWeight = 2.0f,       // Strong buff focus
                HealWeight = 1.0f,
                DebuffWeight = 0.8f,
                MoveWeight = 0.5f,       // Stay back
                KillBonusWeight = 0.3f,
                ResourceConservation = 0.1f,  // Spend on buffs
                EmergencyHealThreshold = 35f
            };

            // Support Midgame: Keep party alive
            _weights[(CombatPhase.Midgame, AIRole.Support)] = new PhaseRoleWeights
            {
                AttackWeight = 0.6f,
                BuffWeight = 1.0f,
                HealWeight = 1.5f,       // Healing priority
                DebuffWeight = 1.0f,
                MoveWeight = 0.7f,
                KillBonusWeight = 0.5f,
                ResourceConservation = 0.4f,
                EmergencyHealThreshold = 40f
            };

            // Support Cleanup: Light support
            _weights[(CombatPhase.Cleanup, AIRole.Support)] = new PhaseRoleWeights
            {
                AttackWeight = 1.0f,     // Can help finish
                BuffWeight = 0.4f,
                HealWeight = 0.8f,
                DebuffWeight = 0.6f,
                MoveWeight = 0.8f,
                KillBonusWeight = 1.0f,
                ResourceConservation = 0.8f,
                EmergencyHealThreshold = 30f
            };

            // Support Desperate: Emergency healing
            _weights[(CombatPhase.Desperate, AIRole.Support)] = new PhaseRoleWeights
            {
                AttackWeight = 0.3f,
                BuffWeight = 0.5f,
                HealWeight = 3.0f,       // Maximum heal priority
                DebuffWeight = 0.5f,
                MoveWeight = 1.0f,
                KillBonusWeight = 1.5f,
                ResourceConservation = 0.0f,
                EmergencyHealThreshold = 50f
            };
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get weights for the specified phase and role combination
        /// </summary>
        public static PhaseRoleWeights GetWeights(CombatPhase phase, AIRole role)
        {
            if (_weights.TryGetValue((phase, role), out var weights))
                return weights;

            // Fallback to DPS Midgame
            return _weights[(CombatPhase.Midgame, AIRole.DPS)];
        }

        /// <summary>
        /// Get the weight multiplier for a specific candidate type
        /// </summary>
        public static float GetWeightForType(CandidateType type, PhaseRoleWeights weights)
        {
            switch (type)
            {
                case CandidateType.AbilityAttack:
                case CandidateType.BasicAttack:
                    return weights.AttackWeight;

                case CandidateType.Buff:
                    return weights.BuffWeight;

                case CandidateType.Heal:
                    return weights.HealWeight;

                case CandidateType.Debuff:
                    return weights.DebuffWeight;

                case CandidateType.Move:
                    return weights.MoveWeight;

                default:
                    return 1.0f;
            }
        }

        #endregion
    }
}
