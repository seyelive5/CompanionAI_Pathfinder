using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using UnityEngine;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.Scoring;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 타겟 스코어링 시스템
    /// Role별 가중치를 적용하여 최적 타겟 선택
    /// </summary>
    public static class TargetScorer
    {
        #region Weight Classes

        /// <summary>
        /// 적 타겟 스코어링 가중치
        /// </summary>
        public class EnemyWeights
        {
            public float HPPercent { get; set; }      // 낮은 HP 우선 (마무리)
            public float Distance { get; set; }       // 거리 패널티/보너스
            public float Threat { get; set; }         // 위협도 (데미지 딜러 등)
            public float Hittable { get; set; }       // 현재 공격 가능 보너스
            public float ACVulnerability { get; set; } // v0.2.18: AC 취약 보너스 (물리 공격자용)
        }

        /// <summary>
        /// 아군 타겟 스코어링 가중치 (Support용)
        /// </summary>
        public class AllyWeights
        {
            public float HPPercent { get; set; }      // 낮은 HP 우선 (힐 필요)
            public float Distance { get; set; }       // 거리 패널티
            public float MissingHP { get; set; }      // 손실 HP 양
        }

        #endregion

        #region Role-based Weight Presets

        // DPS: 약한 적 우선, 마무리 중시
        public static readonly EnemyWeights DPSWeights = new EnemyWeights
        {
            HPPercent = 0.8f,     // 높음 - 마무리 중시
            Distance = 0.3f,      // 낮음 - 이동 OK
            Threat = 0.5f,        // 중간
            Hittable = 0.6f,      // 중간
            ACVulnerability = 0.6f // v0.2.18: 낮은 AC 적 선호
        };

        // Tank: 가까운 적 우선, 거리 중시
        public static readonly EnemyWeights TankWeights = new EnemyWeights
        {
            HPPercent = 0.3f,     // 낮음 - 마무리보다 접근
            Distance = 1.0f,      // 매우 높음 - 가까운 적 우선
            Threat = 0.8f,        // 높음 - 위협 제거
            Hittable = 0.8f,      // 높음 - 바로 공격 가능
            ACVulnerability = 0.4f // v0.2.18: 탱크는 AC 덜 중요 (어차피 때림)
        };

        // Support: 안전한 공격, 위협 제거
        public static readonly EnemyWeights SupportWeights = new EnemyWeights
        {
            HPPercent = 0.5f,     // 중간
            Distance = 0.2f,      // 낮음 - 원거리 공격
            Threat = 1.0f,        // 매우 높음 - 위협 제거 우선
            Hittable = 1.0f,      // 매우 높음 - 이동 없이 공격
            ACVulnerability = 0.2f // v0.2.18: 캐스터는 AC 거의 무관 (세이브 기반)
        };

        #endregion

        #region AIRole-based Scoring

        /// <summary>
        /// AIRole 기반 적 타겟 점수 계산
        /// RealTimeController에서 사용
        /// v0.2.17: 킬 확정 보너스, 적 역할 감지 추가
        /// </summary>
        public static float ScoreTarget(UnitEntityData attacker, UnitEntityData target, AIRole role)
        {
            if (target == null || attacker == null) return -1000f;

            try
            {
                if (target.Descriptor?.State?.IsDead == true) return -1000f;
            }
            catch { }

            var weights = GetWeightsForRole(role);
            float score = 50f;

            try
            {
                // 1. HP% 점수 (낮을수록 높음)
                float hpPercent = GetHPPercent(target);
                float hpScore = (100f - hpPercent) * 0.5f;
                score += hpScore * weights.HPPercent;

                // 2. 거리 점수
                float distance = GetDistance(attacker, target);
                float distanceScore = -distance * 2f;

                if (role == AIRole.Tank && distance <= 5f)
                    distanceScore += 30f;

                score += distanceScore * weights.Distance;

                // 3. 위협도 평가
                float threat = EvaluateThreatSimple(target, attacker);
                score += threat * 30f * weights.Threat;

                // 4. v0.2.17: 킬 확정 보너스 - HP가 매우 낮으면 마무리 우선
                if (hpPercent <= 20f)
                    score += 25f;
                else if (hpPercent <= 35f)
                    score += 12f;

                // 5. v0.2.17: 적 역할 보너스 - 캐스터/힐러 우선 타겟팅
                float roleBonus = EvaluateEnemyRolePriority(target);
                score += roleBonus;

                // 6. v0.2.18: AC 기반 타겟팅 - 물리 공격자만
                if (IsPhysicalAttacker(attacker))
                {
                    int targetAC = GetTargetAC(target);
                    float acScore = (30f - targetAC) * 1.0f; // AC 10→+20, AC 30→0, AC 40→-10
                    score += acScore * weights.ACVulnerability;
                    Main.Verbose($"[TargetScorer] {target.CharacterName}: AC={targetAC}, acBonus={acScore * weights.ACVulnerability:F1}");
                }

                // 7. v0.2.18: 플랭킹 보너스
                if (IsTargetFlanked(target))
                {
                    score += 15f; // 플랭킹 = 공격 +2 보너스 반영
                    if (HasSneakAttack(attacker))
                        score += 30f; // 스닉 어택 보유 시 플랭킹 적 극우선
                    Main.Verbose($"[TargetScorer] {target.CharacterName}: FLANKED bonus, sneakAtk={HasSneakAttack(attacker)}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] ScoreTarget error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// AIRole 기반 가중치 반환
        /// </summary>
        private static EnemyWeights GetWeightsForRole(AIRole role)
        {
            switch (role)
            {
                case AIRole.Tank: return TankWeights;
                case AIRole.Support: return SupportWeights;
                case AIRole.DPS:
                default: return DPSWeights;
            }
        }

        /// <summary>
        /// 간단한 위협도 평가 (Situation 없이)
        /// </summary>
        private static float EvaluateThreatSimple(UnitEntityData target, UnitEntityData from)
        {
            float threat = 0f;

            try
            {
                float hpPercent = GetHPPercent(target);
                float hpNormalized = hpPercent / 100f;
                threat += hpNormalized * 0.4f;

                float distance = GetDistance(from, target);
                float maxRange = 30f;
                float proximityNormalized = 1f - Math.Min(1f, distance / maxRange);
                threat += proximityNormalized * 0.4f;

                if (HasRangedWeapon(target))
                    threat += 0.2f;
            }
            catch
            {
                return 0.5f;
            }

            return Math.Max(0f, Math.Min(1f, threat));
        }

        // Support 아군 타겟 가중치
        public static readonly AllyWeights SupportAllyWeights = new AllyWeights
        {
            HPPercent = 1.0f,     // 매우 높음 - 낮은 HP 우선 힐
            Distance = 0.3f,      // 낮음 - 거리 무시 (힐 사거리 김)
            MissingHP = 0.7f      // 높음 - 손실량 많을수록 우선
        };

        #endregion

        #region Enemy Scoring

        /// <summary>
        /// Role 기반 적 타겟 점수 계산
        /// </summary>
        public static float ScoreEnemy(
            UnitEntityData target,
            Situation situation,
            RangePreference rangePreference)
        {
            if (target == null) return -1000f;

            try
            {
                if (target.Descriptor?.State?.IsDead == true) return -1000f;
            }
            catch { }

            var weights = GetEnemyWeights(rangePreference);
            float score = 50f;  // 기본 점수

            try
            {
                // 1. HP% 점수 (낮을수록 높음) - 마무리 타겟 우선
                float hpPercent = GetHPPercent(target);
                float hpScore = (100f - hpPercent) * 0.5f;  // 0~50
                score += hpScore * weights.HPPercent;

                // 2. 거리 점수 (가까울수록 좋음)
                float distance = GetDistance(situation.Unit, target);
                float distanceScore = -distance * 2f;  // 거리 패널티

                // Melee는 근접 보너스
                if (rangePreference == RangePreference.Melee && distance <= 5f)
                    distanceScore += 30f;

                score += distanceScore * weights.Distance;

                // 3. Hittable 여부
                bool isHittable = situation.HittableEnemies?.Contains(target) ?? false;
                if (isHittable)
                    score += 25f * weights.Hittable;
                else
                    score -= 15f;

                // 4. 위협도 평가 (간소화: HP 기반)
                float threat = EvaluateThreat(target, situation);
                score += threat * 30f * weights.Threat;

                // 5. v0.2.17: 킬 확정 보너스
                if (hpPercent <= 20f)
                    score += 25f;
                else if (hpPercent <= 35f)
                    score += 12f;

                // 6. v0.2.17: 적 역할 보너스
                float roleBonus = EvaluateEnemyRolePriority(target);
                score += roleBonus;

                // 7. v0.2.18: AC 기반 타겟팅 - 물리 공격자만
                if (situation.Unit != null && IsPhysicalAttacker(situation.Unit))
                {
                    int targetAC = GetTargetAC(target);
                    float acScore = (30f - targetAC) * 1.0f;
                    score += acScore * weights.ACVulnerability;
                }

                // 8. v0.2.18: 플랭킹 보너스
                if (IsTargetFlanked(target))
                {
                    score += 15f;
                    if (situation.HasSneakAttack)
                        score += 30f;
                }

                // 9. v0.2.18: 탱크 - 아군을 교전 중인 적 우선
                if (rangePreference == RangePreference.Melee)
                {
                    int alliesEngaged = CountAlliesEngagedBy(target, situation);
                    if (alliesEngaged > 0)
                        score += alliesEngaged * 10f;
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] ScoreEnemy error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// Role별 가중치 반환
        /// </summary>
        private static EnemyWeights GetEnemyWeights(RangePreference preference)
        {
            switch (preference)
            {
                case RangePreference.Melee: return TankWeights;
                case RangePreference.Ranged: return SupportWeights;
                case RangePreference.Mixed:
                default: return DPSWeights;
            }
        }

        /// <summary>
        /// Role 기반 최적 적 타겟 선택
        /// </summary>
        public static UnitEntityData SelectBestEnemy(
            List<UnitEntityData> candidates,
            Situation situation)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            try
            {
                var scored = candidates
                    .Where(t => t != null)
                    .Where(t => {
                        try { return t.Descriptor?.State?.IsDead != true; }
                        catch { return true; }
                    })
                    .Select(t => new { Target = t, Score = ScoreEnemy(t, situation, situation.RangePreference) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (scored != null)
                {
                    Main.Verbose($"[TargetScorer] Best enemy: {scored.Target.CharacterName} (score={scored.Score:F1})");
                    return scored.Target;
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] SelectBestEnemy error: {ex.Message}");
            }

            return candidates.FirstOrDefault();
        }

        #endregion

        #region Ally Scoring

        // ★ v0.2.36: 힐링 우선순위 상수
        private const float HP_CRITICAL = 25f;   // 위험 (긴급 힐)
        private const float HP_LOW = 50f;        // 낮음 (우선 힐)
        private const float HP_MODERATE = 75f;   // 보통 (힐 고려)

        /// <summary>
        /// ★ v0.2.36: 향상된 아군 힐 대상 점수 계산
        /// HP 단계별 긴급도 + 역할 가중치 + 위협 상황 고려
        /// </summary>
        public static float ScoreAllyForHealing(
            UnitEntityData ally,
            Situation situation)
        {
            if (ally == null) return -1000f;

            try
            {
                if (ally.Descriptor?.State?.IsDead == true) return -1000f;
            }
            catch { }

            float score = 0f;

            try
            {
                float hpPercent = GetHPPercent(ally);

                // ═══════════════════════════════════════════════════════════════
                // 1. HP 단계별 긴급도 (핵심 로직)
                // ═══════════════════════════════════════════════════════════════
                if (hpPercent < HP_CRITICAL)
                {
                    // 위험: 즉시 힐 필요 (HP 낮을수록 점수 급상승)
                    float criticalBonus = (HP_CRITICAL - hpPercent) * 4f; // 0~100점
                    score += 100f + criticalBonus;
                }
                else if (hpPercent < HP_LOW)
                {
                    // 낮음: 우선 힐 대상
                    float lowBonus = (HP_LOW - hpPercent) * 2f; // 0~50점
                    score += 50f + lowBonus;
                }
                else if (hpPercent < HP_MODERATE)
                {
                    // 보통: 힐 고려
                    float modBonus = (HP_MODERATE - hpPercent) * 0.8f; // 0~20점
                    score += 20f + modBonus;
                }
                else
                {
                    // HP 충분: 힐 불필요
                    score -= 50f;
                }

                // ═══════════════════════════════════════════════════════════════
                // 2. 역할 기반 가중치 (탱크 > DPS > Support)
                // ═══════════════════════════════════════════════════════════════
                var allyRole = GetUnitRole(ally);
                switch (allyRole)
                {
                    case AIRole.Tank:
                        score += 25f;  // 전선 유지 중요
                        break;
                    case AIRole.DPS:
                        score += 15f;  // 화력 유지
                        break;
                    case AIRole.Support:
                        score += 20f;  // 힐러가 죽으면 파티 붕괴
                        break;
                }

                // ═══════════════════════════════════════════════════════════════
                // 3. 위협 상황 (적에게 타겟팅 당하는 아군)
                // ═══════════════════════════════════════════════════════════════
                int engagedEnemies = CountEngagedEnemies(ally);
                if (engagedEnemies > 0)
                {
                    score += engagedEnemies * 10f;  // 교전 중인 적 수 × 10

                    // HP 낮으면서 교전 중 = 매우 위험
                    if (hpPercent < HP_LOW)
                        score += 15f;
                }

                // ═══════════════════════════════════════════════════════════════
                // 4. 거리 (힐 사거리 고려)
                // ═══════════════════════════════════════════════════════════════
                float distance = GetDistance(situation.Unit, ally);
                // 30ft (6m) 이내는 패널티 없음, 이후 거리당 -2
                if (distance > 6f)
                    score -= (distance - 6f) * 2f;

                // ═══════════════════════════════════════════════════════════════
                // 5. 자기 자신 힐 보너스 (위험 시)
                // ═══════════════════════════════════════════════════════════════
                if (ally == situation.Unit && hpPercent < HP_LOW)
                {
                    score += 10f;  // 자기 자신 = 확실히 힐 가능
                }

                Main.Verbose($"[TargetScorer] HealScore {ally.CharacterName}: HP={hpPercent:F0}%, role={allyRole}, engaged={engagedEnemies}, score={score:F1}");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] ScoreAllyForHealing error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// ★ v0.2.36: 향상된 최적 힐 대상 선택
        /// </summary>
        public static UnitEntityData SelectBestAllyForHealing(
            List<UnitEntityData> allies,
            Situation situation,
            float hpThreshold = 80f)
        {
            if (allies == null || allies.Count == 0)
                return null;

            try
            {
                // 자기 자신도 힐 대상에 포함
                var allTargets = new List<UnitEntityData>(allies);
                if (situation.Unit != null && !allTargets.Contains(situation.Unit))
                    allTargets.Add(situation.Unit);

                var scored = allTargets
                    .Where(a => a != null)
                    .Where(a => {
                        try { return a.Descriptor?.State?.IsDead != true; }
                        catch { return true; }
                    })
                    .Where(a => GetHPPercent(a) < hpThreshold)
                    .Select(a => new { Ally = a, Score = ScoreAllyForHealing(a, situation), HP = GetHPPercent(a) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (scored != null)
                {
                    Main.Log($"[TargetScorer] Best heal target: {scored.Ally.CharacterName} (HP={scored.HP:F0}%, score={scored.Score:F1})");
                    return scored.Ally;
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] SelectBestAllyForHealing error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// ★ v0.2.36: 유닛 역할 추론 (캐릭터 설정 또는 스탯 기반)
        /// </summary>
        private static AIRole GetUnitRole(UnitEntityData unit)
        {
            try
            {
                // 먼저 ModSettings에서 설정된 역할 확인
                string charId = unit?.UniqueId ?? "";
                if (!string.IsNullOrEmpty(charId) && ModSettings.Instance != null)
                {
                    var settings = ModSettings.Instance.GetOrCreateSettings(charId, unit.CharacterName);
                    if (settings != null)
                        return settings.Role;
                }

                // 설정 없으면 스탯 기반 추론
                if (unit?.Stats == null)
                    return AIRole.DPS;

                int str = unit.Stats.Strength?.ModifiedValue ?? 10;
                int dex = unit.Stats.Dexterity?.ModifiedValue ?? 10;
                int con = unit.Stats.Constitution?.ModifiedValue ?? 10;
                int wis = unit.Stats.Wisdom?.ModifiedValue ?? 10;
                int cha = unit.Stats.Charisma?.ModifiedValue ?? 10;
                int inte = unit.Stats.Intelligence?.ModifiedValue ?? 10;

                // AC와 HP가 높으면 Tank
                int ac = unit.Stats.AC?.ModifiedValue ?? 10;
                if (ac >= 25 && con >= 14)
                    return AIRole.Tank;

                // 캐스터 스탯(Wis/Cha/Int) + Spellbook 있으면 Support
                bool hasCaster = false;
                var spellbooks = unit.Descriptor?.Spellbooks;
                if (spellbooks != null)
                {
                    foreach (var sb in spellbooks)
                    {
                        if (sb.CasterLevel > 0)
                        {
                            hasCaster = true;
                            break;
                        }
                    }
                }

                if (hasCaster && (wis >= 16 || cha >= 16 || inte >= 16))
                    return AIRole.Support;

                return AIRole.DPS;
            }
            catch
            {
                return AIRole.DPS;
            }
        }

        /// <summary>
        /// ★ v0.2.36: 유닛이 교전 중인 적 수
        /// </summary>
        private static int CountEngagedEnemies(UnitEntityData unit)
        {
            try
            {
                var engaged = unit?.CombatState?.EngagedUnits;
                return engaged?.Count ?? 0;
            }
            catch { return 0; }
        }

        #endregion

        #region Debuff Scoring

        /// <summary>
        /// ★ v0.2.36: 향상된 디버프 타겟 스코어링
        /// 세이브 약점 + 전투 페이즈 + 기존 디버프 체크 + 면역 체크
        /// </summary>
        public static float ScoreDebuffTarget(UnitEntityData target, SavingThrowType saveType, Situation situation,
            bool isMindAffecting = false, bool isHardCC = false)
        {
            if (target == null) return -1000f;
            try { if (target.Descriptor?.State?.IsDead == true) return -1000f; } catch { }

            float score = 50f;

            try
            {
                // ═══════════════════════════════════════════════════════════════
                // 1. 면역 체크 (가장 먼저)
                // ═══════════════════════════════════════════════════════════════
                if (isMindAffecting && IsMindAffectingImmune(target))
                {
                    Main.Verbose($"[TargetScorer] {target.CharacterName}: Mind-affecting IMMUNE");
                    return -1000f;  // 면역이면 타겟 불가
                }

                // ═══════════════════════════════════════════════════════════════
                // 2. 세이브 기반 점수 - 약한 세이브 공략
                // ═══════════════════════════════════════════════════════════════
                int saveValue = GetSaveValue(target, saveType);
                int fortSave = GetSaveValue(target, SavingThrowType.Fortitude);
                int refSave = GetSaveValue(target, SavingThrowType.Reflex);
                int willSave = GetSaveValue(target, SavingThrowType.Will);
                int minSave = Math.Min(Math.Min(fortSave, refSave), willSave);
                int maxSave = Math.Max(Math.Max(fortSave, refSave), willSave);

                // 약한 세이브 공략 보너스
                if (saveValue == minSave)
                    score += 25f;  // 가장 약한 세이브 타겟팅
                else if (saveValue == maxSave)
                    score -= 15f;  // 가장 강한 세이브 피하기

                // 절대값 기반 추가 점수
                score += (15f - saveValue) * 1.5f;  // save +5 → +15점, save +20 → -7.5점

                // ═══════════════════════════════════════════════════════════════
                // 3. 적 역할 보너스 (캐스터/힐러 우선 CC)
                // ═══════════════════════════════════════════════════════════════
                float roleBonus = EvaluateEnemyRolePriority(target);
                if (isHardCC)
                    roleBonus *= 1.5f;  // Hard CC는 캐스터 제압 효과 극대화
                score += roleBonus;

                // ═══════════════════════════════════════════════════════════════
                // 4. HP 기반 가치 판단
                // ═══════════════════════════════════════════════════════════════
                float hp = GetHPPercent(target);
                if (hp > 80f)
                    score += 15f;  // 만피 적 = CC 가치 높음
                else if (hp > 50f)
                    score += 5f;
                else if (hp < 25f)
                    score -= 20f;  // 빈사 적 = 그냥 죽이는게 나음

                // ═══════════════════════════════════════════════════════════════
                // 5. 전투 페이즈별 조정
                // ═══════════════════════════════════════════════════════════════
                if (situation != null)
                {
                    switch (situation.CombatPhase)
                    {
                        case CombatPhase.Opening:
                            // 전투 초반: CC 매우 가치있음
                            if (isHardCC)
                                score += 20f;
                            else
                                score += 10f;
                            break;

                        case CombatPhase.Midgame:
                            // 중반: 보통
                            if (isHardCC)
                                score += 10f;
                            break;

                        case CombatPhase.Cleanup:
                            // 정리 단계: CC 가치 낮음 (그냥 죽이는게 나음)
                            score -= 15f;
                            break;

                        case CombatPhase.Desperate:
                            // 위기 상황: 생존 위한 CC 가치 높음
                            if (isHardCC)
                                score += 15f;
                            break;
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // 6. 위협도 기반 (아군을 공격 중인 적 우선)
                // ═══════════════════════════════════════════════════════════════
                if (situation?.Allies != null)
                {
                    int alliesEngaged = CountAlliesEngagedBy(target, situation);
                    if (alliesEngaged > 0)
                    {
                        score += alliesEngaged * 12f;  // 아군 교전 중인 적 우선 CC
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // 7. 거리 (사거리 내 우선)
                // ═══════════════════════════════════════════════════════════════
                if (situation?.Unit != null)
                {
                    float dist = GetDistance(situation.Unit, target);
                    if (dist > 15f)
                        score -= (dist - 15f) * 1f;  // 15m 이후 거리 패널티
                }

                Main.Verbose($"[TargetScorer] DebuffTarget {target.CharacterName}: save{saveType}={saveValue}(min={minSave}), HP={hp:F0}%, score={score:F1}");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] ScoreDebuffTarget error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// ★ v0.2.36: 마인드어페팅 면역 체크
        /// 언데드, 구조물, 엘프(Sleep 면역) 등
        /// </summary>
        private static bool IsMindAffectingImmune(UnitEntityData unit)
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
                        if (fName.Contains("immunemind") || fName.Contains("mindaffecting") ||
                            fName.Contains("immunityimmune"))
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
        /// v0.2.18: 세이브 값 조회
        /// </summary>
        private static int GetSaveValue(UnitEntityData unit, SavingThrowType saveType)
        {
            try
            {
                switch (saveType)
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
            catch { return 10; }
        }

        /// <summary>
        /// ★ v0.2.36: 향상된 최적 디버프-타겟 쌍 선택
        /// </summary>
        public static (UnitEntityData target, float score) SelectBestDebuffTarget(
            List<UnitEntityData> enemies, SavingThrowType saveType, Situation situation,
            bool isMindAffecting = false, bool isHardCC = false)
        {
            if (enemies == null || enemies.Count == 0)
                return (null, -1000f);

            UnitEntityData bestTarget = null;
            float bestScore = float.MinValue;

            foreach (var enemy in enemies)
            {
                if (enemy == null) continue;
                try { if (enemy.Descriptor?.State?.IsDead == true) continue; } catch { continue; }

                float score = ScoreDebuffTarget(enemy, saveType, situation, isMindAffecting, isHardCC);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = enemy;
                }
            }

            if (bestTarget != null)
            {
                Main.Log($"[TargetScorer] Best debuff target: {bestTarget.CharacterName} (save{saveType}, score={bestScore:F1})");
            }

            return (bestTarget, bestScore);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// HP 퍼센트 계산
        /// </summary>
        private static float GetHPPercent(UnitEntityData unit)
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

        /// <summary>
        /// 두 유닛 사이 거리 계산
        /// </summary>
        private static float GetDistance(UnitEntityData from, UnitEntityData to)
        {
            try
            {
                if (from == null || to == null) return float.MaxValue;
                return Vector3.Distance(from.Position, to.Position);
            }
            catch { return float.MaxValue; }
        }

        /// <summary>
        /// 위협도 평가 (0.0 ~ 1.0)
        /// 간소화: HP 기반 + 거리 기반
        /// </summary>
        private static float EvaluateThreat(UnitEntityData target, Situation situation)
        {
            float threat = 0f;

            try
            {
                // 1. HP 기반 - 만피일수록 위협적
                float hpPercent = GetHPPercent(target);
                float hpNormalized = hpPercent / 100f;  // 0~1 (0=빈사, 1=만피)
                threat += hpNormalized * 0.4f;

                // 2. 거리 기반 - 가까울수록 위협적
                float distance = GetDistance(situation.Unit, target);
                float maxRange = 30f;
                float proximityNormalized = 1f - Math.Min(1f, distance / maxRange);
                threat += proximityNormalized * 0.4f;

                // 3. 원거리 무기 보유 시 추가 위협
                if (HasRangedWeapon(target))
                    threat += 0.2f;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetScorer] EvaluateThreat error: {ex.Message}");
                return 0.5f;  // 폴백: 중간 위협도
            }

            return Math.Max(0f, Math.Min(1f, threat));
        }

        /// <summary>
        /// v0.2.18: 타겟 AC 값 조회
        /// </summary>
        private static int GetTargetAC(UnitEntityData target)
        {
            try
            {
                return target?.Stats?.AC?.ModifiedValue ?? 20;
            }
            catch { return 20; }
        }

        /// <summary>
        /// v0.2.18: 물리 공격자 여부 (근접 또는 원거리 무기 보유)
        /// </summary>
        private static bool IsPhysicalAttacker(UnitEntityData unit)
        {
            try
            {
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                return weapon != null; // 무기가 있으면 물리 공격자
            }
            catch { return false; }
        }

        /// <summary>
        /// v0.2.18: 적이 교전 중인 아군 수
        /// </summary>
        private static int CountAlliesEngagedBy(UnitEntityData enemy, Situation situation)
        {
            try
            {
                var engagedUnits = enemy?.CombatState?.EngagedUnits;
                if (engagedUnits == null || situation.Allies == null) return 0;

                int count = 0;
                foreach (var engaged in engagedUnits)
                {
                    if (engaged != null && situation.Allies.Contains(engaged))
                        count++;
                }
                return count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// v0.2.18: 적이 플랭킹 당하고 있는지 확인
        /// </summary>
        private static bool IsTargetFlanked(UnitEntityData target)
        {
            try
            {
                if (target?.Descriptor?.State?.Features?.CannotBeFlanked == true)
                    return false;
                return target?.CombatState?.IsFlanked ?? false;
            }
            catch { return false; }
        }

        /// <summary>
        /// v0.2.18: 스닉 어택 보유 여부
        /// </summary>
        private static bool HasSneakAttack(UnitEntityData unit)
        {
            try
            {
                var features = unit?.Descriptor?.Progression?.Features?.Enumerable;
                if (features == null) return false;
                foreach (var f in features)
                {
                    if (f.Blueprint?.name?.Contains("SneakAttack") == true)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 원거리 무기 소지 여부 확인
        /// </summary>
        private static bool HasRangedWeapon(UnitEntityData unit)
        {
            try
            {
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return false;
                return weapon.Blueprint?.IsMelee == false;
            }
            catch { return false; }
        }

        /// <summary>
        /// v0.2.17: 적 역할 우선순위 보너스
        /// 캐스터/힐러는 우선 처리 대상
        /// </summary>
        private static float EvaluateEnemyRolePriority(UnitEntityData enemy)
        {
            float bonus = 0f;

            try
            {
                if (enemy?.Descriptor == null) return 0f;

                // Spellbook 보유 = 캐스터 (힐러/마법사)
                var spellbooks = enemy.Descriptor.Spellbooks;
                bool isCaster = false;
                if (spellbooks != null)
                {
                    foreach (var sb in spellbooks)
                    {
                        if (sb.CasterLevel > 0)
                        {
                            isCaster = true;
                            break;
                        }
                    }
                }

                if (isCaster)
                {
                    bonus += 15f;  // 캐스터 우선 처리

                    // 힐링 능력 확인 - 힐러면 추가 보너스
                    try
                    {
                        foreach (var ability in enemy.Abilities.Enumerable)
                        {
                            var bp = ability.Data?.Blueprint;
                            if (bp == null) continue;

                            // 아군 타겟팅 가능 = 힐러/서포터일 가능성
                            if (bp.CanTargetFriends && !bp.CanTargetEnemies)
                            {
                                bonus += 10f;  // 힐러 최우선
                                break;
                            }
                        }
                    }
                    catch { }
                }

                // 원거리 적 = 위협적 (접근 전에 사격)
                if (HasRangedWeapon(enemy))
                    bonus += 5f;
            }
            catch { }

            return bonus;
        }

        #endregion
    }
}
