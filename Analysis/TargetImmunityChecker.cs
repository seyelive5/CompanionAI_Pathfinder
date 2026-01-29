// ★ v0.2.48: TargetImmunityChecker - 타겟 면역 검사
// 능력의 SpellDescriptor와 타겟의 BuffDescriptorImmunity 매칭
// 악의 눈(Mind-Affecting) → 드레치(악마 면역) 같은 케이스 감지
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.FactLogic;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// ★ v0.2.48: 타겟의 SpellDescriptor 면역 검사
    ///
    /// 문제: CanTarget()은 "이미 적용된 버프" 체크만 수행
    ///       Mind-Affecting 면역 같은 SpellDescriptor 면역은 체크 안 함
    ///       → 악의 눈이 드레치에게 계속 시도되는 무한 루프 발생
    ///
    /// 해결: 능력의 SpellDescriptor와 타겟의 BuffDescriptorImmunity 매칭
    /// </summary>
    public static class TargetImmunityChecker
    {
        #region Cache

        // 면역 정보 캐시 (유닛별)
        private static readonly Dictionary<string, CachedImmunity> _immunityCache = new();
        private const float CACHE_DURATION = 5.0f;  // 5초 캐시

        private class CachedImmunity
        {
            public SpellDescriptor ImmunityFlags;
            public float CacheTime;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 타겟이 해당 능력의 SpellDescriptor에 면역인지 검사
        /// </summary>
        /// <param name="target">타겟 유닛</param>
        /// <param name="ability">사용할 능력</param>
        /// <returns>true = 면역 (사용 불가), false = 면역 아님</returns>
        public static bool IsImmuneTo(UnitEntityData target, AbilityData ability)
        {
            if (target == null || ability == null)
                return false;

            try
            {
                // 능력의 SpellDescriptor 가져오기
                SpellDescriptor abilityDescriptor = GetAbilityDescriptor(ability);

                Main.Verbose($"[ImmunityChecker] Checking {ability.Name} -> {target.CharacterName}: AbilityDesc={abilityDescriptor}");

                if (abilityDescriptor == SpellDescriptor.None)
                    return false;

                // 타겟의 면역 플래그 가져오기
                SpellDescriptor targetImmunity = GetTargetImmunityFlags(target);

                Main.Verbose($"[ImmunityChecker] {target.CharacterName} immunity flags: {targetImmunity}");

                if (targetImmunity == SpellDescriptor.None)
                {
                    // ★ v0.2.48: 면역 감지 실패 시 상세 진단
                    DiagnoseImmunity(target);
                    return false;
                }

                // 면역 매칭 체크
                bool isImmune = (abilityDescriptor & targetImmunity) != 0;

                if (isImmune)
                {
                    Main.Log($"[ImmunityChecker] ★ {target.CharacterName} is IMMUNE to {ability.Name} " +
                        $"(Ability={abilityDescriptor}, Immunity={targetImmunity})");
                }

                return isImmune;
            }
            catch (Exception ex)
            {
                Main.Error($"[ImmunityChecker] Error checking immunity: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.48: 면역 감지 실패 시 상세 진단
        /// </summary>
        private static void DiagnoseImmunity(UnitEntityData target)
        {
            try
            {
                var descriptor = target?.Descriptor;
                if (descriptor == null) return;

                Main.Verbose($"[ImmunityChecker] Diagnosing {target.CharacterName}:");
                Main.Verbose($"  - IsUndead: {descriptor.IsUndead}");
                Main.Verbose($"  - Facts count: {descriptor.Facts.List.Count}");

                // 모든 Facts 이름 출력 (처음 10개만)
                int count = 0;
                foreach (var fact in descriptor.Facts.List)
                {
                    if (count >= 10) break;
                    Main.Verbose($"  - Fact: {fact?.Blueprint?.name ?? "null"}");
                    count++;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[ImmunityChecker] Diagnose error: {ex.Message}");
            }
        }

        /// <summary>
        /// 능력의 SpellDescriptor 가져오기
        /// </summary>
        public static SpellDescriptor GetAbilityDescriptor(AbilityData ability)
        {
            try
            {
                // BlueprintAbility에서 SpellDescriptor 가져오기
                var blueprint = ability?.Blueprint;
                if (blueprint == null)
                    return SpellDescriptor.None;

                return blueprint.SpellDescriptor;
            }
            catch
            {
                return SpellDescriptor.None;
            }
        }

        /// <summary>
        /// 타겟의 모든 면역 플래그 가져오기 (캐시됨)
        /// </summary>
        public static SpellDescriptor GetTargetImmunityFlags(UnitEntityData target)
        {
            if (target == null)
                return SpellDescriptor.None;

            string unitId = target.UniqueId;
            float currentTime = UnityEngine.Time.time;

            // 캐시 확인
            if (_immunityCache.TryGetValue(unitId, out var cached))
            {
                if (currentTime - cached.CacheTime < CACHE_DURATION)
                    return cached.ImmunityFlags;
            }

            // 새로 계산
            SpellDescriptor immunityFlags = CalculateImmunityFlags(target);

            // 캐시 저장
            _immunityCache[unitId] = new CachedImmunity
            {
                ImmunityFlags = immunityFlags,
                CacheTime = currentTime
            };

            return immunityFlags;
        }

        /// <summary>
        /// 캐시 초기화
        /// </summary>
        public static void ClearCache()
        {
            _immunityCache.Clear();
            Main.Verbose("[ImmunityChecker] Cache cleared");
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// 유닛의 모든 면역 플래그 계산
        /// </summary>
        private static SpellDescriptor CalculateImmunityFlags(UnitEntityData unit)
        {
            SpellDescriptor result = SpellDescriptor.None;

            try
            {
                var descriptor = unit?.Descriptor;
                if (descriptor == null)
                    return result;

                // 모든 Facts (Features, Buffs 등) 순회
                foreach (var fact in descriptor.Facts.List)
                {
                    if (fact?.Blueprint == null)
                        continue;

                    // BlueprintComponent에서 BuffDescriptorImmunity 찾기
                    var components = fact.Blueprint.ComponentsArray;
                    if (components == null)
                        continue;

                    foreach (var component in components)
                    {
                        if (component is BuffDescriptorImmunity immunityComponent)
                        {
                            // SpellDescriptorWrapper에서 Value 추출
                            var immuneDescriptor = immunityComponent.Descriptor.Value;
                            result |= immuneDescriptor;

                            Main.Verbose($"[ImmunityChecker] {unit.CharacterName} has immunity: {immuneDescriptor} (from {fact.Blueprint.name})");
                        }
                    }
                }

                // 특수 유닛 타입 기반 면역 체크 (악마, 언데드 등)
                result |= GetCreatureTypeImmunity(unit);
            }
            catch (Exception ex)
            {
                Main.Error($"[ImmunityChecker] Error calculating immunity for {unit?.CharacterName}: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 크리처 타입 기반 면역 (언데드=Mind-Affecting 등)
        /// 참고: 악마(Demon) 면역은 BuffDescriptorImmunity로 체크됨
        /// </summary>
        private static SpellDescriptor GetCreatureTypeImmunity(UnitEntityData unit)
        {
            SpellDescriptor result = SpellDescriptor.None;

            try
            {
                var descriptor = unit?.Descriptor;
                if (descriptor == null)
                    return result;

                // 언데드 체크 - UnitDescriptor.IsUndead 사용
                if (descriptor.IsUndead)
                {
                    result |= SpellDescriptor.MindAffecting;
                    result |= SpellDescriptor.Death;
                    result |= SpellDescriptor.Poison;
                    result |= SpellDescriptor.Disease;
                    Main.Verbose($"[ImmunityChecker] {unit.CharacterName} is Undead - Mind-Affecting/Death/Poison/Disease immune");
                }

                // 나머지 크리처 타입(Construct, Vermin, Ooze 등)은
                // BuffDescriptorImmunity로 체크됨 (Facts에서 자동 감지)
            }
            catch (Exception ex)
            {
                Main.Error($"[ImmunityChecker] Error checking creature type: {ex.Message}");
            }

            return result;
        }

        #endregion
    }
}
