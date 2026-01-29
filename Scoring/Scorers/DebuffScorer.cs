// ★ v0.2.22: Unified Decision Engine - Debuff/CC Scorer
// ★ v0.2.36: Enhanced with duplicate debuff check, immunity check, combat phase awareness
// ★ v0.2.37: Geometric Mean Scoring with Considerations
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

                // ★ v0.2.37: Consideration 기반 점수 구축
                BuildConsiderations(candidate, situation, weights);

                // ═══════════════════════════════════════════════════════════════
                // 0. ★ v0.2.36: 면역 체크 (가장 먼저)
                // ═══════════════════════════════════════════════════════════════
                bool isMindAffecting = candidate.Classification?.IsMindAffecting ?? false;
                if (isMindAffecting && IsMindAffectingImmune(target))
                {
                    Main.Verbose($"[DebuffScorer] {candidate.Ability?.Name}: Target {target.CharacterName} is mind-affecting IMMUNE");
                    candidate.BaseScore = -100f;
                    // Veto는 BuildConsiderations에서 이미 설정됨
                    return;
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
        /// </summary>
        private void BuildConsiderations(ActionCandidate candidate, Situation situation, PhaseRoleWeights weights)
        {
            var cs = candidate.Considerations;
            cs.Clear();

            var target = candidate.Target;

            // ═══════════════════════════════════════════════════════════════
            // 1. 타겟 존재 (Veto)
            // ═══════════════════════════════════════════════════════════════
            cs.AddVeto("HasTarget", target != null);

            if (target == null)
                return;

            // ═══════════════════════════════════════════════════════════════
            // 2. 면역 체크 (Veto)
            // ═══════════════════════════════════════════════════════════════
            bool isMindAffecting = candidate.Classification?.IsMindAffecting ?? false;
            if (isMindAffecting)
            {
                bool immune = IsMindAffectingImmune(target);
                cs.AddVeto("NotImmune", !immune);
            }

            // ═══════════════════════════════════════════════════════════════
            // 3. 중복 디버프 체크
            // ═══════════════════════════════════════════════════════════════
            bool hasSimilar = HasSimilarDebuff(target, candidate);
            cs.Add("NotDuplicate", hasSimilar ? 0.2f : 1.0f);

            // ═══════════════════════════════════════════════════════════════
            // 4. 타겟 가치 (위협 기반)
            // ═══════════════════════════════════════════════════════════════
            float threat = AssessThreat(target, situation);
            cs.Add("TargetThreat", ScoreNormalizer.Threat(threat));

            // ═══════════════════════════════════════════════════════════════
            // 5. HP 기반 가치 (CC는 체력 높은 적에게 더 유용)
            // ═══════════════════════════════════════════════════════════════
            float targetHP = GetHPPercent(target);
            // HP 높을수록 CC 가치 높음 (죽어가는 적은 그냥 처치)
            float hpValue = targetHP / 100f;
            if (targetHP < 30f)
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
            // 9. 세이브 타입 매칭 (약한 세이브 타겟팅)
            // ═══════════════════════════════════════════════════════════════
            var saveType = candidate.Classification?.RequiredSave;
            if (saveType.HasValue)
            {
                float saveMatchScore = ScoreSaveMatch(target, saveType.Value);
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

        /// <summary>
        /// 세이브 타입 매칭 점수
        /// </summary>
        private float ScoreSaveMatch(UnitEntityData target, SavingThrowType saveType)
        {
            try
            {
                int fort = GetSavingThrow(target, SavingThrowType.Fortitude);
                int refx = GetSavingThrow(target, SavingThrowType.Reflex);
                int will = GetSavingThrow(target, SavingThrowType.Will);

                int targetSave;
                switch (saveType)
                {
                    case SavingThrowType.Fortitude: targetSave = fort; break;
                    case SavingThrowType.Reflex: targetSave = refx; break;
                    case SavingThrowType.Will: targetSave = will; break;
                    default: return 0.5f;
                }

                int minSave = Math.Min(Math.Min(fort, refx), will);
                int maxSave = Math.Max(Math.Max(fort, refx), will);

                // 가장 약한 세이브를 타겟팅하면 높은 점수
                if (targetSave == minSave)
                    return 1.0f;
                else if (targetSave == maxSave)
                    return 0.4f;
                else
                    return 0.7f;
            }
            catch
            {
                return 0.5f;
            }
        }

        #endregion

        #endregion

        #region Target Scoring

        /// <summary>
        /// Score target for debuff application
        /// </summary>
        private float ScoreDebuffTarget(UnitEntityData target, Situation situation)
        {
            if (target == null)
                return 0f;

            float score = 0f;

            try
            {
                // Threat-based (high threat targets are better CC targets)
                float threat = AssessThreat(target, situation);
                score += ResponseCurves.CCTargetValue(threat) * 20f;

                // HP-based (full HP targets benefit more from CC)
                float hp = GetHPPercent(target);
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

        #region Save Type Matching

        /// <summary>
        /// Score based on save type vs target's weak save
        /// </summary>
        private float ScoreSaveTypeMatch(ActionCandidate candidate, Situation situation)
        {
            if (candidate.Target == null || candidate.Classification == null)
                return 0f;

            var saveType = candidate.Classification.RequiredSave;
            if (!saveType.HasValue)
                return 0f;

            try
            {
                // Get target's saves
                var target = candidate.Target;
                int fortSave = GetSavingThrow(target, SavingThrowType.Fortitude);
                int refSave = GetSavingThrow(target, SavingThrowType.Reflex);
                int willSave = GetSavingThrow(target, SavingThrowType.Will);

                int targetSave = 0;
                int minSave = Math.Min(Math.Min(fortSave, refSave), willSave);

                switch (saveType.Value)
                {
                    case SavingThrowType.Fortitude:
                        targetSave = fortSave;
                        break;
                    case SavingThrowType.Reflex:
                        targetSave = refSave;
                        break;
                    case SavingThrowType.Will:
                        targetSave = willSave;
                        break;
                }

                // Bonus if targeting weak save
                if (targetSave == minSave)
                    return 15f;  // Targeting weakest save

                // Penalty if targeting strong save
                int maxSave = Math.Max(Math.Max(fortSave, refSave), willSave);
                if (targetSave == maxSave)
                    return -10f;  // Targeting strongest save

                return 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private int GetSavingThrow(UnitEntityData unit, SavingThrowType type)
        {
            try
            {
                switch (type)
                {
                    case SavingThrowType.Fortitude:
                        return unit?.Stats?.SaveFortitude?.ModifiedValue ?? 10;
                    case SavingThrowType.Reflex:
                        return unit?.Stats?.SaveReflex?.ModifiedValue ?? 10;
                    case SavingThrowType.Will:
                        return unit?.Stats?.SaveWill?.ModifiedValue ?? 10;
                    default:
                        return 10;
                }
            }
            catch
            {
                return 10;
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
            try
            {
                float threat = 0.5f;
                float hp = GetHPPercent(target);
                if (hp > 80f) threat += 0.2f;
                if (situation.EnemiesTargetingAllies > 0) threat += 0.15f;
                return Math.Max(0f, Math.Min(1f, threat));
            }
            catch
            {
                return 0.5f;
            }
        }

        private int CountEnemiesInRange(Vector3 center, float radius, Situation situation)
        {
            if (situation.Enemies == null)
                return 0;

            return situation.Enemies.Count(e =>
                e != null && Vector3.Distance(center, e.Position) <= radius);
        }

        /// <summary>
        /// ★ v0.2.36: 마인드어페팅 면역 체크
        /// </summary>
        private bool IsMindAffectingImmune(UnitEntityData unit)
        {
            try
            {
                if (unit?.Descriptor == null)
                    return false;

                // Blueprint type 체크 (언데드, 구조물)
                var blueprint = unit.Descriptor.Blueprint;
                if (blueprint?.Type != null)
                {
                    string typeName = blueprint.Type.name?.ToLower() ?? "";
                    if (typeName.Contains("undead") || typeName.Contains("construct") ||
                        typeName.Contains("ooze") || typeName.Contains("vermin") ||
                        typeName.Contains("언데드") || typeName.Contains("구조물"))
                    {
                        return true;
                    }
                }

                // Feature 기반 면역 체크
                var features = unit.Descriptor?.Progression?.Features?.Enumerable;
                if (features != null)
                {
                    foreach (var f in features)
                    {
                        string fName = f.Blueprint?.name?.ToLower() ?? "";
                        if (fName.Contains("immunemind") || fName.Contains("mindaffecting"))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch { return false; }
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
