// ★ v0.2.22: Unified Decision Engine - Attack Scorer
// ★ v0.2.37: Geometric Mean Scoring with Considerations
// ★ v0.2.41: Charge ability distance penalty
// ★ v0.2.52: TargetAnalyzer 통합 - 중복 분석 코드 제거
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Items;
using Kingmaker.RuleSystem;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;
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

            // ★ v0.2.55: 통합 타겟 스코어링 사용 (ScoreEnemyUnified)
            // Tank의 "아군 교전" 보너스 등 모든 역할별 로직 포함
            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;
            float targetScore = TargetScorer.ScoreEnemyUnified(situation.Unit, candidate.Target, role, situation);
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

            // ★ v0.2.55: Tank "아군 교전" 보너스는 TargetScorer.ScoreEnemyUnified()에서 처리됨

            // 6. Apply phase weight
            candidate.PhaseMultiplier = weights.AttackWeight;
            candidate.BaseScore = score;
        }

        #region ★ v0.2.37: Consideration Building

        /// <summary>
        /// 공격 행동에 대한 Consideration 구축
        /// ★ v0.2.52: TargetAnalyzer 통합
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

            // ★ v0.2.52: 통합 분석 획득 (캐시됨)
            var analysis = TargetAnalyzer.Analyze(candidate.Target, situation.Unit);

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
            // 3. 타겟 가치 (HP + 위협) - ★ v0.2.52: TargetAnalyzer 사용
            // ═══════════════════════════════════════════════════════════════
            float targetHP = analysis?.HPPercent ?? 100f;
            float threat = analysis?.ThreatLevel ?? 0.5f;
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

        // ★ v0.2.55: ScoreTarget() 삭제됨 - TargetScorer.ScoreEnemyUnified() 사용

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
                // ★ v0.2.52: TargetAnalyzer 사용
                var targetAnalysis = candidate.Target != null ? TargetAnalyzer.Analyze(candidate.Target, situation.Unit) : null;
                float targetHP = targetAnalysis?.HPPercent ?? 50f;

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
        /// ★ v0.2.52: TargetAnalyzer 통합
        /// </summary>
        private float ScoreBasicAttack(ActionCandidate candidate, Situation situation)
        {
            float score = 0f;

            // Basic attacks are resource-free
            score += 5f;

            // Better for cleanup phase (no resource cost)
            if (situation.CombatPhase == CombatPhase.Cleanup)
                score += 10f;

            // Good against low HP targets - ★ v0.2.52: TargetAnalyzer 사용
            var analysis = candidate.Target != null ? TargetAnalyzer.Analyze(candidate.Target, situation.Unit) : null;
            float targetHP = analysis?.HPPercent ?? 50f;
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
        /// ★ v0.2.52: TargetAnalyzer 통합
        /// ★ v0.2.57: HP% 체크 - 건강한 적에게 킬보너스 적용 금지
        /// </summary>
        private float CalculateKillBonus(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate.Target == null)
                return 0f;

            try
            {
                // ★ v0.2.52: TargetAnalyzer 사용
                var analysis = TargetAnalyzer.Analyze(candidate.Target, situation.Unit);
                float targetHPPercent = analysis?.HPPercent ?? 100f;
                float targetCurrentHP = candidate.Target.Stats?.HitPoints?.ModifiedValue ?? 100f;

                // ★ v0.2.57: HP% 체크 - 건강한 적은 킬 대상이 아님!
                // HP 70% 이상이면 킬 보너스 없음 (버프로 150% 등이어도 마찬가지)
                if (targetHPPercent >= 70f)
                {
                    Main.Verbose($"[AttackScorer] No kill bonus for {candidate.Target?.CharacterName}: HP={targetHPPercent:F0}% >= 70%");
                    return 0f;
                }

                // HP% 50-70%: 킬 보너스 감소 (50% 스케일)
                float hpScaleFactor = 1f;
                if (targetHPPercent >= 50f)
                {
                    hpScaleFactor = 0.5f;
                }
                // HP% 25-50%: 보통 킬 보너스
                else if (targetHPPercent >= 25f)
                {
                    hpScaleFactor = 1f;
                }
                // HP% < 25%: 킬 보너스 증가 (마무리 우선)
                else
                {
                    hpScaleFactor = 1.5f;
                }

                // Estimate damage (rough approximation)
                float estimatedDamage = EstimateDamage(candidate, situation);
                float damageRatio = targetCurrentHP > 0 ? estimatedDamage / targetCurrentHP : 0f;

                float bonus = ResponseCurves.KillBonus(damageRatio);
                float finalBonus = bonus * weights.KillBonusWeight * hpScaleFactor;

                if (finalBonus > 0)
                {
                    Main.Verbose($"[AttackScorer] Kill bonus for {candidate.Target?.CharacterName}: HP={targetHPPercent:F0}%, dmgRatio={damageRatio:F2}, scale={hpScaleFactor}, bonus={finalBonus:F1}");
                }

                return finalBonus;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Damage estimation using actual game APIs
        /// ★ v0.2.59: 실제 무기/주문 데미지 API 사용
        /// </summary>
        private float EstimateDamage(ActionCandidate candidate, Situation situation)
        {
            if (candidate.ActionType == CandidateType.BasicAttack)
            {
                return EstimateWeaponDamage(situation.Unit);
            }

            var classification = candidate.Classification;
            if (classification == null)
                return 0f;

            // 비공격 능력은 데미지 0
            if (classification.IsSupportive)
            {
                Main.Verbose($"[AttackScorer] EstimateDamage: {candidate.Ability?.Name} is supportive, dmg=0");
                return 0f;
            }

            // 디버프/CC는 간접 데미지로 낮은 값 반환
            if (classification.Timing == AbilityTiming.CrowdControl ||
                classification.Timing == AbilityTiming.Debuff)
            {
                return 2f;
            }

            // 공격 능력: 실제 주문 데미지 추정
            return EstimateSpellDamage(candidate.Ability, situation.Unit);
        }

        /// <summary>
        /// ★ v0.2.59: 실제 무기 데미지 계산
        /// </summary>
        private float EstimateWeaponDamage(UnitEntityData unit)
        {
            try
            {
                // 1. 주무기 가져오기
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null)
                {
                    weapon = unit?.GetFirstWeapon();
                }

                if (weapon == null)
                    return 10f;  // 폴백

                // 2. 무기 블루프린트에서 기본 데미지 가져오기
                var blueprint = weapon.Blueprint;
                if (blueprint == null)
                    return 10f;

                // 3. 주사위 공식에서 평균 데미지 계산
                var baseDamage = blueprint.BaseDamage;
                int rolls = baseDamage.Rolls;
                int diceSides = (int)baseDamage.Dice;
                float avgDice = rolls * (diceSides + 1) / 2f;

                // 4. 능력치 보너스 (STR for melee, DEX for ranged with Weapon Finesse)
                float statBonus = 0f;
                bool isRanged = weapon.Blueprint.IsRanged;

                if (isRanged)
                {
                    // 원거리: 일반적으로 STR 보너스 없음 (Composite Bow 제외)
                    // 간단히 0으로 처리
                    statBonus = 0f;
                }
                else
                {
                    // 근접: STR 보너스
                    var strBonus = unit.Stats?.Strength?.Bonus ?? 0;
                    statBonus = strBonus;

                    // 양손 무기면 1.5배
                    if (unit.Body?.SecondaryHand?.MaybeWeapon == null &&
                        !weapon.Blueprint.IsLight)
                    {
                        statBonus *= 1.5f;
                    }
                }

                // 5. 인챈트 보너스
                int enhancement = weapon.EnchantmentValue;

                float totalDamage = avgDice + statBonus + enhancement;
                Main.Verbose($"[AttackScorer] WeaponDamage {unit.CharacterName}: {rolls}d{diceSides}+{statBonus:F0}+{enhancement} = {totalDamage:F1}");

                return totalDamage;
            }
            catch (Exception ex)
            {
                Main.Error($"[AttackScorer] EstimateWeaponDamage error: {ex.Message}");
                return 10f;  // 폴백
            }
        }

        /// <summary>
        /// ★ v0.2.59: 실제 주문 데미지 계산
        /// </summary>
        private float EstimateSpellDamage(AbilityData ability, UnitEntityData caster)
        {
            try
            {
                if (ability?.Blueprint == null)
                    return 0f;

                // 1. 시전자 레벨 가져오기
                int casterLevel = caster?.Progression?.CharacterLevel ?? 1;
                if (ability.Spellbook != null)
                {
                    casterLevel = ability.Spellbook.CasterLevel;
                }

                // 2. 데미지 액션 찾기
                var runActions = ability.Blueprint.GetComponents<AbilityEffectRunAction>();
                if (runActions == null)
                    return GetFallbackSpellDamage(ability);

                float totalDamage = 0f;

                foreach (var runAction in runActions)
                {
                    if (runAction?.Actions?.Actions == null) continue;

                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is ContextActionDealDamage damageAction)
                        {
                            totalDamage += CalculateContextDamage(damageAction, casterLevel);
                        }
                    }
                }

                if (totalDamage > 0f)
                {
                    Main.Verbose($"[AttackScorer] SpellDamage {ability.Name}: CL={casterLevel}, dmg={totalDamage:F1}");
                    return totalDamage;
                }

                // 데미지 액션 없으면 폴백
                return GetFallbackSpellDamage(ability);
            }
            catch (Exception ex)
            {
                Main.Error($"[AttackScorer] EstimateSpellDamage error: {ex.Message}");
                return GetFallbackSpellDamage(ability);
            }
        }

        /// <summary>
        /// ContextActionDealDamage에서 평균 데미지 계산
        /// </summary>
        private float CalculateContextDamage(ContextActionDealDamage damageAction, int casterLevel)
        {
            try
            {
                var diceValue = damageAction.Value;
                if (diceValue == null)
                    return 0f;

                // 주사위 타입 (D6, D8 등)
                int diceSides = (int)diceValue.DiceType;
                if (diceSides <= 0)
                    return 0f;

                // 주사위 개수 (고정값 또는 시전자 레벨 기반)
                int diceCount = 1;
                var countValue = diceValue.DiceCountValue;
                if (countValue != null)
                {
                    // ValueType에 따라 다르게 처리
                    switch (countValue.ValueType)
                    {
                        case Kingmaker.UnitLogic.Mechanics.ContextValueType.Rank:
                            // 능력 랭크 = 시전자 레벨 (일반적으로)
                            diceCount = casterLevel;
                            break;
                        case Kingmaker.UnitLogic.Mechanics.ContextValueType.Simple:
                            diceCount = countValue.Value;
                            break;
                        default:
                            diceCount = Math.Max(1, countValue.Value);
                            break;
                    }
                }

                // 보너스
                int bonus = 0;
                var bonusValue = diceValue.BonusValue;
                if (bonusValue != null)
                {
                    bonus = bonusValue.Value;
                }

                // 평균 데미지 계산
                float avgDice = diceCount * (diceSides + 1) / 2f;
                float total = avgDice + bonus;

                // HalfIfSaved 고려 (평균적으로 0.75배)
                if (damageAction.HalfIfSaved)
                {
                    total *= 0.75f;
                }

                return total;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 폴백: 주문 레벨 기반 추정
        /// </summary>
        private float GetFallbackSpellDamage(AbilityData ability)
        {
            int spellLevel = ability?.SpellLevel ?? 1;
            return 5f + spellLevel * 6f;  // 대략적인 추정
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

        // ★ v0.2.52: GetHPPercent(), AssessThreat() 삭제됨 - TargetAnalyzer 사용
        // ★ v0.2.55: CountAlliesEngagedByEnemy() 삭제됨 - TargetScorer.ScoreEnemyUnified()에서 처리

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

        /// <summary>
        /// ★ v0.2.59: 실제 게임 API로 능력 사거리 계산
        /// </summary>
        private float GetAbilityRange(AbilityData ability)
        {
            try
            {
                if (ability == null)
                    return 30f;

                // 실제 API 사용: GetApproachDistance는 미터 단위 반환
                // 캐스터/타겟 체격, Reach 메타매직, 무기 사거리 등 모두 포함
                float rangeMeters = ability.GetApproachDistance(null);

                // Personal 능력은 0 반환
                if (ability.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal)
                    return 0f;

                // 유효한 값이면 반환
                if (rangeMeters > 0f && !float.IsPositiveInfinity(rangeMeters))
                    return rangeMeters;

                // 폴백: 기존 로직
                var range = ability.Blueprint?.Range;
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch)
                    return 2f;
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon)
                    return 2f;
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Close)
                    return 9.1f;  // 30 feet
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Medium)
                    return 12.2f;  // 40 feet
                if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Long)
                    return 15.2f;  // 50 feet

                return 30f;  // Default
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
        /// ★ v0.2.62: AbilityClassifier.IsChargeAbility() 사용 (문자열 검색 제거)
        /// </summary>
        private bool IsChargeAbility(AbilityData ability)
        {
            return AbilityClassifier.IsChargeAbility(ability);
        }

        #endregion
    }
}
