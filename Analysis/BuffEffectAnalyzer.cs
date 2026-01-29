// ★ v0.2.39: 버프 효과 분석 시스템
// BlueprintBuff의 Components를 파싱하여 실제 전투 가치 결정
using System;
using System.Collections.Generic;
using System.Reflection;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.EntitySystem.Stats;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    #region Enums

    /// <summary>
    /// ★ v0.2.39: 버프 효과 카테고리
    /// 버프가 제공하는 실제 전투 효과를 분류
    /// </summary>
    [Flags]
    public enum BuffEffectCategory
    {
        None = 0,

        // === 높은 전투 가치 (1.0) ===
        /// <summary>AC 보너스 (방어력)</summary>
        ACBonus = 1 << 0,
        /// <summary>공격 보너스 (명중률)</summary>
        AttackBonus = 1 << 1,
        /// <summary>데미지 보너스</summary>
        DamageBonus = 1 << 2,
        /// <summary>내성 보너스 (Will/Fort/Reflex)</summary>
        SaveBonus = 1 << 3,

        // === 좋은 전투 가치 (0.8) ===
        /// <summary>능력치 보너스 (STR/DEX/CON 등)</summary>
        StatBonus = 1 << 4,

        // === 중간 전투 가치 (0.6~0.7) ===
        /// <summary>임시 HP</summary>
        TempHP = 1 << 5,
        /// <summary>데미지 저항/면역</summary>
        DamageResistance = 1 << 6,
        /// <summary>주문 저항</summary>
        SpellResistance = 1 << 7,
        /// <summary>은폐/강화</summary>
        Concealment = 1 << 8,

        // === 낮은 전투 가치 (0.4) ===
        /// <summary>재생/빠른 치유</summary>
        FastHealing = 1 << 9,
        /// <summary>이동 속도 보너스</summary>
        SpeedBonus = 1 << 10,

        // === 최소 전투 가치 (0.1) ===
        /// <summary>스킬 보너스 (전투 무관)</summary>
        SkillBonus = 1 << 11,

        // === 유틸리티 (0.05) ===
        /// <summary>비전투 효과 (빛, 감지 등)</summary>
        Utility = 1 << 12,

        // === 복합 그룹 ===
        /// <summary>핵심 전투 효과</summary>
        CoreCombat = ACBonus | AttackBonus | DamageBonus | SaveBonus,
        /// <summary>방어 효과</summary>
        Defensive = ACBonus | DamageResistance | SpellResistance | Concealment | TempHP,
        /// <summary>공격 효과</summary>
        Offensive = AttackBonus | DamageBonus,
    }

    #endregion

    #region Analysis Result

    /// <summary>
    /// ★ v0.2.39: 버프 효과 분석 결과
    /// </summary>
    public class BuffEffectAnalysis
    {
        /// <summary>버프 GUID</summary>
        public string BuffGuid { get; set; } = "";

        /// <summary>버프 이름 (디버깅용)</summary>
        public string BuffName { get; set; } = "";

        /// <summary>검출된 효과 카테고리</summary>
        public BuffEffectCategory Categories { get; set; } = BuffEffectCategory.None;

        /// <summary>전투 가치 (0.05 ~ 1.0)</summary>
        public float CombatValue { get; set; } = 0.05f;

        /// <summary>검출된 컴포넌트 목록 (디버깅용)</summary>
        public List<string> DetectedComponents { get; } = new List<string>();

        /// <summary>핵심 전투 효과 보유 여부</summary>
        public bool HasCoreCombatEffect => (Categories & BuffEffectCategory.CoreCombat) != 0;

        /// <summary>방어 효과 보유 여부</summary>
        public bool HasDefensiveEffect => (Categories & BuffEffectCategory.Defensive) != 0;

        /// <summary>공격 효과 보유 여부</summary>
        public bool HasOffensiveEffect => (Categories & BuffEffectCategory.Offensive) != 0;

        /// <summary>유틸리티 전용 버프 여부</summary>
        public bool IsUtilityOnly => Categories == BuffEffectCategory.Utility ||
                                      Categories == BuffEffectCategory.SkillBonus ||
                                      Categories == BuffEffectCategory.None;

        public override string ToString()
        {
            return $"[{BuffName}] Combat={CombatValue:F2}, Categories={Categories}, " +
                   $"Components=[{string.Join(", ", DetectedComponents)}]";
        }
    }

    #endregion

    /// <summary>
    /// ★ v0.2.39: 버프 효과 분석기
    /// BlueprintBuff의 ComponentsArray를 파싱하여 실제 전투 가치 결정
    /// </summary>
    public static class BuffEffectAnalyzer
    {
        #region Cache

        /// <summary>GUID 기반 분석 결과 캐시</summary>
        private static readonly Dictionary<string, BuffEffectAnalysis> _cache
            = new Dictionary<string, BuffEffectAnalysis>();

        /// <summary>캐시 클리어</summary>
        public static void ClearCache()
        {
            _cache.Clear();
            Main.Log("[BuffEffectAnalyzer] Cache cleared");
        }

        #endregion

        #region Public API

        /// <summary>
        /// 버프 효과 분석 (캐시 사용)
        /// </summary>
        public static BuffEffectAnalysis Analyze(BlueprintBuff buff)
        {
            if (buff == null)
            {
                return new BuffEffectAnalysis
                {
                    BuffName = "null",
                    CombatValue = 0.05f,
                    Categories = BuffEffectCategory.Utility
                };
            }

            string guid = buff.AssetGuid.ToString();

            // 캐시 확인
            if (_cache.TryGetValue(guid, out var cached))
                return cached;

            // 캐시 미스: 새로 분석
            var result = AnalyzeInternal(buff);
            _cache[guid] = result;

            Main.Verbose($"[BuffEffectAnalyzer] {result}");

            return result;
        }

        /// <summary>
        /// 버프의 전투 가치만 빠르게 조회 (0.05 ~ 1.0)
        /// </summary>
        public static float GetCombatValue(BlueprintBuff buff)
        {
            return Analyze(buff).CombatValue;
        }

        #endregion

        #region Internal Analysis

        /// <summary>
        /// 실제 분석 수행
        /// </summary>
        private static BuffEffectAnalysis AnalyzeInternal(BlueprintBuff buff)
        {
            var result = new BuffEffectAnalysis
            {
                BuffGuid = buff.AssetGuid.ToString(),
                BuffName = buff.Name ?? buff.name ?? "Unknown",
                Categories = BuffEffectCategory.None,
                CombatValue = 0.05f  // 기본값: 유틸리티
            };

            try
            {
                // ComponentsArray 분석
                var components = buff.ComponentsArray;
                if (components == null || components.Length == 0)
                {
                    result.Categories = BuffEffectCategory.Utility;
                    result.DetectedComponents.Add("NoComponents");
                    return result;
                }

                float maxCombatValue = 0f;

                foreach (var component in components)
                {
                    if (component == null) continue;

                    float componentValue = AnalyzeComponent(component, result);
                    maxCombatValue = Mathf.Max(maxCombatValue, componentValue);
                }

                // 최종 전투 가치 결정
                if (maxCombatValue > 0f)
                {
                    result.CombatValue = maxCombatValue;
                }
                else
                {
                    // 인식된 컴포넌트가 없으면 유틸리티
                    result.Categories = BuffEffectCategory.Utility;
                    result.CombatValue = 0.05f;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[BuffEffectAnalyzer] Analyze error for {buff.Name}: {ex.Message}");
                result.Categories = BuffEffectCategory.Utility;
                result.CombatValue = 0.3f;  // 에러 시 중간값
            }

            return result;
        }

        /// <summary>
        /// 개별 컴포넌트 분석
        /// </summary>
        private static float AnalyzeComponent(BlueprintComponent component, BuffEffectAnalysis result)
        {
            if (component == null) return 0f;

            string typeName = component.GetType().Name;

            switch (typeName)
            {
                // ═══════════════════════════════════════════════════════════════
                // AC 관련 (전투 가치: 1.0)
                // ═══════════════════════════════════════════════════════════════
                case "AcAddAcBuff":
                case "ACBonusAgainstAttacks":
                case "ACBonusAgainstFactOwner":
                case "ACBonusAgainstWeaponCategory":
                    result.Categories |= BuffEffectCategory.ACBonus;
                    result.DetectedComponents.Add($"AC({typeName})");
                    return 1.0f;

                // ═══════════════════════════════════════════════════════════════
                // 공격 보너스 관련 (전투 가치: 1.0)
                // ═══════════════════════════════════════════════════════════════
                case "AddAttackBonus":
                case "AttackBonusAgainstFactOwner":
                case "AttackBonusAgainstAlignment":
                case "AttackBonusAgainstSize":
                    result.Categories |= BuffEffectCategory.AttackBonus;
                    result.DetectedComponents.Add($"Attack({typeName})");
                    return 1.0f;

                // ═══════════════════════════════════════════════════════════════
                // 데미지 보너스 관련 (전투 가치: 1.0)
                // ═══════════════════════════════════════════════════════════════
                case "AddOutgoingDamageBonus":
                case "AddCumulativeDamageBonus":
                case "AddBonusWeaponDamage":
                case "WeaponDamageBonus":
                case "AddAdditionalWeaponDamage":
                case "AddOutgoingPhysicalDamageProperty":
                    result.Categories |= BuffEffectCategory.DamageBonus;
                    result.DetectedComponents.Add($"Damage({typeName})");
                    return 1.0f;

                // ═══════════════════════════════════════════════════════════════
                // 스탯 보너스 - StatType에 따라 가치 다름
                // ═══════════════════════════════════════════════════════════════
                case "AddStatBonus":
                case "AddContextStatBonus":
                case "AddGenericStatBonus":
                case "AddStatBonusAbilityValue":
                case "AddStatBonusIfHasFactFeature":
                    return AnalyzeStatBonus(component, result);

                // ═══════════════════════════════════════════════════════════════
                // 데미지 저항/면역 (전투 가치: 0.6)
                // ═══════════════════════════════════════════════════════════════
                case "AddDamageResistancePhysical":
                case "AddDamageResistanceEnergy":
                case "AddDamageResistanceForce":
                case "AddDamageResistanceHardness":
                case "AddEnergyDamageImmunity":
                case "AddEnergyImmunity":
                case "BuffAllSavesBonus":
                case "BuffAllSkillsBonus":
                    result.Categories |= BuffEffectCategory.DamageResistance;
                    result.DetectedComponents.Add($"DR({typeName})");
                    return 0.6f;

                // ═══════════════════════════════════════════════════════════════
                // 주문 저항 (전투 가치: 0.6)
                // ═══════════════════════════════════════════════════════════════
                case "AddSpellResistance":
                case "SpellResistanceAgainstAlignment":
                    result.Categories |= BuffEffectCategory.SpellResistance;
                    result.DetectedComponents.Add($"SR({typeName})");
                    return 0.6f;

                // ═══════════════════════════════════════════════════════════════
                // 은폐/강화 (전투 가치: 0.6)
                // ═══════════════════════════════════════════════════════════════
                case "AddConcealment":
                case "AddFortification":
                case "SetAttackerMissChance":
                    result.Categories |= BuffEffectCategory.Concealment;
                    result.DetectedComponents.Add($"Conceal({typeName})");
                    return 0.6f;

                // ═══════════════════════════════════════════════════════════════
                // 임시 HP (전투 가치: 0.7)
                // ═══════════════════════════════════════════════════════════════
                case "TemporaryHitPointsConstitutionBased":
                case "TemporaryHitPointsEqualCasterLevel":
                case "TemporaryHitPointsFromAbilityValue":
                case "TemporaryHitPoints":
                case "AddTemporaryHP":
                    result.Categories |= BuffEffectCategory.TempHP;
                    result.DetectedComponents.Add($"TempHP({typeName})");
                    return 0.7f;

                // ═══════════════════════════════════════════════════════════════
                // 재생/빠른 치유 (전투 가치: 0.4)
                // ═══════════════════════════════════════════════════════════════
                case "AddEffectFastHealing":
                case "AddEffectRegeneration":
                    result.Categories |= BuffEffectCategory.FastHealing;
                    result.DetectedComponents.Add($"Regen({typeName})");
                    return 0.4f;

                // ═══════════════════════════════════════════════════════════════
                // 이동 속도 (전투 가치: 0.5)
                // ═══════════════════════════════════════════════════════════════
                case "AddSpeedModifier":
                case "BuffMovementSpeed":
                    result.Categories |= BuffEffectCategory.SpeedBonus;
                    result.DetectedComponents.Add($"Speed({typeName})");
                    return 0.5f;

                // ═══════════════════════════════════════════════════════════════
                // 면역 계열 (전투 가치: 0.6~0.7)
                // ═══════════════════════════════════════════════════════════════
                case "AddConditionImmunity":
                case "AddImmunityToCriticalHits":
                case "AddImmunityToPrecisionDamage":
                case "AddImmunityToAbilityScoreDamage":
                case "AddImmunityToEnergyDrain":
                case "SpellImmunityToSpellDescriptor":
                    result.Categories |= BuffEffectCategory.DamageResistance;
                    result.DetectedComponents.Add($"Immunity({typeName})");
                    return 0.7f;

                // ═══════════════════════════════════════════════════════════════
                // 특수 전투 효과 (전투 가치: 0.8)
                // ═══════════════════════════════════════════════════════════════
                case "AddMirrorImage":
                case "Displacement":
                case "Blink":
                    result.Categories |= BuffEffectCategory.Concealment;
                    result.DetectedComponents.Add($"Special({typeName})");
                    return 0.8f;

                // ═══════════════════════════════════════════════════════════════
                // 명시적 유틸리티 / 무시
                // ═══════════════════════════════════════════════════════════════
                case "SpellDescriptorComponent":
                case "RemoveWhenCombatEnded":
                case "NotDispelable":
                case "SetBuffOnsetDelay":
                case "SummonedUnitBuff":
                case "AddAreaEffect":
                    // 메타 컴포넌트 - 전투 가치 없음
                    return 0f;

                default:
                    // 인식되지 않은 컴포넌트 - 가치 부여 안함
                    return 0f;
            }
        }

        /// <summary>
        /// 스탯 보너스 컴포넌트 상세 분석
        /// StatType에 따라 전투 가치가 다름
        /// </summary>
        private static float AnalyzeStatBonus(BlueprintComponent component, BuffEffectAnalysis result)
        {
            try
            {
                // Reflection으로 Stat 필드 접근
                var type = component.GetType();

                // "Stat" 필드 찾기 (다양한 이름 가능)
                FieldInfo statField = type.GetField("Stat", BindingFlags.Public | BindingFlags.Instance)
                                    ?? type.GetField("m_Stat", BindingFlags.NonPublic | BindingFlags.Instance);

                if (statField == null)
                {
                    // Stat 필드가 없으면 중간값 반환
                    result.DetectedComponents.Add("Stat(Unknown)");
                    return 0.3f;
                }

                var statValue = statField.GetValue(component);
                if (statValue == null)
                {
                    result.DetectedComponents.Add("Stat(Null)");
                    return 0.3f;
                }

                // StatType enum을 문자열로 변환
                string statName = statValue.ToString();

                return ClassifyStatType(statName, result);
            }
            catch (Exception ex)
            {
                Main.Verbose($"[BuffEffectAnalyzer] StatBonus analysis error: {ex.Message}");
                result.DetectedComponents.Add("Stat(Error)");
                return 0.3f;
            }
        }

        /// <summary>
        /// StatType 분류 및 전투 가치 결정
        /// </summary>
        private static float ClassifyStatType(string statName, BuffEffectAnalysis result)
        {
            // ═══════════════════════════════════════════════════════════════
            // 전투 핵심 스탯 (1.0)
            // ═══════════════════════════════════════════════════════════════
            if (statName.Contains("AC") ||
                statName == "BaseAttackBonus" ||
                statName.Contains("AdditionalAttackBonus") ||
                statName.Contains("AdditionalDamage"))
            {
                result.Categories |= BuffEffectCategory.ACBonus | BuffEffectCategory.AttackBonus;
                result.DetectedComponents.Add($"Stat({statName})=Combat");
                return 1.0f;
            }

            // ═══════════════════════════════════════════════════════════════
            // 내성 (0.9)
            // ═══════════════════════════════════════════════════════════════
            if (statName.Contains("Save") ||
                statName == "SaveWill" ||
                statName == "SaveFortitude" ||
                statName == "SaveReflex")
            {
                result.Categories |= BuffEffectCategory.SaveBonus;
                result.DetectedComponents.Add($"Stat({statName})=Save");
                return 0.9f;
            }

            // ═══════════════════════════════════════════════════════════════
            // 물리 능력치 (0.8)
            // ═══════════════════════════════════════════════════════════════
            if (statName == "Strength" ||
                statName == "Dexterity" ||
                statName == "Constitution")
            {
                result.Categories |= BuffEffectCategory.StatBonus;
                result.DetectedComponents.Add($"Stat({statName})=Physical");
                return 0.8f;
            }

            // ═══════════════════════════════════════════════════════════════
            // 정신 능력치 (0.7)
            // ═══════════════════════════════════════════════════════════════
            if (statName == "Intelligence" ||
                statName == "Wisdom" ||
                statName == "Charisma")
            {
                result.Categories |= BuffEffectCategory.StatBonus;
                result.DetectedComponents.Add($"Stat({statName})=Mental");
                return 0.7f;
            }

            // ═══════════════════════════════════════════════════════════════
            // 이동 속도 (0.5)
            // ═══════════════════════════════════════════════════════════════
            if (statName == "Speed" || statName.Contains("Speed"))
            {
                result.Categories |= BuffEffectCategory.SpeedBonus;
                result.DetectedComponents.Add($"Stat({statName})=Speed");
                return 0.5f;
            }

            // ═══════════════════════════════════════════════════════════════
            // 스킬 (전투 무관: 0.1)
            // ═══════════════════════════════════════════════════════════════
            if (statName.Contains("Skill") ||
                statName.Contains("Check") ||
                IsSkillStat(statName))
            {
                result.Categories |= BuffEffectCategory.SkillBonus;
                result.DetectedComponents.Add($"Stat({statName})=Skill");
                return 0.1f;
            }

            // ═══════════════════════════════════════════════════════════════
            // 알 수 없는 스탯 (0.3)
            // ═══════════════════════════════════════════════════════════════
            result.DetectedComponents.Add($"Stat({statName})=Unknown");
            return 0.3f;
        }

        /// <summary>
        /// 스킬 스탯인지 확인
        /// </summary>
        private static bool IsSkillStat(string statName)
        {
            // 패스파인더의 스킬 목록
            string[] skills = new[]
            {
                "Mobility", "Athletics", "Perception", "Persuasion",
                "Stealth", "UseMagicDevice", "KnowledgeArcana", "KnowledgeWorld",
                "LoreNature", "LoreReligion", "Trickery", "CheckBluff",
                "CheckDiplomacy", "CheckIntimidate"
            };

            foreach (var skill in skills)
            {
                if (statName.Contains(skill))
                    return true;
            }

            return false;
        }

        #endregion
    }
}
