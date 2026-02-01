using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.Designers.EventConditionActionSystem.Actions;
using Kingmaker.ElementsSystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums.Damage;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.UnitLogic.Abilities.Components.Base;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;

namespace CompanionAI_Pathfinder.Analysis
{
    #region v0.2.5 BubbleBuffs-Inspired Buff Effects

    /// <summary>
    /// v0.2.5: 능력이 적용하는 모든 버프 효과 (BubbleBuffs 패턴)
    /// 단일 능력이 여러 버프를 적용할 수 있음
    /// v0.2.40: 인챈트 주문 감지 추가
    /// </summary>
    public class AbilityBuffEffects
    {
        /// <summary>적용되는 모든 버프의 GUID (문자열)</summary>
        public HashSet<string> AppliedBuffGuids { get; } = new HashSet<string>();

        /// <summary>적용되는 모든 버프 Blueprint</summary>
        public List<BlueprintBuff> AppliedBuffs { get; } = new List<BlueprintBuff>();

        /// <summary>영구 버프 여부 (하나라도 영구면 true)</summary>
        public bool HasPermanentBuff { get; private set; }

        /// <summary>장기 버프 여부 (60초 이상 또는 분/시간 단위)</summary>
        public bool IsLongDuration { get; private set; }

        /// <summary>버프가 없는지</summary>
        public bool IsEmpty => AppliedBuffGuids.Count == 0 && !IsEnchantment;

        /// <summary>v0.2.18: 발견된 세이브 타입</summary>
        public SavingThrowType? DetectedSaveType { get; set; }

        /// <summary>첫 번째 버프 (레거시 호환)</summary>
        public BlueprintBuff PrimaryBuff => AppliedBuffs.Count > 0 ? AppliedBuffs[0] : null;

        /// <summary>★ v0.2.40: 인챈트 주문 여부 (Magic Weapon, Greater Magic Weapon 등)</summary>
        public bool IsEnchantment { get; private set; }

        /// <summary>★ v0.2.40: 인챈트 보너스 (탐지된 경우)</summary>
        public int EnchantmentBonus { get; private set; }

        /// <summary>★ v0.2.101: Fatigued 조건 적용 여부</summary>
        public bool AppliesFatigue { get; private set; }

        /// <summary>★ v0.2.101: Exhausted 조건 적용 여부</summary>
        public bool AppliesExhausted { get; private set; }

        /// <summary>★ v0.2.40: 인챈트 설정</summary>
        public void SetEnchantment(int bonus = 0)
        {
            IsEnchantment = true;
            if (bonus > EnchantmentBonus)
                EnchantmentBonus = bonus;
        }

        /// <summary>
        /// 버프 추가
        /// ★ v0.2.101: AddCondition 컴포넌트 분석 추가 (Fatigue/Exhausted 감지)
        /// </summary>
        public void AddBuff(BlueprintBuff buff, bool isPermanent, bool isLong)
        {
            if (buff == null) return;

            var guid = buff.AssetGuid.ToString();
            if (!AppliedBuffGuids.Contains(guid))
            {
                AppliedBuffGuids.Add(guid);
                AppliedBuffs.Add(buff);

                // ★ v0.2.101: 버프가 적용하는 조건 분석
                AnalyzeBuffConditions(buff);
            }

            if (isPermanent) HasPermanentBuff = true;
            if (isLong) IsLongDuration = true;
        }

