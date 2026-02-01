// ★ v0.2.72: Hit Chance Calculator - 실제 적중 확률 계산 시스템
// ★ v0.2.73: Weapon Finesse 지원, Save DC 계산 추가
// ★ v0.2.74: Spell Resistance (SR) 돌파 확률 계산 추가
// ★ v0.2.75: 연속 공격 페널티 (Iterative Attacks) 지원
// ★ v0.2.76: Spell Penetration 피트 감지
// ★ v0.2.77: DR (Damage Reduction) 및 Energy Resistance 체크
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;
using Kingmaker.Enums.Damage;
using Kingmaker.Items;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 공격 타입 (어떤 AC를 사용할지 결정)
    /// </summary>
    public enum AttackACType
    {
        Normal,         // 일반 AC
        Touch,          // Touch AC (터치 공격)
        FlatFooted      // Flat-Footed AC (기습/CC 상태)
    }

    /// <summary>
    /// 적중 확률 계산 결과
    /// </summary>
    public class HitChanceResult
    {
        /// <summary>적중 확률 (0.0 ~ 1.0)</summary>
        public float HitChance { get; set; }

        /// <summary>크리티컬 확률 (0.0 ~ 1.0)</summary>
        public float CriticalChance { get; set; }

        /// <summary>공격자의 공격 보너스</summary>
        public int AttackBonus { get; set; }

        /// <summary>타겟의 AC (사용된 AC 타입)</summary>
        public int TargetAC { get; set; }

        /// <summary>사용된 AC 타입</summary>
        public AttackACType ACType { get; set; }

        /// <summary>적중에 필요한 D20 굴림</summary>
        public int NeedToRoll { get; set; }

        /// <summary>플랭킹 적용 여부</summary>
        public bool IsFlanking { get; set; }

        /// <summary>디버그 문자열</summary>
        public override string ToString()
        {
            return $"Hit={HitChance:P0} (Atk+{AttackBonus} vs AC{TargetAC}, need {NeedToRoll}+){(IsFlanking ? " [Flanking]" : "")}";
        }
    }

    /// <summary>
    /// ★ v0.2.73: 세이브 DC 계산 결과 (스펠/능력용)
    /// </summary>
    public class SaveDCResult
    {
        /// <summary>스펠 DC</summary>
        public int SpellDC { get; set; }

        /// <summary>타겟의 세이브 보너스</summary>
        public int TargetSaveBonus { get; set; }

        /// <summary>세이브 타입 (Fort/Ref/Will)</summary>
        public SavingThrowType SaveType { get; set; }

        /// <summary>타겟이 세이브에 실패할 확률 (0.0 ~ 1.0)</summary>
        public float FailureChance { get; set; }

        /// <summary>타겟이 세이브에 성공할 확률</summary>
        public float SuccessChance => 1f - FailureChance;

        /// <summary>디버그 문자열</summary>
        public override string ToString()
        {
            return $"DC{SpellDC} vs {SaveType}+{TargetSaveBonus}, Fail={FailureChance:P0}";
        }
    }

    /// <summary>
    /// ★ v0.2.75: 풀 어택 (연속 공격) 결과
    /// </summary>
    public class FullAttackResult
    {
        /// <summary>총 공격 횟수</summary>
        public int TotalAttacks { get; set; }

        /// <summary>각 공격의 적중 확률 목록</summary>
        public List<float> AttackHitChances { get; set; } = new();

        /// <summary>각 공격의 페널티 목록 (0, -5, -10, -15)</summary>
        public List<int> AttackPenalties { get; set; } = new();

        /// <summary>평균 적중 확률</summary>
        public float AverageHitChance => AttackHitChances.Count > 0
            ? AttackHitChances.Sum() / AttackHitChances.Count : 0f;

        /// <summary>가중 평균 적중 확률 (첫 번째 공격에 더 높은 가중치)</summary>
        public float WeightedHitChance
        {
            get
            {
                if (AttackHitChances.Count == 0) return 0f;
                // 첫 번째 공격 50%, 나머지 50% 균등 분배
                float firstWeight = 0.5f;
                float otherWeight = AttackHitChances.Count > 1
                    ? 0.5f / (AttackHitChances.Count - 1) : 0f;

                float total = AttackHitChances[0] * firstWeight;
                for (int i = 1; i < AttackHitChances.Count; i++)
                    total += AttackHitChances[i] * otherWeight;
                return total;
            }
        }

        /// <summary>최소 1회 명중 확률</summary>
        public float AtLeastOneHitChance
        {
            get
            {
                // 1 - (모두 빗나갈 확률)
                float missAll = 1f;
                foreach (var hit in AttackHitChances)
                    missAll *= (1f - hit);
                return 1f - missAll;
            }
        }

        /// <summary>디버그 문자열</summary>
        public override string ToString()
        {
            if (TotalAttacks == 0) return "No attacks";
            var penalties = string.Join("/", AttackPenalties.Select(p => p == 0 ? "+0" : p.ToString()));
            var chances = string.Join("/", AttackHitChances.Select(c => $"{c:P0}"));
            return $"{TotalAttacks}x attacks [{penalties}] = {chances}, Avg={AverageHitChance:P0}";
        }
    }

    /// <summary>
    /// ★ v0.2.74: Spell Resistance 체크 결과
    /// </summary>
    public class SpellResistanceResult
    {
        /// <summary>타겟의 SR 값</summary>
        public int TargetSR { get; set; }

        /// <summary>시전자의 Spell Penetration (CL + 보너스)</summary>
        public int SpellPenetration { get; set; }

        /// <summary>SR 돌파 확률 (0.0 ~ 1.0)</summary>
        public float PenetrationChance { get; set; }

        /// <summary>스펠이 SR의 영향을 받는지</summary>
        public bool AllowsSR { get; set; }

        /// <summary>타겟이 스펠 면역인지</summary>
        public bool IsImmune { get; set; }

        /// <summary>디버그 문자열</summary>
        public override string ToString()
        {
            if (IsImmune) return "Spell IMMUNE";
            if (!AllowsSR) return "Ignores SR";
            if (TargetSR <= 0) return "No SR";
            return $"SR{TargetSR} vs CL{SpellPenetration}, Penetrate={PenetrationChance:P0}";
        }
    }

    /// <summary>
    /// ★ v0.2.77: Damage Reduction (물리 DR) 체크 결과
    /// </summary>
    public class DamageReductionResult
    {
        /// <summary>타겟의 DR 값</summary>
        public int DRValue { get; set; }

        /// <summary>DR 우회 가능 여부</summary>
        public bool CanBypass { get; set; }

        /// <summary>DR 타입 (Cold Iron, Silver, Adamantine 등)</summary>
        public string DRType { get; set; } = "";

        /// <summary>예상 데미지 감소량</summary>
        public float EstimatedReduction { get; set; }

        /// <summary>데미지 효율 (1.0 = DR 없음, 0.5 = 50% 데미지)</summary>
        public float DamageEfficiency { get; set; } = 1.0f;

        /// <summary>디버그 문자열</summary>
        public override string ToString()
        {
            if (DRValue <= 0) return "No DR";
            if (CanBypass) return $"DR{DRValue}/{DRType} (Bypassed)";
            return $"DR{DRValue}/{DRType}, Efficiency={DamageEfficiency:P0}";
        }
    }

    /// <summary>
    /// ★ v0.2.77: Energy Resistance 체크 결과
    /// </summary>
    public class EnergyResistanceResult
    {
        /// <summary>에너지 타입</summary>
        public DamageEnergyType EnergyType { get; set; }

        /// <summary>저항값 (데미지 감소량)</summary>
        public int ResistanceValue { get; set; }

        /// <summary>면역 여부</summary>
        public bool IsImmune { get; set; }

        /// <summary>예상 데미지 효율 (1.0 = 저항 없음)</summary>
        public float DamageEfficiency { get; set; } = 1.0f;

        /// <summary>디버그 문자열</summary>
        public override string ToString()
        {
            if (IsImmune) return $"{EnergyType} IMMUNE";
            if (ResistanceValue <= 0) return $"No {EnergyType} resistance";
            return $"{EnergyType} Resist {ResistanceValue}, Efficiency={DamageEfficiency:P0}";
        }
    }

    /// <summary>
    /// ★ v0.2.72: 적중 확률 계산기
    ///
    /// 게임의 RuleCalculateAttackBonus를 참조하여
    /// 공격 보너스와 적중 확률을 정확하게 계산
    /// </summary>
    public static class HitChanceCalculator
    {
        #region Cache

        private static readonly Dictionary<string, HitChanceResult> _cache = new();
        private const float CACHE_DURATION = 2.0f;
        private static float _lastCacheTime = 0f;

        #endregion

        #region Public API

        /// <summary>
        /// 무기 공격의 적중 확률 계산
        /// </summary>
        public static HitChanceResult CalculateWeaponHitChance(
            UnitEntityData attacker,
            UnitEntityData target,
            ItemEntityWeapon weapon = null,
            int attackPenalty = 0)
        {
            if (attacker == null || target == null)
                return CreateDefaultResult();

            // 무기 없으면 주무기 사용
            weapon ??= attacker.GetFirstWeapon();
            if (weapon == null)
                return CreateDefaultResult();

            string cacheKey = $"W_{attacker.UniqueId}_{target.UniqueId}_{weapon.UniqueId}_{attackPenalty}";
            if (TryGetCached(cacheKey, out var cached))
                return cached;

            var result = CalculateWeaponHitChanceInternal(attacker, target, weapon, attackPenalty);
            CacheResult(cacheKey, result);

            return result;
        }

        /// <summary>
        /// ★ v0.2.75: 풀 어택 (연속 공격) 적중 확률 계산
        /// BAB에 따라 추가 공격 횟수와 페널티 계산
        /// </summary>
        public static FullAttackResult CalculateFullAttackHitChance(
            UnitEntityData attacker,
            UnitEntityData target,
            ItemEntityWeapon weapon = null)
        {
            var result = new FullAttackResult();

            if (attacker == null || target == null)
                return result;

            weapon ??= attacker.GetFirstWeapon();
            if (weapon == null)
                return result;

            try
            {
                // 1. BAB 가져오기
                int bab = attacker.Stats.BaseAttackBonus?.ModifiedValue ?? 0;

                // 2. 연속 공격 횟수 계산 (게임 공식)
                // penalizedAttacks = Math.Max(0, BAB / 5 - ((BAB % 5 == 0) ? 1 : 0))
                // 최대 3회 추가 공격 (동료는 최대 3)
                int penalizedCount = Math.Max(0, bab / 5 - ((bab % 5 == 0) ? 1 : 0));
                penalizedCount = Math.Min(penalizedCount, 3);

                // 3. 첫 번째 공격 (페널티 없음)
                result.TotalAttacks = 1 + penalizedCount;
                result.AttackPenalties.Add(0);

                var firstHit = CalculateWeaponHitChance(attacker, target, weapon, 0);
                result.AttackHitChances.Add(firstHit.HitChance);

                // 4. 연속 공격들 (-5, -10, -15)
                for (int i = 1; i <= penalizedCount; i++)
                {
                    int penalty = i * 5;
                    result.AttackPenalties.Add(-penalty);

                    var iterativeHit = CalculateWeaponHitChance(attacker, target, weapon, penalty);
                    result.AttackHitChances.Add(iterativeHit.HitChance);
                }

                Main.Verbose($"[HitChance] FullAttack: {attacker.CharacterName} vs {target.CharacterName} (BAB {bab}) - {result}");
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateFullAttackHitChance error: {ex.Message}");
                result.TotalAttacks = 1;
                result.AttackPenalties.Add(0);
                result.AttackHitChances.Add(0.5f);
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.75: 연속 공격 횟수만 빠르게 계산
        /// </summary>
        public static int GetIterativeAttackCount(UnitEntityData attacker)
        {
            if (attacker == null) return 1;

            int bab = attacker.Stats.BaseAttackBonus?.ModifiedValue ?? 0;
            int penalizedCount = Math.Max(0, bab / 5 - ((bab % 5 == 0) ? 1 : 0));
            return 1 + Math.Min(penalizedCount, 3);
        }

        /// <summary>
        /// 능력(스펠) 공격의 적중 확률 계산
        /// </summary>
        public static HitChanceResult CalculateAbilityHitChance(
            UnitEntityData attacker,
            UnitEntityData target,
            AbilityData ability)
        {
            if (attacker == null || target == null || ability == null)
                return CreateDefaultResult();

            string cacheKey = $"A_{attacker.UniqueId}_{target.UniqueId}_{ability.Blueprint.AssetGuid}";
            if (TryGetCached(cacheKey, out var cached))
                return cached;

            var result = CalculateAbilityHitChanceInternal(attacker, target, ability);
            CacheResult(cacheKey, result);

            return result;
        }

        /// <summary>
        /// ★ v0.2.73: 세이브 기반 스펠의 성공 확률 계산
        /// </summary>
        public static SaveDCResult CalculateSaveDC(
            UnitEntityData caster,
            UnitEntityData target,
            AbilityData ability,
            SavingThrowType? overrideSaveType = null)
        {
            var result = new SaveDCResult();

            if (caster == null || target == null || ability == null)
            {
                result.FailureChance = 0.5f;
                return result;
            }

            try
            {
                // 1. 스펠 DC 계산
                // DC = 10 + SpellLevel + CastingStatBonus
                int spellLevel = ability.SpellLevel;
                int castingStatBonus = GetCastingStatBonus(caster, ability);
                result.SpellDC = 10 + spellLevel + castingStatBonus;

                // 2. 세이브 타입 결정
                result.SaveType = overrideSaveType ?? GetAbilitySaveType(ability);

                // 3. 타겟의 세이브 보너스 가져오기
                result.TargetSaveBonus = GetSaveBonus(target, result.SaveType);

                // 4. 실패 확률 계산
                // 실패 = D20 + SaveBonus < DC
                // 실패 = D20 < (DC - SaveBonus)
                int needToFail = result.SpellDC - result.TargetSaveBonus;
                result.FailureChance = CalculateSaveFailureProbability(needToFail);

                Main.Verbose($"[HitChance] SaveDC: {ability.Name} {caster.CharacterName} vs {target.CharacterName} - {result}");
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateSaveDC error: {ex.Message}");
                result.FailureChance = 0.5f;
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.73: 세이브 실패 확률 계산
        /// </summary>
        public static float CalculateSaveFailureProbability(int dcMinusSaveBonus)
        {
            // D20 < (DC - SaveBonus) 일 때 실패
            // 자연 1 = 항상 실패 (5%)
            // 자연 20 = 항상 성공 (5%)
            if (dcMinusSaveBonus <= 1)
                return 0.05f;  // DC가 너무 낮아 거의 성공
            if (dcMinusSaveBonus >= 21)
                return 0.95f;  // DC가 너무 높아 거의 실패

            // 실패 확률 = (DC - SaveBonus - 1) / 20
            // 예: DC 15, Save +5 → need 10 → 실패 확률 = 9/20 = 45%
            return (dcMinusSaveBonus - 1) / 20f;
        }

        /// <summary>
        /// ★ v0.2.74: Spell Resistance 돌파 확률 계산
        /// SR 체크: D20 + CasterLevel >= SR
        /// </summary>
        public static SpellResistanceResult CalculateSpellResistance(
            UnitEntityData caster,
            UnitEntityData target,
            AbilityData ability)
        {
            var result = new SpellResistanceResult();

            if (caster == null || target == null || ability == null)
                return result;

            try
            {
                // 1. 스펠이 SR의 영향을 받는지 확인
                result.AllowsSR = ability.Blueprint?.SpellResistance ?? false;

                if (!result.AllowsSR)
                {
                    result.PenetrationChance = 1.0f;  // SR 무시
                    return result;
                }

                // 2. 타겟의 SR Part 확인
                var srPart = target.Get<Kingmaker.UnitLogic.Parts.UnitPartSpellResistance>();

                // 3. 스펠 면역 체크
                if (srPart?.IsImmune(ability.Blueprint, caster) == true)
                {
                    result.IsImmune = true;
                    result.PenetrationChance = 0f;
                    Main.Verbose($"[HitChance] SR: {target.CharacterName} is IMMUNE to {ability.Name}");
                    return result;
                }

                // 4. SR 값 가져오기
                result.TargetSR = srPart?.GetValue(ability.Blueprint, caster) ?? 0;

                if (result.TargetSR <= 0)
                {
                    result.PenetrationChance = 1.0f;  // SR 없음
                    return result;
                }

                // 5. Caster Level (Spell Penetration) 계산
                // CL = Spellbook CL 또는 캐릭터 레벨
                int casterLevel = ability.Spellbook?.CasterLevel ?? caster.Progression?.CharacterLevel ?? 1;

                // ★ v0.2.76: Spell Penetration 피트 보너스 감지
                int spellPenBonus = GetSpellPenetrationBonus(caster);

                result.SpellPenetration = casterLevel + spellPenBonus;

                // 6. 돌파 확률 계산
                // D20 + CL >= SR → 성공
                // 필요 굴림 = SR - CL
                int needToRoll = result.TargetSR - result.SpellPenetration;
                result.PenetrationChance = CalculateHitProbability(result.SpellPenetration, result.TargetSR);

                Main.Verbose($"[HitChance] SR: {ability.Name} {caster.CharacterName} vs {target.CharacterName} - {result}");
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateSpellResistance error: {ex.Message}");
                result.PenetrationChance = 0.8f;  // 폴백
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.74: 스펠의 종합 성공 확률 (SR + Save)
        /// </summary>
        public static float CalculateOverallSpellSuccess(
            UnitEntityData caster,
            UnitEntityData target,
            AbilityData ability,
            SavingThrowType? saveType = null)
        {
            // 1. SR 돌파 확률
            var srResult = CalculateSpellResistance(caster, target, ability);
            if (srResult.IsImmune)
                return 0f;

            float srChance = srResult.PenetrationChance;

            // 2. 세이브 실패 확률 (세이브가 있는 경우)
            float saveFailChance = 1.0f;  // 세이브 없으면 100% 적용
            if (saveType.HasValue || ability.Blueprint.SpellDescriptor != Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.None)
            {
                var saveDCResult = CalculateSaveDC(caster, target, ability, saveType);
                saveFailChance = saveDCResult.FailureChance;
            }

            // 종합 확률 = SR 돌파 * 세이브 실패
            return srChance * saveFailChance;
        }

        /// <summary>
        /// ★ v0.2.73: 시전자의 캐스팅 스탯 보너스 가져오기
        /// </summary>
        public static int GetCastingStatBonus(UnitEntityData caster, AbilityData ability)
        {
            if (caster == null)
                return 0;

            try
            {
                var stats = caster.Stats;

                // Spellbook이 있으면 그 스탯 사용
                if (ability?.Spellbook != null)
                {
                    var spellbook = ability.Spellbook;
                    var castingStat = spellbook.Blueprint?.CastingAttribute ?? StatType.Intelligence;
                    return stats.GetStat<ModifiableValueAttributeStat>(castingStat)?.Bonus ?? 0;
                }

                // 클래스별 기본 캐스팅 스탯 추정
                // INT: Wizard, Magus, Alchemist
                // WIS: Cleric, Druid, Inquisitor, Monk
                // CHA: Sorcerer, Bard, Oracle, Paladin
                int intBonus = stats.Intelligence?.Bonus ?? 0;
                int wisBonus = stats.Wisdom?.Bonus ?? 0;
                int chaBonus = stats.Charisma?.Bonus ?? 0;

                // 가장 높은 것 사용 (폴백)
                return Math.Max(intBonus, Math.Max(wisBonus, chaBonus));
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// ★ v0.2.73: 능력의 세이브 타입 결정
        /// </summary>
        public static SavingThrowType GetAbilitySaveType(AbilityData ability)
        {
            if (ability?.Blueprint == null)
                return SavingThrowType.Will;

            try
            {
                // 능력의 저장형 컴포넌트에서 세이브 타입 추출
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        // ContextActionSavingThrow에서 세이브 타입 확인
                        var actionType = action?.GetType();
                        if (actionType?.Name?.Contains("SavingThrow") == true)
                        {
                            // 리플렉션으로 Type 속성 접근 시도
                            var propInfo = actionType.GetProperty("Type");
                            var fieldInfo = actionType.GetField("Type");

                            object value = null;
                            if (propInfo != null)
                                value = propInfo.GetValue(action);
                            else if (fieldInfo != null)
                                value = fieldInfo.GetValue(action);

                            if (value is SavingThrowType saveType)
                                return saveType;
                        }
                    }
                }

                // SpellDescriptor 기반 추정
                var descriptor = ability.Blueprint.SpellDescriptor;

                // 정신 영향 → Will
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.MindAffecting) != 0)
                    return SavingThrowType.Will;

                // Fear → Will
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Fear) != 0)
                    return SavingThrowType.Will;

                // Poison, Disease → Fortitude
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Poison) != 0 ||
                    (descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Disease) != 0)
                    return SavingThrowType.Fortitude;

                // 범위 데미지 → Reflex (보통)
                if (ability.Blueprint.GetComponent<AbilityTargetsAround>() != null)
                    return SavingThrowType.Reflex;

                // 기본값: Will
                return SavingThrowType.Will;
            }
            catch
            {
                return SavingThrowType.Will;
            }
        }

        /// <summary>
        /// ★ v0.2.73: 타겟의 세이브 보너스 가져오기
        /// </summary>
        public static int GetSaveBonus(UnitEntityData target, SavingThrowType saveType)
        {
            if (target == null)
                return 0;

            try
            {
                var stats = target.Stats;
                switch (saveType)
                {
                    case SavingThrowType.Fortitude:
                        return stats.SaveFortitude?.ModifiedValue ?? 0;
                    case SavingThrowType.Reflex:
                        return stats.SaveReflex?.ModifiedValue ?? 0;
                    case SavingThrowType.Will:
                        return stats.SaveWill?.ModifiedValue ?? 0;
                    default:
                        return 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// ★ v0.2.76: Spell Penetration 피트 보너스 계산
        /// 피트별 보너스:
        /// - Spell Penetration: +2
        /// - Greater Spell Penetration: +2 (총 +4)
        /// - Mythic Spell Penetration: +MythicLevel/2 (Greater 있으면 +MythicLevel)
        /// </summary>
        public static int GetSpellPenetrationBonus(UnitEntityData caster)
        {
            if (caster == null)
                return 0;

            int bonus = 0;

            try
            {
                // 방법 1: SpellPenetrationBonus 컴포넌트로 피트 감지
                // 유닛의 모든 Facts에서 SpellPenetrationBonus 컴포넌트 찾기
                var features = caster.Descriptor?.Progression?.Features;
                if (features != null)
                {
                    foreach (var feature in features)
                    {
                        if (feature?.Blueprint == null)
                            continue;

                        // 피트 이름 기반 감지 (폴백)
                        string bpName = feature.Blueprint.name?.ToLowerInvariant() ?? "";

                        // Spell Penetration (+2)
                        if (bpName.Contains("spellpenetration") && !bpName.Contains("greater") && !bpName.Contains("mythic"))
                        {
                            bonus = Math.Max(bonus, 2);
                        }
                        // Greater Spell Penetration (+4 총합)
                        else if (bpName.Contains("greaterspellpenetration"))
                        {
                            bonus = Math.Max(bonus, 4);
                        }
                        // Mythic Spell Penetration
                        else if (bpName.Contains("mythicspellpenetration") || bpName.Contains("spellpenetrationmythic"))
                        {
                            int mythicLevel = caster.Progression?.MythicLevel ?? 0;
                            // Greater 있으면 전체 Mythic Level, 없으면 절반
                            int mythicBonus = (bonus >= 4) ? mythicLevel : (mythicLevel / 2);
                            bonus += mythicBonus;
                        }
                    }
                }

                // 방법 2: 버프에서도 Spell Penetration 보너스 확인 (아이템, 일시적 효과)
                var buffs = caster.Buffs;
                if (buffs != null)
                {
                    foreach (var buff in buffs)
                    {
                        if (buff?.Blueprint == null)
                            continue;

                        string buffName = buff.Blueprint.name?.ToLowerInvariant() ?? "";
                        if (buffName.Contains("spellpenetration"))
                        {
                            // 버프에서 추가 보너스 (보통 +1~+2)
                            bonus += 2;
                            break; // 중복 방지
                        }
                    }
                }

                if (bonus > 0)
                {
                    Main.Verbose($"[HitChance] SpellPen: {caster.CharacterName} has +{bonus} spell penetration bonus");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] GetSpellPenetrationBonus error: {ex.Message}");
            }

            return bonus;
        }

        /// <summary>
        /// ★ v0.2.77: 타겟의 물리 Damage Reduction 확인
        /// DR/- (모든 물리), DR/Magic, DR/Cold Iron 등
        /// </summary>
        public static DamageReductionResult CalculateDamageReduction(
            UnitEntityData attacker,
            UnitEntityData target,
            ItemEntityWeapon weapon = null,
            float estimatedDamage = 20f)
        {
            var result = new DamageReductionResult();

            if (target == null)
                return result;

            try
            {
                var drPart = target.Get<UnitPartDamageReduction>();
                if (drPart == null)
                {
                    result.DamageEfficiency = 1.0f;
                    return result;
                }

                // 무기 정보 가져오기
                weapon ??= attacker?.GetFirstWeapon();

                // 모든 DR 소스에서 가장 높은 DR 찾기
                int highestDR = 0;
                string drType = "-";
                bool canBypass = true;

                foreach (var drSource in drPart.AllSources)
                {
                    if (drSource?.Settings == null)
                        continue;

                    int currentDR = drSource.GetCurrentValue();
                    if (currentDR <= 0)
                        continue;

                    // 물리 DR인지 확인
                    var physicalDR = drSource.Settings as AddDamageResistancePhysical;
                    if (physicalDR != null)
                    {
                        // DR 우회 가능 여부 확인
                        // (무기의 재질, 마법 보너스, 정렬 등)
                        bool bypassed = false;

                        if (weapon != null)
                        {
                            // 마법 무기 (+1 이상) → DR/Magic 우회
                            if (physicalDR.BypassedByMagic && weapon.EnchantmentValue >= physicalDR.MinEnhancementBonus)
                            {
                                bypassed = true;
                            }

                            // TODO: 재질, 정렬 등 추가 체크 가능
                            // Cold Iron, Silver, Adamantine, Good/Evil 등
                        }

                        if (!bypassed && currentDR > highestDR)
                        {
                            highestDR = currentDR;
                            drType = GetDRTypeString(physicalDR);
                            canBypass = false;
                        }
                    }
                }

                result.DRValue = highestDR;
                result.DRType = drType;
                result.CanBypass = canBypass;
                result.EstimatedReduction = canBypass ? 0 : Math.Min(highestDR, estimatedDamage);

                // 데미지 효율 계산
                if (estimatedDamage > 0 && !canBypass)
                {
                    float afterDR = Math.Max(0, estimatedDamage - highestDR);
                    result.DamageEfficiency = afterDR / estimatedDamage;
                }
                else
                {
                    result.DamageEfficiency = 1.0f;
                }

                if (highestDR > 0)
                {
                    Main.Verbose($"[HitChance] DR: {target.CharacterName} has {result}");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateDamageReduction error: {ex.Message}");
                result.DamageEfficiency = 0.8f;
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.77: 타겟의 Energy Resistance 확인
        /// Fire, Cold, Acid, Electricity, Sonic 등
        /// </summary>
        public static EnergyResistanceResult CalculateEnergyResistance(
            UnitEntityData target,
            DamageEnergyType energyType,
            float estimatedDamage = 30f)
        {
            var result = new EnergyResistanceResult
            {
                EnergyType = energyType
            };

            if (target == null)
                return result;

            try
            {
                var drPart = target.Get<UnitPartDamageReduction>();
                if (drPart == null)
                {
                    result.DamageEfficiency = 1.0f;
                    return result;
                }

                // 1. 면역 체크
                if (drPart.IsImmune(energyType))
                {
                    result.IsImmune = true;
                    result.DamageEfficiency = 0f;
                    Main.Verbose($"[HitChance] Energy: {target.CharacterName} is IMMUNE to {energyType}");
                    return result;
                }

                // 2. 저항값 찾기
                int highestResistance = 0;

                foreach (var drSource in drPart.AllSources)
                {
                    if (drSource?.Settings == null)
                        continue;

                    var energyResist = drSource.Settings as AddDamageResistanceEnergy;
                    if (energyResist != null && energyResist.Type == energyType)
                    {
                        int currentValue = drSource.GetCurrentValue();
                        if (currentValue > highestResistance)
                        {
                            highestResistance = currentValue;
                        }
                    }
                }

                result.ResistanceValue = highestResistance;

                // 3. 데미지 효율 계산
                if (estimatedDamage > 0 && highestResistance > 0)
                {
                    float afterResist = Math.Max(0, estimatedDamage - highestResistance);
                    result.DamageEfficiency = afterResist / estimatedDamage;
                }
                else
                {
                    result.DamageEfficiency = 1.0f;
                }

                if (highestResistance > 0)
                {
                    Main.Verbose($"[HitChance] Energy: {target.CharacterName} has {result}");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateEnergyResistance error: {ex.Message}");
                result.DamageEfficiency = 0.8f;
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.77: 스펠의 에너지 타입 감지
        /// </summary>
        public static DamageEnergyType? GetAbilityEnergyType(AbilityData ability)
        {
            if (ability?.Blueprint == null)
                return null;

            try
            {
                var descriptor = ability.Blueprint.SpellDescriptor;

                // SpellDescriptor에서 에너지 타입 추출
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Fire) != 0)
                    return DamageEnergyType.Fire;
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Cold) != 0)
                    return DamageEnergyType.Cold;
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Acid) != 0)
                    return DamageEnergyType.Acid;
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Electricity) != 0)
                    return DamageEnergyType.Electricity;
                if ((descriptor & Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Sonic) != 0)
                    return DamageEnergyType.Sonic;

                // 이름 기반 폴백
                string name = ability.Blueprint.name?.ToLowerInvariant() ?? "";
                if (name.Contains("fire") || name.Contains("flame") || name.Contains("burn"))
                    return DamageEnergyType.Fire;
                if (name.Contains("cold") || name.Contains("ice") || name.Contains("frost"))
                    return DamageEnergyType.Cold;
                if (name.Contains("acid"))
                    return DamageEnergyType.Acid;
                if (name.Contains("electric") || name.Contains("lightning") || name.Contains("shock"))
                    return DamageEnergyType.Electricity;
                if (name.Contains("sonic") || name.Contains("thunder"))
                    return DamageEnergyType.Sonic;

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ★ v0.2.77: 스펠의 종합 데미지 효율 (SR + Save + Energy Resist)
        /// </summary>
        public static float CalculateOverallDamageEfficiency(
            UnitEntityData caster,
            UnitEntityData target,
            AbilityData ability,
            float estimatedDamage = 30f)
        {
            // 1. SR 돌파 확률
            var srResult = CalculateSpellResistance(caster, target, ability);
            if (srResult.IsImmune)
                return 0f;

            float efficiency = srResult.PenetrationChance;

            // 2. 에너지 저항/면역 체크
            var energyType = GetAbilityEnergyType(ability);
            if (energyType.HasValue)
            {
                var energyResult = CalculateEnergyResistance(target, energyType.Value, estimatedDamage);
                if (energyResult.IsImmune)
                    return 0f;

                efficiency *= energyResult.DamageEfficiency;
            }

            // 3. 세이브 확률 반영 (세이브 성공 시 절반 데미지)
            var saveType = GetAbilitySaveType(ability);
            var saveResult = CalculateSaveDC(caster, target, ability, saveType);

            // 세이브 실패 = 풀 데미지, 세이브 성공 = 절반 데미지 (대부분의 경우)
            float saveMultiplier = saveResult.FailureChance + (saveResult.SuccessChance * 0.5f);
            efficiency *= saveMultiplier;

            return efficiency;
        }

        /// <summary>
        /// ★ v0.2.77: DR 타입 문자열 생성
        /// </summary>
        private static string GetDRTypeString(AddDamageResistancePhysical drSettings)
        {
            if (drSettings == null)
                return "-";

            var types = new List<string>();

            if (drSettings.BypassedByMagic)
                types.Add("Magic");
            if (drSettings.BypassedByMaterial)
            {
                if ((drSettings.Material & PhysicalDamageMaterial.ColdIron) != 0)
                    types.Add("Cold Iron");
                if ((drSettings.Material & PhysicalDamageMaterial.Silver) != 0)
                    types.Add("Silver");
                if ((drSettings.Material & PhysicalDamageMaterial.Adamantite) != 0)
                    types.Add("Adamantine");
            }
            if (drSettings.BypassedByAlignment)
            {
                if ((drSettings.Alignment & DamageAlignment.Good) != 0)
                    types.Add("Good");
                if ((drSettings.Alignment & DamageAlignment.Evil) != 0)
                    types.Add("Evil");
                if ((drSettings.Alignment & DamageAlignment.Lawful) != 0)
                    types.Add("Lawful");
                if ((drSettings.Alignment & DamageAlignment.Chaotic) != 0)
                    types.Add("Chaotic");
            }
            if (drSettings.BypassedByEpic)
                types.Add("Epic");

            return types.Count > 0 ? string.Join(" or ", types) : "-";
        }

        /// <summary>
        /// 공격자의 기본 공격 보너스 가져오기 (무기 기반)
        /// ★ v0.2.73: Weapon Finesse 지원 추가
        /// </summary>
        public static int GetAttackBonus(UnitEntityData attacker, ItemEntityWeapon weapon = null)
        {
            if (attacker == null)
                return 0;

            try
            {
                var stats = attacker.Stats;
                if (stats == null)
                    return 0;

                // Base Attack Bonus
                int bab = stats.BaseAttackBonus?.ModifiedValue ?? 0;

                // Additional Attack Bonus (버프, 피트 등)
                int additional = stats.AdditionalAttackBonus?.ModifiedValue ?? 0;

                // 무기 기반 능력치 보너스
                int abilityMod = 0;
                weapon ??= attacker.GetFirstWeapon();

                if (weapon != null)
                {
                    // 무기 인챈트 보너스
                    int enhancement = weapon.EnchantmentValue;
                    additional += enhancement;

                    // 원거리 무기 = DEX
                    if (weapon.Blueprint.IsRanged)
                    {
                        abilityMod = stats.Dexterity?.Bonus ?? 0;
                    }
                    else
                    {
                        // ★ v0.2.73: Weapon Finesse 체크
                        // Finessable 무기 + DEX > STR이면 DEX 사용
                        int strBonus = stats.Strength?.Bonus ?? 0;
                        int dexBonus = stats.Dexterity?.Bonus ?? 0;

                        if (CanUseWeaponFinesse(attacker, weapon) && dexBonus > strBonus)
                        {
                            abilityMod = dexBonus;
                        }
                        else
                        {
                            abilityMod = strBonus;
                        }
                    }
                }
                else
                {
                    // 무기 없으면 STR 사용
                    abilityMod = stats.Strength?.Bonus ?? 0;
                }

                return bab + abilityMod + additional;
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] GetAttackBonus error: {ex.Message}");
                return 10; // 기본값
            }
        }

        /// <summary>
        /// ★ v0.2.73: Weapon Finesse 사용 가능 여부 확인
        /// Finessable 무기 (경량/레이피어/단검 등) + Weapon Finesse 피트
        /// </summary>
        public static bool CanUseWeaponFinesse(UnitEntityData unit, ItemEntityWeapon weapon)
        {
            if (unit == null || weapon == null)
                return false;

            try
            {
                // 1. 무기가 Finessable 카테고리인지 확인
                var category = weapon.Blueprint.Category;
                bool isFinessable = category.HasSubCategory(Kingmaker.Enums.WeaponSubCategory.Finessable);

                if (!isFinessable)
                    return false;

                // 2. 캐릭터가 Weapon Finesse 피트를 가지고 있는지 확인
                // UnitMechanicFeatures.WeaponFinesse 플래그 체크
                bool hasWeaponFinesse = unit.Descriptor?.State?.Features?.WeaponFinesse ?? false;

                return hasWeaponFinesse;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 타겟의 AC 가져오기 (타입별)
        /// </summary>
        public static int GetTargetAC(UnitEntityData target, AttackACType acType, UnitEntityData attacker = null)
        {
            if (target == null)
                return 10;

            try
            {
                var stats = target.Stats;
                if (stats?.AC == null)
                    return 10;

                // CC 상태면 Flat-Footed AC 사용
                bool isFlatFooted = IsTargetFlatFooted(target);

                switch (acType)
                {
                    case AttackACType.Touch:
                        return stats.AC.Touch;

                    case AttackACType.FlatFooted:
                        return stats.AC.FlatFooted;

                    case AttackACType.Normal:
                    default:
                        // CC 걸린 상태면 Flat-Footed AC 사용 (더 낮음)
                        if (isFlatFooted)
                            return stats.AC.FlatFooted;
                        return stats.AC.ModifiedValue;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] GetTargetAC error: {ex.Message}");
                return 15; // 기본값
            }
        }

        /// <summary>
        /// 적중 확률을 0.0~1.0 범위로 계산
        /// </summary>
        public static float CalculateHitProbability(int attackBonus, int targetAC)
        {
            // 필요 굴림 = AC - 공격보너스
            int needToRoll = targetAC - attackBonus;

            // 자연 1 = 항상 실패 (5%)
            // 자연 20 = 항상 성공 (5%)
            if (needToRoll <= 1)
                return 0.95f;  // 1을 제외한 모든 굴림 성공
            if (needToRoll >= 20)
                return 0.05f;  // 20만 성공

            // (21 - 필요값) / 20
            // 예: need 10 = (21-10)/20 = 11/20 = 55%
            return (21 - needToRoll) / 20f;
        }

        /// <summary>
        /// 플랭킹 여부 확인
        /// </summary>
        public static bool IsFlanking(UnitEntityData attacker, UnitEntityData target)
        {
            if (attacker == null || target == null)
                return false;

            try
            {
                // 게임 API 사용
                return target.CombatState?.IsFlanked ?? false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Internal Calculation

        private static HitChanceResult CalculateWeaponHitChanceInternal(
            UnitEntityData attacker,
            UnitEntityData target,
            ItemEntityWeapon weapon,
            int attackPenalty)
        {
            var result = new HitChanceResult();

            try
            {
                // 1. 공격 보너스 계산
                int baseAttackBonus = GetAttackBonus(attacker, weapon);

                // 2. 플랭킹 보너스 (+2)
                bool flanking = IsFlanking(attacker, target);
                if (flanking && weapon.Blueprint.IsMelee)
                {
                    baseAttackBonus += 2;
                    result.IsFlanking = true;
                }

                // 3. 공격 페널티 적용
                result.AttackBonus = baseAttackBonus - attackPenalty;

                // 4. AC 타입 결정 및 가져오기
                result.ACType = AttackACType.Normal;
                if (IsTargetFlatFooted(target))
                    result.ACType = AttackACType.FlatFooted;

                result.TargetAC = GetTargetAC(target, result.ACType, attacker);

                // 5. 적중 확률 계산
                result.NeedToRoll = result.TargetAC - result.AttackBonus;
                result.HitChance = CalculateHitProbability(result.AttackBonus, result.TargetAC);

                // 6. 크리티컬 확률 (기본 19-20 = 10%)
                int critRange = weapon.Blueprint?.CriticalRollEdge ?? 20;
                float critThreatChance = (21 - critRange) / 20f;
                result.CriticalChance = result.HitChance * critThreatChance * result.HitChance; // 위협 * 확정

                Main.Verbose($"[HitChance] Weapon: {attacker.CharacterName} vs {target.CharacterName} - {result}");
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateWeaponHitChance error: {ex.Message}");
                result.HitChance = 0.5f;
                result.AttackBonus = 10;
                result.TargetAC = 15;
            }

            return result;
        }

        private static HitChanceResult CalculateAbilityHitChanceInternal(
            UnitEntityData attacker,
            UnitEntityData target,
            AbilityData ability)
        {
            var result = new HitChanceResult();

            try
            {
                var blueprint = ability.Blueprint;

                // 1. 공격 타입 확인 (Touch/Ranged Touch/Normal)
                bool isTouch = blueprint.GetComponent<AbilityDeliverTouch>() != null;
                bool isRangedTouch = blueprint.GetComponent<AbilityDeliverProjectile>() != null &&
                                     IsRangedTouchAbility(blueprint);

                // 2. AC 타입 결정
                if (isTouch || isRangedTouch)
                {
                    result.ACType = AttackACType.Touch;
                }
                else if (IsTargetFlatFooted(target))
                {
                    result.ACType = AttackACType.FlatFooted;
                }
                else
                {
                    result.ACType = AttackACType.Normal;
                }

                // 3. 스펠이 공격 굴림을 요구하는지 확인
                bool requiresAttackRoll = isTouch || isRangedTouch ||
                                          blueprint.GetComponent<AbilityEffectRunAction>() != null &&
                                          HasAttackRollAction(blueprint);

                if (!requiresAttackRoll)
                {
                    // 세이브 기반 스펠 - 적중률 개념 없음 (100%)
                    result.HitChance = 1.0f;
                    result.AttackBonus = 0;
                    result.TargetAC = 0;
                    result.NeedToRoll = 0;
                    return result;
                }

                // 4. 터치 공격 보너스 계산
                // BAB + DEX (원거리 터치) 또는 BAB + STR (근접 터치)
                int bab = attacker.Stats.BaseAttackBonus?.ModifiedValue ?? 0;
                int additional = attacker.Stats.AdditionalAttackBonus?.ModifiedValue ?? 0;
                int abilityMod = isRangedTouch
                    ? (attacker.Stats.Dexterity?.Bonus ?? 0)
                    : (attacker.Stats.Strength?.Bonus ?? 0);

                result.AttackBonus = bab + abilityMod + additional;

                // 5. 타겟 AC 가져오기
                result.TargetAC = GetTargetAC(target, result.ACType, attacker);

                // 6. 적중 확률 계산
                result.NeedToRoll = result.TargetAC - result.AttackBonus;
                result.HitChance = CalculateHitProbability(result.AttackBonus, result.TargetAC);

                // 7. 스펠 크리티컬 없음 (대부분)
                result.CriticalChance = 0f;

                Main.Verbose($"[HitChance] Ability({(isTouch ? "Touch" : "Ranged")}): {ability.Name} " +
                           $"{attacker.CharacterName} vs {target.CharacterName} - {result}");
            }
            catch (Exception ex)
            {
                Main.Error($"[HitChanceCalculator] CalculateAbilityHitChance error: {ex.Message}");
                result.HitChance = 0.75f; // 스펠 기본값 높게
                result.AttackBonus = 15;
                result.TargetAC = 10;
            }

            return result;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 타겟이 Flat-Footed 상태인지 (CC 등)
        /// </summary>
        public static bool IsTargetFlatFooted(UnitEntityData target)
        {
            if (target == null)
                return false;

            try
            {
                var state = target.Descriptor?.State;
                if (state == null)
                    return false;

                // Flat-footed 조건들 (행동 불가 상태)
                return state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Stunned) ||
                       state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Paralyzed) ||
                       state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Sleeping) ||
                       state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Unconscious) ||
                       state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Prone) ||
                       state.HasCondition(Kingmaker.UnitLogic.UnitCondition.Helpless);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 원거리 터치 공격인지 확인
        /// </summary>
        private static bool IsRangedTouchAbility(BlueprintAbility blueprint)
        {
            // SpellDescriptor나 이름으로 판별
            string name = blueprint.name?.ToLowerInvariant() ?? "";
            return name.Contains("ray") ||
                   name.Contains("scorching") ||
                   name.Contains("disintegrate") ||
                   name.Contains("enervation") ||
                   name.Contains("polar");
        }

        /// <summary>
        /// 능력이 공격 굴림을 요구하는지
        /// </summary>
        private static bool HasAttackRollAction(BlueprintAbility blueprint)
        {
            // 대부분의 터치/레이 스펠은 공격 굴림 필요
            // 세이브 기반 스펠은 불필요
            var runAction = blueprint.GetComponent<AbilityEffectRunAction>();
            if (runAction?.Actions?.Actions == null)
                return false;

            // ContextActionMeleeAttack, ContextActionRangedAttack 등이 있으면 true
            foreach (var action in runAction.Actions.Actions)
            {
                string typeName = action?.GetType().Name ?? "";
                if (typeName.Contains("Attack") || typeName.Contains("Touch"))
                    return true;
            }

            return false;
        }

        private static HitChanceResult CreateDefaultResult()
        {
            return new HitChanceResult
            {
                HitChance = 0.5f,
                CriticalChance = 0.05f,
                AttackBonus = 10,
                TargetAC = 15,
                ACType = AttackACType.Normal,
                NeedToRoll = 5
            };
        }

        #endregion

        #region Cache Management

        private static bool TryGetCached(string key, out HitChanceResult result)
        {
            float currentTime = Time.time;
            if (currentTime - _lastCacheTime > CACHE_DURATION)
            {
                _cache.Clear();
                _lastCacheTime = currentTime;
            }

            return _cache.TryGetValue(key, out result);
        }

        private static void CacheResult(string key, HitChanceResult result)
        {
            _cache[key] = result;
        }

        /// <summary>
        /// 캐시 무효화 (전투 상태 변경 시)
        /// </summary>
        public static void InvalidateCache()
        {
            _cache.Clear();
        }

        #endregion
    }
}
