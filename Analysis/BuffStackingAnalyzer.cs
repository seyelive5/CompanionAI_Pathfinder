// ★ v0.2.64: Generic Buff Stacking Analysis
// ModifierDescriptor 기반 보너스 충돌 감지
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Buffs.Blueprints;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// ★ v0.2.64: 버프 보너스 정보
    /// </summary>
    public class BuffBonusInfo
    {
        public StatType Stat { get; set; }
        public ModifierDescriptor Descriptor { get; set; }
        public int Value { get; set; }
        public string ComponentType { get; set; }

        public override string ToString()
        {
            return $"{Stat}+{Value}({Descriptor})";
        }
    }

    /// <summary>
    /// ★ v0.2.64: 버프 스태킹 분석 결과
    /// </summary>
    public class StackingAnalysisResult
    {
        /// <summary>버프가 효과적인지</summary>
        public bool IsEffective { get; set; } = true;

        /// <summary>충돌 이유</summary>
        public string Reason { get; set; } = "";

        /// <summary>충돌하는 보너스 목록</summary>
        public List<string> ConflictingBonuses { get; } = new List<string>();

        /// <summary>버프가 제공하는 보너스 목록</summary>
        public List<BuffBonusInfo> ProvidedBonuses { get; } = new List<BuffBonusInfo>();
    }

    /// <summary>
    /// ★ v0.2.64: 범용 버프 스태킹 분석기
    /// - AddStatBonus 컴포넌트에서 ModifierDescriptor 추출
    /// - 대상의 현재 보너스와 비교
    /// - 효과 여부 판정
    /// </summary>
    public static class BuffStackingAnalyzer
    {
        #region Cache

        /// <summary>버프 GUID → 보너스 정보 캐시</summary>
        private static readonly Dictionary<string, List<BuffBonusInfo>> _buffBonusCache
            = new Dictionary<string, List<BuffBonusInfo>>();

        /// <summary>캐시 클리어</summary>
        public static void ClearCache()
        {
            _buffBonusCache.Clear();
            Main.Log("[BuffStackingAnalyzer] Cache cleared");
        }

        #endregion

        #region Public API

        /// <summary>
        /// 버프가 대상에게 효과적인지 분석
        /// </summary>
        public static StackingAnalysisResult Analyze(BlueprintBuff buff, UnitEntityData target)
        {
            var result = new StackingAnalysisResult();

            if (buff == null || target == null)
                return result;

            try
            {
                // 1. 버프의 보너스 정보 추출 (캐시 사용)
                var bonuses = GetBuffBonuses(buff);
                result.ProvidedBonuses.AddRange(bonuses);

                if (bonuses.Count == 0)
                {
                    // 보너스가 없는 버프 = 효과 있음 (다른 효과가 있을 수 있음)
                    return result;
                }

                // 2. 각 보너스에 대해 충돌 체크
                foreach (var bonus in bonuses)
                {
                    if (!IsDescriptorStackable(bonus.Descriptor))
                    {
                        // 비스태킹 보너스 - 기존 값과 비교
                        int existingBonus = GetExistingBonus(target, bonus.Stat, bonus.Descriptor);

                        if (existingBonus >= bonus.Value)
                        {
                            result.ConflictingBonuses.Add(
                                $"{bonus.Stat}:{bonus.Descriptor} (existing={existingBonus}, buff={bonus.Value})");
                        }
                    }
                }

                // 3. 결과 판정
                if (result.ConflictingBonuses.Count > 0)
                {
                    // 모든 보너스가 충돌하면 효과 없음
                    if (result.ConflictingBonuses.Count >= bonuses.Count)
                    {
                        result.IsEffective = false;
                        result.Reason = $"All bonuses superseded: {string.Join(", ", result.ConflictingBonuses)}";
                    }
                    else
                    {
                        // 일부만 충돌 - 여전히 효과 있음 (부분적)
                        result.Reason = $"Partial conflict: {string.Join(", ", result.ConflictingBonuses)}";
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[BuffStackingAnalyzer] Analyze error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 버프가 대상에게 효과적인지 간단 체크
        /// ★ v0.2.69: 특수 케이스 (Mage Armor, Shield) 통합
        /// </summary>
        public static bool WouldBeEffective(BlueprintBuff buff, UnitEntityData target)
        {
            if (buff == null || target == null)
                return true;

            // ★ v0.2.69: 특수 케이스 먼저 체크
            string buffName = buff.Name?.ToLowerInvariant() ?? "";
            string bpName = buff.name?.ToLowerInvariant() ?? "";

            // Mage Armor 체크 (Armor 보너스 - 갑옷 착용 시 무효)
            if (buffName.Contains("mage armor") || buffName.Contains("마법사 갑옷") ||
                bpName.Contains("magearmor"))
            {
                if (!IsMageArmorEffective(target))
                {
                    Main.Verbose($"[BuffStackingAnalyzer] Mage Armor ineffective on {target.CharacterName}: wearing armor");
                    return false;
                }
            }

            // Shield 스펠 체크 (Shield 보너스 - 방패 착용 시 무효)
            // Shield 스펠 이름: "Shield", "쉴드", "방패" (방어 버프)
            if ((buffName.Contains("shield") && !buffName.Contains("faith")) ||
                buffName.Contains("쉴드") ||
                bpName.Contains("shieldbuff"))
            {
                if (!IsShieldSpellEffective(target))
                {
                    Main.Verbose($"[BuffStackingAnalyzer] Shield spell ineffective on {target.CharacterName}: has shield equipped");
                    return false;
                }
            }

            // 일반 스태킹 분석
            var result = Analyze(buff, target);
            return result.IsEffective;
        }

        #endregion

        #region Bonus Extraction

        /// <summary>
        /// 버프에서 보너스 정보 추출 (캐시 사용)
        /// </summary>
        private static List<BuffBonusInfo> GetBuffBonuses(BlueprintBuff buff)
        {
            string guid = buff.AssetGuid.ToString();

            if (_buffBonusCache.TryGetValue(guid, out var cached))
                return cached;

            var bonuses = ExtractBonusesFromBuff(buff);
            _buffBonusCache[guid] = bonuses;

            if (bonuses.Count > 0)
            {
                Main.Verbose($"[BuffStackingAnalyzer] {buff.Name}: {string.Join(", ", bonuses)}");
            }

            return bonuses;
        }

        /// <summary>
        /// 버프 컴포넌트에서 보너스 정보 추출
        /// </summary>
        private static List<BuffBonusInfo> ExtractBonusesFromBuff(BlueprintBuff buff)
        {
            var bonuses = new List<BuffBonusInfo>();

            if (buff?.ComponentsArray == null)
                return bonuses;

            foreach (var component in buff.ComponentsArray)
            {
                if (component == null) continue;

                var componentType = component.GetType();
                string typeName = componentType.Name;

                // AddStatBonus 및 유사 컴포넌트 처리
                if (typeName.Contains("AddStatBonus") ||
                    typeName.Contains("AddContextStatBonus") ||
                    typeName.Contains("AddGenericStatBonus"))
                {
                    var bonus = ExtractFromAddStatBonus(component, componentType);
                    if (bonus != null)
                    {
                        bonus.ComponentType = typeName;
                        bonuses.Add(bonus);
                    }
                }
                // AC 보너스 컴포넌트
                else if (typeName.Contains("ACBonus") || typeName.Contains("ArmorClass"))
                {
                    var bonus = ExtractFromACBonus(component, componentType);
                    if (bonus != null)
                    {
                        bonus.ComponentType = typeName;
                        bonuses.Add(bonus);
                    }
                }
            }

            return bonuses;
        }

        /// <summary>
        /// AddStatBonus 컴포넌트에서 정보 추출
        /// </summary>
        private static BuffBonusInfo ExtractFromAddStatBonus(BlueprintComponent component, Type componentType)
        {
            try
            {
                // Descriptor 필드 찾기
                var descriptorField = componentType.GetField("Descriptor",
                    BindingFlags.Public | BindingFlags.Instance);
                if (descriptorField == null)
                    descriptorField = componentType.GetField("m_Descriptor",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                // Stat 필드 찾기
                var statField = componentType.GetField("Stat",
                    BindingFlags.Public | BindingFlags.Instance);
                if (statField == null)
                    statField = componentType.GetField("m_Stat",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                // Value 필드 찾기
                var valueField = componentType.GetField("Value",
                    BindingFlags.Public | BindingFlags.Instance);
                if (valueField == null)
                    valueField = componentType.GetField("m_Value",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                if (descriptorField == null || statField == null)
                    return null;

                var descriptor = descriptorField.GetValue(component);
                var stat = statField.GetValue(component);
                var value = valueField?.GetValue(component);

                if (descriptor == null || stat == null)
                    return null;

                return new BuffBonusInfo
                {
                    Descriptor = (ModifierDescriptor)descriptor,
                    Stat = (StatType)stat,
                    Value = value != null ? Convert.ToInt32(value) : 0
                };
            }
            catch (Exception ex)
            {
                Main.Verbose($"[BuffStackingAnalyzer] ExtractFromAddStatBonus error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// AC 보너스 컴포넌트에서 정보 추출
        /// </summary>
        private static BuffBonusInfo ExtractFromACBonus(BlueprintComponent component, Type componentType)
        {
            try
            {
                // Descriptor 필드 찾기
                var descriptorField = componentType.GetField("Descriptor",
                    BindingFlags.Public | BindingFlags.Instance) ??
                    componentType.GetField("m_Descriptor",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                // Value 필드 찾기
                var valueField = componentType.GetField("Value",
                    BindingFlags.Public | BindingFlags.Instance) ??
                    componentType.GetField("m_Value",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                var descriptor = descriptorField?.GetValue(component);
                var value = valueField?.GetValue(component);

                // AC 보너스는 기본적으로 AC 스탯에 적용
                return new BuffBonusInfo
                {
                    Descriptor = descriptor != null ? (ModifierDescriptor)descriptor : ModifierDescriptor.UntypedStackable,
                    Stat = StatType.AC,
                    Value = value != null ? Convert.ToInt32(value) : 0
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Stacking Rules

        /// <summary>
        /// ModifierDescriptor가 스태킹 가능한지 확인
        /// </summary>
        private static bool IsDescriptorStackable(ModifierDescriptor descriptor)
        {
            // 스태킹 가능한 타입들
            switch (descriptor)
            {
                case ModifierDescriptor.None:
                case ModifierDescriptor.Racial:
                case ModifierDescriptor.Dodge:
                case ModifierDescriptor.UntypedStackable:
                case ModifierDescriptor.Feat:
                case ModifierDescriptor.Penalty:
                case ModifierDescriptor.NaturalArmor: // 기본 자연 갑옷은 스택
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 대상의 현재 보너스 값 조회
        /// </summary>
        private static int GetExistingBonus(UnitEntityData target, StatType statType, ModifierDescriptor descriptor)
        {
            try
            {
                var stat = target.Stats?.GetStat(statType);
                if (stat == null) return 0;

                // GetDescriptorBonus 메서드 사용
                // 해당 Descriptor의 총 보너스 반환
                int total = 0;

                // Modifiers 컬렉션에서 해당 Descriptor의 보너스 합산
                foreach (var modifier in stat.Modifiers)
                {
                    if (modifier.ModDescriptor == descriptor)
                    {
                        // 비스태킹 보너스는 최고값만 적용
                        if (!IsDescriptorStackable(descriptor))
                        {
                            total = Math.Max(total, modifier.ModValue);
                        }
                        else
                        {
                            total += modifier.ModValue;
                        }
                    }
                }

                return total;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[BuffStackingAnalyzer] GetExistingBonus error: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Special Cases

        /// <summary>
        /// ★ v0.2.69: 버프가 Armor 보너스를 제공하는지 확인
        /// Mage Armor, Bracers of Armor 등
        /// </summary>
        public static bool ProvidesArmorBonus(BlueprintBuff buff)
        {
            if (buff == null) return false;

            try
            {
                var bonuses = GetBuffBonuses(buff);
                return bonuses.Any(b =>
                    b.Stat == StatType.AC &&
                    b.Descriptor == ModifierDescriptor.Armor);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.69: 버프가 Shield 보너스를 제공하는지 확인
        /// Shield 스펠 등
        /// </summary>
        public static bool ProvidesShieldBonus(BlueprintBuff buff)
        {
            if (buff == null) return false;

            try
            {
                var bonuses = GetBuffBonuses(buff);
                return bonuses.Any(b =>
                    b.Stat == StatType.AC &&
                    b.Descriptor == ModifierDescriptor.Shield);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 특수 케이스: Mage Armor는 갑옷 착용 시 무효
        /// ★ v0.2.69: 실제 갑옷의 AC 보너스도 비교
        /// </summary>
        public static bool IsMageArmorEffective(UnitEntityData target)
        {
            try
            {
                var armor = target?.Body?.Armor?.MaybeArmor;
                if (armor == null) return true;

                var blueprint = armor.Blueprint;
                if (blueprint == null) return true;

                // 실제 갑옷(Light/Medium/Heavy)이면 Mage Armor 무효
                // Bracers of Armor, Robes 등은 갑옷이 아님
                var profGroup = blueprint.ProficiencyGroup;
                if (profGroup == Kingmaker.Blueprints.Items.Armors.ArmorProficiencyGroup.Light ||
                    profGroup == Kingmaker.Blueprints.Items.Armors.ArmorProficiencyGroup.Medium ||
                    profGroup == Kingmaker.Blueprints.Items.Armors.ArmorProficiencyGroup.Heavy)
                {
                    // 갑옷의 AC 보너스가 있으면 Mage Armor (+4) 무효
                    // Mage Armor = +4 Armor bonus
                    int armorAC = blueprint.ArmorBonus;
                    if (armorAC >= 4) // Mage Armor는 +4
                        return false;

                    // 갑옷 AC가 Mage Armor보다 낮아도 스택 안됨
                    // 패스파인더에서 Armor 보너스는 스택하지 않음
                    return false;
                }

                return true; // 로브/브레이서 등은 Mage Armor와 호환
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 특수 케이스: Shield 스펠은 방패 착용 시 무효
        /// ★ v0.2.69: 방패의 Shield 보너스와 비교
        /// </summary>
        public static bool IsShieldSpellEffective(UnitEntityData target)
        {
            try
            {
                var shield = target?.Body?.SecondaryHand?.MaybeShield;
                if (shield == null) return true;

                // 방패가 있으면 Shield 스펠 (+4 Shield) 무효
                // Shield 보너스는 스택하지 않음
                return false;
            }
            catch
            {
                return true;
            }
        }

        #endregion
    }
}