        /// <summary>
        /// ★ v0.2.101: 버프의 AddCondition 컴포넌트 분석
        /// </summary>
        private void AnalyzeBuffConditions(BlueprintBuff buff)
        {
            try
            {
                var addCondition = buff.GetComponent<AddCondition>();
                if (addCondition != null)
                {
                    if (addCondition.Condition == UnitCondition.Fatigued)
                    {
                        AppliesFatigue = true;
                    }
                    else if (addCondition.Condition == UnitCondition.Exhausted)
                    {
                        AppliesExhausted = true;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 타겟에게 이미 적용된 버프가 있는지 확인 (HashSet.Overlaps 사용)
        /// </summary>
        public bool IsAnyBuffPresent(UnitEntityData target)
        {
            if (target?.Buffs == null || AppliedBuffGuids.Count == 0)
                return false;

            // 타겟의 모든 버프 GUID를 HashSet으로 스냅샷
            var targetBuffGuids = new HashSet<string>();
            foreach (var buff in target.Buffs.RawFacts)
            {
                if (buff?.Blueprint != null)
                    targetBuffGuids.Add(buff.Blueprint.AssetGuid.ToString());
            }

            // Overlaps로 O(n) 비교
            return targetBuffGuids.Overlaps(AppliedBuffGuids);
        }

        /// <summary>
        /// 타겟에게 모든 버프가 적용되어 있는지 확인
        /// </summary>
        public bool AreAllBuffsPresent(UnitEntityData target)
        {
            if (target?.Buffs == null || AppliedBuffGuids.Count == 0)
                return false;

            var targetBuffGuids = new HashSet<string>();
            foreach (var buff in target.Buffs.RawFacts)
            {
                if (buff?.Blueprint != null)
                    targetBuffGuids.Add(buff.Blueprint.AssetGuid.ToString());
            }

            return targetBuffGuids.IsSupersetOf(AppliedBuffGuids);
        }
    }

    /// <summary>
    /// v0.2.5: 유닛 버프 스냅샷 (캐시용)
    /// </summary>
    public class UnitBuffSnapshot
    {
        public UnitEntityData Unit { get; }
        public HashSet<string> BuffGuids { get; }

        public UnitBuffSnapshot(UnitEntityData unit)
        {
            Unit = unit;
            BuffGuids = new HashSet<string>();

            if (unit?.Buffs?.RawFacts != null)
            {
                foreach (var buff in unit.Buffs.RawFacts)
                {
                    if (buff?.Blueprint != null)
                        BuffGuids.Add(buff.Blueprint.AssetGuid.ToString());
                }
            }
        }

        public bool HasBuff(string buffGuid) => BuffGuids.Contains(buffGuid);
        public bool HasAnyBuff(HashSet<string> buffGuids) => BuffGuids.Overlaps(buffGuids);
    }

    #endregion
    #region Enums

    /// <summary>
    /// 능력 타이밍 분류
    /// </summary>
    public enum AbilityTiming
    {
        /// <summary>일반 능력</summary>
        Normal,

        /// <summary>선제 버프 - 전투 시작 시 사용</summary>
        PreCombatBuff,

        /// <summary>영구 버프 - 무한 지속, 전투 외에서도 사용 가능</summary>
        PermanentBuff,

        /// <summary>힐링 - 치료/회복</summary>
        Healing,

        /// <summary>디버프 - 적 약화</summary>
        Debuff,

        /// <summary>군중 제어 (CC)</summary>
        CrowdControl,

        /// <summary>소환 - 소환수 호출</summary>
        Summon,

        /// <summary>채널 에너지</summary>
        Channel,

        /// <summary>위험한 AoE - 아군 피해 가능</summary>
        DangerousAoE,

        /// <summary>공격 능력</summary>
        Attack,

        /// <summary>유틸리티 - 기타</summary>
        Utility,
    }

    /// <summary>
    /// CC 타입 세분류 (v0.2.0)
    /// AI가 CC 면역 적에게 특정 CC 사용 회피 가능
    /// </summary>
    [Flags]
    public enum CCType
    {
        None = 0,

        // === Hard CC (행동 불가) ===
        Stun = 1,           // SpellDescriptor.Stun
        Paralysis = 2,      // SpellDescriptor.Paralysis
        Petrified = 4,      // SpellDescriptor.Petrified
        Sleep = 8,          // SpellDescriptor.Sleep

        // === Soft CC (행동 제한) ===
        Confusion = 16,     // SpellDescriptor.Confusion
        Charm = 32,         // SpellDescriptor.Charm
        Fear = 64,          // SpellDescriptor.Fear, Frightened, Shaken
        Daze = 128,         // SpellDescriptor.Daze

        // === Movement CC ===
        MovementImpair = 256, // SpellDescriptor.MovementImpairing

        // === Mind-Affecting (정신 면역 체크) ===
        MindAffecting = 512,  // SpellDescriptor.MindAffecting

        // === v0.2.59: Additional CC types for immunity checks ===
        Blind = 1024,         // UnitCondition.Blindness
        Slow = 2048,          // UnitCondition.Slowed
        Entangle = 4096,      // UnitCondition.Entangled
        Nauseate = 8192,      // UnitCondition.Nauseated

        // === 그룹 ===
        HardCC = Stun | Paralysis | Petrified | Sleep,
        SoftCC = Confusion | Charm | Fear | Daze,
        AllCC = HardCC | SoftCC | MovementImpair | Blind | Slow | Entangle | Nauseate,
    }

    /// <summary>
    /// 원소 데미지 타입 (v0.2.0)
    /// AI가 원소 저항/면역 적에게 해당 원소 공격 회피 가능
    /// </summary>
    [Flags]
    public enum DamageElement
    {
        None = 0,
        Physical = 1,       // AbilityType.Physical
        Fire = 2,           // SpellDescriptor.Fire
        Cold = 4,           // SpellDescriptor.Cold
        Electricity = 8,    // SpellDescriptor.Electricity
        Acid = 16,          // SpellDescriptor.Acid
        Sonic = 32,         // SpellDescriptor.Sonic
        Force = 64,         // SpellDescriptor.Force
        Positive = 128,     // Channel Positive
        Negative = 256,     // Channel Negative
        Divine = 512,       // SpellDescriptor.Divine
        Arcane = 1024,      // SpellDescriptor.Arcane
    }

    /// <summary>
    /// 디버프 타입 세분류 (v0.2.0)
    /// AI가 특정 디버프 면역 적에게 해당 디버프 회피 가능
    /// </summary>
    [Flags]
    public enum DebuffType
    {
        None = 0,

        // === 상태이상 ===
        Poison = 1,         // SpellDescriptor.Poison
        Disease = 2,        // SpellDescriptor.Disease
        Curse = 4,          // SpellDescriptor.Curse
        Bleed = 8,          // SpellDescriptor.Bleed

        // === 스탯 디버프 ===
        StatDebuff = 16,    // SpellDescriptor.StatDebuff
        NegativeLevel = 32, // SpellDescriptor.NegativeLevel

        // === 컨디션 ===
        Sickened = 64,      // SpellDescriptor.Sickened
        Nauseated = 128,    // SpellDescriptor.Nauseated
        Fatigued = 256,     // SpellDescriptor.Fatigue
        Exhausted = 512,    // SpellDescriptor.Exhausted
        Staggered = 1024,   // SpellDescriptor.Staggered
        Blindness = 2048,   // SpellDescriptor.Blindness

        // === 죽음 ===
        Death = 4096,       // SpellDescriptor.Death
    }

    /// <summary>
    /// 타겟팅 타입 (v0.2.0)
    /// </summary>
    public enum TargetingType
    {
        None,
        Self,           // 자신만
        SingleAlly,     // 단일 아군
        SingleEnemy,    // 단일 적
        AllAllies,      // 전체 아군
        AllEnemies,     // 전체 적
        AoEPoint,       // 지점 AoE
        AoECone,        // 원뿔형
        AoELine,        // 직선
        Touch,          // 접촉
    }

    #endregion

    /// <summary>
    /// 능력 리소스 유형
    /// </summary>
    public enum AbilityResourceType
    {
        /// <summary>무료/무한 - 캔트립, 오리슨 등</summary>
        Free,

        /// <summary>스펠 슬롯 소모</summary>
        SpellSlot,

        /// <summary>기타 리소스 소모 (채널 에너지 등)</summary>
        AbilityResource,

        /// <summary>1일 N회 사용 가능</summary>
        LimitedUse,
    }

    /// <summary>
    /// 능력 분류 결과 (v0.2.0 Enhanced)
    /// Blueprint의 상세 속성을 활용한 세분화된 분류
    /// </summary>
    public class AbilityClassification
    {
        #region Core Properties

        public AbilityData Ability { get; }
        public AbilityTiming Timing { get; }
        public AbilityResourceType ResourceType { get; }
        public int SpellLevel { get; }
        public float ResourceScore { get; }
        public BlueprintBuff AppliedBuff { get; }
        public bool IsPermanentBuff { get; }
        public bool IsAlreadyApplied { get; set; }

        #endregion

        #region v0.2.0 Enhanced Properties

        /// <summary>CC 타입 (Stun, Fear, Charm 등)</summary>
        public CCType CCType { get; set; }

        /// <summary>원소 데미지 타입 (Fire, Cold, Electricity 등)</summary>
        public DamageElement DamageElement { get; set; }

        /// <summary>디버프 타입 (Poison, Curse, StatDebuff 등)</summary>
        public DebuffType DebuffType { get; set; }

        /// <summary>타겟팅 방식</summary>
        public TargetingType TargetingType { get; set; }

        /// <summary>SpellSchool (Evocation, Necromancy 등)</summary>
        public SpellSchool School { get; set; }

        /// <summary>AbilityType (Spell, SpellLike, Supernatural 등)</summary>
        public AbilityType AbilityType { get; set; }

        /// <summary>AoE 반경 (미터)</summary>
        public float AoERadius { get; set; }

        /// <summary>사거리 (미터)</summary>
        public float Range { get; set; }

        /// <summary>정신 영향 능력 여부 (언데드/구조물 면역)</summary>
        public bool IsMindAffecting { get; set; }

        /// <summary>죽음 효과 여부 (죽음 면역 체크)</summary>
        public bool IsDeathEffect { get; set; }

        /// <summary>접촉 공격 여부</summary>
        public bool IsTouch { get; set; }

        /// <summary>원거리 접촉 공격 여부</summary>
        public bool IsRangedTouch { get; set; }

        /// <summary>v0.2.18: 요구 세이브 타입 (Will/Reflex/Fortitude)</summary>
        public SavingThrowType? RequiredSave { get; set; }

        /// <summary>★ v0.2.78: 액션 타입 (Standard, Move, Swift, Free)</summary>
        public UnitCommand.CommandType CommandActionType { get; set; } = UnitCommand.CommandType.Standard;

        /// <summary>★ v0.2.78: Full-Round Action 여부 (Standard + Move 소비)</summary>
        public bool IsFullRoundAction { get; set; }

        #endregion

        #region Constructor

        public AbilityClassification(AbilityData ability, AbilityTiming timing,
            AbilityResourceType resourceType, int spellLevel, float resourceScore,
            BlueprintBuff appliedBuff = null, bool isPermanentBuff = false)
        {
            Ability = ability;
            Timing = timing;
            ResourceType = resourceType;
            SpellLevel = spellLevel;
            ResourceScore = resourceScore;
            AppliedBuff = appliedBuff;
            IsPermanentBuff = isPermanentBuff;
            IsAlreadyApplied = false;

            // Initialize new properties
            CCType = CCType.None;
            DamageElement = DamageElement.None;
            DebuffType = DebuffType.None;
            TargetingType = TargetingType.None;
            School = SpellSchool.None;
            AbilityType = AbilityType.Special;

            // ★ v0.2.78: 액션 타입 기본값
            CommandActionType = UnitCommand.CommandType.Standard;
            IsFullRoundAction = false;
        }

        #endregion

        #region Helper Properties

        /// <summary>
        /// 리소스 비용이 높은 능력인지 (보수적 사용 필요)
        /// </summary>
        public bool IsHighCost => SpellLevel >= 4 || ResourceType == AbilityResourceType.LimitedUse;

        /// <summary>
        /// 자유롭게 사용 가능한 능력인지
        /// </summary>
        public bool IsFreeToUse => ResourceType == AbilityResourceType.Free;

        /// <summary>
        /// Hard CC 여부 (Stun, Paralysis, Sleep, Petrified)
        /// </summary>
        public bool IsHardCC => (CCType & CCType.HardCC) != 0;

        /// <summary>
        /// Soft CC 여부 (Confusion, Charm, Fear, Daze)
        /// </summary>
        public bool IsSoftCC => (CCType & CCType.SoftCC) != 0;

        /// <summary>
        /// 원소 데미지 능력 여부
        /// </summary>
        public bool IsElementalDamage => DamageElement != DamageElement.None &&
                                          DamageElement != DamageElement.Physical;

        /// <summary>
        /// AoE 능력 여부
        /// </summary>
        public bool IsAoE => AoERadius > 0;

        /// <summary>
        /// 적에게 사용하는 능력 여부
        /// </summary>
        public bool IsOffensive => Timing == AbilityTiming.Attack ||
                                    Timing == AbilityTiming.CrowdControl ||
                                    Timing == AbilityTiming.Debuff ||
                                    Timing == AbilityTiming.DangerousAoE;

        /// <summary>
        /// 아군에게 사용하는 능력 여부
        /// </summary>
        public bool IsSupportive => Timing == AbilityTiming.Healing ||
                                     Timing == AbilityTiming.PreCombatBuff ||
                                     Timing == AbilityTiming.PermanentBuff ||
                                     Timing == AbilityTiming.Channel;

        /// <summary>★ v0.2.78: Swift Action인지 여부</summary>
        public bool IsSwiftAction => CommandActionType == UnitCommand.CommandType.Swift;

        /// <summary>★ v0.2.78: Free Action인지 여부</summary>
        public bool IsFreeAction => CommandActionType == UnitCommand.CommandType.Free;

        /// <summary>★ v0.2.78: Standard Action인지 여부</summary>
        public bool IsStandardAction => CommandActionType == UnitCommand.CommandType.Standard;

        #endregion

        #region ToString

        public override string ToString()
        {
            var parts = new List<string>
            {
                Timing.ToString(),
                $"Lv{SpellLevel}"
            };

            if (CCType != CCType.None)
                parts.Add($"CC:{CCType}");
            if (DamageElement != DamageElement.None)
                parts.Add($"Elem:{DamageElement}");
            if (DebuffType != DebuffType.None)
                parts.Add($"Debuff:{DebuffType}");
            if (IsAoE)
                parts.Add($"AoE:{AoERadius:F0}m");

            return $"[{Ability?.Name}] {string.Join(", ", parts)}";
        }

        #endregion
    }

    /// <summary>
    /// Pathfinder 능력 자동 분류 시스템
    /// Blueprint 속성, SpellDescriptor, 컴포넌트 분석 기반
    /// v0.2.5: BubbleBuffs 패턴 적용 - GUID 캐싱, 다중 버프 추출
    /// </summary>
    public static class AbilityClassifier
    {
        #region v0.2.5 GUID-Based Caching

        /// <summary>
        /// 능력 → 버프 효과 캐시 (GUID 문자열 기반)
        /// 한 번 계산 후 재사용
        /// </summary>
        private static readonly Dictionary<string, AbilityBuffEffects> _abilityBuffCache = new Dictionary<string, AbilityBuffEffects>();

        /// <summary>
        /// ★ v0.2.102: 능력 분류 결과 캐시 (GUID 문자열 기반)
        /// Blueprint 속성만 분석하므로 캐싱 가능
        /// </summary>
        private static readonly Dictionary<string, AbilityClassification> _classificationCache = new Dictionary<string, AbilityClassification>();

        /// <summary>
        /// 캐시 클리어 (게임 재시작 시 등)
        /// </summary>
        public static void ClearCache()
        {
            _abilityBuffCache.Clear();
            _classificationCache.Clear();
            _ccRemovalCache.Clear();
            Main.Log("[AbilityClassifier] All caches cleared");
        }

        /// <summary>
        /// 능력의 버프 효과 가져오기 (캐시 사용)
        /// </summary>
        public static AbilityBuffEffects GetBuffEffects(AbilityData ability)
        {
            if (ability?.Blueprint == null)
                return new AbilityBuffEffects();

            var abilityGuid = ability.Blueprint.AssetGuid.ToString();

            // 캐시 확인
            if (_abilityBuffCache.TryGetValue(abilityGuid, out var cached))
                return cached;

            // 캐시 미스: 새로 계산
            var effects = ExtractAllBuffEffects(ability);
            _abilityBuffCache[abilityGuid] = effects;

            if (!effects.IsEmpty)
            {
                Main.Verbose($"[AbilityClassifier] Cached {ability.Name}: {effects.AppliedBuffs.Count} buffs, permanent={effects.HasPermanentBuff}, long={effects.IsLongDuration}");
            }

            return effects;
        }

        /// <summary>
        /// 능력에서 모든 버프 효과 추출 (BubbleBuffs 패턴)
        /// </summary>
        private static AbilityBuffEffects ExtractAllBuffEffects(AbilityData ability)
        {
            var effects = new AbilityBuffEffects();

            try
            {
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions == null)
                    return effects;

                // 모든 버프 재귀적으로 수집
                CollectBuffsRecursive(runAction.Actions.Actions, effects, 0);
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] ExtractAllBuffEffects error for {ability?.Name}: {ex.Message}");
            }

            return effects;
        }

        /// <summary>
        /// 재귀적으로 모든 버프 수집 (반환 대신 수집)
        /// ★ v0.2.40: 인챈트 주문 감지 추가 (EnhanceWeapon, ContextActionEnchantWornItem)
        /// </summary>
        private static void CollectBuffsRecursive(GameAction[] actions, AbilityBuffEffects effects, int depth)
        {
            if (actions == null || depth > 10)
                return;

            foreach (var action in actions)
            {
                if (action == null)
                    continue;

                // ContextActionApplyBuff - 직접 버프 적용
                if (action is ContextActionApplyBuff applyBuff && applyBuff.Buff != null)
                {
                    bool isPermanent = applyBuff.Permanent;
                    bool isLong = IsLongDuration(applyBuff);
                    effects.AddBuff(applyBuff.Buff, isPermanent, isLong);
                    continue; // 계속 탐색 (다른 버프도 있을 수 있음)
                }

                // ★ v0.2.40: 인챈트 주문 감지 (타입명 기반)
                // EnhanceWeapon, ContextActionEnchantWornItem 등은 버프가 아닌 인챈트
                string actionTypeName = action.GetType().Name;
                if (actionTypeName.Contains("EnchantWornItem") ||
                    actionTypeName.Contains("EnhanceWeapon") ||
                    actionTypeName.Contains("ItemEnchantment"))
                {
                    // 인챈트 보너스 추출 시도
                    int bonus = TryExtractEnchantmentBonus(action);
                    effects.SetEnchantment(bonus);
                    Main.Verbose($"[AbilityClassifier] Detected enchantment action: {actionTypeName}, bonus={bonus}");
                    continue;
                }

                // Conditional (IfTrue / IfFalse)
                if (action is Conditional conditional)
                {
                    CollectBuffsRecursive(conditional.IfTrue?.Actions, effects, depth + 1);
                    CollectBuffsRecursive(conditional.IfFalse?.Actions, effects, depth + 1);
                }

                // ContextActionConditionalSaved (Succeed / Failed)
                if (action is ContextActionConditionalSaved conditionalSaved)
                {
                    CollectBuffsRecursive(conditionalSaved.Failed?.Actions, effects, depth + 1);
                    CollectBuffsRecursive(conditionalSaved.Succeed?.Actions, effects, depth + 1);
                }

                // ContextActionOnContextCaster
                if (action is ContextActionOnContextCaster onCaster)
                {
                    CollectBuffsRecursive(onCaster.Actions?.Actions, effects, depth + 1);
                }

                // ContextActionSavingThrow
                if (action is ContextActionSavingThrow savingThrow)
                {
                    // v0.2.18: 세이브 타입 추출
                    if (savingThrow.Type != SavingThrowType.Unknown && !effects.DetectedSaveType.HasValue)
                    {
                        effects.DetectedSaveType = savingThrow.Type;
                    }
                    CollectBuffsRecursive(savingThrow.Actions?.Actions, effects, depth + 1);
                }

                // ContextActionsOnPet
                if (action is ContextActionsOnPet onPet)
                {
                    CollectBuffsRecursive(onPet.Actions?.Actions, effects, depth + 1);
                }

                // ContextActionPartyMembers
                if (action is ContextActionPartyMembers partyMembers)
                {
                    CollectBuffsRecursive(partyMembers.Action?.Actions, effects, depth + 1);
                }
            }
        }

        /// <summary>
        /// ★ v0.2.40: 인챈트 보너스 추출 시도 (Reflection)
        /// </summary>
        private static int TryExtractEnchantmentBonus(GameAction action)
        {
            try
            {
                // EnchantmentBonus, Enhancement, Bonus 등의 필드 탐색
                var type = action.GetType();

                // 일반적인 필드명 시도
                foreach (var fieldName in new[] { "EnchantmentBonus", "Enhancement", "Bonus", "m_EnhancementBonus" })
                {
                    var field = type.GetField(fieldName,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

                    if (field != null)
                    {
                        var value = field.GetValue(action);
                        if (value is int intVal)
                            return intVal;
                    }
                }

                // 프로퍼티도 시도
                foreach (var propName in new[] { "EnchantmentBonus", "Enhancement", "Bonus" })
                {
                    var prop = type.GetProperty(propName,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

                    if (prop != null)
                    {
                        var value = prop.GetValue(action);
                        if (value is int intVal)
                            return intVal;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[AbilityClassifier] TryExtractEnchantmentBonus error: {ex.Message}");
            }

            return 1; // 기본 +1 보너스
        }

        /// <summary>
        /// 지속시간이 긴 버프인지 확인 (BubbleBuffs IsLong 패턴)
        /// </summary>
        private static bool IsLongDuration(ContextActionApplyBuff action)
        {
            try
            {
                // 영구 버프
                if (action.Permanent)
                    return true;

                // 초 단위 60초 이상
                if (action.UseDurationSeconds && action.DurationSeconds >= 60)
                    return true;

                // 라운드가 아닌 지속시간 (분, 시간, 일)
                // DurationValue.Rate가 Rounds(0)가 아니면 장기 버프
                if (action.DurationValue != null)
                {
                    // Rate enum: Rounds=0, Minutes=1, TenMinutes=2, Hours=3, Days=4
                    int rate = (int)action.DurationValue.Rate;
                    if (rate > 0) // Rounds가 아닌 경우
                        return true;
                }

                return false;
            }
            catch
            {
                // 안전하게 기본값 반환
                return action.Permanent;
            }
        }

        #endregion

        /// <summary>
        /// 능력 분류 (v0.2.0 Enhanced)
        /// Blueprint의 SpellDescriptor, School, AbilityType 등 상세 속성 활용
        /// ★ v0.2.102: GUID 기반 캐싱 추가 - Blueprint 속성만 분석하므로 캐싱 가능
        /// </summary>
        public static AbilityClassification Classify(AbilityData ability, UnitEntityData caster = null)
        {
            if (ability?.Blueprint == null)
                return new AbilityClassification(ability, AbilityTiming.Normal,
                    AbilityResourceType.Free, 0, 0f);

            var bp = ability.Blueprint;

            // ★ v0.2.102: 캐시 확인
            string abilityGuid = bp.AssetGuid.ToString();
            if (_classificationCache.TryGetValue(abilityGuid, out var cached))
            {
                // 캐시된 분류 결과 반환 (Ability 참조만 업데이트)
                return CloneWithAbility(cached, ability);
            }
            int spellLevel = ability.SpellLevel;

            // 리소스 타입 결정
            var resourceType = DetermineResourceType(ability);
            float resourceScore = CalculateResourceScore(ability, resourceType, spellLevel);

            // v0.2.5: 버프 정보 추출 (캐시 사용)
            var buffEffects = GetBuffEffects(ability);
            var appliedBuff = buffEffects.PrimaryBuff;
            bool isPermanent = buffEffects.HasPermanentBuff;

            // 타이밍 결정
            var timing = DetermineTiming(ability, bp, appliedBuff, isPermanent);

            var result = new AbilityClassification(ability, timing, resourceType,
                spellLevel, resourceScore, appliedBuff, isPermanent);

            // ════════════════════════════════════════════════════════════
            // v0.2.0: Enhanced classification using Blueprint properties
            // ════════════════════════════════════════════════════════════

            try
            {
                var descriptor = bp.SpellDescriptor;

                // 1. CC 타입 분석
                result.CCType = AnalyzeCCType(descriptor);

                // 2. 원소 데미지 타입 분석
                result.DamageElement = AnalyzeDamageElement(descriptor, bp);

                // 3. 디버프 타입 분석
                result.DebuffType = AnalyzeDebuffType(descriptor);

                // 4. 타겟팅 타입 분석
                result.TargetingType = AnalyzeTargetingType(bp);

                // 5. Blueprint 기본 속성
                result.School = bp.School;
                result.AbilityType = bp.Type;
                result.AoERadius = bp.AoERadius.Meters;
                result.Range = GetRangeInMeters(bp);

                // 6. 특수 플래그
                result.IsMindAffecting = descriptor.HasFlag(SpellDescriptor.MindAffecting);
                result.IsDeathEffect = descriptor.HasFlag(SpellDescriptor.Death);

                // 7. 접촉 공격 여부
                var deliverTouch = bp.GetComponent<AbilityDeliverTouch>();
                result.IsTouch = deliverTouch != null;
                result.IsRangedTouch = bp.GetComponent<AbilityDeliverProjectile>() != null &&
                                        bp.EffectOnEnemy == AbilityEffectOnUnit.Harmful;

                // Timing 재조정 (CC vs Debuff 분리)
                if (result.CCType != CCType.None && timing == AbilityTiming.Debuff)
                {
                    result = new AbilityClassification(ability, AbilityTiming.CrowdControl, resourceType,
                        spellLevel, resourceScore, appliedBuff, isPermanent)
                    {
                        CCType = result.CCType,
                        DamageElement = result.DamageElement,
                        DebuffType = result.DebuffType,
                        TargetingType = result.TargetingType,
                        School = result.School,
                        AbilityType = result.AbilityType,
                        AoERadius = result.AoERadius,
                        Range = result.Range,
                        IsMindAffecting = result.IsMindAffecting,
                        IsDeathEffect = result.IsDeathEffect,
                        IsTouch = result.IsTouch,
                        IsRangedTouch = result.IsRangedTouch
                    };
                }

                // v0.2.1: 사기저하(Demoralize) 특수 처리 - Attack으로 분류되지만 실제로는 CC
                if (IsDemoralize(ability) && timing == AbilityTiming.Attack)
                {
                    Main.Verbose($"[AbilityClassifier] {ability.Name}: Reclassifying Demoralize as CrowdControl");
                    result = new AbilityClassification(ability, AbilityTiming.CrowdControl, resourceType,
                        spellLevel, resourceScore, appliedBuff, isPermanent)
                    {
                        CCType = CCType.Fear | CCType.MindAffecting, // Shaken은 Fear 계열
                        DamageElement = result.DamageElement,
                        DebuffType = DebuffType.Sickened, // Shaken 효과
                        TargetingType = result.TargetingType,
                        School = result.School,
                        AbilityType = result.AbilityType,
                        AoERadius = result.AoERadius,
                        Range = result.Range,
                        IsMindAffecting = true,
                        IsDeathEffect = false,
                        IsTouch = result.IsTouch,
                        IsRangedTouch = result.IsRangedTouch
                    };
                }

                Main.Verbose($"[AbilityClassifier] {ability.Name}: {result}");
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] Enhanced classification error for {ability?.Name}: {ex.Message}");
            }

            // ★ v0.2.40: REMOVED - IsAlreadyApplied should be checked per TARGET, not caster
            // The caster parameter is NOT the buff target. For ally buffs, we must check
            // each potential target individually. BuffScorer.IsBuffAlreadyApplied() does this correctly.
            // Setting this field here was a bug that caused permanent buffs to be skipped
            // when the CASTER (not target) already had the buff.
            // result.IsAlreadyApplied is now always false from classification;
            // actual check happens in BuffScorer.BuildBuffConsiderations() per target.

            // v0.2.18: 세이브 타입 전파
            if (buffEffects.DetectedSaveType.HasValue)
            {
                result.RequiredSave = buffEffects.DetectedSaveType;
            }

            // ★ v0.2.78: 액션 타입 결정
            DetermineActionType(ability, result);

            // ★ v0.2.102: 결과 캐싱
            _classificationCache[abilityGuid] = result;

            return result;
        }

        /// <summary>
        /// ★ v0.2.102: 캐시된 분류 결과를 복제하면서 Ability 참조만 업데이트
        /// AbilityData는 인스턴스마다 다를 수 있으므로 복제 필요
        /// </summary>
        private static AbilityClassification CloneWithAbility(AbilityClassification cached, AbilityData ability)
        {
            var clone = new AbilityClassification(
                ability,
                cached.Timing,
                cached.ResourceType,
                cached.SpellLevel,
                cached.ResourceScore,
                cached.AppliedBuff,
                cached.IsPermanentBuff)
            {
                CCType = cached.CCType,
                DamageElement = cached.DamageElement,
                DebuffType = cached.DebuffType,
                TargetingType = cached.TargetingType,
                School = cached.School,
                AbilityType = cached.AbilityType,
                AoERadius = cached.AoERadius,
                Range = cached.Range,
                IsMindAffecting = cached.IsMindAffecting,
                IsDeathEffect = cached.IsDeathEffect,
                IsTouch = cached.IsTouch,
                IsRangedTouch = cached.IsRangedTouch,
                RequiredSave = cached.RequiredSave,
                CommandActionType = cached.CommandActionType,
                IsFullRoundAction = cached.IsFullRoundAction
            };

            return clone;
        }

        /// <summary>
        /// ★ v0.2.78: 능력의 액션 타입 결정 (Standard/Move/Swift/Free)
        /// </summary>
        private static void DetermineActionType(AbilityData ability, AbilityClassification result)
        {
            try
            {
                var bp = ability?.Blueprint;
                if (bp == null) return;

                // 1. Blueprint에서 ActionType 가져오기
                result.CommandActionType = bp.ActionType;

                // 2. Full-Round Action 체크
                result.IsFullRoundAction = bp.IsFullRoundAction;

                // 3. 로깅
                if (result.CommandActionType != UnitCommand.CommandType.Standard ||
                    result.IsFullRoundAction)
                {
                    Main.Verbose($"[AbilityClassifier] {ability.Name}: ActionType={result.CommandActionType}, FullRound={result.IsFullRoundAction}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[AbilityClassifier] DetermineActionType error: {ex.Message}");
            }
        }

        #region v0.2.0 Enhanced Analysis Methods

        /// <summary>
        /// SpellDescriptor에서 CC 타입 추출
        /// </summary>
        private static CCType AnalyzeCCType(SpellDescriptor descriptor)
        {
            CCType result = CCType.None;

            // Hard CC
            if (descriptor.HasFlag(SpellDescriptor.Stun))
                result |= CCType.Stun;
            if (descriptor.HasFlag(SpellDescriptor.Paralysis))
                result |= CCType.Paralysis;
            if (descriptor.HasFlag(SpellDescriptor.Petrified))
                result |= CCType.Petrified;
            if (descriptor.HasFlag(SpellDescriptor.Sleep))
                result |= CCType.Sleep;

            // Soft CC
            if (descriptor.HasFlag(SpellDescriptor.Confusion))
                result |= CCType.Confusion;
            if (descriptor.HasFlag(SpellDescriptor.Charm) ||
                descriptor.HasFlag(SpellDescriptor.Compulsion))
                result |= CCType.Charm;
            if (descriptor.HasFlag(SpellDescriptor.Fear) ||
                descriptor.HasFlag(SpellDescriptor.Frightened) ||
                descriptor.HasFlag(SpellDescriptor.Shaken))
                result |= CCType.Fear;
            if (descriptor.HasFlag(SpellDescriptor.Daze))
                result |= CCType.Daze;

            // Movement CC
            if (descriptor.HasFlag(SpellDescriptor.MovementImpairing))
                result |= CCType.MovementImpair;

            // Mind-Affecting flag
            if (descriptor.HasFlag(SpellDescriptor.MindAffecting))
                result |= CCType.MindAffecting;

            return result;
        }

        /// <summary>
        /// SpellDescriptor에서 원소 데미지 타입 추출
        /// </summary>
        private static DamageElement AnalyzeDamageElement(SpellDescriptor descriptor, BlueprintAbility bp)
        {
            DamageElement result = DamageElement.None;

            // 원소 타입
            if (descriptor.HasFlag(SpellDescriptor.Fire))
                result |= DamageElement.Fire;
            if (descriptor.HasFlag(SpellDescriptor.Cold))
                result |= DamageElement.Cold;
            if (descriptor.HasFlag(SpellDescriptor.Electricity))
                result |= DamageElement.Electricity;
            if (descriptor.HasFlag(SpellDescriptor.Acid))
                result |= DamageElement.Acid;
            if (descriptor.HasFlag(SpellDescriptor.Sonic))
                result |= DamageElement.Sonic;
            if (descriptor.HasFlag(SpellDescriptor.Force))
                result |= DamageElement.Force;

            // 신성/비전
            if (descriptor.HasFlag(SpellDescriptor.Divine))
                result |= DamageElement.Divine;
            if (descriptor.HasFlag(SpellDescriptor.Arcane))
                result |= DamageElement.Arcane;

            // 채널 에너지
            if (descriptor.HasFlag(SpellDescriptor.ChannelPositiveHarm) ||
                descriptor.HasFlag(SpellDescriptor.ChannelPositiveHeal))
                result |= DamageElement.Positive;
            if (descriptor.HasFlag(SpellDescriptor.ChannelNegativeHarm) ||
                descriptor.HasFlag(SpellDescriptor.ChannelNegativeHeal))
                result |= DamageElement.Negative;

            // 물리 공격 (AbilityType 기반)
            if (bp.Type == AbilityType.Physical || bp.Type == AbilityType.CombatManeuver)
                result |= DamageElement.Physical;

            return result;
        }

        /// <summary>
        /// SpellDescriptor에서 디버프 타입 추출
        /// </summary>
        private static DebuffType AnalyzeDebuffType(SpellDescriptor descriptor)
        {
            DebuffType result = DebuffType.None;

            // 상태이상
            if (descriptor.HasFlag(SpellDescriptor.Poison))
                result |= DebuffType.Poison;
            if (descriptor.HasFlag(SpellDescriptor.Disease))
                result |= DebuffType.Disease;
            if (descriptor.HasFlag(SpellDescriptor.Curse))
                result |= DebuffType.Curse;
            if (descriptor.HasFlag(SpellDescriptor.Bleed))
                result |= DebuffType.Bleed;

            // 스탯 디버프
            if (descriptor.HasFlag(SpellDescriptor.StatDebuff))
                result |= DebuffType.StatDebuff;
            if (descriptor.HasFlag(SpellDescriptor.NegativeLevel))
                result |= DebuffType.NegativeLevel;

            // 컨디션
            if (descriptor.HasFlag(SpellDescriptor.Sickened))
                result |= DebuffType.Sickened;
            if (descriptor.HasFlag(SpellDescriptor.Nauseated))
                result |= DebuffType.Nauseated;
            if (descriptor.HasFlag(SpellDescriptor.Fatigue))
                result |= DebuffType.Fatigued;
            if (descriptor.HasFlag(SpellDescriptor.Exhausted))
                result |= DebuffType.Exhausted;
            if (descriptor.HasFlag(SpellDescriptor.Staggered))
                result |= DebuffType.Staggered;
            if (descriptor.HasFlag(SpellDescriptor.Blindness))
                result |= DebuffType.Blindness;

            // 죽음
            if (descriptor.HasFlag(SpellDescriptor.Death))
                result |= DebuffType.Death;

            return result;
        }

        /// <summary>
        /// 능력 사거리를 미터로 변환
        /// </summary>
        private static float GetRangeInMeters(BlueprintAbility bp)
        {
            try
            {
                // AbilityRange는 복잡한 타입이므로 안전하게 처리
                var range = bp.Range;
                // 기본적인 사거리 추정 (실제 구현은 게임 API에 따라 다름)
                return 30f; // 기본값
            }
            catch
            {
                return 30f;
            }
        }

        /// <summary>
        /// Blueprint에서 타겟팅 타입 분석
        /// </summary>
        private static TargetingType AnalyzeTargetingType(BlueprintAbility bp)
        {
            bool canTargetSelf = bp.CanTargetSelf;
            bool canTargetFriends = bp.CanTargetFriends;
            bool canTargetEnemies = bp.CanTargetEnemies;
            bool isAoE = bp.AoERadius.Meters > 0;

            // 자신만
            if (canTargetSelf && !canTargetFriends && !canTargetEnemies)
                return TargetingType.Self;

            // 접촉
            if (bp.GetComponent<AbilityDeliverTouch>() != null)
                return TargetingType.Touch;

            // AoE
            if (isAoE)
            {
                // 원뿔/직선 체크
                if (bp.GetComponent<AbilityDeliverProjectile>() != null)
                {
                    var projectile = bp.GetComponent<AbilityDeliverProjectile>();
                    if (projectile?.Type == AbilityProjectileType.Cone)
                        return TargetingType.AoECone;
                    if (projectile?.Type == AbilityProjectileType.Line)
                        return TargetingType.AoELine;
                }
                return TargetingType.AoEPoint;
            }

            // 단일 타겟
            if (canTargetEnemies && !canTargetFriends)
                return TargetingType.SingleEnemy;
            if (canTargetFriends && !canTargetEnemies)
                return TargetingType.SingleAlly;

            return TargetingType.None;
        }

        /// <summary>
        /// v0.2.1: 사기저하(Demoralize) 능력인지 감지
        /// ★ v0.2.62: SpellDescriptor 기반으로 변경 (문자열 검색 제거)
        /// </summary>
        private static bool IsDemoralize(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;

            try
            {
                // 1. SpellDescriptor로 Fear/Shaken 감지
                var descriptor = ability.Blueprint.SpellDescriptor;
                bool hasFearDescriptor = (descriptor & (SpellDescriptor.Fear | SpellDescriptor.Shaken)) != 0;
                if (hasFearDescriptor)
                {
                    // Demoralize는 보통 Shaken만 적용 (Fear 아님)
                    // 그리고 타겟이 적이어야 함
                    if (ability.Blueprint.CanTargetEnemies)
                        return true;
                }

                // 2. ContextActionApplyBuff로 Shaken 적용하는지 체크
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is ContextActionApplyBuff applyBuff && applyBuff.Buff != null)
                        {
                            // Shaken 버프 적용하는지 체크
                            var buffDesc = applyBuff.Buff.SpellDescriptor;
                            if ((buffDesc & SpellDescriptor.Shaken) != 0)
                                return true;

                            // 버프가 Shaken 조건을 추가하는지 체크
                            var addCondition = applyBuff.Buff.GetComponent<AddCondition>();
                            if (addCondition?.Condition == UnitCondition.Shaken)
                                return true;
                        }

                        // Demoralize 타입 액션 체크
                        if (action is Demoralize)
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] IsDemoralize error for {ability?.Name}: {ex.Message}");
            }

            return false;
        }

        #endregion

        /// <summary>
        /// 리소스 타입 결정
        /// </summary>
        private static AbilityResourceType DetermineResourceType(AbilityData ability)
        {
            // 레벨 0 스펠 = 캔트립/오리슨 (무한 사용)
            if (ability.SpellLevel == 0)
                return AbilityResourceType.Free;

            // 스펠북에서 사용하는 스펠인지 확인
            if (ability.Spellbook != null)
            {
                // 스펠 슬롯을 사용하는지 확인
                if (!ability.Spellbook.CanSpend(ability))
                    return AbilityResourceType.SpellSlot; // 슬롯 부족

                return AbilityResourceType.SpellSlot;
            }

            // 기타 리소스 확인
            var resourceLogic = ability.Blueprint.GetComponent<IAbilityResourceLogic>();
            if (resourceLogic != null && resourceLogic.IsSpendResource)
            {
                return AbilityResourceType.AbilityResource;
            }

            // 리소스 없음 = 무료
            return AbilityResourceType.Free;
        }

        /// <summary>
        /// 리소스 점수 계산 (0.0 ~ 1.0)
        /// 높을수록 자유롭게 사용 가능
        /// </summary>
        private static float CalculateResourceScore(AbilityData ability, AbilityResourceType resourceType, int spellLevel)
        {
            switch (resourceType)
            {
                case AbilityResourceType.Free:
                    return 1.0f; // 무제한 사용

                case AbilityResourceType.SpellSlot:
                    // 스펠 레벨에 따른 점수 (낮은 레벨 = 높은 점수)
                    // 레벨 1: 0.8, 레벨 5: 0.4, 레벨 9: 0.0
                    return Math.Max(0f, 1f - (spellLevel * 0.1f));

                case AbilityResourceType.AbilityResource:
                    // 리소스 소모 능력은 중간 점수
                    return 0.5f;

                case AbilityResourceType.LimitedUse:
                    // 제한 사용 능력은 낮은 점수
                    return 0.2f;

                default:
                    return 0.5f;
            }
        }

        // v0.2.5: ExtractBuffInfo and FindApplyBuffRecursive 메서드가 제거됨
        // 새로운 캐싱 시스템 GetBuffEffects/CollectBuffsRecursive 사용

        /// <summary>
        /// 타이밍 결정 (자동 분류)
        /// </summary>
        private static AbilityTiming DetermineTiming(AbilityData ability, BlueprintAbility bp,
            BlueprintBuff appliedBuff, bool isPermanentBuff)
        {
            try
            {
                var descriptor = bp.SpellDescriptor;
                bool canTargetEnemies = bp.CanTargetEnemies;
                bool canTargetFriends = bp.CanTargetFriends;
                bool canTargetSelf = bp.CanTargetSelf;
                bool isAoE = bp.AoERadius.Meters > 0;

                // ═══════════════════════════════════════════════════════════════
                // Phase 1: SpellDescriptor 기반 (높은 신뢰도)
                // ═══════════════════════════════════════════════════════════════

                // 힐링 체크
                if (descriptor.HasFlag(SpellDescriptor.Cure) ||
                    descriptor.HasFlag(SpellDescriptor.RestoreHP))
                {
                    return AbilityTiming.Healing;
                }

                // 채널 에너지
                if (descriptor.HasFlag(SpellDescriptor.ChannelNegativeHarm) ||
                    descriptor.HasFlag(SpellDescriptor.ChannelNegativeHeal) ||
                    descriptor.HasFlag(SpellDescriptor.ChannelPositiveHarm) ||
                    descriptor.HasFlag(SpellDescriptor.ChannelPositiveHeal))
                {
                    return AbilityTiming.Channel;
                }

                // 소환
                if (descriptor.HasFlag(SpellDescriptor.Summoning))
                {
                    return AbilityTiming.Summon;
                }

                // ═══════════════════════════════════════════════════════════════
                // Phase 2: 영구 버프 체크
                // v0.2.2: 자신/아군만 타겟 가능한 능력만 영구 버프로 분류
                // Smite Evil처럼 적을 타겟으로 하지만 시전자에게 버프를 주는 능력은 제외
                // ═══════════════════════════════════════════════════════════════

                if (isPermanentBuff && appliedBuff != null)
                {
                    // 적을 타겟으로 하는 능력은 영구 버프가 아님
                    bool isEnemyTargeting = canTargetEnemies && !canTargetSelf && !canTargetFriends;
                    bool isSelfOrAllyBuff = canTargetSelf || (canTargetFriends && !canTargetEnemies);

                    if (isSelfOrAllyBuff)
                    {
                        return AbilityTiming.PermanentBuff;
                    }
                    // 적 타겟팅 + 영구 버프 = 공격으로 분류 (Smite Evil 등)
                    Main.Verbose($"[AbilityClassifier] {ability.Name}: Permanent buff but enemy-targeting, not classifying as PermanentBuff");
                }

                // ═══════════════════════════════════════════════════════════════
                // Phase 3: 타겟팅 기반
                // ═══════════════════════════════════════════════════════════════

                // 적만 타겟 가능 + AoE
                if (canTargetEnemies && isAoE)
                {
                    // 아군도 영향받을 수 있는 AoE는 위험
                    if (canTargetFriends || !bp.EffectOnAlly.Equals(AbilityEffectOnUnit.None))
                    {
                        return AbilityTiming.DangerousAoE;
                    }
                }

                // 적만 타겟 가능 + 비공격 (NotOffensive)
                if (canTargetEnemies && bp.NotOffensive)
                {
                    // 디버프 관련 키워드 체크
                    if (descriptor.HasFlag(SpellDescriptor.Blindness) ||
                        descriptor.HasFlag(SpellDescriptor.Fear) ||
                        descriptor.HasFlag(SpellDescriptor.Stun) ||
                        descriptor.HasFlag(SpellDescriptor.Hex) ||
                        descriptor.HasFlag(SpellDescriptor.Nauseated) ||
                        descriptor.HasFlag(SpellDescriptor.Sickened) ||
                        descriptor.HasFlag(SpellDescriptor.Paralysis) ||
                        descriptor.HasFlag(SpellDescriptor.Confusion))
                    {
                        return AbilityTiming.Debuff;
                    }
                }

                // 적만 타겟 가능 → 공격
                if (canTargetEnemies && !canTargetFriends)
                {
                    return AbilityTiming.Attack;
                }

                // 아군만 타겟 가능 (또는 자신만)
                if ((canTargetFriends || canTargetSelf) && !canTargetEnemies)
                {
                    // 버프가 있으면 버프
                    if (appliedBuff != null)
                    {
                        return AbilityTiming.PreCombatBuff;
                    }

                    // 없어도 아군 대상이면 버프로 간주
                    return AbilityTiming.PreCombatBuff;
                }

                // ═══════════════════════════════════════════════════════════════
                // Phase 4: 기본값
                // ═══════════════════════════════════════════════════════════════

                return AbilityTiming.Normal;
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] DetermineTiming error: {ex.Message}");
                return AbilityTiming.Normal;
            }
        }

        /// <summary>
        /// v0.2.5: 특정 타겟에게 버프가 이미 적용되었는지 확인 (다중 버프 지원)
        /// BubbleBuffs 패턴 - HashSet.Overlaps 사용
        /// </summary>
        public static bool IsBuffAlreadyApplied(AbilityData ability, UnitEntityData target)
        {
            if (target == null || ability?.Blueprint == null)
                return false;

            // 캐시된 버프 효과 가져오기
            var buffEffects = GetBuffEffects(ability);
            if (buffEffects.IsEmpty)
                return false;

            // 하나라도 적용되어 있으면 true (HashSet.Overlaps 사용)
            return buffEffects.IsAnyBuffPresent(target);
        }

        /// <summary>
        /// v0.2.5: 특정 타겟에게 모든 버프가 적용되었는지 확인
        /// </summary>
        public static bool AreAllBuffsApplied(AbilityData ability, UnitEntityData target)
        {
            if (target == null || ability?.Blueprint == null)
                return false;

            var buffEffects = GetBuffEffects(ability);
            if (buffEffects.IsEmpty)
                return false;

            return buffEffects.AreAllBuffsPresent(target);
        }

        /// <summary>
        /// v0.2.5: 스냅샷 기반 버프 체크 (여러 유닛 체크 시 효율적)
        /// </summary>
        public static bool IsBuffAlreadyApplied(AbilityData ability, UnitBuffSnapshot targetSnapshot)
        {
            if (targetSnapshot == null || ability?.Blueprint == null)
                return false;

            var buffEffects = GetBuffEffects(ability);
            if (buffEffects.IsEmpty)
                return false;

            return targetSnapshot.HasAnyBuff(buffEffects.AppliedBuffGuids);
        }

        /// <summary>
        /// 유닛의 모든 사용 가능한 능력 분류
        /// </summary>
        public static List<AbilityClassification> ClassifyAllAbilities(UnitEntityData unit)
        {
            var results = new List<AbilityClassification>();

            if (unit?.Abilities == null)
                return results;

            foreach (var ability in unit.Abilities.Visible)
            {
                if (ability?.Data == null || !ability.Data.IsAvailableForCast)
                    continue;

                var classification = Classify(ability.Data, unit);
                results.Add(classification);
            }

            return results;
        }

        /// <summary>
        /// 영구 버프 중 아직 적용되지 않은 것 찾기
        /// </summary>
        public static List<AbilityClassification> GetUnappliedPermanentBuffs(UnitEntityData unit)
        {
            return ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.PermanentBuff && !c.IsAlreadyApplied)
                .ToList();
        }

        /// <summary>
        /// 힐링 능력 중 사용 가능한 것 찾기 (리소스 점수순 정렬)
        /// </summary>
        public static List<AbilityClassification> GetHealingAbilities(UnitEntityData unit)
        {
            return ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.Healing)
                .OrderByDescending(c => c.ResourceScore) // 자원 효율 좋은 것 우선
                .ToList();
        }

        /// <summary>
        /// 버프 능력 중 사용 가능한 것 찾기 (영구 버프 우선)
        /// </summary>
        public static List<AbilityClassification> GetBuffAbilities(UnitEntityData unit)
        {
            return ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.PreCombatBuff ||
                            c.Timing == AbilityTiming.PermanentBuff)
                .Where(c => !c.IsAlreadyApplied) // 이미 적용된 버프 제외
                .OrderByDescending(c => c.IsPermanentBuff ? 1 : 0) // 영구 버프 우선
                .ThenByDescending(c => c.ResourceScore) // 그 다음 리소스 효율
                .ToList();
        }

