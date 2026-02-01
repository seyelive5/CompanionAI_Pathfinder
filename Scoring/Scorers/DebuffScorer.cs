// ★ v0.2.22: Unified Decision Engine - Debuff/CC Scorer
// ★ v0.2.36: Enhanced with duplicate debuff check, immunity check, combat phase awareness
// ★ v0.2.37: Geometric Mean Scoring with Considerations
// ★ v0.2.52: TargetAnalyzer 통합 - 중복 분석 코드 제거
// ★ v0.2.74: Save DC 계산 통합 - 실제 세이브 실패 확률 기반 스코어링
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Scoring.Scorers
{
    /// <summary>
    /// Scores debuff and crowd control actions.
    /// Considers save types, immunities, and target prioritization.
    /// </summary>
    public class DebuffScorer
    {
        #region Main Scoring

        /// <summary>
        /// ★ v0.2.36: Enhanced debuff/CC scoring
        /// ★ v0.2.37: Geometric Mean Scoring with Considerations
        /// Checks for duplicate debuffs, immunities, and uses improved target scoring
        /// </summary>
        public void Score(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            if (candidate == null || situation == null)
                return;

            float score = candidate.BaseScore;

            try
            {
                var target = candidate.Target;
                if (target == null)
                {
                    candidate.BaseScore = -100f;
                    // ★ v0.2.37: Veto 추가
                    candidate.Considerations.AddVeto("HasTarget", false);
                    return;
                }

                // ★ v0.2.52: 통합 분석 획득 (캐시됨)
                var analysis = TargetAnalyzer.Analyze(target, situation.Unit);

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildConsiderations(candidate, situation, weights, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 0. ★ v0.2.36/0.2.52: 면역 체크 (가장 먼저) - TargetAnalyzer 사용
                // ═══════════════════════════════════════════════════════════════
                bool isMindAffecting = candidate.Classification?.IsMindAffecting ?? false;
                if (isMindAffecting && (analysis?.IsMindAffectingImmune ?? false))
                {
                    Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name}: Target {target.CharacterName} is mind-affecting IMMUNE");
                    candidate.BaseScore = -100f;
                    // Veto는 BuildConsiderations에서 이미 설정됨
                    return;
                }

                // ═══════════════════════════════════════════════════════════════
                // 0.5 ★ v0.2.74: Spell Resistance 체크
                // ═══════════════════════════════════════════════════════════════
                if (candidate.Ability != null)
                {
                    var srResult = HitChanceCalculator.CalculateSpellResistance(
                        situation.Unit, target, candidate.Ability);

                    if (srResult.IsImmune)
                    {
                        Main.Verbose($"[DebuffScorer] {candidate.Ability.Name}: Target {target.CharacterName} is SPELL IMMUNE");
                        candidate.BaseScore = -100f;
                        candidate.Considerations.AddVeto("SpellImmune", false);
                        return;
                    }

                    // SR이 높아서 돌파 확률이 낮으면 페널티
                    if (srResult.AllowsSR && srResult.TargetSR > 0)
                    {
                        if (srResult.PenetrationChance < 0.3f)
                        {
                            score -= 25f;  // 30% 미만 돌파율 = 상당한 페널티
                            Main.Verbose($"[DebuffScorer] {candidate.Ability.Name}: Low SR penetration {srResult.PenetrationChance:P0} vs {target.CharacterName}");
                        }
                        else if (srResult.PenetrationChance < 0.5f)
                        {
                            score -= 10f;  // 50% 미만
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // 1. ★ v0.2.36: 기존 디버프 체크 (중복 시전 방지)
                // ═══════════════════════════════════════════════════════════════
                if (HasSimilarDebuff(target, candidate))
                {
                    Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name}: Target {target.CharacterName} already has similar debuff");
                    score -= 50f;  // 중복 디버프 페널티 (완전 차단은 아님 - 스택 가능할 수도)
                }

                // ═══════════════════════════════════════════════════════════════
                // 2. Target value using enhanced TargetScorer
                // ═══════════════════════════════════════════════════════════════
                bool isHardCC = candidate.Classification?.IsHardCC ?? false;
                var saveType = candidate.Classification?.RequiredSave ?? SavingThrowType.Will;

                float targetValue = TargetScorer.ScoreDebuffTarget(
                    target, saveType, situation, isMindAffecting, isHardCC);
                score += targetValue * 0.5f;

                // ═══════════════════════════════════════════════════════════════
                // 2.5 ★ v0.2.74: 실제 세이브 DC 기반 성공률 체크
                // ═══════════════════════════════════════════════════════════════
                if (candidate.Ability != null)
                {
                    var saveDCResult = HitChanceCalculator.CalculateSaveDC(
                        situation.Unit, target, candidate.Ability, saveType);

                    if (saveDCResult != null)
                    {
                        // 세이브 실패 확률이 높으면 보너스
                        if (saveDCResult.FailureChance > 0.7f)
                            score += 15f;  // 70%+ 실패 확률 = 좋은 타겟
                        else if (saveDCResult.FailureChance < 0.2f)
                            score -= 30f;  // 20% 미만 실패 = 낭비 가능성

                        Main.Verbose($"[DebuffScorer] {candidate.Ability.Name}: SaveDC check - {saveDCResult}");
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // 3. CC type considerations
                // ═══════════════════════════════════════════════════════════════
                if (candidate.Classification != null)
                {
                    score += ScoreCCType(candidate, situation);
                }

                // ═══════════════════════════════════════════════════════════════
                // 4. Phase-based adjustment (TargetScorer에서도 처리하지만 추가 조정)
                // ═══════════════════════════════════════════════════════════════
                switch (situation.CombatPhase)
                {
                    case CombatPhase.Opening:
                        // CC is excellent at combat start
                        if (isHardCC)
                            score += 20f;
                        else
                            score += 10f;
                        break;

                    case CombatPhase.Midgame:
                        // Normal value
                        if (isHardCC)
                            score += 5f;
                        break;

                    case CombatPhase.Cleanup:
                        // Less valuable in cleanup (just kill them)
                        score -= 15f;
                        break;

                    case CombatPhase.Desperate:
                        // CC to reduce incoming damage is critical
                        if (isHardCC)
                            score += 20f;
                        else
                            score += 10f;
                        break;
                }

                // ═══════════════════════════════════════════════════════════════
                // 5. Resource consideration
                // ═══════════════════════════════════════════════════════════════
                if (candidate.Classification != null)
                {
                    int spellLevel = candidate.Classification.SpellLevel;
                    candidate.ResourcePenalty = ResponseCurves.SpellLevelPenalty(spellLevel, weights.ResourceConservation);

                    // ★ v0.2.36: 적 수에 따른 리소스 페널티 조정
                    int enemyCount = situation.Enemies?.Count ?? 1;
                    if (enemyCount >= 4 && isHardCC)
                    {
                        // 적이 많으면 CC 리소스 덜 아낌
                        candidate.ResourcePenalty *= 0.7f;
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // 6. AoE CC bonus
                // ═══════════════════════════════════════════════════════════════
                if (candidate.Classification?.IsAoE == true)
                {
                    int enemiesInRange = CountEnemiesInRange(
                        candidate.Target?.Position ?? situation.Unit.Position,
                        candidate.Classification.AoERadius,
                        situation);

                    if (enemiesInRange > 1)
                    {
                        // ★ v0.2.36: AoE CC 보너스 증가
                        score += enemiesInRange * 15f;
                        Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name}: AoE hits {enemiesInRange} enemies, +{enemiesInRange * 15f}");
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // 7. ★ v0.2.36: 캐스터/힐러 타겟 보너스
                // ═══════════════════════════════════════════════════════════════
                if (IsEnemyCaster(target))
                {
                    score += 15f;
                    if (isHardCC)
                        score += 10f;  // Hard CC로 캐스터 제압 = 매우 가치있음
                }

                // Apply phase weight
                candidate.PhaseMultiplier = weights.DebuffWeight;
                candidate.BaseScore = score;

                Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name} -> {target.CharacterName}: score={score:F1}, hardCC={isHardCC}, mindAffecting={isMindAffecting}");
            }
            catch (Exception ex)
            {
                Main.Error($"[DebuffScorer] Score error: {ex.Message}");
            }
        }

        #region ★ v0.2.37: Consideration Building

        /// <summary>
        /// 디버프/CC 행동에 대한 Consideration 구축
        /// ★ v0.2.52: TargetAnalyzer 통합
        /// </summary>
        private void BuildConsiderations(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights, TargetAnalysis analysis)
        {
            var cs = candidate.Considerations;
            cs.Clear();

            var target = candidate.Target;

            // ═══════════════════════════════════════════════════════════════
            // 1. 타겟 존재 (Veto)
            // ═══════════════════════════════════════════════════════════════
            cs.AddVeto("HasTarget", target != null);

            if (target == null || analysis == null)
                return;

            // ═══════════════════════════════════════════════════════════════
            // 2. 면역 체크 (Veto) - ★ v0.2.52: TargetAnalyzer 사용
            // ═══════════════════════════════════════════════════════════════
            bool isMindAffecting = candidate.Classification?.IsMindAffecting ?? false;
            if (isMindAffecting)
            {
                cs.AddVeto("NotImmune", !analysis.IsMindAffectingImmune);
            }

            // ═══════════════════════════════════════════════════════════════
            // 2.5 ★ v0.2.74: Spell Resistance 체크
            // ═══════════════════════════════════════════════════════════════
            if (candidate.Ability != null)
            {
                var srResult = HitChanceCalculator.CalculateSpellResistance(
                    situation.Unit, target, candidate.Ability);

                // 스펠 면역이면 Veto
                cs.AddVeto("NotSpellImmune", !srResult.IsImmune);

                // SR 돌파 확률을 Consideration에 추가
                if (srResult.AllowsSR && srResult.TargetSR > 0)
                {
                    cs.Add("SRPenetration", srResult.PenetrationChance);
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // 3. 중복 디버프 체크
            // ═══════════════════════════════════════════════════════════════
            bool hasSimilar = HasSimilarDebuff(target, candidate);
            cs.Add("NotDuplicate", hasSimilar ? 0.2f : 1.0f);

            // ═══════════════════════════════════════════════════════════════
            // 4. 타겟 가치 (위협 기반) - ★ v0.2.52: TargetAnalyzer 사용
            // ═══════════════════════════════════════════════════════════════
            cs.Add("TargetThreat", ScoreNormalizer.Threat(analysis.ThreatLevel));

            // ═══════════════════════════════════════════════════════════════
            // 5. HP 기반 가치 (CC는 체력 높은 적에게 더 유용) - ★ v0.2.52: TargetAnalyzer 사용
            // ═══════════════════════════════════════════════════════════════
            // HP 높을수록 CC 가치 높음 (죽어가는 적은 그냥 처치)
            float hpValue = analysis.HPPercent / 100f;
            if (analysis.HPPercent < 30f)
                hpValue *= 0.5f;  // 거의 죽은 적에게 CC는 낭비
            cs.Add("TargetHP", hpValue);

            // ═══════════════════════════════════════════════════════════════
            // 6. 전투 페이즈 적합성
            // ═══════════════════════════════════════════════════════════════
            bool isHardCC = candidate.Classification?.IsHardCC ?? false;
            cs.Add("PhaseFit", ScoreNormalizer.CCPhaseFit(situation.CombatPhase, isHardCC));

            // ═══════════════════════════════════════════════════════════════
            // 7. 역할 적합성
            // ═══════════════════════════════════════════════════════════════
            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;
            cs.Add("RoleFit", ScoreNormalizer.RoleActionFit(role, CandidateType.Debuff));

            // ═══════════════════════════════════════════════════════════════
            // 8. 리소스 가용성
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
            // 9. ★ v0.2.74: 세이브 기반 점수 - 실제 세이브 실패 확률 계산
            // ═══════════════════════════════════════════════════════════════
            var saveType = candidate.Classification?.RequiredSave;
            if (saveType.HasValue && candidate.Ability != null)
            {
                // (a) 약한 세이브 타입 매칭 보너스 유지
                float saveMatchScore = TargetNormalizer.WeakSaveBonus(saveType.Value, analysis.WeakestSaveType);
                cs.Add("SaveMatch", saveMatchScore);

                // (b) ★ v0.2.74: 실제 세이브 DC와 타겟 세이브 보너스로 실패 확률 계산
                var saveDCResult = HitChanceCalculator.CalculateSaveDC(
                    situation.Unit,
                    target,
                    candidate.Ability,
                    saveType);

                // 세이브 실패 확률이 높을수록 좋음 (0.0~1.0)
                float failureChance = saveDCResult?.FailureChance ?? 0.5f;
                cs.Add("SaveFailure", failureChance);

                // 세이브 실패 확률이 너무 낮으면 패널티 (10% 미만)
                if (failureChance < 0.1f)
                {
                    cs.Add("LowSavePenalty", 0.3f);  // 70% 패널티
                    Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name}: Low save failure {failureChance:P0} vs {target.CharacterName}");
                }
            }
            else if (saveType.HasValue)
            {
                // 능력 데이터 없으면 타입 매칭만
                float saveMatchScore = TargetNormalizer.WeakSaveBonus(saveType.Value, analysis.WeakestSaveType);
                cs.Add("SaveMatch", saveMatchScore);
            }

            // ═══════════════════════════════════════════════════════════════
            // 10. Hard CC 보너스
            // ═══════════════════════════════════════════════════════════════
            if (isHardCC)
            {
                cs.Add("HardCC", 1.0f);  // Hard CC는 기본적으로 가치 있음
            }

            Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name} -> {target.CharacterName}: {cs.ToDebugString()}");
        }

        // ★ v0.2.52: ScoreSaveMatch() 삭제됨 - TargetNormalizer.WeakSaveBonus() 사용

        #endregion

        #endregion

        #region Target Scoring

        /// <summary>
        /// Score target for debuff application
        /// ★ v0.2.52: TargetAnalyzer 통합
        /// </summary>
        private float ScoreDebuffTarget(UnitEntityData target, Situation situation)
        {
            if (target == null)
                return 0f;

            float score = 0f;

            try
            {
                // ★ v0.2.52: 통합 분석 획득 (캐시됨)
                var analysis = TargetAnalyzer.Analyze(target, situation.Unit);

                // Threat-based (high threat targets are better CC targets)
                float threat = analysis?.ThreatLevel ?? 0.5f;
                score += ResponseCurves.CCTargetValue(threat) * 20f;

                // HP-based (full HP targets benefit more from CC)
                float hp = analysis?.HPPercent ?? 100f;
                if (hp > 70f)
                    score += 10f;  // Full HP - CC is valuable
                else if (hp < 30f)
                    score -= 10f;  // Nearly dead - just kill them

                // Distance bonus for close threats
                float dist = Vector3.Distance(situation.Unit.Position, target.Position);
                if (dist < 5f)
                    score += 5f;  // Close threat

                // Targeting our allies = higher priority for CC
                if (situation.EnemiesTargetingAllies > 0)
                    score += 10f;
            }
            catch (Exception ex)
            {
                Main.Error($"[DebuffScorer] ScoreDebuffTarget error: {ex.Message}");
            }

            return score;
        }

        #endregion

        #region CC Type Scoring

        /// <summary>
        /// Score based on CC type
        /// </summary>
        private float ScoreCCType(ActionCandidate candidate, Situation situation)
        {
            float score = 0f;

            if (candidate.Classification == null)
                return score;

            var ccType = candidate.Classification.CCType;

            // Hard CC is more valuable
            if (candidate.Classification.IsHardCC)
            {
                score += 15f;

                // Even better against multiple enemies
                if ((situation.Enemies?.Count ?? 0) >= 3)
                    score += 10f;
            }
            else if (candidate.Classification.IsSoftCC)
            {
                score += 8f;
            }

            // Mind-affecting check (undead/constructs may be immune)
            if (candidate.Classification.IsMindAffecting)
            {
                // Could add immunity check here
                // For now, small penalty as safeguard
                score -= 3f;
            }

            return score;
        }

        /// <summary>
        /// Check if this is hard CC
        /// </summary>
        private bool IsHardCC(ActionCandidate candidate)
        {
            return candidate.Classification?.IsHardCC == true;
        }

        #endregion

        // ★ v0.2.52: Save Type Matching 영역 삭제됨 - TargetAnalyzer/TargetNormalizer 사용

        #region Helper Methods

        // ★ v0.2.52: GetHPPercent(), AssessThreat(), IsMindAffectingImmune() 삭제됨 - TargetAnalyzer 사용

        private int CountEnemiesInRange(Vector3 center, float radius, Situation situation)
        {
            if (situation.Enemies == null)
                return 0;

            return situation.Enemies.Count(e =>
                e != null && Vector3.Distance(center, e.Position) <= radius);
        }

        /// <summary>
        /// ★ v0.2.36: 타겟에게 유사한 디버프가 이미 있는지 확인
        /// </summary>
        private bool HasSimilarDebuff(UnitEntityData target, ActionCandidate candidate)
        {
            try
            {
                if (target?.Buffs == null || candidate?.Classification == null)
                    return false;

                // 능력이 적용하는 버프/디버프 정보 가져오기
                var buffEffects = AbilityClassifier.GetBuffEffects(candidate.Ability);
                if (buffEffects == null || buffEffects.IsEmpty)
                    return false;

                // 타겟에게 동일한 버프가 있는지 확인
                return buffEffects.IsAnyBuffPresent(target);
            }
            catch { return false; }
        }

        /// <summary>
        /// ★ v0.2.36: 적이 캐스터인지 확인
        /// </summary>
        private bool IsEnemyCaster(UnitEntityData enemy)
        {
            try
            {
                if (enemy?.Descriptor == null)
                    return false;

                var spellbooks = enemy.Descriptor.Spellbooks;
                if (spellbooks == null)
                    return false;

                foreach (var sb in spellbooks)
                {
                    if (sb.CasterLevel > 0)
                        return true;
                }

                return false;
            }
            catch { return false; }
        }

        #endregion
    }
}
