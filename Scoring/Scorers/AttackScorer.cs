// ★ v0.2.22: Unified Decision Engine - Attack Scorer
// ★ v0.2.37: Geometric Mean Scoring with Considerations
// ★ v0.2.41: Charge ability distance penalty
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Scoring.Scorers
{
    /// <summary>
    /// Scores attack actions (both ability attacks and basic attacks).
    /// Integrates with existing TargetScorer and AbilityClassifier.
    /// </summary>
    public class AttackScorer
    {
        #region Main Scoring

        /// <summary>
        /// Score an attack candidate
        /// ★ v0.2.37: Consideration 기반 Geometric Mean 점수 추가
        /// </summary>
        public void Score(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate == null || situation == null)
                return;

            // ★ v0.2.37: Consideration 기반 점수 구축
            BuildConsiderations(candidate, situation, weights);

            // 기존 로직 유지 (호환성 + 보너스 계산용)
            float score = candidate.BaseScore;

            // 1. Target value (from existing TargetScorer integration)
            float targetScore = ScoreTarget(candidate.Target, situation);
            score += targetScore * 0.4f;

            // 2. Ability-specific scoring (for ability attacks)
            if (candidate.ActionType == CandidateType.AbilityAttack)
            {
                score += ScoreAbility(candidate, situation, weights);
            }
            else
            {
                // Basic attack scoring
                score += ScoreBasicAttack(candidate, situation);
            }

            // 3. Kill potential bonus (BonusScore로 이동)
            float killBonus = CalculateKillBonus(candidate, situation, weights);
            candidate.BonusScore += killBonus;  // ★ BonusScore에 추가

            // 4. Distance considerations
            score += ScoreDistance(candidate, situation);

            // 5. Flanking bonus (if applicable) → BonusScore
            if (situation.HasSneakAttack && IsTargetFlanked(candidate.Target, situation))
            {
                candidate.BonusScore += 20f;  // ★ BonusScore에 추가
            }

            // 6. Apply phase weight
            candidate.PhaseMultiplier = weights.AttackWeight;
            candidate.BaseScore = score;
        }

        #region ★ v0.2.37: Consideration Building

        /// <summary>
        /// 공격 행동에 대한 Consideration 구축
        /// </summary>
        private void BuildConsiderations(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            var cs = candidate.Considerations;
            cs.Clear();  // 이전 Consideration 제거

            // ═══════════════════════════════════════════════════════════════
            // 1. 타겟 존재 (Veto) - 타겟 없으면 공격 불가
            // ═══════════════════════════════════════════════════════════════
            cs.AddVeto("HasTarget", candidate.Target != null);

            if (candidate.Target == null)
                return;  // Veto - 추가 평가 불필요

            // ═══════════════════════════════════════════════════════════════
            // 2. 사거리 (Veto for ability attacks)
            // ═══════════════════════════════════════════════════════════════
            float distance = Vector3.Distance(situation.Unit.Position, candidate.Target.Position);
            float range = GetAbilityRange(candidate.Ability);

            if (candidate.ActionType == CandidateType.AbilityAttack && range > 0)
            {
                // 능력 공격: 사거리 밖이면 Veto
                cs.AddVeto("InRange", distance <= range * 1.1f);  // 10% 여유
            }
            else
            {
                // 기본 공격: 거리 점수화
                cs.Add("MeleeDistance", ScoreNormalizer.MeleeDistance(distance));
            }

            // ═══════════════════════════════════════════════════════════════
            // 3. 타겟 가치 (HP + 위협)
            // ═══════════════════════════════════════════════════════════════
            float targetHP = GetHPPercent(candidate.Target);
            float threat = AssessThreat(candidate.Target, situation);
            cs.Add("TargetValue", ScoreNormalizer.TargetValue(targetHP, threat));

            // ═══════════════════════════════════════════════════════════════
            // 4. 역할 적합성
            // ═══════════════════════════════════════════════════════════════
            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;
            cs.Add("RoleFit", ScoreNormalizer.RoleActionFit(role, candidate.ActionType));

            // ═══════════════════════════════════════════════════════════════
            // 5. 전투 페이즈 적합성
            // ═══════════════════════════════════════════════════════════════
            cs.Add("PhaseFit", ScoreNormalizer.AttackPhaseFit(situation.CombatPhase));

            // ═══════════════════════════════════════════════════════════════
            // 6. 리소스 가용성 (능력 공격일 경우)
            // ═══════════════════════════════════════════════════════════════
            if (candidate.ActionType == CandidateType.AbilityAttack && candidate.Classification != null)
            {
                // 리소스 여유도 (높은 레벨 주문 = 낮은 점수)
                int spellLevel = candidate.Classification.SpellLevel;
                float resourceScore = ScoreNormalizer.SpellLevelValue(spellLevel, weights.ResourceConservation);
                cs.Add("Resource", resourceScore);

                // 무료 능력 보너스
                if (candidate.Classification.IsFreeToUse)
                {
                    cs.Add("FreeAbility", 1.0f);
                }
            }
            else
            {
                // 기본 공격은 무료
                cs.Add("Resource", 1.0f);
            }

            // ═══════════════════════════════════════════════════════════════
            // 7. 거리 선호 (캐릭터 설정 기반)
            // ═══════════════════════════════════════════════════════════════
            switch (situation.RangePreference)
            {
                case RangePreference.Melee:
                    cs.Add("RangePref", ScoreNormalizer.MeleeDistance(distance));
                    break;
                case RangePreference.Ranged:
                    cs.Add("RangePref", ScoreNormalizer.RangedDistance(distance));
                    break;
                default:
                    // Mixed: 둘 중 나은 것
                    float meleeScore = ScoreNormalizer.MeleeDistance(distance);
                    float rangedScore = ScoreNormalizer.RangedDistance(distance);
                    cs.Add("RangePref", Math.Max(meleeScore, rangedScore));
                    break;
            }

            // ═══════════════════════════════════════════════════════════════
            // 8. 공격 가능 여부 (Hittable)
            // ═══════════════════════════════════════════════════════════════
            bool hittable = situation.HittableEnemies?.Contains(candidate.Target) ?? true;
            cs.Add("Hittable", hittable ? 1.0f : 0.4f);

            // ═══════════════════════════════════════════════════════════════
            // ★ v0.2.41: 돌격(Charge) 거리 패널티
            // 돌격은 최소 거리(10피트=3m) 요구 - 이미 근접하면 기본 공격이 더 나음
            // ═══════════════════════════════════════════════════════════════
            if (candidate.Ability != null && IsChargeAbility(candidate.Ability))
            {
                const float CHARGE_MIN_DISTANCE = 3.0f;  // ~10 feet
                const float CHARGE_OPTIMAL_DISTANCE = 10.0f;  // 돌격에 이상적인 거리

                if (distance < CHARGE_MIN_DISTANCE)
                {
                    // 이미 근접 - Veto (돌격 불가)
                    cs.AddVeto("ChargeDistance", false);
                    Main.Verbose($"[AttackScorer] Charge vetoed - too close (dist={distance:F1}m < {CHARGE_MIN_DISTANCE}m)");
                }
                else if (distance < CHARGE_OPTIMAL_DISTANCE)
                {
                    // 가깝지만 돌격 가능 - 패널티 부여 (기본 공격 선호)
                    float chargeScore = (distance - CHARGE_MIN_DISTANCE) / (CHARGE_OPTIMAL_DISTANCE - CHARGE_MIN_DISTANCE);
                    cs.Add("ChargeDistance", Mathf.Clamp(chargeScore, 0.3f, 1.0f));
                    Main.Verbose($"[AttackScorer] Charge penalty applied (dist={distance:F1}m, score={chargeScore:F2})");
                }
                else
                {
                    // 충분히 멀다 - 돌격 보너스
                    cs.Add("ChargeDistance", 1.0f);
                }
            }

            Main.Verbose($"[AttackScorer] {candidate.Ability?.Name ?? "BasicAttack"} -> {candidate.Target?.CharacterName}: {cs.ToDebugString()}");
        }

        #endregion

        #endregion

        #region Target Scoring

        /// <summary>
        /// Score the target based on various factors
        /// </summary>
        private float ScoreTarget(UnitEntityData target, Situation situation)
        {
            if (target == null)
                return 0f;

            float score = 0f;

            try
            {
                // HP-based priority (lower HP = higher priority)
                float targetHP = GetHPPercent(target);
                score += ResponseCurves.TargetHPPriority(targetHP) * 15f;

                // Threat assessment
                float threat = AssessThreat(target, situation);
                score += ResponseCurves.CCTargetValue(threat) * 10f;

                // Distance factor
                float dist = Vector3.Distance(situation.Unit.Position, target.Position);
                if (dist <= 2f)
                    score += 5f;  // Close target bonus
                else if (dist > 15f)
                    score -= 5f;  // Far target penalty

                // Hittable bonus
                if (situation.HittableEnemies?.Contains(target) == true)
                    score += 10f;
            }
            catch (Exception ex)
            {
                Main.Error($"[AttackScorer] ScoreTarget error: {ex.Message}");
            }

            return score;
        }

        #endregion

        #region Ability Scoring

        /// <summary>
        /// Score an ability attack
        /// </summary>
        private float ScoreAbility(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate.Ability == null)
                return 0f;

            float score = 0f;

            try
            {
                var ability = candidate.Ability;
                var classification = candidate.Classification;

                // Resource consideration
                if (classification != null)
                {
                    int spellLevel = classification.SpellLevel;
                    float resourcePenalty = ResponseCurves.SpellLevelPenalty(spellLevel, weights.ResourceConservation);
                    candidate.ResourcePenalty = resourcePenalty;

                    // Free abilities get bonus
                    if (classification.IsFreeToUse)
                        score += 10f;

                    // AoE bonus
                    if (classification.IsAoE && classification.AoERadius > 0)
                    {
                        int enemiesInAoE = CountEnemiesInAoE(candidate.Target?.Position ?? situation.Unit.Position,
                            classification.AoERadius, situation);
                        int alliesInAoE = CountAlliesInAoE(candidate.Target?.Position ?? situation.Unit.Position,
                            classification.AoERadius, situation);

                        score += ResponseCurves.AoETargetCountBonus(enemiesInAoE, alliesInAoE);
                    }
                }

                // Range suitability
                float range = GetAbilityRange(ability);
                float dist = candidate.Target != null
                    ? Vector3.Distance(situation.Unit.Position, candidate.Target.Position)
                    : 0f;

                if (range > 0 && dist <= range)
                    score += 5f;  // In range
                else if (range > 0 && dist > range)
                    score -= 15f;  // Out of range penalty

                // Weapon ability vs spell
                bool isWeapon = IsWeaponAbility(ability);
                float targetHP = candidate.Target != null ? GetHPPercent(candidate.Target) : 50f;

                // Low HP targets: weapons are efficient
                if (targetHP <= 30f && isWeapon)
                    score += 8f;

                // High HP targets: spells may be better
                if (targetHP > 70f && !isWeapon && classification?.IsFreeToUse != true)
                    score += 10f;
            }
            catch (Exception ex)
            {
                Main.Error($"[AttackScorer] ScoreAbility error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// Score a basic attack (UnitAttack)
        /// </summary>
        private float ScoreBasicAttack(ActionCandidate candidate, Situation situation)
        {
            float score = 0f;

            // Basic attacks are resource-free
            score += 5f;

            // Better for cleanup phase (no resource cost)
            if (situation.CombatPhase == CombatPhase.Cleanup)
                score += 10f;

            // Good against low HP targets
            float targetHP = candidate.Target != null ? GetHPPercent(candidate.Target) : 50f;
            if (targetHP <= 25f)
                score += 15f;

            // Melee range check
            float dist = candidate.Target != null
                ? Vector3.Distance(situation.Unit.Position, candidate.Target.Position)
                : 999f;

            if (dist <= 2f)
                score += 10f;  // In melee range
            else
                score -= 20f;  // Out of melee range - significant penalty

            return score;
        }

        #endregion

        #region Kill Bonus

        /// <summary>
        /// Calculate bonus for potential kills
        /// </summary>
        private float CalculateKillBonus(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate.Target == null)
                return 0f;

            try
            {
                float targetHP = GetHPPercent(candidate.Target);
                float targetCurrentHP = candidate.Target.Stats?.HitPoints?.ModifiedValue ?? 100f;

                // Estimate damage (rough approximation)
                float estimatedDamage = EstimateDamage(candidate, situation);
                float damageRatio = targetCurrentHP > 0 ? estimatedDamage / targetCurrentHP : 0f;

                float bonus = ResponseCurves.KillBonus(damageRatio);
                return bonus * weights.KillBonusWeight;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Rough damage estimation
        /// </summary>
        private float EstimateDamage(ActionCandidate candidate, Situation situation)
        {
            // This is a simplified estimation
            // In a full implementation, this would analyze the ability's damage components

            if (candidate.ActionType == CandidateType.BasicAttack)
            {
                // Basic attack: roughly weapon damage
                return 10f;  // Placeholder
            }

            if (candidate.Classification != null)
            {
                int level = candidate.Classification.SpellLevel;
                // Rough: spell damage scales with level
                return 5f + level * 8f;
            }

            return 10f;
        }

        #endregion

        #region Distance Scoring

        /// <summary>
        /// Score based on distance to target
        /// </summary>
        private float ScoreDistance(ActionCandidate candidate, Situation situation)
        {
            if (candidate.Target == null)
                return 0f;

            float dist = Vector3.Distance(situation.Unit.Position, candidate.Target.Position);

            // Based on range preference
            switch (situation.RangePreference)
            {
                case RangePreference.Melee:
                    return ResponseCurves.MeleeDistanceBonus(dist);

                case RangePreference.Ranged:
                    return ResponseCurves.RangedDistanceBonus(dist);

                case RangePreference.Mixed:
                default:
                    // Use whichever is better
                    return Math.Max(
                        ResponseCurves.MeleeDistanceBonus(dist),
                        ResponseCurves.RangedDistanceBonus(dist) * 0.8f
                    );
            }
        }

        #endregion

        #region Helper Methods

        private float GetHPPercent(UnitEntityData unit)
        {
            try
            {
                if (unit?.Stats?.HitPoints == null) return 100f;
                float current = unit.Stats.HitPoints.ModifiedValue;
                float max = unit.Stats.HitPoints.BaseValue;
                if (max <= 0) return 100f;
                return (current / max) * 100f;
            }
            catch { return 100f; }
        }

        private float AssessThreat(UnitEntityData target, Situation situation)
        {
            // Simple threat assessment (0.0 to 1.0)
            // Could be expanded with more factors
            try
            {
                float threat = 0.5f;

                // HP-based (lower HP = less threat, but killing is valuable)
                float hp = GetHPPercent(target);
                if (hp > 80f) threat += 0.2f;
                else if (hp < 30f) threat -= 0.1f;

                // Targeting allies increases threat
                if (situation.EnemiesTargetingAllies > 0)
                    threat += 0.15f;

                return Math.Max(0f, Math.Min(1f, threat));
            }
            catch
            {
                return 0.5f;
            }
        }

        private bool IsTargetFlanked(UnitEntityData target, Situation situation)
        {
            return situation.FlankedEnemies?.Contains(target) == true;
        }

        private bool IsWeaponAbility(AbilityData ability)
        {
            try
            {
                return ability?.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon;
            }
            catch
            {
                return false;
            }
        }

        private float GetAbilityRange(AbilityData ability)
        {
            try
            {
                // Simplified range calculation
                var range = ability?.Blueprint?.Range;
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch)
                    return 2f;
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon)
                    return 2f;
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal)
                    return 0f;
                return 30f;  // Default medium range
            }
            catch
            {
                return 30f;
            }
        }

        private int CountEnemiesInAoE(Vector3 center, float radius, Situation situation)
        {
            if (situation.Enemies == null)
                return 0;

            return situation.Enemies.Count(e =>
                e != null && Vector3.Distance(center, e.Position) <= radius);
        }

        private int CountAlliesInAoE(Vector3 center, float radius, Situation situation)
        {
            if (situation.Allies == null)
                return 0;

            int count = 0;

            // Count allies
            count += situation.Allies.Count(a =>
                a != null && Vector3.Distance(center, a.Position) <= radius);

            // Count self
            if (Vector3.Distance(center, situation.Unit.Position) <= radius)
                count++;

            return count;
        }

        /// <summary>
        /// ★ v0.2.41: 돌격(Charge) 능력인지 확인
        /// 돌격은 최소 거리 요구가 있어서 특수 처리 필요
        /// </summary>
        private bool IsChargeAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null)
                return false;

            try
            {
                // 이름 기반 감지 (다국어 지원)
                string name = ability.Name?.ToLower() ?? "";
                string bpName = ability.Blueprint.name?.ToLower() ?? "";

                // 영어: charge
                // 한국어: 돌격
                if (name.Contains("charge") || name.Contains("돌격") ||
                    bpName.Contains("charge"))
                {
                    return true;
                }

                // AbilityType이 CombatManeuver이고 Full Round인 경우도 Charge일 가능성
                // (추가 검증 필요 시 구현)

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