        #region v0.2.0 AI Decision Helper Methods

        /// <summary>
        /// 공격 능력 가져오기 (데미지 딜링)
        /// </summary>
        public static List<AbilityClassification> GetAttackAbilities(UnitEntityData unit)
        {
            return ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.Attack ||
                            c.Timing == AbilityTiming.DangerousAoE)
                .OrderByDescending(c => c.IsAoE ? 1 : 0) // AoE 우선 (다중 타겟)
                .ThenByDescending(c => c.SpellLevel) // 높은 레벨 우선
                .ThenByDescending(c => c.ResourceScore)
                .ToList();
        }

        /// <summary>
        /// CC 능력 가져오기 (Hard CC 우선)
        /// </summary>
        public static List<AbilityClassification> GetCCAbilities(UnitEntityData unit)
        {
            return ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.CrowdControl ||
                            c.CCType != CCType.None)
                .OrderByDescending(c => c.IsHardCC ? 2 : (c.IsSoftCC ? 1 : 0)) // Hard CC > Soft CC
                .ThenByDescending(c => c.IsAoE ? 1 : 0) // AoE CC 우선
                .ThenByDescending(c => c.ResourceScore)
                .ToList();
        }

        /// <summary>
        /// 디버프 능력 가져오기
        /// </summary>
        public static List<AbilityClassification> GetDebuffAbilities(UnitEntityData unit)
        {
            return ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.Debuff ||
                            c.DebuffType != DebuffType.None)
                .Where(c => c.CCType == CCType.None) // CC는 제외 (별도 카테고리)
                .OrderByDescending(c => c.IsAoE ? 1 : 0)
                .ThenByDescending(c => c.ResourceScore)
                .ToList();
        }

        /// <summary>
        /// 특정 원소에 저항/면역이 있는지 확인
        /// ★ v0.2.59: UnitPartDamageReduction API 사용
        /// </summary>
        public static bool HasElementResistance(UnitEntityData target, DamageElement element)
        {
            if (target == null)
                return false;

            try
            {
                var drPart = target.Get<UnitPartDamageReduction>();
                if (drPart == null)
                    return false;

                // DamageElement → DamageEnergyType 매핑
                DamageEnergyType? energyType = ElementToEnergyType(element);
                if (!energyType.HasValue)
                    return false;

                // 면역 체크
                if (drPart.IsImmune(energyType.Value))
                {
                    Main.Verbose($"[AbilityClassifier] {target.CharacterName} is immune to {element}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] HasElementResistance error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// DamageElement → DamageEnergyType 변환
        /// </summary>
        private static DamageEnergyType? ElementToEnergyType(DamageElement element)
        {
            switch (element)
            {
                case DamageElement.Fire: return DamageEnergyType.Fire;
                case DamageElement.Cold: return DamageEnergyType.Cold;
                case DamageElement.Electricity: return DamageEnergyType.Electricity;
                case DamageElement.Acid: return DamageEnergyType.Acid;
                case DamageElement.Sonic: return DamageEnergyType.Sonic;
                default: return null;
            }
        }

        /// <summary>
        /// 특정 CC에 면역이 있는지 확인
        /// ★ v0.2.59: UnitState.HasConditionImmunity API 사용
        /// </summary>
        public static bool HasCCImmunity(UnitEntityData target, CCType ccType)
        {
            if (target == null)
                return false;

            try
            {
                var state = target.Descriptor?.State;
                if (state == null)
                    return false;

                // CCType → UnitCondition 매핑하여 면역 체크
                switch (ccType)
                {
                    case CCType.Stun:
                        return state.HasConditionImmunity(UnitCondition.Stunned);

                    case CCType.Fear:
                        return state.HasConditionImmunity(UnitCondition.Frightened) ||
                               state.HasConditionImmunity(UnitCondition.Shaken) ||
                               state.HasConditionImmunity(UnitCondition.Cowering);

                    case CCType.Paralysis:
                        return state.HasConditionImmunity(UnitCondition.Paralyzed);

                    case CCType.Sleep:
                        return state.HasConditionImmunity(UnitCondition.Sleeping);

                    case CCType.Blind:
                        return state.HasConditionImmunity(UnitCondition.Blindness);

                    case CCType.Confusion:
                        return state.HasConditionImmunity(UnitCondition.Confusion);

                    case CCType.Slow:
                        return state.HasConditionImmunity(UnitCondition.Slowed);

                    case CCType.Entangle:
                        return state.HasConditionImmunity(UnitCondition.Entangled);

                    case CCType.Nauseate:
                        return state.HasConditionImmunity(UnitCondition.Nauseated);

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] HasCCImmunity error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.59: 언데드/구조물 여부 확인
        /// </summary>
        public static bool IsUndeadOrConstruct(UnitEntityData target)
        {
            if (target == null)
                return false;

            try
            {
                // 언데드 체크
                var state = target.Descriptor?.State;
                if (state != null)
                {
                    // 언데드는 일반적으로 정신 영향 면역
                    if (state.HasConditionImmunity(UnitCondition.Sleeping) &&
                        state.HasConditionImmunity(UnitCondition.Stunned))
                    {
                        // 추가 확인: 크리처 타입 (가능한 경우)
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.59: 죽음 면역 여부 확인
        /// </summary>
        public static bool HasDeathImmunity(UnitEntityData target)
        {
            if (target == null)
                return false;

            try
            {
                // 죽음 면역은 특정 버프나 특성으로 확인
                // UnitPartSpellResistance로 Death descriptor 면역 체크
                var srPart = target.Get<UnitPartSpellResistance>();
                if (srPart == null)
                    return false;

                // 일부 크리처는 죽음 효과 면역 (언데드, 구조물 등)
                return IsUndeadOrConstruct(target);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 특정 타겟에게 능력이 효과적인지 평가 (0.0 ~ 1.0)
        /// 높을수록 효과적
        /// ★ v0.2.59: 실제 면역/저항 API 사용
        /// </summary>
        public static float EvaluateEffectiveness(AbilityClassification ability, UnitEntityData target)
        {
            if (ability == null || target == null)
                return 0f;

            float score = 1.0f;

            // ★ v0.2.59: Use Ability?.Blueprint?.Name for logging
            string abilityName = ability.Ability?.Blueprint?.Name ?? "Unknown";

            // 원소 저항/면역 체크
            if (ability.DamageElement != DamageElement.None)
            {
                if (HasElementResistance(target, ability.DamageElement))
                {
                    Main.Verbose($"[AbilityClassifier] {abilityName}: {target.CharacterName} has {ability.DamageElement} immunity");
                    return 0f;  // 면역이면 0
                }
            }

            // CC 면역 체크
            if (ability.CCType != CCType.None)
            {
                if (HasCCImmunity(target, ability.CCType))
                {
                    Main.Verbose($"[AbilityClassifier] {abilityName}: {target.CharacterName} has {ability.CCType} immunity");
                    return 0f;  // CC 면역이면 사용 불가
                }

                // 정신 영향 + 언데드/구조물 체크
                if (ability.IsMindAffecting)
                {
                    if (IsUndeadOrConstruct(target))
                    {
                        Main.Verbose($"[AbilityClassifier] {abilityName}: {target.CharacterName} is undead/construct (mind-affecting immune)");
                        return 0f;
                    }
                }
            }

            // 죽음 효과 면역 체크
            if (ability.IsDeathEffect)
            {
                if (HasDeathImmunity(target))
                {
                    Main.Verbose($"[AbilityClassifier] {abilityName}: {target.CharacterName} has death immunity");
                    return 0f;
                }
            }

            return score;
        }

        /// <summary>
        /// 특정 타겟에게 가장 효과적인 공격 능력 선택
        /// </summary>
        public static AbilityClassification GetBestAttackAbilityForTarget(
            UnitEntityData caster,
            UnitEntityData target,
            bool preferAoE = false)
        {
            var attacks = GetAttackAbilities(caster);

            if (attacks.Count == 0)
                return null;

            // 효과성 평가하여 정렬
            var ranked = attacks
                .Select(a => new
                {
                    Ability = a,
                    Effectiveness = EvaluateEffectiveness(a, target)
                })
                .Where(x => x.Effectiveness > 0) // 면역인 것 제외
                .OrderByDescending(x => x.Effectiveness)
                .ThenByDescending(x => preferAoE && x.Ability.IsAoE ? 1 : 0)
                .ThenByDescending(x => x.Ability.ResourceScore)
                .FirstOrDefault();

            return ranked?.Ability;
        }

        /// <summary>
        /// 특정 타겟에게 가장 효과적인 CC 능력 선택
        /// </summary>
        public static AbilityClassification GetBestCCAbilityForTarget(
            UnitEntityData caster,
            UnitEntityData target)
        {
            var ccAbilities = GetCCAbilities(caster);

            if (ccAbilities.Count == 0)
                return null;

            // 효과성 평가 + CC 면역 체크
            var ranked = ccAbilities
                .Select(a => new
                {
                    Ability = a,
                    Effectiveness = EvaluateEffectiveness(a, target)
                })
                .Where(x => x.Effectiveness > 0)
                .OrderByDescending(x => x.Ability.IsHardCC ? 2 : 1) // Hard CC 우선
                .ThenByDescending(x => x.Effectiveness)
                .ThenByDescending(x => x.Ability.ResourceScore)
                .FirstOrDefault();

            return ranked?.Ability;
        }

        /// <summary>
        /// 상황에 맞는 최적의 능력 추천
        /// </summary>
        public static AbilityClassification RecommendAbility(
            UnitEntityData caster,
            UnitEntityData primaryTarget,
            int enemyCount,
            float casterHPPercent,
            bool needsHealing)
        {
            // 1. 자신이 위험하면 자가 힐 우선
            if (needsHealing && casterHPPercent < 50)
            {
                var heals = GetHealingAbilities(caster);
                if (heals.Count > 0)
                    return heals.First();
            }

            // 2. 적이 많으면 AoE CC 고려
            if (enemyCount >= 3)
            {
                var aoeCC = GetCCAbilities(caster)
                    .FirstOrDefault(c => c.IsAoE && c.IsHardCC);
                if (aoeCC != null && EvaluateEffectiveness(aoeCC, primaryTarget) > 0)
                    return aoeCC;
            }

            // 3. 강력한 적에게 CC
            if (primaryTarget != null)
            {
                var cc = GetBestCCAbilityForTarget(caster, primaryTarget);
                if (cc != null && cc.IsHardCC)
                    return cc;
            }

            // 4. 기본 공격
            if (primaryTarget != null)
            {
                var attack = GetBestAttackAbilityForTarget(caster, primaryTarget, enemyCount >= 2);
                if (attack != null)
                    return attack;
            }

            // 5. 버프
            var buffs = GetBuffAbilities(caster);
            if (buffs.Count > 0)
                return buffs.First();

            return null;
        }

        #endregion

        #region v0.2.62 String-free Detection APIs

        /// <summary>
        /// ★ v0.2.62: 스닉 어택 보유 여부 (게임 API 사용, 문자열 검색 제거)
        /// </summary>
        public static bool HasSneakAttack(UnitEntityData unit)
        {
            if (unit?.Stats?.SneakAttack == null)
                return false;

            return unit.Stats.SneakAttack.BaseValue > 0;
        }

        /// <summary>
        /// ★ v0.2.62: Charge 능력 여부 (컴포넌트 타입 체크, 문자열 검색 제거)
        /// </summary>
        public static bool IsChargeAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null)
                return false;

            return ability.Blueprint.GetComponent<AbilityCustomCharge>() != null;
        }

        /// <summary>
        /// ★ v0.2.62: CC 해제 능력 정보
        /// </summary>
        public class CCRemovalInfo
        {
            public bool RemovesFear { get; set; }        // SpellDescriptor.Fear 해제
            public bool RemovesParalysis { get; set; }   // SpellDescriptor.Paralysis 해제
            public bool RemovesMovement { get; set; }    // MovementImpairing 해제
            public bool RemovesDisease { get; set; }     // SpellDescriptor.Disease 해제
            public bool RemovesCurse { get; set; }       // SpellDescriptor.Curse 해제
            public bool RemovesPoison { get; set; }      // SpellDescriptor.Poison 해제
            public bool RemovesAny { get; set; }         // DispelMagic, BreakEnchantment
            public bool IsRestoration { get; set; }      // Restoration 계열
            public bool IsHeal { get; set; }             // Heal 계열 (모든 상태이상 해제)

            /// <summary>특정 조건 해제 가능 여부</summary>
            public bool CanRemove(UnitCondition condition)
            {
                if (RemovesAny || IsHeal) return true;

                switch (condition)
                {
                    case UnitCondition.Frightened:
                    case UnitCondition.Shaken:
                        return RemovesFear;

                    case UnitCondition.Paralyzed:
                        return RemovesParalysis;

                    case UnitCondition.Entangled:
                    case UnitCondition.CantMove:
                    case UnitCondition.MovementBan:
                        return RemovesMovement;

                    case UnitCondition.Nauseated:
                    case UnitCondition.Sickened:
                        return RemovesDisease || IsRestoration;

                    case UnitCondition.Confusion:
                        return RemovesAny;

                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// ★ v0.2.62: CC 해제 능력 캐시 (GUID 기반)
        /// </summary>
        private static readonly Dictionary<string, CCRemovalInfo> _ccRemovalCache
            = new Dictionary<string, CCRemovalInfo>();

        /// <summary>
        /// ★ v0.2.62: CC 해제 능력 정보 가져오기 (캐싱)
        /// </summary>
        public static CCRemovalInfo GetCCRemovalInfo(BlueprintAbility blueprint)
        {
            if (blueprint == null) return null;

            string key = blueprint.AssetGuid.ToString();
            if (_ccRemovalCache.TryGetValue(key, out var cached))
                return cached;

            var info = AnalyzeCCRemoval(blueprint);
            _ccRemovalCache[key] = info;
            return info;
        }

        /// <summary>
        /// ★ v0.2.62: CC 해제 능력 분석 (SpellDescriptor 기반)
        /// </summary>
        private static CCRemovalInfo AnalyzeCCRemoval(BlueprintAbility blueprint)
        {
            var info = new CCRemovalInfo();

            try
            {
                // 1. SpellDescriptor 분석
                var descriptor = blueprint.SpellDescriptor;

                // Heal 계열 감지 (SpellDescriptor.RestoreHP + 상태이상 해제 효과 확인)
                // Heal 주문은 RestoreHP와 함께 상태이상 해제 효과가 있음
                if ((descriptor & SpellDescriptor.RestoreHP) != 0)
                {
                    // Heal 주문인지 확인 (Heal은 보통 많은 HP 회복 + 상태이상 해제)
                    // ContextActionHealStatDamage나 ContextActionRemoveBuffsByDescriptor가 있으면 Heal 계열
                    info.IsHeal = false;  // 나중에 컴포넌트 분석에서 설정
                }

                // 2. 컴포넌트 분석 (ContextActionRemoveBuff, ContextActionDispelMagic 등)
                var runAction = blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action == null) continue;

                        // ContextActionDispelMagic - 마법 해제
                        if (action is ContextActionDispelMagic)
                        {
                            info.RemovesAny = true;
                        }

                        // ContextActionRemoveBuff - 특정 버프 제거
                        if (action is ContextActionRemoveBuff removeBuff && removeBuff.Buff != null)
                        {
                            var buffDesc = removeBuff.Buff.SpellDescriptor;
                            if ((buffDesc & SpellDescriptor.Fear) != 0 ||
                                (buffDesc & SpellDescriptor.Shaken) != 0 ||
                                (buffDesc & SpellDescriptor.Frightened) != 0)
                                info.RemovesFear = true;

                            if ((buffDesc & SpellDescriptor.Paralysis) != 0)
                                info.RemovesParalysis = true;

                            if ((buffDesc & SpellDescriptor.MovementImpairing) != 0)
                                info.RemovesMovement = true;

                            if ((buffDesc & SpellDescriptor.Disease) != 0)
                                info.RemovesDisease = true;

                            if ((buffDesc & SpellDescriptor.Curse) != 0)
                                info.RemovesCurse = true;

                            if ((buffDesc & SpellDescriptor.Poison) != 0)
                                info.RemovesPoison = true;
                        }

                        // ContextActionRemoveBuffsByDescriptor - 타입별 버프 제거
                        if (action is ContextActionRemoveBuffsByDescriptor removeByDesc)
                        {
                            var targetDesc = removeByDesc.SpellDescriptor;
                            if ((targetDesc & SpellDescriptor.Fear) != 0)
                                info.RemovesFear = true;
                            if ((targetDesc & SpellDescriptor.Paralysis) != 0)
                                info.RemovesParalysis = true;
                            if ((targetDesc & SpellDescriptor.MovementImpairing) != 0)
                                info.RemovesMovement = true;
                            if ((targetDesc & SpellDescriptor.Disease) != 0)
                                info.RemovesDisease = true;
                            if ((targetDesc & SpellDescriptor.Curse) != 0)
                                info.RemovesCurse = true;
                            if ((targetDesc & SpellDescriptor.Poison) != 0)
                                info.RemovesPoison = true;
                        }

                        // Restoration 계열 (HealStatDamage, RestoreResource 등)
                        if (action is ContextActionHealStatDamage)
                        {
                            info.IsRestoration = true;
                        }
                    }
                }

                // 3. AbilityExecuteActionOnCast 등 다른 컴포넌트도 체크
                var executeOnCast = blueprint.GetComponent<AbilityExecuteActionOnCast>();
                if (executeOnCast?.Actions?.Actions != null)
                {
                    foreach (var action in executeOnCast.Actions.Actions)
                    {
                        if (action is ContextActionDispelMagic)
                            info.RemovesAny = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[AbilityClassifier] AnalyzeCCRemoval error for {blueprint?.name}: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// ★ v0.2.62: CC 해제 캐시 초기화
        /// </summary>
        public static void ClearCCRemovalCache()
        {
            _ccRemovalCache.Clear();
            Main.Log("[AbilityClassifier] CC Removal cache cleared");
        }

        #endregion

        #region ★ v0.2.100: Combat Blacklist (Shared)

        /// <summary>
        /// ★ v0.2.101: 전투에서 사용하면 안 되는 능력인지 확인 (공유 메서드)
        /// SituationAnalyzer와 UnifiedDecisionEngine 모두에서 사용
        /// 버프 효과 분석 (AppliesFatigue 등)을 활용하여 정확한 감지
        /// ★ v0.2.110: HasVariants 체크 추가 - 부모 능력(키네티시스트 블라스트 등) 제외
        /// </summary>
        public static bool IsBlacklistedForCombat(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;

            try
            {
                var bp = ability.Blueprint;
                var descriptor = bp.SpellDescriptor;

                // ★ v0.2.110: 부모 능력 체크 (하위 메뉴가 있는 능력)
                // 키네티시스트 블라스트, 원소 공격 등은 부모 능력이며 직접 시전 불가
                // 게임 API: IsSuitableForAutoUse도 HasVariants를 체크함
                if (bp.HasVariants)
                {
                    Main.Verbose($"[AbilityClassifier] BLACKLISTED: {ability.Name} (Parent ability with variants - not directly castable)");
                    return true;
                }

                // 1. Demoralize 액션 포함 능력 블랙리스트 (AI가 제대로 사용 못함)
                var runAction = bp.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is Demoralize)
                        {
                            Main.Verbose($"[AbilityClassifier] BLACKLISTED: {ability.Name} (Demoralize action)");
                            return true;
                        }
                    }
                }

                // ★ v0.2.107: 터치 스펠 체크 (SpellLevel과 무관)
                // 피로의 손길 같은 능력이 SpellLevel 0이 아닐 수 있음 (위치 헥스 등)
                bool isTouch = bp.GetComponent<AbilityDeliverTouch>() != null ||
                               bp.GetComponent<AbilityEffectStickyTouch>() != null;
                bool canTargetEnemy = bp.CanTargetEnemies;

                if (isTouch && canTargetEnemy)
                {
                    // 데미지와 Fatigue 디버프 분석
                    bool hasDamage = false;
                    bool appliesFatigueDebuff = false;

                    if (runAction?.Actions?.Actions != null)
                    {
                        AnalyzeActionsForDebuffs(runAction.Actions.Actions, ref hasDamage, ref appliesFatigueDebuff, 0);
                    }

                    // ★ v0.2.107: Fatigue 디버프만 적용하는 터치 스펠 = 무쓸모 (SpellLevel 무관)
                    if (appliesFatigueDebuff && !hasDamage)
                    {
                        Main.Log($"[AbilityClassifier] BLACKLISTED: {ability.Name} (Fatigue-only touch spell, SpellLevel={ability.SpellLevel})");
                        return true;
                    }

                    // SpellDescriptor.Fatigue가 있는 터치 스펠 = 무쓸모
                    if ((descriptor & SpellDescriptor.Fatigue) != 0 && !hasDamage)
                    {
                        Main.Log($"[AbilityClassifier] BLACKLISTED: {ability.Name} (Fatigue descriptor touch spell)");
                        return true;
                    }
                }

                // 2. 캔트립(0레벨) 전용 블랙리스트
                if (ability.SpellLevel == 0)
                {
                    // 적에게 사용하는 터치 캔트립 + 데미지 없음 = 무쓸모
                    if (canTargetEnemy && isTouch)
                    {
                        bool hasDamage = false;
                        bool appliesFatigueDebuff = false;
                        if (runAction?.Actions?.Actions != null)
                        {
                            AnalyzeActionsForDebuffs(runAction.Actions.Actions, ref hasDamage, ref appliesFatigueDebuff, 0);
                        }
                        if (!hasDamage)
                        {
                            Main.Log($"[AbilityClassifier] BLACKLISTED: {ability.Name} (Enemy-targeting touch cantrip with no damage)");
                            return true;
                        }
                    }

                    // Daze: HD 제한이 너무 낮아서 전투에서 거의 쓸모없음
                    if ((descriptor & SpellDescriptor.Daze) != 0)
                    {
                        Main.Verbose($"[AbilityClassifier] BLACKLISTED: {ability.Name} (Daze cantrip - low HD limit)");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[AbilityClassifier] IsBlacklistedForCombat error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.103: 액션 트리를 재귀적으로 분석하여 데미지와 Fatigue 디버프 감지
        /// Touch of Fatigue처럼 적에게 Fatigue 디버프만 적용하는 능력 감지용
        /// </summary>
        private static void AnalyzeActionsForDebuffs(GameAction[] actions, ref bool hasDamage, ref bool appliesFatigueDebuff, int depth)
        {
            if (actions == null || depth > 10) return;

            foreach (var action in actions)
            {
                if (action == null) continue;

                // 데미지 액션 체크
                if (action is ContextActionDealDamage)
                {
                    hasDamage = true;
                }

                // 버프/디버프 적용 체크
                if (action is ContextActionApplyBuff applyBuff && applyBuff.Buff != null)
                {
                    try
                    {
                        // 버프가 Fatigue/Exhausted 조건을 적용하는지 직접 체크
                        var addCondition = applyBuff.Buff.GetComponent<AddCondition>();
                        if (addCondition != null)
                        {
                            if (addCondition.Condition == UnitCondition.Fatigued ||
                                addCondition.Condition == UnitCondition.Exhausted)
                            {
                                appliesFatigueDebuff = true;
                                Main.Verbose($"[AbilityClassifier] Found Fatigue debuff in buff: {applyBuff.Buff.name}");
                            }
                        }
                    }
                    catch { }
                }

                // 재귀: Conditional
                if (action is Conditional conditional)
                {
                    AnalyzeActionsForDebuffs(conditional.IfTrue?.Actions, ref hasDamage, ref appliesFatigueDebuff, depth + 1);
                    AnalyzeActionsForDebuffs(conditional.IfFalse?.Actions, ref hasDamage, ref appliesFatigueDebuff, depth + 1);
                }

                // 재귀: ContextActionSavingThrow (내성 굴림 후 효과)
                if (action is ContextActionSavingThrow savingThrow)
                {
                    AnalyzeActionsForDebuffs(savingThrow.Actions?.Actions, ref hasDamage, ref appliesFatigueDebuff, depth + 1);
                }

                // 재귀: ContextActionConditionalSaved
                if (action is ContextActionConditionalSaved conditionalSaved)
                {
                    AnalyzeActionsForDebuffs(conditionalSaved.Failed?.Actions, ref hasDamage, ref appliesFatigueDebuff, depth + 1);
                    AnalyzeActionsForDebuffs(conditionalSaved.Succeed?.Actions, ref hasDamage, ref appliesFatigueDebuff, depth + 1);
                }
            }
        }

        #endregion
    }
}
