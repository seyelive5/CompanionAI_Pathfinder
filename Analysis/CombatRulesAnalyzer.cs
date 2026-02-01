// ★ v0.2.66: Combat Rules Analyzer
// ★ v0.2.67: Resistance/Immunity Analysis 추가
// ★ v0.2.68: Swift/Free Action AoO 면제 수정
// ★ v0.2.77: DR (Damage Reduction) 및 Energy Resistance 지원 추가
// Pathfinder 전투 규칙 분석 - AoO, 위협 범위, 방어적 시전, 저항/면역 등
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums.Damage;
using Kingmaker.Items.Slots;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Parts;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    #region Data Classes

    /// <summary>
    /// ★ v0.2.66: 유닛의 위협 상태 분석 결과
    /// </summary>
    public class ThreatAnalysis
    {
        /// <summary>적의 근접 위협 범위 내에 있는지</summary>
        public bool IsThreatenedByMelee { get; set; }

        /// <summary>위협하는 적 수</summary>
        public int ThreateningEnemyCount { get; set; }

        /// <summary>가장 가까운 위협 적</summary>
        public UnitEntityData NearestThreateningEnemy { get; set; }

        /// <summary>가장 가까운 위협 적까지 거리</summary>
        public float NearestThreatDistance { get; set; } = float.MaxValue;

        /// <summary>원거리 공격 시 AoO 유발 여부</summary>
        public bool RangedAttackProvokesAoO => IsThreatenedByMelee;

        /// <summary>주문 시전 시 AoO 유발 여부</summary>
        public bool SpellCastingProvokesAoO => IsThreatenedByMelee;
    }

    /// <summary>
    /// ★ v0.2.66: 능력 사용 시 규칙 분석 결과
    /// </summary>
    public class AbilityRulesAnalysis
    {
        /// <summary>이 능력이 AoO를 유발하는지</summary>
        public bool ProvokesAoO { get; set; }

        /// <summary>AoO 유발 사유</summary>
        public string AoOProvocationReason { get; set; } = "";

        /// <summary>방어적 시전 가능한지</summary>
        public bool CanCastDefensively { get; set; } = true;

        /// <summary>방어적 시전 DC</summary>
        public int DefensiveCastingDC { get; set; }

        /// <summary>예상 방어적 시전 성공률 (0~1)</summary>
        public float DefensiveCastingSuccessChance { get; set; }

        /// <summary>AoO 면역 피트/능력 보유</summary>
        public bool HasAoOImmunity { get; set; }

        /// <summary>AoO 면역 사유</summary>
        public string AoOImmunityReason { get; set; } = "";

        /// <summary>추천 행동</summary>
        public AbilityRecommendation Recommendation { get; set; } = AbilityRecommendation.Safe;

        /// <summary>페널티 점수 (0 = 페널티 없음, 음수 = 페널티)</summary>
        public float PenaltyScore { get; set; }
    }

    /// <summary>
    /// 능력 사용 추천
    /// </summary>
    public enum AbilityRecommendation
    {
        Safe,           // 안전하게 사용 가능
        Risky,          // AoO 유발 가능하지만 성공률 높음
        Dangerous,      // AoO 유발 가능성 높음
        Avoid           // 사용 자제 권장
    }

    /// <summary>
    /// ★ v0.2.67: 타겟의 저항/면역 분석 결과
    /// </summary>
    public class ResistanceImmunityAnalysis
    {
        /// <summary>타겟 유닛</summary>
        public UnitEntityData Target { get; set; }

        /// <summary>주문 저항 (SR) 값</summary>
        public int SpellResistance { get; set; }

        /// <summary>SR이 있는지</summary>
        public bool HasSpellResistance => SpellResistance > 0;

        /// <summary>주문 자체에 면역인지</summary>
        public bool IsSpellImmune { get; set; }

        /// <summary>면역 사유 목록</summary>
        public List<string> ImmunityReasons { get; } = new List<string>();

        /// <summary>에너지 면역 목록</summary>
        public List<DamageEnergyType> EnergyImmunities { get; } = new List<DamageEnergyType>();

        /// <summary>★ v0.2.77: 에너지 저항값 (면역이 아닌 경우)</summary>
        public Dictionary<DamageEnergyType, int> EnergyResistances { get; } = new Dictionary<DamageEnergyType, int>();

        /// <summary>★ v0.2.77: 에너지 저항으로 인한 데미지 효율 (1.0 = 저항 없음)</summary>
        public float EnergyDamageEfficiency { get; set; } = 1.0f;

        /// <summary>★ v0.2.77: 물리 DR 값</summary>
        public int PhysicalDR { get; set; }

        /// <summary>★ v0.2.77: DR 타입 (Magic, Cold Iron 등)</summary>
        public string DRType { get; set; } = "";

        /// <summary>★ v0.2.77: DR 우회 가능 여부</summary>
        public bool CanBypassDR { get; set; } = true;

        /// <summary>★ v0.2.77: DR로 인한 데미지 효율 (1.0 = DR 없음)</summary>
        public float PhysicalDamageEfficiency { get; set; } = 1.0f;

        /// <summary>상태이상 면역 목록</summary>
        public List<UnitCondition> ConditionImmunities { get; } = new List<UnitCondition>();

        /// <summary>SpellDescriptor 면역 (MindAffecting, Fear 등)</summary>
        public SpellDescriptor DescriptorImmunities { get; set; } = SpellDescriptor.None;

        /// <summary>예상 SR 통과 확률 (0~1)</summary>
        public float SRPenetrationChance { get; set; } = 1f;

        /// <summary>이 능력이 저항/면역으로 인해 무효화될 확률 (0~1)</summary>
        public float ImmunityChance { get; set; }

        /// <summary>추천 여부</summary>
        public ResistanceRecommendation Recommendation { get; set; } = ResistanceRecommendation.Effective;

        /// <summary>페널티 점수 (0 = 페널티 없음, 음수 = 페널티)</summary>
        public float PenaltyScore { get; set; }

        /// <summary>면역이라 완전히 비효율적인지</summary>
        public bool IsCompletelyIneffective =>
            IsSpellImmune || ImmunityChance >= 0.99f;

        /// <summary>효과적으로 사용 가능한지</summary>
        public bool IsEffective =>
            Recommendation == ResistanceRecommendation.Effective ||
            Recommendation == ResistanceRecommendation.PartiallyEffective;
    }

    /// <summary>
    /// 저항/면역 기반 추천
    /// </summary>
    public enum ResistanceRecommendation
    {
        Effective,           // 효과적
        PartiallyEffective,  // 부분적으로 효과적 (SR 있음)
        LikelyResisted,      // 저항될 가능성 높음
        Immune               // 면역
    }

    #endregion

    /// <summary>
    /// ★ v0.2.66: Pathfinder 전투 규칙 분석기
    /// - 위협 범위 분석
    /// - AoO 유발 여부 체크
    /// - 방어적 시전 DC/성공률 계산
    /// - 관련 피트/능력 캐싱
    /// </summary>
    public static class CombatRulesAnalyzer
    {
        #region Cache

        /// <summary>유닛별 위협 분석 캐시 (프레임당 1회)</summary>
        private static readonly Dictionary<string, (ThreatAnalysis analysis, int frame)> _threatCache
            = new Dictionary<string, (ThreatAnalysis, int)>();

        /// <summary>유닛별 AoO 면역 피트 캐시</summary>
        private static readonly Dictionary<string, AoOImmunityInfo> _aoOImmunityCache
            = new Dictionary<string, AoOImmunityInfo>();

        /// <summary>★ v0.2.67: 유닛별 기본 면역 정보 캐시</summary>
        private static readonly Dictionary<string, BaseImmunityInfo> _baseImmunityCache
            = new Dictionary<string, BaseImmunityInfo>();

        /// <summary>캐시 클리어</summary>
        public static void ClearCache()
        {
            _threatCache.Clear();
            _aoOImmunityCache.Clear();
            _baseImmunityCache.Clear();
            Main.Verbose("[CombatRulesAnalyzer] Cache cleared");
        }

        #endregion

        #region Threat Analysis

        /// <summary>
        /// 유닛의 위협 상태 분석 (캐시 사용)
        /// </summary>
        public static ThreatAnalysis AnalyzeThreat(UnitEntityData unit)
        {
            if (unit == null)
                return new ThreatAnalysis();

            string unitId = unit.UniqueId;
            int currentFrame = Time.frameCount;

            // 프레임 내 캐시 확인
            if (_threatCache.TryGetValue(unitId, out var cached) && cached.frame == currentFrame)
                return cached.analysis;

            var analysis = AnalyzeThreatInternal(unit);
            _threatCache[unitId] = (analysis, currentFrame);

            return analysis;
        }

        private static ThreatAnalysis AnalyzeThreatInternal(UnitEntityData unit)
        {
            var result = new ThreatAnalysis();

            try
            {
                // 게임 API 사용: CombatState.IsEngaged
                var combatState = unit.CombatState;
                if (combatState == null)
                    return result;

                // EngagedBy = 이 유닛을 위협하는 적들
                var engagedBy = combatState.EngagedBy;
                if (engagedBy == null || !engagedBy.Any())
                    return result;

                result.IsThreatenedByMelee = true;
                result.ThreateningEnemyCount = engagedBy.Count();

                // 가장 가까운 위협 적 찾기
                Vector3 unitPos = unit.Position;
                foreach (var enemy in engagedBy)
                {
                    if (enemy == null || enemy.Descriptor?.State?.IsDead == true)
                        continue;

                    float dist = Vector3.Distance(unitPos, enemy.Position);
                    if (dist < result.NearestThreatDistance)
                    {
                        result.NearestThreatDistance = dist;
                        result.NearestThreateningEnemy = enemy;
                    }
                }

                Main.Verbose($"[CombatRulesAnalyzer] {unit.CharacterName}: Threatened by {result.ThreateningEnemyCount} enemies, nearest={result.NearestThreatDistance:F1}m");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeThreat error: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region Ability Rules Analysis

        /// <summary>
        /// 능력 사용 시 규칙 분석
        /// </summary>
        public static AbilityRulesAnalysis AnalyzeAbility(UnitEntityData caster, AbilityData ability)
        {
            var result = new AbilityRulesAnalysis();

            if (caster == null || ability == null)
                return result;

            try
            {
                var threatAnalysis = AnalyzeThreat(caster);
                var immunityInfo = GetAoOImmunityInfo(caster);

                // 1. 이 능력이 AoO를 유발하는지
                result.ProvokesAoO = CheckAbilityProvokesAoO(ability, threatAnalysis, immunityInfo, result);

                // 2. 방어적 시전 분석 (주문인 경우)
                if (result.ProvokesAoO && ability.Blueprint?.IsSpell == true)
                {
                    AnalyzeDefensiveCasting(caster, ability, result);
                }

                // 3. AoO 면역 체크
                if (immunityInfo.HasAnyImmunity)
                {
                    result.HasAoOImmunity = CheckAoOImmunityForAbility(ability, immunityInfo);
                    if (result.HasAoOImmunity)
                    {
                        result.AoOImmunityReason = immunityInfo.GetImmunityReason(ability);
                        result.ProvokesAoO = false;
                    }
                }

                // 4. 추천 및 페널티 계산
                CalculateRecommendation(result, threatAnalysis);

                if (result.ProvokesAoO || result.PenaltyScore < 0)
                {
                    Main.Verbose($"[CombatRulesAnalyzer] {ability.Name}: ProvokesAoO={result.ProvokesAoO}, Penalty={result.PenaltyScore:F0}, Rec={result.Recommendation}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeAbility error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 능력이 AoO를 유발하는지 체크
        /// </summary>
        private static bool CheckAbilityProvokesAoO(
            AbilityData ability,
            ThreatAnalysis threat,
            AoOImmunityInfo immunity,
            AbilityRulesAnalysis result)
        {
            // 위협받지 않으면 AoO 없음
            if (!threat.IsThreatenedByMelee)
                return false;

            var bp = ability.Blueprint;
            if (bp == null)
                return false;

            // ★ v0.2.68: Swift/Free Action은 AoO 유발하지 않음
            // Pathfinder 규칙: Swift, Immediate, Free Action은 AoO를 유발하지 않음
            try
            {
                var actionType = bp.ActionType;
                if (actionType == Kingmaker.UnitLogic.Commands.Base.UnitCommand.CommandType.Swift ||
                    actionType == Kingmaker.UnitLogic.Commands.Base.UnitCommand.CommandType.Free)
                {
                    return false;
                }
            }
            catch { /* 무시 */ }

            // 1. 주문은 기본적으로 AoO 유발 (ProvokeAttackOfOpportunity 체크)
            if (bp.IsSpell)
            {
                // 게임 API: ability.ProvokeAttackOfOpportunity
                try
                {
                    if (ability.ProvokeAttackOfOpportunity)
                    {
                        result.AoOProvocationReason = "Spell casting while threatened";
                        return true;
                    }
                }
                catch
                {
                    // 프로퍼티 접근 실패 시 안전하게 true 반환
                    result.AoOProvocationReason = "Spell (assumed provokes)";
                    return true;
                }
            }

            // 2. 원거리 공격 체크
            var range = bp.Range;
            bool isRanged = range != AbilityRange.Touch &&
                           range != AbilityRange.Personal &&
                           range != AbilityRange.Weapon;

            // Weapon 범위지만 원거리 무기 사용인 경우
            if (range == AbilityRange.Weapon)
            {
                var weapon = ability.Caster?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon?.Blueprint?.IsRanged == true)
                {
                    isRanged = true;
                }
            }

            if (isRanged && !immunity.HasPointBlankMaster)
            {
                result.AoOProvocationReason = "Ranged attack while threatened";
                return true;
            }

            return false;
        }

        /// <summary>
        /// 방어적 시전 분석
        /// </summary>
        private static void AnalyzeDefensiveCasting(
            UnitEntityData caster,
            AbilityData ability,
            AbilityRulesAnalysis result)
        {
            try
            {
                // DC = 15 + (SpellLevel × 2)
                int spellLevel = ability.SpellLevel;
                result.DefensiveCastingDC = 15 + spellLevel * 2;

                // Concentration 보너스 가져오기
                int concentration = GetConcentrationBonus(caster, ability);

                // 성공 확률 계산: (21 - (DC - concentration)) / 20
                // d20 + concentration >= DC 이면 성공
                // 필요한 최소 굴림 = DC - concentration
                int minRoll = result.DefensiveCastingDC - concentration;
                minRoll = Mathf.Clamp(minRoll, 1, 20);

                // 성공 확률 = (21 - minRoll) / 20
                result.DefensiveCastingSuccessChance = Mathf.Clamp01((21f - minRoll) / 20f);

                result.CanCastDefensively = minRoll <= 20; // 자연 20도 실패면 불가능

                Main.Verbose($"[CombatRulesAnalyzer] Defensive casting: DC={result.DefensiveCastingDC}, Conc={concentration}, MinRoll={minRoll}, Success={result.DefensiveCastingSuccessChance:P0}");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeDefensiveCasting error: {ex.Message}");
                result.DefensiveCastingSuccessChance = 0.5f; // 기본값
            }
        }

        /// <summary>
        /// Concentration 보너스 가져오기
        /// </summary>
        private static int GetConcentrationBonus(UnitEntityData caster, AbilityData ability)
        {
            try
            {
                // RuleCalculateAbilityParams를 통해 Concentration 가져오기
                var paramsRule = new Kingmaker.RuleSystem.Rules.Abilities.RuleCalculateAbilityParams(caster, ability);
                Kingmaker.RuleSystem.Rulebook.Trigger(paramsRule);
                return paramsRule.Result?.Concentration ?? 0;
            }
            catch
            {
                // 폴백: 시전자 레벨 + 능력치 수정치 추정
                try
                {
                    int casterLevel = caster.Progression?.CharacterLevel ?? 1;
                    int abilityMod = 0;

                    // 주 시전 능력치 추정
                    var stats = caster.Stats;
                    if (stats != null)
                    {
                        abilityMod = Mathf.Max(
                            stats.Intelligence?.Bonus ?? 0,
                            stats.Wisdom?.Bonus ?? 0,
                            stats.Charisma?.Bonus ?? 0
                        );
                    }

                    return casterLevel + abilityMod;
                }
                catch
                {
                    return 5; // 최소 추정값
                }
            }
        }

        #endregion

        #region AoO Immunity

        /// <summary>
        /// AoO 면역 정보 클래스
        /// </summary>
        public class AoOImmunityInfo
        {
            public bool HasPointBlankMaster { get; set; }
            public bool HasCombatCasting { get; set; }
            public bool HasImprovedCombatCasting { get; set; }
            public bool HasSnapShot { get; set; }
            public bool HasMobilitySkill { get; set; }  // 5ft step 등
            public List<string> ImmunityFeats { get; } = new List<string>();

            public bool HasAnyImmunity =>
                HasPointBlankMaster || HasCombatCasting || HasImprovedCombatCasting || HasSnapShot;

            public string GetImmunityReason(AbilityData ability)
            {
                if (HasPointBlankMaster)
                    return "Point Blank Master";
                if (HasImprovedCombatCasting)
                    return "Improved Combat Casting";
                if (HasCombatCasting)
                    return "Combat Casting (+4 Concentration)";
                if (ImmunityFeats.Count > 0)
                    return string.Join(", ", ImmunityFeats);
                return "";
            }
        }

        /// <summary>
        /// 유닛의 AoO 면역 정보 가져오기 (캐싱)
        /// </summary>
        private static AoOImmunityInfo GetAoOImmunityInfo(UnitEntityData unit)
        {
            string unitId = unit.UniqueId;

            if (_aoOImmunityCache.TryGetValue(unitId, out var cached))
                return cached;

            var info = AnalyzeAoOImmunity(unit);
            _aoOImmunityCache[unitId] = info;

            return info;
        }

        /// <summary>
        /// AoO 면역 피트/능력 분석
        /// </summary>
        private static AoOImmunityInfo AnalyzeAoOImmunity(UnitEntityData unit)
        {
            var info = new AoOImmunityInfo();

            try
            {
                var features = unit.Descriptor?.State?.Features;
                if (features == null)
                    return info;

                // 게임 피처 체크
                // SnapShot은 원거리 무기로 위협 가능 (AoO 면역 아님)
                info.HasSnapShot = features.SnapShot;

                // Facts (피트/버프) 스캔
                var factsManager = unit.Descriptor?.Facts;
                if (factsManager != null)
                {
                    foreach (var fact in factsManager.List)
                    {
                        if (fact?.Blueprint == null) continue;
                        string name = fact.Blueprint.name ?? "";

                        // Point Blank Master 계열
                        if (name.Contains("PointBlankMaster"))
                        {
                            info.HasPointBlankMaster = true;
                            info.ImmunityFeats.Add("Point Blank Master");
                        }
                        // Combat Casting
                        else if (name.Contains("CombatCasting"))
                        {
                            if (name.Contains("Improved"))
                            {
                                info.HasImprovedCombatCasting = true;
                                info.ImmunityFeats.Add("Improved Combat Casting");
                            }
                            else
                            {
                                info.HasCombatCasting = true;
                                info.ImmunityFeats.Add("Combat Casting");
                            }
                        }
                        // Mobility 스킬
                        else if (name.Contains("Mobility") && name.Contains("Defensive"))
                        {
                            info.HasMobilitySkill = true;
                        }
                    }
                }

                if (info.HasAnyImmunity)
                {
                    Main.Verbose($"[CombatRulesAnalyzer] {unit.CharacterName} has AoO immunity: {string.Join(", ", info.ImmunityFeats)}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeAoOImmunity error: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// 특정 능력에 대해 AoO 면역 적용되는지 체크
        /// </summary>
        private static bool CheckAoOImmunityForAbility(AbilityData ability, AoOImmunityInfo immunity)
        {
            // Point Blank Master: 원거리 공격 AoO 면역
            if (immunity.HasPointBlankMaster)
            {
                var range = ability.Blueprint?.Range;
                if (range != AbilityRange.Touch && range != AbilityRange.Personal)
                    return true;
            }

            // Improved Combat Casting: 주문 AoO 면역
            if (immunity.HasImprovedCombatCasting && ability.Blueprint?.IsSpell == true)
                return true;

            return false;
        }

        #endregion

        #region Recommendation Calculation

        /// <summary>
        /// 추천 및 페널티 계산
        /// </summary>
        private static void CalculateRecommendation(AbilityRulesAnalysis result, ThreatAnalysis threat)
        {
            if (!result.ProvokesAoO)
            {
                result.Recommendation = AbilityRecommendation.Safe;
                result.PenaltyScore = 0;
                return;
            }

            // AoO 면역이면 안전
            if (result.HasAoOImmunity)
            {
                result.Recommendation = AbilityRecommendation.Safe;
                result.PenaltyScore = 0;
                return;
            }

            // 방어적 시전 성공률 기반 판단
            float successChance = result.DefensiveCastingSuccessChance;

            if (successChance >= 0.95f)
            {
                result.Recommendation = AbilityRecommendation.Safe;
                result.PenaltyScore = -5f;  // 약간의 리스크
            }
            else if (successChance >= 0.75f)
            {
                result.Recommendation = AbilityRecommendation.Risky;
                result.PenaltyScore = -15f;
            }
            else if (successChance >= 0.5f)
            {
                result.Recommendation = AbilityRecommendation.Dangerous;
                result.PenaltyScore = -30f;
            }
            else
            {
                result.Recommendation = AbilityRecommendation.Avoid;
                result.PenaltyScore = -50f;
            }

            // 위협하는 적 수에 따라 페널티 증가
            if (threat.ThreateningEnemyCount > 1)
            {
                result.PenaltyScore -= (threat.ThreateningEnemyCount - 1) * 10f;
            }

            // 가까운 적일수록 더 위험
            if (threat.NearestThreatDistance < 2f)
            {
                result.PenaltyScore -= 10f;
            }
        }

        #endregion

        #region Quick Checks

        /// <summary>
        /// 빠른 체크: 이 유닛이 현재 위협받고 있는가?
        /// </summary>
        public static bool IsThreatened(UnitEntityData unit)
        {
            return AnalyzeThreat(unit).IsThreatenedByMelee;
        }

        /// <summary>
        /// 빠른 체크: 이 능력을 사용해도 안전한가?
        /// </summary>
        public static bool IsSafeToUse(UnitEntityData caster, AbilityData ability)
        {
            var analysis = AnalyzeAbility(caster, ability);
            return analysis.Recommendation == AbilityRecommendation.Safe;
        }

        /// <summary>
        /// 빠른 체크: AoO 페널티 점수 반환 (능력 스코어링용)
        /// </summary>
        public static float GetAoOPenalty(UnitEntityData caster, AbilityData ability)
        {
            var analysis = AnalyzeAbility(caster, ability);
            return analysis.PenaltyScore;
        }

        #endregion

        #region ★ v0.2.67: Resistance/Immunity Analysis

        /// <summary>
        /// 유닛의 기본 면역 정보 클래스
        /// </summary>
        public class BaseImmunityInfo
        {
            public List<DamageEnergyType> EnergyImmunities { get; } = new List<DamageEnergyType>();
            public List<UnitCondition> ConditionImmunities { get; } = new List<UnitCondition>();
            public SpellDescriptor DescriptorImmunities { get; set; } = SpellDescriptor.None;
            public bool IsMindAffectingImmune { get; set; }
            public bool IsFearImmune { get; set; }
            public bool IsDeathEffectImmune { get; set; }
            public bool IsPoisonImmune { get; set; }
            public bool IsDiseaseImmune { get; set; }
            public bool IsParalysisImmune { get; set; }
            public bool IsStunImmune { get; set; }
            public bool IsSleepImmune { get; set; }
        }

        /// <summary>
        /// 타겟에 대한 능력의 저항/면역 분석 (메인 API)
        /// </summary>
        public static ResistanceImmunityAnalysis AnalyzeResistance(
            UnitEntityData caster,
            UnitEntityData target,
            AbilityData ability)
        {
            var result = new ResistanceImmunityAnalysis { Target = target };

            if (caster == null || target == null || ability == null)
                return result;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null)
                    return result;

                // 1. SR 체크
                AnalyzeSpellResistance(caster, target, ability, result);

                // 2. 에너지/원소 면역 체크
                AnalyzeEnergyImmunity(target, ability, result);

                // 3. 상태이상/SpellDescriptor 면역 체크
                AnalyzeDescriptorImmunity(target, ability, result);

                // 4. 최종 추천 및 페널티 계산
                CalculateResistanceRecommendation(result);

                if (result.PenaltyScore < 0 || result.HasSpellResistance)
                {
                    Main.Verbose($"[CombatRulesAnalyzer] {ability.Name} vs {target.CharacterName}: " +
                        $"SR={result.SpellResistance}, Immune={result.IsSpellImmune}, " +
                        $"Rec={result.Recommendation}, Penalty={result.PenaltyScore:F0}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeResistance error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 주문 저항 (SR) 분석
        /// </summary>
        private static void AnalyzeSpellResistance(
            UnitEntityData caster,
            UnitEntityData target,
            AbilityData ability,
            ResistanceImmunityAnalysis result)
        {
            try
            {
                // SR 적용 대상 주문인지 확인
                if (ability.Blueprint?.SpellResistance != true)
                    return;

                var srPart = target.Get<UnitPartSpellResistance>();
                if (srPart == null)
                    return;

                // SR 값 가져오기
                result.SpellResistance = srPart.GetValue(ability.Blueprint, caster);

                // 주문 면역 체크
                if (srPart.IsImmune(ability.Blueprint, caster))
                {
                    result.IsSpellImmune = true;
                    result.ImmunityReasons.Add("Spell Immunity");
                    result.ImmunityChance = 1f;
                    return;
                }

                // SR 통과 확률 계산
                if (result.SpellResistance > 0)
                {
                    // CasterLevel + d20 >= SR
                    int casterLevel = GetCasterLevel(caster, ability);

                    // 필요한 최소 굴림 = SR - CasterLevel
                    int minRoll = result.SpellResistance - casterLevel;
                    minRoll = Mathf.Clamp(minRoll, 1, 21);  // 21이면 불가능

                    // 성공 확률 = (21 - minRoll) / 20
                    result.SRPenetrationChance = Mathf.Clamp01((21f - minRoll) / 20f);

                    if (result.SRPenetrationChance < 1f)
                    {
                        result.ImmunityReasons.Add($"SR {result.SpellResistance}");
                        result.ImmunityChance = 1f - result.SRPenetrationChance;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeSpellResistance error: {ex.Message}");
            }
        }

        /// <summary>
        /// 에너지/원소 면역 분석
        /// ★ v0.2.77: Energy Resistance 값 추적 추가
        /// </summary>
        private static void AnalyzeEnergyImmunity(
            UnitEntityData target,
            AbilityData ability,
            ResistanceImmunityAnalysis result)
        {
            try
            {
                var drPart = target.Get<UnitPartDamageReduction>();
                if (drPart == null)
                    return;

                // 능력의 데미지 타입 추출
                var damageTypes = GetAbilityEnergyTypes(ability);
                float totalEfficiency = 1.0f;
                float estimatedDamage = 30f;  // 기본 추정 데미지

                foreach (var energyType in damageTypes)
                {
                    // 1. 면역 체크
                    if (drPart.IsImmune(energyType))
                    {
                        result.EnergyImmunities.Add(energyType);
                        result.ImmunityReasons.Add($"{energyType} Immunity");
                        totalEfficiency = 0f;  // 면역이면 효율 0
                        continue;
                    }

                    // ★ v0.2.77: 저항값 계산 (면역이 아닌 경우)
                    var energyResult = HitChanceCalculator.CalculateEnergyResistance(target, energyType, estimatedDamage);
                    if (energyResult.ResistanceValue > 0)
                    {
                        result.EnergyResistances[energyType] = energyResult.ResistanceValue;

                        // 효율 업데이트 (여러 에너지 타입 중 최악의 경우 사용)
                        if (energyResult.DamageEfficiency < totalEfficiency)
                        {
                            totalEfficiency = energyResult.DamageEfficiency;
                        }

                        // 저항이 높으면 사유 추가
                        if (energyResult.DamageEfficiency < 0.5f)
                        {
                            result.ImmunityReasons.Add($"{energyType} Resist {energyResult.ResistanceValue}");
                        }
                    }
                }

                // 모든 데미지 타입에 면역이면 완전 면역
                if (damageTypes.Count > 0 && damageTypes.All(t => result.EnergyImmunities.Contains(t)))
                {
                    result.ImmunityChance = 1f;
                    totalEfficiency = 0f;
                }

                result.EnergyDamageEfficiency = totalEfficiency;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeEnergyImmunity error: {ex.Message}");
            }
        }

        /// <summary>
        /// SpellDescriptor 기반 면역 분석 (MindAffecting, Fear, Paralysis 등)
        /// </summary>
        private static void AnalyzeDescriptorImmunity(
            UnitEntityData target,
            AbilityData ability,
            ResistanceImmunityAnalysis result)
        {
            try
            {
                var baseImmunity = GetBaseImmunityInfo(target);
                var abilityDescriptor = ability.Blueprint?.SpellDescriptor ?? SpellDescriptor.None;

                // MindAffecting 면역 체크
                if ((abilityDescriptor & SpellDescriptor.MindAffecting) != 0 && baseImmunity.IsMindAffectingImmune)
                {
                    result.ImmunityReasons.Add("Mind-Affecting Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.MindAffecting;
                }

                // Fear 면역 체크
                if ((abilityDescriptor & (SpellDescriptor.Fear | SpellDescriptor.Frightened | SpellDescriptor.Shaken)) != 0
                    && baseImmunity.IsFearImmune)
                {
                    result.ImmunityReasons.Add("Fear Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Fear;
                }

                // Death Effect 면역 체크
                if ((abilityDescriptor & SpellDescriptor.Death) != 0 && baseImmunity.IsDeathEffectImmune)
                {
                    result.ImmunityReasons.Add("Death Effect Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Death;
                }

                // Poison 면역 체크
                if ((abilityDescriptor & SpellDescriptor.Poison) != 0 && baseImmunity.IsPoisonImmune)
                {
                    result.ImmunityReasons.Add("Poison Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Poison;
                }

                // Disease 면역 체크
                if ((abilityDescriptor & SpellDescriptor.Disease) != 0 && baseImmunity.IsDiseaseImmune)
                {
                    result.ImmunityReasons.Add("Disease Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Disease;
                }

                // Paralysis 면역 체크
                if ((abilityDescriptor & SpellDescriptor.Paralysis) != 0 && baseImmunity.IsParalysisImmune)
                {
                    result.ImmunityReasons.Add("Paralysis Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Paralysis;
                }

                // Stun 면역 체크
                if ((abilityDescriptor & SpellDescriptor.Stun) != 0 && baseImmunity.IsStunImmune)
                {
                    result.ImmunityReasons.Add("Stun Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Stun;
                }

                // Sleep 면역 체크
                if ((abilityDescriptor & SpellDescriptor.Sleep) != 0 && baseImmunity.IsSleepImmune)
                {
                    result.ImmunityReasons.Add("Sleep Immunity");
                    result.ImmunityChance = 1f;
                    result.DescriptorImmunities |= SpellDescriptor.Sleep;
                }

                // 조건 면역 목록 복사
                result.ConditionImmunities.AddRange(baseImmunity.ConditionImmunities);
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeDescriptorImmunity error: {ex.Message}");
            }
        }

        /// <summary>
        /// 유닛의 기본 면역 정보 가져오기 (캐싱)
        /// </summary>
        private static BaseImmunityInfo GetBaseImmunityInfo(UnitEntityData unit)
        {
            string unitId = unit.UniqueId;

            if (_baseImmunityCache.TryGetValue(unitId, out var cached))
                return cached;

            var info = AnalyzeBaseImmunity(unit);
            _baseImmunityCache[unitId] = info;

            return info;
        }

        /// <summary>
        /// 유닛의 기본 면역 정보 분석
        /// </summary>
        private static BaseImmunityInfo AnalyzeBaseImmunity(UnitEntityData unit)
        {
            var info = new BaseImmunityInfo();

            try
            {
                var state = unit.Descriptor?.State;
                if (state == null)
                    return info;

                // 조건 면역 체크
                var conditionsToCheck = new[]
                {
                    UnitCondition.Frightened,
                    UnitCondition.Shaken,
                    UnitCondition.Stunned,
                    UnitCondition.Paralyzed,
                    UnitCondition.Sleeping,
                    UnitCondition.Dazed,
                    UnitCondition.Confusion,
                    UnitCondition.Nauseated
                };

                foreach (var condition in conditionsToCheck)
                {
                    if (state.HasConditionImmunity(condition))
                    {
                        info.ConditionImmunities.Add(condition);
                    }
                }

                // 특정 면역 플래그 설정
                info.IsFearImmune = state.HasConditionImmunity(UnitCondition.Frightened) ||
                                   state.HasConditionImmunity(UnitCondition.Shaken);
                info.IsStunImmune = state.HasConditionImmunity(UnitCondition.Stunned);
                info.IsParalysisImmune = state.HasConditionImmunity(UnitCondition.Paralyzed);
                info.IsSleepImmune = state.HasConditionImmunity(UnitCondition.Sleeping);

                // SpellDescriptor 면역 체크 (SR Part에서)
                var srPart = unit.Get<UnitPartSpellResistance>();
                if (srPart?.Immunities != null)
                {
                    foreach (var immunity in srPart.Immunities)
                    {
                        info.DescriptorImmunities |= immunity.SpellDescriptor;

                        // SpellDescriptor로 면역 플래그 업데이트
                        if ((immunity.SpellDescriptor & SpellDescriptor.MindAffecting) != 0)
                            info.IsMindAffectingImmune = true;
                        if ((immunity.SpellDescriptor & SpellDescriptor.Fear) != 0)
                            info.IsFearImmune = true;
                        if ((immunity.SpellDescriptor & SpellDescriptor.Death) != 0)
                            info.IsDeathEffectImmune = true;
                        if ((immunity.SpellDescriptor & SpellDescriptor.Poison) != 0)
                            info.IsPoisonImmune = true;
                        if ((immunity.SpellDescriptor & SpellDescriptor.Disease) != 0)
                            info.IsDiseaseImmune = true;
                    }
                }

                // 에너지 면역 체크
                var drPart = unit.Get<UnitPartDamageReduction>();
                if (drPart != null)
                {
                    foreach (DamageEnergyType energyType in Enum.GetValues(typeof(DamageEnergyType)))
                    {
                        if (drPart.IsImmune(energyType))
                        {
                            info.EnergyImmunities.Add(energyType);
                        }
                    }
                }

                if (info.ConditionImmunities.Count > 0 || info.EnergyImmunities.Count > 0 ||
                    info.IsMindAffectingImmune || info.IsFearImmune)
                {
                    Main.Verbose($"[CombatRulesAnalyzer] {unit.CharacterName} immunities: " +
                        $"Conditions={string.Join(",", info.ConditionImmunities)}, " +
                        $"Energy={string.Join(",", info.EnergyImmunities)}, " +
                        $"MindAffecting={info.IsMindAffectingImmune}, Fear={info.IsFearImmune}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] AnalyzeBaseImmunity error: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// 추천 및 페널티 계산
        /// ★ v0.2.77: Energy Resistance 효율 반영
        /// </summary>
        private static void CalculateResistanceRecommendation(ResistanceImmunityAnalysis result)
        {
            float totalPenalty = 0f;

            // 완전 면역
            if (result.IsSpellImmune || result.ImmunityChance >= 0.99f)
            {
                result.Recommendation = ResistanceRecommendation.Immune;
                result.PenaltyScore = -100f;  // 사용 금지 수준
                return;
            }

            // SR 기반 판단
            if (result.HasSpellResistance)
            {
                if (result.SRPenetrationChance >= 0.8f)
                {
                    totalPenalty += -5f;  // 약간의 불확실성
                }
                else if (result.SRPenetrationChance >= 0.5f)
                {
                    totalPenalty += -20f;
                }
                else if (result.SRPenetrationChance >= 0.25f)
                {
                    totalPenalty += -40f;
                }
                else
                {
                    totalPenalty += -60f;
                }
            }

            // ★ v0.2.77: 에너지 저항 효율 반영
            if (result.EnergyDamageEfficiency < 1.0f)
            {
                // 효율이 낮을수록 페널티 증가
                // 0.5 효율 = -25 페널티, 0.2 효율 = -40 페널티
                float efficiencyPenalty = -50f * (1f - result.EnergyDamageEfficiency);
                totalPenalty += efficiencyPenalty;

                if (result.EnergyDamageEfficiency < 0.3f)
                {
                    // 30% 미만 효율이면 추천하지 않음
                    result.Recommendation = ResistanceRecommendation.LikelyResisted;
                    result.PenaltyScore = totalPenalty;
                    return;
                }
            }

            // 부분 면역 (일부 효과만)
            if (result.ImmunityChance > 0f && result.ImmunityChance < 0.99f)
            {
                totalPenalty += -15f * result.ImmunityChance;
            }

            // 최종 추천 결정
            if (totalPenalty < -60f)
            {
                result.Recommendation = ResistanceRecommendation.LikelyResisted;
            }
            else if (totalPenalty < -30f)
            {
                result.Recommendation = ResistanceRecommendation.PartiallyEffective;
            }
            else
            {
                result.Recommendation = ResistanceRecommendation.Effective;
            }

            result.PenaltyScore = totalPenalty;
        }

        /// <summary>
        /// 능력의 에너지 타입 추출
        /// </summary>
        private static List<DamageEnergyType> GetAbilityEnergyTypes(AbilityData ability)
        {
            var types = new List<DamageEnergyType>();

            try
            {
                var descriptor = ability.Blueprint?.SpellDescriptor ?? SpellDescriptor.None;

                // SpellDescriptor에서 에너지 타입 추출
                if ((descriptor & SpellDescriptor.Fire) != 0)
                    types.Add(DamageEnergyType.Fire);
                if ((descriptor & SpellDescriptor.Cold) != 0)
                    types.Add(DamageEnergyType.Cold);
                if ((descriptor & SpellDescriptor.Acid) != 0)
                    types.Add(DamageEnergyType.Acid);
                if ((descriptor & SpellDescriptor.Electricity) != 0)
                    types.Add(DamageEnergyType.Electricity);
                if ((descriptor & SpellDescriptor.Sonic) != 0)
                    types.Add(DamageEnergyType.Sonic);
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CombatRulesAnalyzer] GetAbilityEnergyTypes error: {ex.Message}");
            }

            return types;
        }

        /// <summary>
        /// 시전자 레벨 가져오기
        /// </summary>
        private static int GetCasterLevel(UnitEntityData caster, AbilityData ability)
        {
            try
            {
                // Spellbook이 있으면 그 레벨 사용
                if (ability.Spellbook != null)
                    return ability.Spellbook.CasterLevel;

                // 없으면 캐릭터 레벨 사용
                return caster.Progression?.CharacterLevel ?? 1;
            }
            catch
            {
                return caster.Progression?.CharacterLevel ?? 1;
            }
        }

        #region Quick Resistance Checks

        /// <summary>
        /// 빠른 체크: 타겟이 이 능력에 면역인지
        /// </summary>
        public static bool IsImmuneTo(UnitEntityData caster, UnitEntityData target, AbilityData ability)
        {
            var analysis = AnalyzeResistance(caster, target, ability);
            return analysis.IsCompletelyIneffective;
        }

        /// <summary>
        /// 빠른 체크: 저항/면역 페널티 점수 반환
        /// </summary>
        public static float GetResistancePenalty(UnitEntityData caster, UnitEntityData target, AbilityData ability)
        {
            var analysis = AnalyzeResistance(caster, target, ability);
            return analysis.PenaltyScore;
        }

        /// <summary>
        /// 빠른 체크: 타겟이 특정 에너지 타입에 면역인지
        /// </summary>
        public static bool IsEnergyImmune(UnitEntityData target, DamageEnergyType energyType)
        {
            try
            {
                var drPart = target?.Get<UnitPartDamageReduction>();
                return drPart?.IsImmune(energyType) ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 빠른 체크: 타겟이 MindAffecting에 면역인지
        /// </summary>
        public static bool IsMindAffectingImmune(UnitEntityData target)
        {
            return GetBaseImmunityInfo(target).IsMindAffectingImmune;
        }

        /// <summary>
        /// 빠른 체크: 타겟이 Fear에 면역인지
        /// </summary>
        public static bool IsFearImmune(UnitEntityData target)
        {
            return GetBaseImmunityInfo(target).IsFearImmune;
        }

        /// <summary>
        /// 빠른 체크: 타겟의 SR 값 가져오기
        /// </summary>
        public static int GetSpellResistance(UnitEntityData target, AbilityData ability, UnitEntityData caster)
        {
            try
            {
                var srPart = target?.Get<UnitPartSpellResistance>();
                if (srPart == null)
                    return 0;

                return srPart.GetValue(ability?.Blueprint, caster);
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #endregion
    }
}
