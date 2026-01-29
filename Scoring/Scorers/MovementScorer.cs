// ★ v0.2.22: Unified Decision Engine - Movement Scorer
// ★ v0.2.37: Geometric Mean Scoring with Considerations
using System;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Scoring.Scorers
{
    /// <summary>
    /// Scores movement actions.
    /// Considers range preferences, safety, and tactical positioning.
    /// </summary>
    public class MovementScorer
    {
        #region Constants

        private const float MELEE_OPTIMAL_DIST = 1.5f;
        private const float RANGED_OPTIMAL_MIN = 6f;
        private const float RANGED_OPTIMAL_MAX = 15f;
        private const float DANGER_THRESHOLD = 4f;

        #endregion

        #region Main Scoring

        /// <summary>
        /// Score a movement candidate
        /// ★ v0.2.37: Geometric Mean Scoring with Considerations
        /// </summary>
        public void Score(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate == null || situation == null || !candidate.MoveDestination.HasValue)
            {
                candidate?.Considerations.AddVeto("HasDestination", false);
                return;
            }

            float score = candidate.BaseScore;

            try
            {
                var destination = candidate.MoveDestination.Value;

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildConsiderations(candidate, destination, situation, weights);

                // 1. Purpose-based scoring
                score += ScoreMovementPurpose(candidate, destination, situation);

                // 2. Safety considerations
                score += ScoreSafety(destination, situation);

                // 3. Range preference alignment
                score += ScoreRangeAlignment(destination, situation);

                // 4. Phase-based adjustment
                switch (situation.CombatPhase)
                {
                    case CombatPhase.Opening:
                        // Movement to engage is valuable
                        if (IsMovingToEngage(destination, situation))
                            score += 10f;
                        break;

                    case CombatPhase.Cleanup:
                        // Chase down fleeing enemies
                        if (IsMovingToEngage(destination, situation))
                            score += 15f;
                        break;

                    case CombatPhase.Desperate:
                        // Retreat is valuable in desperate situations
                        if (IsMovingAway(destination, situation))
                            score += 15f;
                        break;
                }

                // 5. Already moved penalty
                if (situation.HasMovedThisTurn)
                    score -= 15f;

                // Apply phase weight
                candidate.PhaseMultiplier = weights.MoveWeight;
                candidate.BaseScore = score;
            }
            catch (Exception ex)
            {
                Main.Error($"[MovementScorer] Score error: {ex.Message}");
            }
        }

        #endregion

        #region Purpose Scoring

        /// <summary>
        /// Score based on the purpose of movement
        /// </summary>
        private float ScoreMovementPurpose(ActionCandidate candidate, Vector3 destination, Situation situation)
        {
            float score = 0f;

            // Movement to attack (can't hit anyone from current position)
            if (!situation.HasHittableEnemies && situation.HasLivingEnemies)
            {
                float currentDistToNearest = situation.NearestEnemyDistance;
                float newDistToNearest = GetDistanceToNearestEnemy(destination, situation);

                if (newDistToNearest < currentDistToNearest)
                {
                    // Moving closer to engage
                    score += 20f;

                    // Better if moving to optimal range
                    if (IsInOptimalRange(newDistToNearest, situation.RangePreference))
                        score += 15f;
                }
            }

            // Retreat movement (ranged character in danger)
            if (situation.IsInDanger && situation.PrefersRanged)
            {
                float currentDist = situation.NearestEnemyDistance;
                float newDist = GetDistanceToNearestEnemy(destination, situation);

                if (newDist > currentDist)
                {
                    score += 25f;  // Retreat is valuable
                }
            }

            // Flanking movement
            if (situation.HasSneakAttack)
            {
                // Could add flanking position detection here
                // For now, give small bonus for any movement when sneak attack is available
                score += 5f;
            }

            return score;
        }

        #endregion

        #region Safety Scoring

        /// <summary>
        /// Score based on safety of destination
        /// </summary>
        private float ScoreSafety(Vector3 destination, Situation situation)
        {
            float score = 0f;

            try
            {
                // Count enemies near destination
                int enemiesNearDest = CountEnemiesNear(destination, DANGER_THRESHOLD, situation);
                int enemiesNearCurrent = CountEnemiesNear(situation.Unit.Position, DANGER_THRESHOLD, situation);

                // Fewer nearby enemies = safer
                int enemyDiff = enemiesNearCurrent - enemiesNearDest;
                score += enemyDiff * 8f;

                // Range preference affects safety perception
                if (situation.PrefersRanged)
                {
                    // Ranged wants distance
                    float distToNearest = GetDistanceToNearestEnemy(destination, situation);
                    if (distToNearest >= RANGED_OPTIMAL_MIN)
                        score += 10f;
                    else if (distToNearest < DANGER_THRESHOLD)
                        score -= 15f;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[MovementScorer] ScoreSafety error: {ex.Message}");
            }

            return score;
        }

        #endregion

        #region Range Alignment

        /// <summary>
        /// Score based on how well destination aligns with range preference
        /// </summary>
        private float ScoreRangeAlignment(Vector3 destination, Situation situation)
        {
            if (situation.NearestEnemy == null)
                return 0f;

            float newDist = Vector3.Distance(destination, situation.NearestEnemy.Position);

            switch (situation.RangePreference)
            {
                case RangePreference.Melee:
                    // Want to be close
                    if (newDist <= MELEE_OPTIMAL_DIST)
                        return 15f;
                    else if (newDist <= 3f)
                        return 10f;
                    else
                        return -5f;

                case RangePreference.Ranged:
                    // Want optimal ranged distance
                    if (newDist >= RANGED_OPTIMAL_MIN && newDist <= RANGED_OPTIMAL_MAX)
                        return 15f;
                    else if (newDist < DANGER_THRESHOLD)
                        return -15f;  // Too close!
                    else if (newDist > RANGED_OPTIMAL_MAX)
                        return 5f;  // Bit far but safe
                    else
                        return 8f;  // Getting to optimal

                case RangePreference.Mixed:
                default:
                    // Flexible - any reasonable distance is ok
                    if (newDist <= 15f)
                        return 5f;
                    return 0f;
            }
        }

        #endregion

        #region ★ v0.2.37: Consideration Building

        /// <summary>
        /// 이동 행동에 대한 Consideration 구축
        /// </summary>
        private void BuildConsiderations(ActionCandidate candidate, Vector3 destination, Situation situation, PhaseRoleWeights weights)
        {
            var cs = candidate.Considerations;
            cs.Clear();

            // ═══════════════════════════════════════════════════════════════
            // 1. 이미 이동함 체크
            // ═══════════════════════════════════════════════════════════════
            cs.Add("NotMovedYet", situation.HasMovedThisTurn ? 0.3f : 1.0f);

            // ═══════════════════════════════════════════════════════════════
            // 2. 이동 필요성 (현재 위치에서 공격 가능하면 이동 불필요)
            // ═══════════════════════════════════════════════════════════════
            float movementNeed = 0.5f;  // 기본값
            if (!situation.HasHittableEnemies && situation.HasLivingEnemies)
            {
                // 공격할 적이 없으면 이동 필요
                movementNeed = 1.0f;
            }
            else if (situation.HasHittableEnemies)
            {
                // 이미 공격 가능하면 이동 덜 필요
                movementNeed = 0.3f;
            }
            cs.Add("MovementNeed", movementNeed);

            // ═══════════════════════════════════════════════════════════════
            // 3. 거리 개선도 (목표 지점이 더 나은 위치인지)
            // ═══════════════════════════════════════════════════════════════
            float currentDistToNearest = situation.NearestEnemyDistance;
            float newDistToNearest = GetDistanceToNearestEnemy(destination, situation);

            float distanceImprovement = 0.5f;
            switch (situation.RangePreference)
            {
                case RangePreference.Melee:
                    // 근접: 가까워지면 좋음
                    if (newDistToNearest < currentDistToNearest)
                        distanceImprovement = Mathf.Clamp01(1f - newDistToNearest / 10f);
                    else
                        distanceImprovement = 0.2f;
                    break;

                case RangePreference.Ranged:
                    // 원거리: 최적 거리로 이동
                    if (newDistToNearest >= RANGED_OPTIMAL_MIN && newDistToNearest <= RANGED_OPTIMAL_MAX)
                        distanceImprovement = 1.0f;
                    else if (newDistToNearest < DANGER_THRESHOLD)
                        distanceImprovement = 0.1f;  // 너무 가까움 = 위험
                    else
                        distanceImprovement = 0.6f;
                    break;

                default:
                    // 혼합: 적당한 거리면 OK
                    distanceImprovement = newDistToNearest <= 15f ? 0.7f : 0.4f;
                    break;
            }
            cs.Add("DistanceImprovement", distanceImprovement);

            // ═══════════════════════════════════════════════════════════════
            // 4. 안전도
            // ═══════════════════════════════════════════════════════════════
            int enemiesNearDest = CountEnemiesNear(destination, DANGER_THRESHOLD, situation);
            int enemiesNearCurrent = CountEnemiesNear(situation.Unit.Position, DANGER_THRESHOLD, situation);

            float safety = 0.5f;
            if (situation.PrefersRanged)
            {
                // 원거리 캐릭터: 적 적을수록 안전
                safety = Mathf.Clamp01(1f - enemiesNearDest * 0.2f);
            }
            else
            {
                // 근접 캐릭터: 적당한 수의 적은 괜찮음
                if (enemiesNearDest == 0)
                    safety = 0.4f;  // 적 없으면 공격 못함
                else if (enemiesNearDest <= 2)
                    safety = 1.0f;  // 적당
                else
                    safety = Mathf.Clamp01(1f - (enemiesNearDest - 2) * 0.15f);  // 너무 많음
            }
            cs.Add("Safety", safety);

            // ═══════════════════════════════════════════════════════════════
            // 5. 전투 페이즈 적합성
            // ═══════════════════════════════════════════════════════════════
            float phaseFit = 0.5f;
            switch (situation.CombatPhase)
            {
                case CombatPhase.Opening:
                    // 교전을 위한 이동은 좋음
                    phaseFit = IsMovingToEngage(destination, situation) ? 0.9f : 0.4f;
                    break;
                case CombatPhase.Midgame:
                    phaseFit = 0.6f;
                    break;
                case CombatPhase.Cleanup:
                    // 도망치는 적 추격
                    phaseFit = IsMovingToEngage(destination, situation) ? 0.8f : 0.3f;
                    break;
                case CombatPhase.Desperate:
                    // 후퇴는 좋음
                    phaseFit = IsMovingAway(destination, situation) ? 0.9f : 0.3f;
                    break;
            }
            cs.Add("PhaseFit", phaseFit);

            // ═══════════════════════════════════════════════════════════════
            // 6. 역할 적합성
            // ═══════════════════════════════════════════════════════════════
            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;
            cs.Add("RoleFit", ScoreNormalizer.RoleActionFit(role, CandidateType.Move));

            Main.Verbose($"[MovementScorer] Move to {destination}: {cs.ToDebugString()}");
        }

        #endregion

        #region Helper Methods

        private float GetDistanceToNearestEnemy(Vector3 position, Situation situation)
        {
            if (situation.Enemies == null || situation.Enemies.Count == 0)
                return float.MaxValue;

            float minDist = float.MaxValue;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null) continue;
                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < minDist)
                    minDist = dist;
            }

            return minDist;
        }

        private int CountEnemiesNear(Vector3 position, float radius, Situation situation)
        {
            if (situation.Enemies == null)
                return 0;

            return situation.Enemies.Count(e =>
                e != null && Vector3.Distance(position, e.Position) <= radius);
        }

        private bool IsInOptimalRange(float distance, RangePreference preference)
        {
            switch (preference)
            {
                case RangePreference.Melee:
                    return distance <= MELEE_OPTIMAL_DIST;
                case RangePreference.Ranged:
                    return distance >= RANGED_OPTIMAL_MIN && distance <= RANGED_OPTIMAL_MAX;
                default:
                    return distance <= 15f;
            }
        }

        private bool IsMovingToEngage(Vector3 destination, Situation situation)
        {
            float currentDist = situation.NearestEnemyDistance;
            float newDist = GetDistanceToNearestEnemy(destination, situation);
            return newDist < currentDist;
        }

        private bool IsMovingAway(Vector3 destination, Situation situation)
        {
            float currentDist = situation.NearestEnemyDistance;
            float newDist = GetDistanceToNearestEnemy(destination, situation);
            return newDist > currentDist;
        }

        #endregion
    }
}
