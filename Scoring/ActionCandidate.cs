// ★ v0.2.22: Unified Decision Engine - Action Candidate
// ★ v0.2.37: Geometric Mean Scoring with Consideration System
// ★ v0.2.49: PriorityBoost for escape actions (AoE/CC)
using System;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// Action candidate types
    /// </summary>
    public enum CandidateType
    {
        AbilityAttack,  // Ability-based attack (spell, weapon ability)
        BasicAttack,    // UnitAttack (basic melee/ranged)
        Buff,           // Self or ally buff
        Heal,           // Self or ally heal
        Debuff,         // Enemy debuff/CC
        Move,           // Movement action
        EndTurn         // End turn (fallback)
    }

    /// <summary>
    /// A scored action candidate for the unified decision engine.
    /// All possible actions are wrapped in this class with scoring components.
    /// </summary>
    public class ActionCandidate
    {
        #region Action Data

        /// <summary>Type of action</summary>
        public CandidateType ActionType { get; set; }

        /// <summary>Ability to use (null for BasicAttack/Move/EndTurn)</summary>
        public AbilityData Ability { get; set; }

        /// <summary>Ability classification from AbilityClassifier</summary>
        public AbilityClassification Classification { get; set; }

        /// <summary>Target unit (for attacks, buffs, heals, debuffs)</summary>
        public UnitEntityData Target { get; set; }

        /// <summary>Move destination (for movement actions)</summary>
        public Vector3? MoveDestination { get; set; }

        /// <summary>Human-readable reason for this action</summary>
        public string Reason { get; set; }

        #endregion

        #region Scoring Components

        /// <summary>Base score before modifiers (typically 40-60)</summary>
        public float BaseScore { get; set; }

        /// <summary>Effectiveness multiplier from AbilityClassifier (0.0-1.0+)</summary>
        public float EffectivenessMultiplier { get; set; } = 1f;

        /// <summary>Phase weight multiplier (Opening/Midgame/Cleanup/Desperate)</summary>
        public float PhaseMultiplier { get; set; } = 1f;

        /// <summary>Role weight multiplier (DPS/Tank/Support)</summary>
        public float RoleMultiplier { get; set; } = 1f;

        /// <summary>Penalty for using limited resources</summary>
        public float ResourcePenalty { get; set; } = 0f;

        /// <summary>Bonus score from special conditions (kill bonus, AoE, flanking, etc.)</summary>
        public float BonusScore { get; set; } = 0f;

        /// <summary>★ v0.2.49: Priority boost for urgent actions (escape AoE, CC removal)</summary>
        public float PriorityBoost { get; set; } = 0f;

        #endregion

        #region ★ v0.2.37: Geometric Mean Scoring

        /// <summary>
        /// Consideration 집합 - Geometric Mean 계산에 사용
        /// </summary>
        public ConsiderationSet Considerations { get; private set; } = new ConsiderationSet();

        /// <summary>
        /// Geometric Mean 기반 점수 (0.0 ~ 1.0)
        /// Veto 발생 시 0 반환
        /// </summary>
        public float GeometricScore => Considerations.ComputeGeometricMean();

        /// <summary>
        /// 이 행동이 Veto되었는지 (불가능한 행동)
        /// </summary>
        public bool IsVetoed => Considerations.HasVeto;

        /// <summary>
        /// 하이브리드 최종 점수 (Geometric Mean + Bonus + PriorityBoost)
        /// GM 점수를 100배 스케일링 후 BonusScore/PriorityBoost 가산
        /// ★ v0.2.49: PriorityBoost 추가 (위험 탈출 우선순위)
        /// Veto 시 -1000 반환
        /// </summary>
        public float HybridFinalScore
        {
            get
            {
                // Veto된 행동은 선택 불가
                if (IsVetoed)
                    return -1000f;

                // Consideration이 없으면 기존 FinalScore 사용 (호환성)
                if (Considerations.Count == 0)
                    return FinalScore + PriorityBoost;

                // Geometric Mean을 100점 스케일로 변환 후 BonusScore + PriorityBoost 가산
                float gmScore = GeometricScore * 100f;
                return gmScore + BonusScore + PriorityBoost;
            }
        }

        /// <summary>
        /// 디버깅용: Consideration 상세 정보
        /// </summary>
        public string ConsiderationDebug => Considerations.ToDebugString();

        /// <summary>
        /// Consideration 초기화 (재사용 시)
        /// </summary>
        public void ResetConsiderations()
        {
            Considerations.Clear();
        }

        #endregion

        #region Computed Properties

        /// <summary>
        /// Final computed score used for action selection.
        /// Formula: (Base × Effectiveness × Phase × Role) - ResourcePenalty + Bonus + Priority
        /// ★ v0.2.49: PriorityBoost 추가
        /// </summary>
        public float FinalScore =>
            (BaseScore * EffectivenessMultiplier * PhaseMultiplier * RoleMultiplier)
            - ResourcePenalty + BonusScore + PriorityBoost;

        /// <summary>Whether this is an attack action</summary>
        public bool IsAttack => ActionType == CandidateType.AbilityAttack || ActionType == CandidateType.BasicAttack;

        /// <summary>Whether this is a supportive action (buff/heal)</summary>
        public bool IsSupportive => ActionType == CandidateType.Buff || ActionType == CandidateType.Heal;

        /// <summary>Whether this action targets an enemy</summary>
        public bool TargetsEnemy => IsAttack || ActionType == CandidateType.Debuff;

        /// <summary>Whether this action uses an ability</summary>
        public bool UsesAbility => Ability != null;

        #endregion

        #region Factory Methods

        /// <summary>
        /// Create an ability attack candidate
        /// </summary>
        public static ActionCandidate AbilityAttack(
            AbilityData ability,
            AbilityClassification classification,
            UnitEntityData target,
            float effectiveness,
            string reason = null)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.AbilityAttack,
                Ability = ability,
                Classification = classification,
                Target = target,
                BaseScore = 50f,
                EffectivenessMultiplier = effectiveness,
                Reason = reason ?? $"Attack {target?.CharacterName} with {ability?.Name}"
            };
        }

        /// <summary>
        /// Create a basic attack candidate (UnitAttack)
        /// </summary>
        public static ActionCandidate BasicAttack(UnitEntityData target, string reason = null)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.BasicAttack,
                Target = target,
                BaseScore = 40f,  // Lower than ability attacks
                Reason = reason ?? $"Basic attack on {target?.CharacterName}"
            };
        }

        /// <summary>
        /// Create a buff candidate
        /// </summary>
        public static ActionCandidate Buff(
            AbilityData ability,
            AbilityClassification classification,
            UnitEntityData target,
            string reason = null)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.Buff,
                Ability = ability,
                Classification = classification,
                Target = target,
                BaseScore = 45f,
                Reason = reason ?? $"Buff {target?.CharacterName} with {ability?.Name}"
            };
        }

        /// <summary>
        /// Create a heal candidate
        /// </summary>
        public static ActionCandidate Heal(
            AbilityData ability,
            AbilityClassification classification,
            UnitEntityData target,
            float urgency,
            string reason = null)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.Heal,
                Ability = ability,
                Classification = classification,
                Target = target,
                BaseScore = 45f,
                BonusScore = urgency * 30f,  // Urgency adds significant bonus
                Reason = reason ?? $"Heal {target?.CharacterName} with {ability?.Name}"
            };
        }

        /// <summary>
        /// Create a debuff/CC candidate
        /// </summary>
        public static ActionCandidate Debuff(
            AbilityData ability,
            AbilityClassification classification,
            UnitEntityData target,
            float effectiveness,
            string reason = null)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.Debuff,
                Ability = ability,
                Classification = classification,
                Target = target,
                BaseScore = 48f,
                EffectivenessMultiplier = effectiveness,
                Reason = reason ?? $"Debuff {target?.CharacterName} with {ability?.Name}"
            };
        }

        /// <summary>
        /// Create a movement candidate
        /// </summary>
        public static ActionCandidate Move(Vector3 destination, string reason)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.Move,
                MoveDestination = destination,
                BaseScore = 30f,  // Lower than actions
                Reason = reason
            };
        }

        /// <summary>
        /// Create an end turn candidate
        /// </summary>
        public static ActionCandidate EndTurn(string reason)
        {
            return new ActionCandidate
            {
                ActionType = CandidateType.EndTurn,
                BaseScore = 0f,  // Always lowest priority
                Reason = reason
            };
        }

        #endregion

        #region Conversion

        /// <summary>
        /// Convert to PlannedAction for execution
        /// </summary>
        public PlannedAction ToPlannedAction()
        {
            switch (ActionType)
            {
                case CandidateType.AbilityAttack:
                    return PlannedAction.Attack(Ability, Target, Reason, 1f);

                case CandidateType.BasicAttack:
                    // Special handling - PlannedAction doesn't have BasicAttack type
                    // This will be handled specially in ActionExecutor (when Ability is null)
                    return new PlannedAction
                    {
                        Type = Core.ActionType.Attack,
                        Target = Target != null ? new TargetWrapper(Target) : null,
                        Reason = Reason,
                        ActionCost = 1f
                    };

                case CandidateType.Buff:
                    return PlannedAction.Buff(Ability, Target, Reason, 1f);

                case CandidateType.Heal:
                    return PlannedAction.Heal(Ability, Target, Reason, 1f);

                case CandidateType.Debuff:
                    return PlannedAction.Debuff(Ability, Target, Reason, 1f);

                case CandidateType.Move:
                    return PlannedAction.Move(MoveDestination.Value, Reason);

                case CandidateType.EndTurn:
                default:
                    return PlannedAction.EndTurn(Reason);
            }
        }

        #endregion

        #region ToString

        public override string ToString()
        {
            string targetStr = Target?.CharacterName ?? (MoveDestination.HasValue ? "position" : "none");
            string abilityStr = Ability?.Name ?? ActionType.ToString();

            // ★ v0.2.49: PriorityBoost 표시 추가
            string priorityStr = PriorityBoost > 0 ? $", Priority={PriorityBoost:F0}" : "";

            // ★ v0.2.37: Geometric Mean 정보 포함
            if (Considerations.Count > 0)
            {
                return $"[{ActionType}] {abilityStr} -> {targetStr} (Hybrid={HybridFinalScore:F1}, GM={GeometricScore:F3}, Bonus={BonusScore:F1}{priorityStr}{(IsVetoed ? ", VETOED" : "")})";
            }

            // 기존 포맷 (호환성)
            return $"[{ActionType}] {abilityStr} -> {targetStr} (Score={FinalScore:F1}, Base={BaseScore:F0}, Eff={EffectivenessMultiplier:F2}, Phase={PhaseMultiplier:F2}{priorityStr})";
        }

        #endregion
    }
}
