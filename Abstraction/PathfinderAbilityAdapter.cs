using System;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;

namespace CompanionAI_Pathfinder.Abstraction
{
    /// <summary>
    /// Pathfinder의 AbilityData를 IAbilityData 인터페이스로 래핑
    /// ★ v0.2.59: RemainingCharges 실제 구현
    /// </summary>
    public class PathfinderAbilityAdapter : IAbilityData
    {
        private readonly AbilityData _ability;
        private readonly BlueprintAbility _blueprint;

        public PathfinderAbilityAdapter(AbilityData ability)
        {
            _ability = ability;
            _blueprint = ability?.Blueprint;
        }

        // === 식별 ===
        public string Guid => _blueprint != null ? _blueprint.AssetGuid.ToString() : string.Empty;
        public string Name => _blueprint?.Name ?? "Unknown";

        // === 사용 가능 여부 ===
        public bool IsAvailable => _ability?.IsAvailableForCast ?? false;
        public bool NeedsTarget => _blueprint?.CanTargetEnemies ?? false;
        public bool IsFullRoundAction => _blueprint?.IsFullRoundAction ?? false;

        // === 범위 ===
        public float Range => _ability?.GetApproachDistance(_ability?.Caster) ?? 0f;
        public bool IsRanged => Range > 2f; // 2미터 이상이면 원거리로 간주
        public bool IsMelee => !IsRanged;
        public bool IsAoE => _blueprint != null && _blueprint.AoERadius.Meters > 0;
        public float AoERadius => _blueprint?.AoERadius.Meters ?? 0f;

        // === 분류 ===
        public AbilityCategory Category => DetermineCategory();
        public bool IsHostile => _blueprint?.CanTargetEnemies ?? false;
        public bool IsBeneficial => _blueprint?.CanTargetFriends ?? false;

        // === 리소스 ===
        public bool HasCharges => _ability?.ResourceCost > 0 || SpellLevel > 0;
        /// <summary>
        /// ★ v0.2.59: 실제 능력 리소스 잔여 횟수 반환
        /// </summary>
        public int RemainingCharges
        {
            get
            {
                try
                {
                    if (_ability?.Caster == null || _blueprint == null)
                        return -1;  // 무제한

                    // AbilityResourceLogic 컴포넌트에서 리소스 정보 가져오기
                    var resourceLogic = _blueprint.GetComponent<AbilityResourceLogic>();
                    if (resourceLogic?.RequiredResource == null)
                        return -1;  // 리소스 제한 없음

                    // 캐스터의 리소스 컬렉션에서 잔여 횟수 가져오기
                    return _ability.Caster.Resources.GetResourceAmount(resourceLogic.RequiredResource);
                }
                catch
                {
                    return -1;  // 오류 시 무제한으로 처리
                }
            }
        }
        public int SpellLevel => _ability?.SpellLevel ?? 0;

        // === 원본 객체 접근 ===
        public object GetNativeAbility() => _ability;

        /// <summary>
        /// AbilityData로 직접 캐스팅
        /// </summary>
        public AbilityData NativeAbility => _ability;

        /// <summary>
        /// 능력 카테고리 결정
        /// </summary>
        private AbilityCategory DetermineCategory()
        {
            if (_blueprint == null)
                return AbilityCategory.Unknown;

            // SpellDescriptor를 기반으로 분류
            var descriptor = _blueprint.SpellDescriptor;

            // 힐 체크
            if (descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Cure) ||
                descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.RestoreHP))
            {
                return AbilityCategory.Heal;
            }

            // 버프 체크 (아군만 대상)
            if (_blueprint.CanTargetFriends && !_blueprint.CanTargetEnemies)
            {
                return AbilityCategory.Buff;
            }

            // 디버프/공격 체크
            if (_blueprint.CanTargetEnemies)
            {
                // 직접 데미지가 없으면 디버프
                if (descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Blindness) ||
                    descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Fear) ||
                    descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Stun) ||
                    descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Hex))
                {
                    return AbilityCategory.Debuff;
                }

                return AbilityCategory.Attack;
            }

            // 소환 체크
            if (descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.Summoning))
            {
                return AbilityCategory.Summon;
            }

            // 채널 에너지 체크
            if (descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.ChannelNegativeHarm) ||
                descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.ChannelNegativeHeal) ||
                descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.ChannelPositiveHarm) ||
                descriptor.HasFlag(Kingmaker.Blueprints.Classes.Spells.SpellDescriptor.ChannelPositiveHeal))
            {
                return AbilityCategory.Channel;
            }

            return AbilityCategory.Utility;
        }
    }
}
