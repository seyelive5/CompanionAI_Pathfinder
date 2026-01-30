// ★ v0.2.22: Unified Decision Engine - Buff/Heal Scorer
// ★ v0.2.36: Enhanced heal target selection with role-based priority
// ★ v0.2.37: Geometric Mean Scoring with Considerations
// ★ v0.2.39: 버프 전투 가치 분석 (CombatValue Consideration)
// ★ v0.2.40: 인챈트 주문 지원 및 IsAlreadyApplied 버그 수정
// ★ v0.2.52: TargetAnalyzer 통합 - 중복 분석 코드 제거
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

                // ★ v0.2.52: TargetAnalyzer 통합 사용
                var analysis = TargetAnalyzer.Analyze(target, situation.Unit);
                float targetHP = analysis?.HPPercent ?? 100f;
                bool isSelf = target == situation.Unit;

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildHealConsiderations(candidate, situation, target, analysis, isSelf, weights);

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
                // 5. ★ v0.2.52: 타겟 역할 기반 힐 우선순위 (TargetAnalyzer 사용)
                // ═══════════════════════════════════════════════════════════════
                if (!isSelf && analysis != null)
                {
                    // TargetRole → AIRole 변환하여 점수 적용
                    switch (analysis.EstimatedRole)
                    {
                        case TargetRole.Tank:
                            score += 15f;  // 탱크 유지 중요
                            break;
                        case TargetRole.Healer:
                            score += 20f;  // 힐러가 죽으면 파티 붕괴
                            break;
                        case TargetRole.Caster:
                            score += 12f;  // 캐스터 보호
                            break;
                        case TargetRole.Melee:
                        case TargetRole.Ranged:
                            score += 10f;  // DPS
                            break;
                    }

                    // ★ v0.2.52: 교전 중인 아군 우선 힐 (캐시된 값 사용)
                    int engagedEnemies = analysis.EngagedEnemyCount;
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

        // ★ v0.2.52: GetTargetRole, CountEngagedEnemies 삭제됨
        // → TargetAnalyzer.Analyze().EstimatedRole, .EngagedEnemyCount 사용

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

                // ★ v0.2.52: TargetAnalyzer 통합 사용
                var analysis = TargetAnalyzer.Analyze(target, situation.Unit);

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildBuffConsiderations(candidate, situation, target, analysis, isSelf, weights);

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

                // 4. ★ v0.2.61: 버프 타입 + 타겟 역할 매칭 (스마트 타겟팅)
                if (!isSelf && analysis != null)
                {
                    float targetHP = analysis.HPPercent;

                    // Don't buff dying allies (heal them instead)
                    if (targetHP < 30f)
                        score -= 15f;

                    // ★ v0.2.61: 방어 버프 → 탱크/교전중 아군 우선
                    if (IsDefensiveBuff(candidate))
                    {
                        // 탱크 역할에게 방어 버프 보너스
                        if (analysis.EstimatedRole == TargetRole.Tank)
                            score += 25f;

                        // 교전 중인 아군에게 방어 버프 보너스
                        if (analysis.EngagedEnemyCount > 0)
                            score += 15f + analysis.EngagedEnemyCount * 5f;

                        // HP 낮은 아군에게 방어 버프 보너스 (생존 도움)
                        if (targetHP < 50f && targetHP > 30f)
                            score += 10f;

                        Main.Verbose($"[BuffScorer] Defensive buff {candidate.Ability?.Name} -> {target.CharacterName}: " +
                            $"Role={analysis.EstimatedRole}, Engaged={analysis.EngagedEnemyCount}");
                    }
                    // ★ v0.2.61: 공격 버프 → DPS/딜러 우선
                    else if (IsOffensiveBuff(candidate))
                    {
                        // 딜러 역할에게 공격 버프 보너스
                        if (analysis.EstimatedRole == TargetRole.Melee ||
                            analysis.EstimatedRole == TargetRole.Ranged)
                            score += 20f;

                        // 캐스터에게도 공격 버프 (스펠 데미지 증가 가능)
                        if (analysis.EstimatedRole == TargetRole.Caster)
                            score += 15f;

                        // HP 높은 아군에게 공격 버프 (생존자가 딜을 넣어야 함)
                        if (targetHP > 70f)
                            score += 10f;

                        Main.Verbose($"[BuffScorer] Offensive buff {candidate.Ability?.Name} -> {target.CharacterName}: " +
                            $"Role={analysis.EstimatedRole}, HP={targetHP:F0}%");
                    }
                    else
                    {
                        // 기타 버프: HP 높은 아군 우선 (활용 가능)
                        if (targetHP > 70f)
                            score += 5f;
                    }
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
        /// ★ v0.2.52: TargetAnalysis 파라미터 사용
        /// </summary>
        private void BuildHealConsiderations(ActionCandidate candidate, Situation situation,
            UnitEntityData target, TargetAnalysis analysis, bool isSelf, PhaseRoleWeights weights)
        {
            var cs = candidate.Considerations;
            cs.Clear();

            float targetHP = analysis?.HPPercent ?? 100f;

            // ═══════════════════════════════════════════════════════════════
            // 1. ★ v0.2.52: 힐 필요도 (TargetNormalizer 사용)
            // ═══════════════════════════════════════════════════════════════
            cs.Add("HealNeed", TargetNormalizer.HPUrgency(targetHP));

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
            // 4. ★ v0.2.52: 타겟 역할 중요도 (TargetNormalizer 사용)
            // ═══════════════════════════════════════════════════════════════
            if (!isSelf && analysis != null)
            {
                // TargetNormalizer.AllyRoleValue 사용
                float roleImportance = TargetNormalizer.AllyRoleValue(analysis.EstimatedRole);
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
        /// ★ v0.2.52: TargetAnalysis 파라미터 사용
        /// </summary>
        private void BuildBuffConsiderations(ActionCandidate candidate, Situation situation,
            UnitEntityData target, TargetAnalysis analysis, bool isSelf, PhaseRoleWeights weights)
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
            // 6. ★ v0.2.52: 타겟 HP (TargetAnalyzer 사용)
            // ═══════════════════════════════════════════════════════════════
            float targetHP = analysis?.HPPercent ?? 100f;
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

        // ★ v0.2.52: GetHPPercent 삭제됨 → TargetAnalyzer.Analyze().HPPercent 사용

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

        /// <summary>
        /// ★ v0.2.61: 방어 버프인지 확인 (타입 기반 - BuffEffectAnalyzer 사용)
        /// AC 증가, DR, 저항, 회피, 임시 HP 등
        /// </summary>
        private bool IsDefensiveBuff(ActionCandidate candidate)
        {
            try
            {
                // 1. BuffEffectAnalyzer로 버프 효과 분석 (타입 기반)
                var buffEffects = AbilityClassifier.GetBuffEffects(candidate.Ability);
                if (buffEffects?.PrimaryBuff != null)
                {
                    var analysis = BuffEffectAnalyzer.Analyze(buffEffects.PrimaryBuff);
                    if (analysis.HasDefensiveEffect)
                        return true;
                }

                // 2. Classification의 AppliedBuff도 확인
                var appliedBuff = candidate.Classification?.AppliedBuff;
                if (appliedBuff != null)
                {
                    var analysis = BuffEffectAnalyzer.Analyze(appliedBuff);
                    return analysis.HasDefensiveEffect;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// ★ v0.2.61: 공격 버프인지 확인 (타입 기반 - BuffEffectAnalyzer 사용)
        /// 공격 보너스, 데미지 보너스, 무기 인챈트 등
        /// </summary>
        private bool IsOffensiveBuff(ActionCandidate candidate)
        {
            try
            {
                // 1. BuffEffectAnalyzer로 버프 효과 분석 (타입 기반)
                var buffEffects = AbilityClassifier.GetBuffEffects(candidate.Ability);
                if (buffEffects != null)
                {
                    // 인챈트는 무조건 공격 버프
                    if (buffEffects.IsEnchantment)
                        return true;

                    if (buffEffects.PrimaryBuff != null)
                    {
                        var analysis = BuffEffectAnalyzer.Analyze(buffEffects.PrimaryBuff);
                        if (analysis.HasOffensiveEffect)
                            return true;
                    }
                }

                // 2. Classification의 AppliedBuff도 확인
                var appliedBuff = candidate.Classification?.AppliedBuff;
                if (appliedBuff != null)
                {
                    var analysis = BuffEffectAnalyzer.Analyze(appliedBuff);
                    return analysis.HasOffensiveEffect;
                }
            }
            catch { }

            return false;
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
