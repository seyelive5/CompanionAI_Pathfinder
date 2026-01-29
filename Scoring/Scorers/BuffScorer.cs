// ★ v0.2.22: Unified Decision Engine - Buff/Heal Scorer
// ★ v0.2.36: Enhanced heal target selection with role-based priority
// ★ v0.2.37: Geometric Mean Scoring with Considerations
// ★ v0.2.39: 버프 전투 가치 분석 (CombatValue Consideration)
// ★ v0.2.40: 인챈트 주문 지원 및 IsAlreadyApplied 버그 수정
using System;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Scoring.Scorers
{
    /// <summary>
    /// Scores buff and heal actions.
    /// Handles both self-targeting and ally-targeting.
    /// </summary>
    public class BuffScorer
    {
        #region Main Scoring

        /// <summary>
        /// Score a buff or heal candidate
        /// </summary>
        public void Score(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate == null || situation == null)
                return;

            if (candidate.ActionType == CandidateType.Heal)
            {
                ScoreHeal(candidate, situation, weights);
            }
            else
            {
                ScoreBuff(candidate, situation, weights);
            }
        }

        #endregion

        #region Heal Scoring

        /// <summary>
        /// ★ v0.2.36: Enhanced heal scoring
        /// ★ v0.2.37: Geometric Mean Scoring with Considerations
        /// Uses TargetScorer's improved ally scoring + additional heal-specific factors
        /// </summary>
        private void ScoreHeal(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            float score = candidate.BaseScore;

            try
            {
                var target = candidate.Target ?? situation.Unit;
                float targetHP = GetHPPercent(target);
                bool isSelf = target == situation.Unit;

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildHealConsiderations(candidate, situation, target, targetHP, isSelf, weights);

                // ═══════════════════════════════════════════════════════════════
                // 1. ★ v0.2.36: Use enhanced TargetScorer for ally scoring
                // ═══════════════════════════════════════════════════════════════
                float targetScore = TargetScorer.ScoreAllyForHealing(target, situation);
                score += targetScore * 0.6f;

                // ═══════════════════════════════════════════════════════════════
                // 2. Urgency based on HP (기존 로직 유지)
                // ═══════════════════════════════════════════════════════════════
                float urgency = ResponseCurves.HealUrgency(targetHP);
                candidate.BonusScore = urgency * 25f;

                // ═══════════════════════════════════════════════════════════════
                // 3. ★ v0.2.36: HP 단계별 긴급도 보너스
                // ═══════════════════════════════════════════════════════════════
                if (targetHP < 25f)
                {
                    // Critical: 즉시 힐 필요
                    score += 50f;
                    if (isSelf && situation.CombatPhase == CombatPhase.Desperate)
                        score += 30f;  // 자신 힐 + 위기상황 = 최우선
                }
                else if (targetHP < 50f)
                {
                    // Low: 우선 힐
                    score += 25f;
                    if (isSelf)
                        score += 10f;
                }
                else if (targetHP < weights.EmergencyHealThreshold)
                {
                    // Moderate: 힐 고려
                    score += 10f;
                }

                // ═══════════════════════════════════════════════════════════════
                // 4. Role-based adjustment
                // ═══════════════════════════════════════════════════════════════
                if (situation.CharacterSettings?.Role == AIRole.Support)
                {
                    // Support prioritizes ally healing
                    if (!isSelf)
                        score += 15f;
                }
                else
                {
                    // Non-support prioritizes self-healing
                    if (isSelf)
                        score += 10f;
                }

                // ═══════════════════════════════════════════════════════════════
                // 5. ★ v0.2.36: 타겟 역할 기반 힐 우선순위
                // ═══════════════════════════════════════════════════════════════
                if (!isSelf)
                {
                    var targetRole = GetTargetRole(target);
                    switch (targetRole)
                    {
                        case AIRole.Tank:
                            score += 15f;  // 탱크 유지 중요
                            break;
                        case AIRole.Support:
                            score += 20f;  // 힐러가 죽으면 파티 붕괴
                            break;
                        case AIRole.DPS:
                            score += 10f;
                            break;
                    }

                    // 교전 중인 아군 우선 힐
                    int engagedEnemies = CountEngagedEnemies(target);
                    if (engagedEnemies > 0 && targetHP < 50f)
                        score += engagedEnemies * 8f;
                }

                // ═══════════════════════════════════════════════════════════════
                // 6. Resource consideration
                // ═══════════════════════════════════════════════════════════════
                if (candidate.Classification != null)
                {
                    int spellLevel = candidate.Classification.SpellLevel;
                    candidate.ResourcePenalty = ResponseCurves.SpellLevelPenalty(spellLevel, weights.ResourceConservation);

                    // Emergency heals = 리소스 아끼지 않음
                    if (targetHP < 25f)
                        candidate.ResourcePenalty *= 0.2f;
                    else if (targetHP < 50f)
                        candidate.ResourcePenalty *= 0.5f;
                }

                // ═══════════════════════════════════════════════════════════════
                // 7. Overhealing penalty (오버힐 방지)
                // ═══════════════════════════════════════════════════════════════
                float healEstimate = EstimateHealAmount(candidate);
                float missingHP = 100f - targetHP;
                if (healEstimate > missingHP * 1.5f && targetHP > 50f)
                {
                    // 오버힐이면서 HP가 높은 경우만 페널티
                    score -= 15f;
                }

                // Apply phase weight
                candidate.PhaseMultiplier = weights.HealWeight;
                candidate.BaseScore = score;

                Main.Verbose($"[BuffScorer] Heal {candidate.Ability?.Name} -> {target.CharacterName}: HP={targetHP:F0}%, score={score:F1}");
            }
            catch (Exception ex)
            {
                Main.Error($"[BuffScorer] ScoreHeal error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.36: 타겟 역할 추론
        /// </summary>
        private AIRole GetTargetRole(UnitEntityData unit)
        {
            try
            {
                string charId = unit?.UniqueId ?? "";
                if (!string.IsNullOrEmpty(charId) && ModSettings.Instance != null)
                {
                    var settings = ModSettings.Instance.GetOrCreateSettings(charId, unit.CharacterName);
                    if (settings != null)
                        return settings.Role;
                }

                // 스탯 기반 추론
                if (unit?.Stats == null)
                    return AIRole.DPS;

                int ac = unit.Stats.AC?.ModifiedValue ?? 10;
                int con = unit.Stats.Constitution?.ModifiedValue ?? 10;
                if (ac >= 25 && con >= 14)
                    return AIRole.Tank;

                // Spellbook 있으면 Support 가능성
                var spellbooks = unit.Descriptor?.Spellbooks;
                if (spellbooks != null)
                {
                    foreach (var sb in spellbooks)
                    {
                        if (sb.CasterLevel > 0)
                            return AIRole.Support;
                    }
                }

                return AIRole.DPS;
            }
            catch { return AIRole.DPS; }
        }

        /// <summary>
        /// ★ v0.2.36: 유닛이 교전 중인 적 수
        /// </summary>
        private int CountEngagedEnemies(UnitEntityData unit)
        {
            try
            {
                var engaged = unit?.CombatState?.EngagedUnits;
                return engaged?.Count ?? 0;
            }
            catch { return 0; }
        }

        #endregion

        #region Buff Scoring

        /// <summary>
        /// Score a buff action
        /// ★ v0.2.37: Geometric Mean Scoring with Considerations
        /// </summary>
        private void ScoreBuff(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            float score = candidate.BaseScore;

            try
            {
                var target = candidate.Target ?? situation.Unit;
                bool isSelf = target == situation.Unit;

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildBuffConsiderations(candidate, situation, target, isSelf, weights);

                // 1. Already buffed check (diminishing returns)
                int currentBuffCount = CountActiveBuffs(target);
                float stackValue = ResponseCurves.BuffStackValue(currentBuffCount);
                score *= stackValue;

                // 2. Phase-based buff timing
                switch (situation.CombatPhase)
                {
                    case CombatPhase.Opening:
                        score += 20f;  // Buffs are great at combat start
                        break;
                    case CombatPhase.Midgame:
                        // Normal value
                        break;
                    case CombatPhase.Cleanup:
                        score -= 15f;  // Don't waste buffs on cleanup
                        break;
                    case CombatPhase.Desperate:
                        // Only defensive buffs are valuable
                        if (IsDefensiveBuff(candidate))
                            score += 10f;
                        else
                            score -= 20f;
                        break;
                }

                // 3. Timing classification from AbilityClassifier
                if (candidate.Classification != null)
                {
                    var timing = candidate.Classification.Timing;

                    switch (timing)
                    {
                        case AbilityTiming.PermanentBuff:
                            // Permanent buffs are always good if not applied
                            if (!IsBuffAlreadyApplied(candidate.Ability, target))
                                score += 25f;
                            else
                                score -= 100f;  // Already applied
                            break;

                        case AbilityTiming.PreCombatBuff:
                            if (situation.CombatPhase == CombatPhase.Opening)
                                score += 15f;
                            break;
                    }
                }

                // 4. Target selection for ally buffs
                if (!isSelf)
                {
                    // Prioritize buffing allies who will benefit most
                    float targetHP = GetHPPercent(target);

                    // Don't buff dying allies (heal them instead)
                    if (targetHP < 30f)
                        score -= 15f;

                    // Buff healthy allies who can make use of it
                    if (targetHP > 70f)
                        score += 5f;
                }

                // 5. First action bonus (buff before attack)
                if (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn)
                    score += 10f;

                // 6. Resource consideration
                if (candidate.Classification != null)
                {
                    int spellLevel = candidate.Classification.SpellLevel;
                    candidate.ResourcePenalty = ResponseCurves.SpellLevelPenalty(spellLevel, weights.ResourceConservation);
                }

                // Apply phase weight
                candidate.PhaseMultiplier = weights.BuffWeight;
                candidate.BaseScore = score;
            }
            catch (Exception ex)
            {
                Main.Error($"[BuffScorer] ScoreBuff error: {ex.Message}");
            }
        }

        #endregion

        #region ★ v0.2.37: Consideration Building

        /// <summary>
        /// 힐링 행동에 대한 Consideration 구축
        /// </summary>
        private void BuildHealConsiderations(ActionCandidate candidate, Situation situation,
            UnitEntityData target, float targetHP, bool isSelf, PhaseRoleWeights weights)
        {
            var cs = candidate.Considerations;
            cs.Clear();

            // ═══════════════════════════════════════════════════════════════
            // 1. 힐 필요도 (가장 중요)
            // ═══════════════════════════════════════════════════════════════
            cs.Add("HealNeed", ScoreNormalizer.HealNeed(targetHP));

            // ═══════════════════════════════════════════════════════════════
            // 2. 전투 페이즈 적합성
            // ═══════════════════════════════════════════════════════════════
            cs.Add("PhaseFit", ScoreNormalizer.HealPhaseFit(situation.CombatPhase));

            // ═══════════════════════════════════════════════════════════════
            // 3. 역할 적합성
            // ═══════════════════════════════════════════════════════════════
            var casterRole = situation.CharacterSettings?.Role ?? AIRole.DPS;
            cs.Add("RoleFit", ScoreNormalizer.RoleActionFit(casterRole, CandidateType.Heal));

            // ═══════════════════════════════════════════════════════════════
            // 4. 타겟 역할 중요도
            // ═══════════════════════════════════════════════════════════════
            if (!isSelf)
            {
                var targetRole = GetTargetRole(target);
                float roleImportance;
                switch (targetRole)
                {
                    case AIRole.Support:
                        roleImportance = 1.0f;  // 힐러 생존 최우선
                        break;
                    case AIRole.Tank:
                        roleImportance = 0.9f;  // 탱커 유지 중요
                        break;
                    case AIRole.DPS:
                        roleImportance = 0.7f;  // 딜러도 중요
                        break;
                    default:
                        roleImportance = 0.6f;
                        break;
                }
                cs.Add("TargetImportance", roleImportance);
            }
            else
            {
                // 자힐은 기본 중요도
                cs.Add("TargetImportance", 0.8f);
            }

            // ═══════════════════════════════════════════════════════════════
            // 5. 리소스 가용성
            // ═══════════════════════════════════════════════════════════════
            if (candidate.Classification != null)
            {
                int spellLevel = candidate.Classification.SpellLevel;
                // 긴급 힐은 리소스 아끼지 않음
                float conservationModifier = targetHP < 25f ? 0.2f : (targetHP < 50f ? 0.5f : 1.0f);
                float resourceScore = ScoreNormalizer.SpellLevelValue(
                    spellLevel, weights.ResourceConservation * conservationModifier);
                cs.Add("Resource", resourceScore);
            }
            else
            {
                cs.Add("Resource", 0.8f);
            }

            // ═══════════════════════════════════════════════════════════════
            // 6. 오버힐 방지
            // ═══════════════════════════════════════════════════════════════
            float healEstimate = EstimateHealAmount(candidate);
            float missingHP = 100f - targetHP;
            float overhealFactor = 1f;
            if (healEstimate > missingHP * 1.5f && targetHP > 50f)
            {
                // 오버힐이 예상되고 HP도 높으면 낮은 점수
                overhealFactor = 0.5f;
            }
            cs.Add("NoOverheal", overhealFactor);

            Main.Verbose($"[BuffScorer] Heal {candidate.Ability?.Name} -> {target.CharacterName}: {cs.ToDebugString()}");
        }

        /// <summary>
        /// 버프 행동에 대한 Consideration 구축
        /// ★ v0.2.39: CombatValue consideration 추가
        /// </summary>
        private void BuildBuffConsiderations(ActionCandidate candidate, Situation situation,
            UnitEntityData target, bool isSelf, PhaseRoleWeights weights)
        {
            var cs = candidate.Considerations;
            cs.Clear();

            // ═══════════════════════════════════════════════════════════════
            // 1. 이미 버프 적용됨 체크 (Veto 가능)
            // ═══════════════════════════════════════════════════════════════
            bool alreadyBuffed = IsBuffAlreadyApplied(candidate.Ability, target);
            if (alreadyBuffed && candidate.Classification?.Timing == AbilityTiming.PermanentBuff)
            {
                cs.AddVeto("NotAlreadyBuffed", false);
                return;
            }
            cs.Add("NotAlreadyBuffed", alreadyBuffed ? 0.1f : 1.0f);

            // ═══════════════════════════════════════════════════════════════
            // 2. ★ v0.2.39: 버프 전투 가치 (핵심 Consideration)
            // Guidance(스킬+1) vs Shield of Faith(AC+2) 차별화
            // ═══════════════════════════════════════════════════════════════
            float combatValue = GetBuffCombatValue(candidate);
            cs.Add("CombatValue", ScoreNormalizer.BuffCombatValue(combatValue));

            // ═══════════════════════════════════════════════════════════════
            // 3. 전투 페이즈 적합성
            // ★ v0.2.39: 전투 가치가 낮은 버프는 Opening 보너스 감소
            // ═══════════════════════════════════════════════════════════════
            float phaseFit = ScoreNormalizer.BuffPhaseFit(situation.CombatPhase);
            if (combatValue < 0.3f && situation.CombatPhase == CombatPhase.Opening)
            {
                // 유틸리티 버프는 Opening에서도 낮은 적합성
                phaseFit *= 0.3f;
            }
            cs.Add("PhaseFit", phaseFit);

            // ═══════════════════════════════════════════════════════════════
            // 4. 역할 적합성
            // ═══════════════════════════════════════════════════════════════
            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;
            cs.Add("RoleFit", ScoreNormalizer.RoleActionFit(role, CandidateType.Buff));

            // ═══════════════════════════════════════════════════════════════
            // 5. 버프 스택 가치 (이미 많은 버프 → 낮은 가치)
            // ★ v0.2.37 fix: 최소 0.25 유지하여 Veto 방지
            // ═══════════════════════════════════════════════════════════════
            int currentBuffs = CountActiveBuffs(target);
            float stackValue = ResponseCurves.BuffStackValue(currentBuffs);
            // 0.3~1.5를 0.25~1.0으로 정규화 (최소 0.25 보장 → Veto 방지)
            float normalizedStackValue = 0.25f + (stackValue - 0.3f) / 1.2f * 0.75f;
            cs.Add("BuffStackValue", Mathf.Clamp(normalizedStackValue, 0.25f, 1.0f));

            // ═══════════════════════════════════════════════════════════════
            // 6. 타겟 HP (죽어가는 아군에게 버프 낭비 방지)
            // ═══════════════════════════════════════════════════════════════
            float targetHP = GetHPPercent(target);
            float hpFactor = targetHP > 70f ? 1f : (targetHP > 30f ? 0.6f : 0.2f);
            cs.Add("TargetHP", hpFactor);

            // ═══════════════════════════════════════════════════════════════
            // 7. 리소스 가용성
            // ═══════════════════════════════════════════════════════════════
            if (candidate.Classification != null)
            {
                int spellLevel = candidate.Classification.SpellLevel;
                cs.Add("Resource", ScoreNormalizer.SpellLevelValue(spellLevel, weights.ResourceConservation));
            }
            else
            {
                cs.Add("Resource", 0.8f);
            }

            // ═══════════════════════════════════════════════════════════════
            // 8. 첫 행동 보너스 (버프 후 공격이 효율적)
            // ═══════════════════════════════════════════════════════════════
            float firstActionBonus = (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn) ? 1f : 0.7f;
            cs.Add("FirstAction", firstActionBonus);

            Main.Verbose($"[BuffScorer] Buff {candidate.Ability?.Name} -> {target.CharacterName}: Combat={combatValue:F2}, {cs.ToDebugString()}");
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

        private int CountActiveBuffs(UnitEntityData unit)
        {
            try
            {
                return unit?.Buffs?.RawFacts?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private bool IsDefensiveBuff(ActionCandidate candidate)
        {
            // Check if the buff is defensive in nature
            // This could be expanded with more detailed classification
            if (candidate.Ability?.Name == null)
                return false;

            string name = candidate.Ability.Name.ToLower();
            return name.Contains("shield") ||
                   name.Contains("armor") ||
                   name.Contains("protect") ||
                   name.Contains("resist") ||
                   name.Contains("defense") ||
                   name.Contains("방어") ||
                   name.Contains("보호") ||
                   name.Contains("저항");
        }

        private bool IsBuffAlreadyApplied(AbilityData ability, UnitEntityData target)
        {
            try
            {
                return AbilityClassifier.IsBuffAlreadyApplied(ability, target);
            }
            catch
            {
                return false;
            }
        }

        private float EstimateHealAmount(ActionCandidate candidate)
        {
            // Rough estimation of heal amount
            if (candidate.Classification == null)
                return 20f;

            int level = candidate.Classification.SpellLevel;
            // Heal scales with spell level
            return 10f + level * 8f;
        }

        /// <summary>
        /// ★ v0.2.39: 버프의 전투 가치 조회
        /// ★ v0.2.40: 인챈트 주문 지원 추가
        /// BuffEffectAnalyzer를 사용하여 버프의 실제 전투 효과 분석
        /// </summary>
        private float GetBuffCombatValue(ActionCandidate candidate)
        {
            try
            {
                // ★ v0.2.40: AbilityClassifier에서 버프/인챈트 정보 추출
                AbilityBuffEffects buffEffects = null;
                if (candidate.Ability != null)
                {
                    buffEffects = AbilityClassifier.GetBuffEffects(candidate.Ability);

                    // ★ v0.2.40: 인챈트 주문은 높은 전투 가치 (무기 강화)
                    if (buffEffects != null && buffEffects.IsEnchantment)
                    {
                        // 인챈트 보너스에 따른 가치: +1 = 0.9, +2 = 0.95, +3+ = 1.0
                        float enchantValue = 0.85f + buffEffects.EnchantmentBonus * 0.05f;
                        enchantValue = Mathf.Clamp(enchantValue, 0.85f, 1.0f);

                        Main.Verbose($"[BuffScorer] {candidate.Ability?.Name} -> Enchantment +{buffEffects.EnchantmentBonus}, CombatValue={enchantValue:F2}");
                        return enchantValue;
                    }
                }

                // 1. Classification에서 AppliedBuff 확인
                var buff = candidate.Classification?.AppliedBuff;

                // 2. 없으면 buffEffects에서 추출
                if (buff == null && buffEffects != null)
                {
                    buff = buffEffects.PrimaryBuff;
                }

                // 3. 버프가 없으면 중간값 반환
                if (buff == null)
                {
                    Main.Verbose($"[BuffScorer] No buff found for {candidate.Ability?.Name}, using default combat value");
                    return 0.3f;
                }

                // 4. BuffEffectAnalyzer로 전투 가치 분석
                float combatValue = BuffEffectAnalyzer.GetCombatValue(buff);

                Main.Verbose($"[BuffScorer] {candidate.Ability?.Name} -> Buff={buff.Name ?? "?"}, CombatValue={combatValue:F2}");

                return combatValue;
            }
            catch (Exception ex)
            {
                Main.Error($"[BuffScorer] GetBuffCombatValue error: {ex.Message}");
                return 0.3f;  // 에러 시 중간값
            }
        }

        #endregion
    }
}
