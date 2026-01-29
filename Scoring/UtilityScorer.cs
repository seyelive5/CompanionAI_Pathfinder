// ★ v0.2.22: Unified Decision Engine - Utility Scorer
// ★ v0.2.37: Geometric Mean Scoring with Hysteresis
using System;
using System.Collections.Generic;
using System.Linq;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Scoring.Scorers;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// Central scoring orchestrator.
    /// Applies phase weights, role weights, and delegates to specialized scorers.
    /// ★ v0.2.37: Geometric Mean + Hysteresis 지원
    /// </summary>
    public class UtilityScorer
    {
        #region Scorers

        private readonly AttackScorer _attackScorer;
        private readonly BuffScorer _buffScorer;
        private readonly DebuffScorer _debuffScorer;
        private readonly MovementScorer _movementScorer;

        #endregion

        #region ★ v0.2.37: Hysteresis Tracking

        /// <summary>유닛별 마지막 행동 유형</summary>
        private static readonly Dictionary<string, CandidateType> _lastActionType = new Dictionary<string, CandidateType>();

        /// <summary>유닛별 마지막 능력 ID</summary>
        private static readonly Dictionary<string, string> _lastAbilityId = new Dictionary<string, string>();

        /// <summary>유닛별 마지막 타겟 ID</summary>
        private static readonly Dictionary<string, string> _lastTargetId = new Dictionary<string, string>();

        /// <summary>같은 행동 유형 보너스</summary>
        private const float HYSTERESIS_ACTION_BONUS = 5f;

        /// <summary>같은 능력 사용 보너스</summary>
        private const float HYSTERESIS_ABILITY_BONUS = 3f;

        /// <summary>같은 타겟 공격 보너스</summary>
        private const float HYSTERESIS_TARGET_BONUS = 2f;

        #endregion

        #region Constructor

        public UtilityScorer()
        {
            _attackScorer = new AttackScorer();
            _buffScorer = new BuffScorer();
            _debuffScorer = new DebuffScorer();
            _movementScorer = new MovementScorer();
        }

        #endregion

        #region Main Scoring

        /// <summary>
        /// Score all candidates with phase and role weights
        /// </summary>
        public void ScoreAll(List<ActionCandidate> candidates, Situation situation, CombatPhase phase)
        {
            if (candidates == null || candidates.Count == 0 || situation == null)
                return;

            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;
            var weights = ScoringWeights.GetWeights(phase, role);

            Main.Verbose($"[UtilityScorer] Scoring {candidates.Count} candidates (Phase={phase}, Role={role})");

            foreach (var candidate in candidates)
            {
                try
                {
                    ScoreCandidate(candidate, situation, weights);
                }
                catch (Exception ex)
                {
                    Main.Error($"[UtilityScorer] Error scoring {candidate?.ActionType}: {ex.Message}");
                    candidate.BaseScore = -1000f;  // Penalize errored candidates
                }
            }
        }

        /// <summary>
        /// Score a single candidate
        /// </summary>
        private void ScoreCandidate(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate == null)
                return;

            switch (candidate.ActionType)
            {
                case CandidateType.AbilityAttack:
                case CandidateType.BasicAttack:
                    _attackScorer.Score(candidate, situation, weights);
                    break;

                case CandidateType.Buff:
                case CandidateType.Heal:
                    _buffScorer.Score(candidate, situation, weights);
                    break;

                case CandidateType.Debuff:
                    _debuffScorer.Score(candidate, situation, weights);
                    break;

                case CandidateType.Move:
                    _movementScorer.Score(candidate, situation, weights);
                    break;

                case CandidateType.EndTurn:
                    // EndTurn always has score 0
                    candidate.BaseScore = 0f;
                    candidate.PhaseMultiplier = 1f;
                    candidate.RoleMultiplier = 1f;
                    break;
            }

            // Apply role multiplier based on action type
            ApplyRoleMultiplier(candidate, situation.CharacterSettings?.Role ?? AIRole.DPS);
        }

        /// <summary>
        /// Apply role-specific multipliers
        /// </summary>
        private void ApplyRoleMultiplier(ActionCandidate candidate, AIRole role)
        {
            float multiplier = 1f;

            switch (role)
            {
                case AIRole.DPS:
                    if (candidate.IsAttack)
                        multiplier = 1.1f;  // DPS favors attacks
                    else if (candidate.IsSupportive)
                        multiplier = 0.9f;  // Less support focus
                    break;

                case AIRole.Tank:
                    if (candidate.ActionType == CandidateType.Buff)
                        multiplier = 1.1f;  // Tank values defensive buffs
                    else if (candidate.ActionType == CandidateType.Debuff)
                        multiplier = 1.05f;  // CC/taunt
                    break;

                case AIRole.Support:
                    if (candidate.IsSupportive)
                        multiplier = 1.15f;  // Support favors healing/buffing
                    else if (candidate.IsAttack)
                        multiplier = 0.85f;  // Less attack focus
                    break;
            }

            candidate.RoleMultiplier = multiplier;
        }

        #endregion

        #region ★ v0.2.37: Hysteresis

        /// <summary>
        /// Hysteresis 적용: 이전과 유사한 행동에 보너스
        /// 행동의 연속성을 유지하여 떨림 방지
        /// </summary>
        /// <param name="candidate">평가 대상 후보</param>
        /// <param name="unitId">유닛 고유 ID</param>
        private void ApplyHysteresis(ActionCandidate candidate, string unitId)
        {
            if (string.IsNullOrEmpty(unitId))
                return;

            float bonus = 0f;

            // 1. 같은 행동 유형 보너스
            if (_lastActionType.TryGetValue(unitId, out var lastType))
            {
                if (candidate.ActionType == lastType)
                {
                    bonus += HYSTERESIS_ACTION_BONUS;
                }
            }

            // 2. 같은 능력 사용 보너스
            if (_lastAbilityId.TryGetValue(unitId, out var lastAbility) &&
                !string.IsNullOrEmpty(lastAbility))
            {
                string currentAbilityId = candidate.Ability?.UniqueId ?? "";
                if (!string.IsNullOrEmpty(currentAbilityId) && currentAbilityId == lastAbility)
                {
                    bonus += HYSTERESIS_ABILITY_BONUS;
                }
            }

            // 3. 같은 타겟 공격 보너스
            if (candidate.TargetsEnemy &&
                _lastTargetId.TryGetValue(unitId, out var lastTarget) &&
                !string.IsNullOrEmpty(lastTarget))
            {
                string currentTargetId = candidate.Target?.UniqueId ?? "";
                if (!string.IsNullOrEmpty(currentTargetId) && currentTargetId == lastTarget)
                {
                    bonus += HYSTERESIS_TARGET_BONUS;
                }
            }

            // BonusScore에 추가
            if (bonus > 0f)
            {
                candidate.BonusScore += bonus;
                Main.Verbose($"[Hysteresis] {candidate.ActionType}: +{bonus:F1} bonus");
            }
        }

        /// <summary>
        /// 결정 후 Hysteresis 추적 기록
        /// </summary>
        /// <param name="chosen">선택된 행동</param>
        /// <param name="unitId">유닛 고유 ID</param>
        public void RecordDecision(ActionCandidate chosen, string unitId)
        {
            if (chosen == null || string.IsNullOrEmpty(unitId))
                return;

            _lastActionType[unitId] = chosen.ActionType;
            _lastAbilityId[unitId] = chosen.Ability?.UniqueId ?? "";
            _lastTargetId[unitId] = chosen.Target?.UniqueId ?? "";
        }

        /// <summary>
        /// 전투 종료 시 Hysteresis 데이터 초기화
        /// </summary>
        public static void ClearHysteresisData()
        {
            _lastActionType.Clear();
            _lastAbilityId.Clear();
            _lastTargetId.Clear();
        }

        #endregion

        #region ★ v0.2.37: Geometric Mean Selection

        /// <summary>
        /// Geometric Mean 기반 최적 후보 선택
        /// </summary>
        /// <param name="candidates">후보 목록</param>
        /// <param name="unitId">유닛 ID (Hysteresis용)</param>
        /// <returns>최적 후보 또는 null</returns>
        public ActionCandidate SelectBest(List<ActionCandidate> candidates, string unitId = null)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            // 1. Hysteresis 적용
            if (!string.IsNullOrEmpty(unitId))
            {
                foreach (var c in candidates)
                {
                    ApplyHysteresis(c, unitId);
                }
            }

            // 2. Veto된 행동 필터링
            var viable = candidates.Where(c => !c.IsVetoed).ToList();
            if (viable.Count == 0)
            {
                Main.Log("[UtilityScorer] All candidates vetoed, returning EndTurn");
                return candidates.FirstOrDefault(c => c.ActionType == CandidateType.EndTurn)
                    ?? ActionCandidate.EndTurn("All actions vetoed");
            }

            // 3. HybridFinalScore로 정렬 (높은 순)
            viable.Sort((a, b) => b.HybridFinalScore.CompareTo(a.HybridFinalScore));

            var best = viable[0];

            // 4. 결정 기록 (다음 Hysteresis용)
            if (!string.IsNullOrEmpty(unitId))
            {
                RecordDecision(best, unitId);
            }

            return best;
        }

        /// <summary>
        /// 상위 N개 후보 반환 (Geometric Mean 기반)
        /// </summary>
        public List<ActionCandidate> GetTopCandidatesGM(List<ActionCandidate> candidates, int count = 3)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<ActionCandidate>();

            // Veto 제외, HybridFinalScore 정렬
            var sorted = candidates
                .Where(c => !c.IsVetoed)
                .OrderByDescending(c => c.HybridFinalScore)
                .Take(count)
                .ToList();

            return sorted;
        }

        /// <summary>
        /// Geometric Mean 기반 상위 후보 로깅
        /// </summary>
        public void LogTopCandidatesGM(List<ActionCandidate> candidates, string unitName, int count = 3)
        {
            var top = GetTopCandidatesGM(candidates, count);

            if (top.Count == 0)
            {
                Main.Log($"[UtilityScorer] {unitName}: No viable candidates (all vetoed)");
                return;
            }

            Main.Log($"[UtilityScorer] {unitName} Top {top.Count} (GM-based):");
            for (int i = 0; i < top.Count; i++)
            {
                var c = top[i];
                Main.Log($"  {i + 1}. {c}");
            }

            // 첫 번째 후보의 Consideration 상세 로그
            if (top.Count > 0 && top[0].Considerations.Count > 0)
            {
                Main.Verbose($"  [Details] {top[0].ConsiderationDebug}");
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Get top N candidates by score
        /// ★ v0.2.42: HybridFinalScore 사용 (Geometric Mean 기반 선택과 일치)
        /// </summary>
        public List<ActionCandidate> GetTopCandidates(List<ActionCandidate> candidates, int count = 3)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<ActionCandidate>();

            // ★ v0.2.42: HybridFinalScore로 정렬 (선택 로직과 동일)
            candidates.Sort((a, b) => b.HybridFinalScore.CompareTo(a.HybridFinalScore));
            return candidates.GetRange(0, Math.Min(count, candidates.Count));
        }

        /// <summary>
        /// Log top candidates for debugging
        /// ★ v0.2.42: HybridFinalScore 기반 로깅
        /// </summary>
        public void LogTopCandidates(List<ActionCandidate> candidates, string unitName, int count = 3)
        {
            var top = GetTopCandidates(candidates, count);

            Main.Log($"[UtilityScorer] {unitName} Top {top.Count} decisions (by HybridFinalScore):");
            for (int i = 0; i < top.Count; i++)
            {
                var c = top[i];
                Main.Log($"  {i + 1}. {c}");
            }
        }

        #endregion
    }
}
