// ★ v0.2.51: TargetAnalyzer - 통합 타겟 분석 시스템
// 적/아군의 모든 전투 관련 정보를 한 곳에서 분석 및 캐싱
// 게임 API: UnitDescriptor, Stats, State, Buffs 등을 통합
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    #region Data Classes

    /// <summary>
    /// 타겟의 종합 분석 결과
    /// </summary>
    public class TargetAnalysis
    {
        // 기본 정보
        public string UnitId { get; set; }
        public string Name { get; set; }
        public bool IsEnemy { get; set; }

        // 생존 상태
        public float HPPercent { get; set; }
        public int CurrentHP { get; set; }
        public int MaxHP { get; set; }
        public bool IsDead { get; set; }
        public bool IsUnconscious { get; set; }

        // 전투 스탯
        public int AC { get; set; }
        public int TouchAC { get; set; }
        public int FlatFootedAC { get; set; }
        public int FortitudeSave { get; set; }
        public int ReflexSave { get; set; }
        public int WillSave { get; set; }
        public int WeakestSave { get; set; }  // 가장 낮은 세이브 값
        public SavingThrowType WeakestSaveType { get; set; }

        // 상태이상 (CC)
        public bool IsStunned { get; set; }
        public bool IsParalyzed { get; set; }
        public bool IsProne { get; set; }
        public bool IsBlind { get; set; }
        public bool IsFrightened { get; set; }
        public bool IsConfused { get; set; }
        public bool IsHeld { get; set; }
        public bool IsSleeping { get; set; }
        public bool HasAnyCC { get; set; }  // 어떤 CC든 걸려있음

        // 면역
        public bool IsMindAffectingImmune { get; set; }
        public bool IsCritImmune { get; set; }
        public bool IsSneakAttackImmune { get; set; }
        public bool IsDeathEffectImmune { get; set; }
        public bool IsPoisonImmune { get; set; }
        public bool IsDiseaseImmune { get; set; }
        public SpellDescriptor ImmunityFlags { get; set; }

        // 전투 상태
        public bool IsFlanked { get; set; }
        public bool CanBeFlanked { get; set; }
        public int EngagedEnemyCount { get; set; }
        public int EngagedAllyCount { get; set; }
        public bool HasRangedWeapon { get; set; }
        public bool IsCaster { get; set; }
        public bool IsHealer { get; set; }

        // 역할 추정
        public TargetRole EstimatedRole { get; set; }

        // 위협도 (0.0 ~ 1.0)
        public float ThreatLevel { get; set; }

        // 캐시 정보
        public float CacheTime { get; set; }
    }

    /// <summary>
    /// 타겟 역할 분류
    /// </summary>
    public enum TargetRole
    {
        Unknown,
        Tank,       // 높은 AC/HP
        Melee,      // 근접 딜러
        Ranged,     // 원거리 딜러
        Caster,     // 마법 사용자
        Healer,     // 힐러/서포터
        Minion      // 잡몹
    }

    #endregion

    /// <summary>
    /// ★ v0.2.51: 통합 타겟 분석 시스템
    ///
    /// 게임의 DecisionContext/TargetInfo와 유사한 역할을 하되,
    /// 우리 모드에 필요한 모든 정보를 캐싱하여 제공
    /// </summary>
    public static class TargetAnalyzer
    {
        #region Cache

        private static readonly Dictionary<string, TargetAnalysis> _analysisCache = new();
        private const float CACHE_DURATION = 2.0f;  // 2초 캐시 (전투 중 변화 반영)

        #endregion

        #region Public API

        /// <summary>
        /// 타겟의 종합 분석 결과 반환 (캐시됨)
        /// </summary>
        public static TargetAnalysis Analyze(UnitEntityData target, UnitEntityData viewer = null)
        {
            if (target == null)
                return null;

            string unitId = target.UniqueId;
            float currentTime = Time.time;

            // 캐시 확인
            if (_analysisCache.TryGetValue(unitId, out var cached))
            {
                if (currentTime - cached.CacheTime < CACHE_DURATION)
                    return cached;
            }

            // 새로 분석
            var analysis = PerformAnalysis(target, viewer);
            analysis.CacheTime = currentTime;
            _analysisCache[unitId] = analysis;

            return analysis;
        }

        /// <summary>
        /// 캐시 초기화 (전투 종료 시 등)
        /// </summary>
        public static void ClearCache()
        {
            _analysisCache.Clear();
            Main.Verbose("[TargetAnalyzer] Cache cleared");
        }

        /// <summary>
        /// 적의 가장 약한 세이브 타입 반환
        /// </summary>
        public static SavingThrowType GetWeakestSave(UnitEntityData target)
        {
            var analysis = Analyze(target);
            return analysis?.WeakestSaveType ?? SavingThrowType.Unknown;
        }

        /// <summary>
        /// 타겟이 특정 SpellDescriptor에 면역인지 빠른 체크
        /// </summary>
        public static bool IsImmuneTo(UnitEntityData target, SpellDescriptor descriptor)
        {
            var analysis = Analyze(target);
            if (analysis == null)
                return false;

            return (analysis.ImmunityFlags & descriptor) != 0;
        }

        /// <summary>
        /// CC 대상으로 적합한지 (이미 CC 상태가 아닌지)
        /// </summary>
        public static bool IsGoodCCTarget(UnitEntityData target)
        {
            var analysis = Analyze(target);
            if (analysis == null)
                return false;

            // 이미 CC 상태면 추가 CC 불필요
            if (analysis.HasAnyCC)
                return false;

            // 죽었거나 의식불명
            if (analysis.IsDead || analysis.IsUnconscious)
                return false;

            // HP가 너무 낮으면 그냥 죽이는게 나음
            if (analysis.HPPercent < 15f)
                return false;

            return true;
        }

        /// <summary>
        /// 우선 타겟인지 (캐스터/힐러)
        /// </summary>
        public static bool IsPriorityTarget(UnitEntityData target)
        {
            var analysis = Analyze(target);
            if (analysis == null)
                return false;

            return analysis.IsCaster || analysis.IsHealer;
        }

        /// <summary>
        /// 타겟 분석 요약 문자열 (디버깅용)
        /// </summary>
        public static string GetSummary(UnitEntityData target)
        {
            var a = Analyze(target);
            if (a == null)
                return "null";

            return $"{a.Name}: HP={a.HPPercent:F0}%, AC={a.AC}, " +
                   $"Saves(F/R/W)={a.FortitudeSave}/{a.ReflexSave}/{a.WillSave}, " +
                   $"WeakSave={a.WeakestSaveType}, Role={a.EstimatedRole}, " +
                   $"CC={a.HasAnyCC}, Threat={a.ThreatLevel:F2}";
        }

        #endregion

        #region Analysis Implementation

        private static TargetAnalysis PerformAnalysis(UnitEntityData target, UnitEntityData viewer)
        {
            var analysis = new TargetAnalysis
            {
                UnitId = target.UniqueId,
                Name = target.CharacterName ?? "Unknown"
            };

            try
            {
                var descriptor = target.Descriptor;
                if (descriptor == null)
                    return analysis;

                // 적/아군 판정
                if (viewer != null)
                {
                    analysis.IsEnemy = target.IsEnemy(viewer);
                }

                // ═══════════════════════════════════════════════════════════════
                // 1. 생존 상태
                // ═══════════════════════════════════════════════════════════════
                AnalyzeHealth(target, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 2. 전투 스탯
                // ═══════════════════════════════════════════════════════════════
                AnalyzeStats(target, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 3. 상태이상 (CC)
                // ═══════════════════════════════════════════════════════════════
                AnalyzeConditions(descriptor, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 4. 면역
                // ═══════════════════════════════════════════════════════════════
                AnalyzeImmunities(target, descriptor, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 5. 전투 상태
                // ═══════════════════════════════════════════════════════════════
                AnalyzeCombatState(target, descriptor, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 6. 역할 추정
                // ═══════════════════════════════════════════════════════════════
                EstimateRole(target, descriptor, analysis);

                // ═══════════════════════════════════════════════════════════════
                // 7. 위협도 계산
                // ═══════════════════════════════════════════════════════════════
                CalculateThreat(target, analysis);

                // ★ v0.2.52 FIX: GetSummary(target)는 Analyze()를 호출하므로 무한 재귀 발생!
                // 이미 계산된 analysis 객체를 직접 사용
                Main.Verbose($"[TargetAnalyzer] Analyzed {analysis.Name}: HP={analysis.HPPercent:F0}%, AC={analysis.AC}, " +
                    $"Saves(F/R/W)={analysis.FortitudeSave}/{analysis.ReflexSave}/{analysis.WillSave}, " +
                    $"WeakSave={analysis.WeakestSaveType}, Role={analysis.EstimatedRole}, Threat={analysis.ThreatLevel:F2}");
            }
            catch (Exception ex)
            {
                Main.Error($"[TargetAnalyzer] Error analyzing {target?.CharacterName}: {ex.Message}");
            }

            return analysis;
        }

        private static void AnalyzeHealth(UnitEntityData target, TargetAnalysis analysis)
        {
            try
            {
                var stats = target.Stats;
                if (stats?.HitPoints != null)
                {
                    analysis.CurrentHP = stats.HitPoints.ModifiedValue;
                    analysis.MaxHP = stats.HitPoints.BaseValue;
                    if (analysis.MaxHP > 0)
                    {
                        analysis.HPPercent = (float)analysis.CurrentHP / analysis.MaxHP * 100f;
                    }
                }

                var state = target.Descriptor?.State;
                if (state != null)
                {
                    analysis.IsDead = state.IsDead;
                    analysis.IsUnconscious = state.IsUnconscious;
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] AnalyzeHealth error: {ex.Message}");
            }
        }

        private static void AnalyzeStats(UnitEntityData target, TargetAnalysis analysis)
        {
            try
            {
                var stats = target.Stats;
                if (stats == null) return;

                // AC 관련
                analysis.AC = stats.AC?.ModifiedValue ?? 10;
                analysis.TouchAC = stats.AC?.Touch ?? 10;
                analysis.FlatFootedAC = stats.AC?.FlatFooted ?? 10;

                // Saves
                analysis.FortitudeSave = stats.SaveFortitude?.ModifiedValue ?? 0;
                analysis.ReflexSave = stats.SaveReflex?.ModifiedValue ?? 0;
                analysis.WillSave = stats.SaveWill?.ModifiedValue ?? 0;

                // 가장 약한 세이브 찾기
                int minSave = analysis.FortitudeSave;
                SavingThrowType minType = SavingThrowType.Fortitude;

                if (analysis.ReflexSave < minSave)
                {
                    minSave = analysis.ReflexSave;
                    minType = SavingThrowType.Reflex;
                }
                if (analysis.WillSave < minSave)
                {
                    minSave = analysis.WillSave;
                    minType = SavingThrowType.Will;
                }

                analysis.WeakestSave = minSave;
                analysis.WeakestSaveType = minType;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] AnalyzeStats error: {ex.Message}");
            }
        }

        private static void AnalyzeConditions(UnitDescriptor descriptor, TargetAnalysis analysis)
        {
            try
            {
                var state = descriptor?.State;
                if (state == null) return;

                analysis.IsStunned = state.HasCondition(UnitCondition.Stunned);
                analysis.IsParalyzed = state.HasCondition(UnitCondition.Paralyzed);
                analysis.IsProne = state.HasCondition(UnitCondition.Prone);
                analysis.IsBlind = state.HasCondition(UnitCondition.Blindness);
                analysis.IsFrightened = state.HasCondition(UnitCondition.Frightened);
                analysis.IsConfused = state.HasCondition(UnitCondition.Confusion);
                analysis.IsHeld = state.HasCondition(UnitCondition.MovementBan);
                analysis.IsSleeping = state.HasCondition(UnitCondition.Sleeping);

                analysis.HasAnyCC = analysis.IsStunned || analysis.IsParalyzed ||
                                    analysis.IsProne || analysis.IsBlind ||
                                    analysis.IsFrightened || analysis.IsConfused ||
                                    analysis.IsHeld || analysis.IsSleeping;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] AnalyzeConditions error: {ex.Message}");
            }
        }

        private static void AnalyzeImmunities(UnitEntityData target, UnitDescriptor descriptor, TargetAnalysis analysis)
        {
            try
            {
                // TargetImmunityChecker의 면역 플래그 사용
                analysis.ImmunityFlags = TargetImmunityChecker.GetTargetImmunityFlags(target);

                // 주요 면역 플래그 체크
                analysis.IsMindAffectingImmune =
                    (analysis.ImmunityFlags & SpellDescriptor.MindAffecting) != 0 ||
                    descriptor.IsUndead;

                analysis.IsDeathEffectImmune =
                    (analysis.ImmunityFlags & SpellDescriptor.Death) != 0 ||
                    descriptor.IsUndead;

                analysis.IsPoisonImmune =
                    (analysis.ImmunityFlags & SpellDescriptor.Poison) != 0 ||
                    descriptor.IsUndead;

                analysis.IsDiseaseImmune =
                    (analysis.ImmunityFlags & SpellDescriptor.Disease) != 0 ||
                    descriptor.IsUndead;

                // 크리티컬/스닉 면역 체크 (Facts 기반 - 컴포넌트 이름 검색)
                analysis.IsCritImmune = HasFactComponent(target, "ImmunityToCriticalHits");
                analysis.IsSneakAttackImmune = HasFactComponent(target, "ImmunityToPrecisionDamage");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] AnalyzeImmunities error for {target?.CharacterName}: {ex.Message}");
            }
        }

        private static void AnalyzeCombatState(UnitEntityData target, UnitDescriptor descriptor, TargetAnalysis analysis)
        {
            try
            {
                var combatState = target.CombatState;
                if (combatState != null)
                {
                    analysis.IsFlanked = combatState.IsFlanked;
                    analysis.EngagedEnemyCount = combatState.EngagedUnits?.Count ?? 0;
                }

                // 플랭킹 가능 여부
                var features = descriptor.State?.Features;
                analysis.CanBeFlanked = features?.CannotBeFlanked != true;

                // 무기 타입
                var weapon = target.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null)
                {
                    analysis.HasRangedWeapon = weapon.Blueprint?.IsMelee == false;
                }

                // 캐스터 여부
                var spellbooks = descriptor.Spellbooks;
                if (spellbooks != null)
                {
                    foreach (var sb in spellbooks)
                    {
                        if (sb.CasterLevel > 0)
                        {
                            analysis.IsCaster = true;
                            break;
                        }
                    }
                }

                // ★ v0.2.52: 힐러 여부 - 안전한 체크 (크래시 방지)
                if (analysis.IsCaster)
                {
                    try
                    {
                        var abilities = target?.Abilities;
                        if (abilities != null)
                        {
                            // ToList()로 복사하여 안전하게 순회
                            var abilityList = abilities.Enumerable?.ToList();
                            if (abilityList != null)
                            {
                                foreach (var ability in abilityList)
                                {
                                    if (ability == null) continue;
                                    var bp = ability.Data?.Blueprint;
                                    if (bp?.CanTargetFriends == true && bp?.CanTargetEnemies == false)
                                    {
                                        analysis.IsHealer = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Main.Verbose($"[TargetAnalyzer] IsHealer check failed for {target?.CharacterName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] AnalyzeCombatState error for {target?.CharacterName}: {ex.Message}");
            }
        }

        private static void EstimateRole(UnitEntityData target, UnitDescriptor descriptor, TargetAnalysis analysis)
        {
            try
            {
                // 힐러
                if (analysis.IsHealer)
                {
                    analysis.EstimatedRole = TargetRole.Healer;
                    return;
                }

                // 캐스터
                if (analysis.IsCaster && !analysis.HasRangedWeapon)
                {
                    analysis.EstimatedRole = TargetRole.Caster;
                    return;
                }

                // 높은 AC = 탱크
                if (analysis.AC >= 25)
                {
                    analysis.EstimatedRole = TargetRole.Tank;
                    return;
                }

                // 원거리 무기
                if (analysis.HasRangedWeapon)
                {
                    analysis.EstimatedRole = TargetRole.Ranged;
                    return;
                }

                // 근접 무기
                var weapon = target.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon?.Blueprint?.IsMelee == true)
                {
                    analysis.EstimatedRole = TargetRole.Melee;
                    return;
                }

                // 낮은 HP = 잡몹
                if (analysis.MaxHP < 30)
                {
                    analysis.EstimatedRole = TargetRole.Minion;
                    return;
                }

                analysis.EstimatedRole = TargetRole.Unknown;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] EstimateRole error for {target?.CharacterName}: {ex.Message}");
                analysis.EstimatedRole = TargetRole.Unknown;
            }
        }

        private static void CalculateThreat(UnitEntityData target, TargetAnalysis analysis)
        {
            try
            {
                float threat = 0f;

                // HP 기반 (살아있을수록 위협)
                threat += (analysis.HPPercent / 100f) * 0.3f;

                // 역할 기반
                switch (analysis.EstimatedRole)
                {
                    case TargetRole.Healer:
                        threat += 0.4f;  // 힐러 최우선
                        break;
                    case TargetRole.Caster:
                        threat += 0.35f;
                        break;
                    case TargetRole.Ranged:
                        threat += 0.25f;
                        break;
                    case TargetRole.Melee:
                    case TargetRole.Tank:
                        threat += 0.2f;
                        break;
                    case TargetRole.Minion:
                        threat += 0.1f;
                        break;
                }

                // CC 상태면 위협 감소
                if (analysis.HasAnyCC)
                    threat *= 0.3f;

                analysis.ThreatLevel = Mathf.Clamp01(threat);
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] CalculateThreat error for {target?.CharacterName}: {ex.Message}");
                analysis.ThreatLevel = 0.5f;
            }
        }

        #endregion

        #region Batch Analysis

        /// <summary>
        /// 여러 타겟 일괄 분석
        /// </summary>
        public static List<TargetAnalysis> AnalyzeMultiple(
            IEnumerable<UnitEntityData> targets,
            UnitEntityData viewer = null)
        {
            var results = new List<TargetAnalysis>();

            foreach (var target in targets)
            {
                if (target == null) continue;
                var analysis = Analyze(target, viewer);
                if (analysis != null)
                    results.Add(analysis);
            }

            return results;
        }

        /// <summary>
        /// 위협도 순 정렬된 적 목록
        /// </summary>
        public static List<TargetAnalysis> GetEnemiesByThreat(
            IEnumerable<UnitEntityData> enemies,
            UnitEntityData viewer = null)
        {
            var analyses = AnalyzeMultiple(enemies, viewer);
            analyses.Sort((a, b) => b.ThreatLevel.CompareTo(a.ThreatLevel));
            return analyses;
        }

        /// <summary>
        /// CC 적합 타겟 필터링 (면역 아니고, 이미 CC 아닌 적)
        /// </summary>
        public static List<TargetAnalysis> GetValidCCTargets(
            IEnumerable<UnitEntityData> enemies,
            SpellDescriptor ccDescriptor,
            UnitEntityData viewer = null)
        {
            var results = new List<TargetAnalysis>();

            foreach (var enemy in enemies)
            {
                if (enemy == null) continue;
                var analysis = Analyze(enemy, viewer);
                if (analysis == null) continue;

                // 이미 CC 상태
                if (analysis.HasAnyCC) continue;

                // 죽거나 의식불명
                if (analysis.IsDead || analysis.IsUnconscious) continue;

                // 해당 효과 면역
                if ((analysis.ImmunityFlags & ccDescriptor) != 0) continue;

                results.Add(analysis);
            }

            return results;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 유닛의 Facts에서 특정 컴포넌트 이름 검색
        /// 크리티컬/스닉 면역 등 컴포넌트 기반 면역 체크용
        /// </summary>
        private static bool HasFactComponent(UnitEntityData target, string componentNameContains)
        {
            try
            {
                var descriptor = target?.Descriptor;
                if (descriptor == null) return false;

                foreach (var fact in descriptor.Facts.List)
                {
                    if (fact?.Blueprint?.ComponentsArray == null) continue;

                    foreach (var component in fact.Blueprint.ComponentsArray)
                    {
                        string typeName = component?.GetType()?.Name ?? "";
                        if (typeName.Contains(componentNameContains))
                            return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TargetAnalyzer] HasFactComponent error for {target?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
