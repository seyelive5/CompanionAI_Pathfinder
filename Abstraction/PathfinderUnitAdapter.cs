using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Abstraction
{
    /// <summary>
    /// Pathfinder의 UnitEntityData를 IGameUnit 인터페이스로 래핑
    /// </summary>
    public class PathfinderUnitAdapter : IGameUnit
    {
        private readonly UnitEntityData _unit;

        public PathfinderUnitAdapter(UnitEntityData unit)
        {
            _unit = unit;
        }

        // === 식별 ===
        public string UniqueId => _unit.UniqueId;
        public string CharacterName => _unit.Descriptor?.CharacterName ?? "Unknown";

        // === 위치 ===
        public Vector3 Position => _unit.Position;
        public float Orientation => _unit.Orientation;

        // === 생명력 ===
        public int CurrentHP => _unit.Descriptor?.HPLeft ?? 0;
        public int MaxHP => _unit.Descriptor?.MaxHP ?? 1;
        public float HPPercent => MaxHP > 0 ? (float)CurrentHP / MaxHP * 100f : 0f;
        public bool IsAlive => CurrentHP > 0;
        public bool IsConscious => IsAlive && (_unit.Descriptor?.State?.IsConscious ?? false);

        // === 진영 ===
        public bool IsPlayerFaction => _unit.IsPlayerFaction;

        public bool IsEnemy(IGameUnit other)
        {
            if (other is PathfinderUnitAdapter otherAdapter)
            {
                return _unit.IsEnemy(otherAdapter._unit);
            }
            return false;
        }

        public bool IsAlly(IGameUnit other)
        {
            if (other is PathfinderUnitAdapter otherAdapter)
            {
                return _unit.IsAlly(otherAdapter._unit);
            }
            return false;
        }

        // === 전투 상태 ===
        public bool IsInCombat => _unit.CombatState?.IsInCombat ?? false;
        public bool HasPendingCommands => !(_unit.Commands?.Empty ?? true);
        public bool CanAct => _unit.Descriptor?.State?.CanAct ?? false;

        // === 능력/스펠 ===
        public IEnumerable<IAbilityData> GetUsableAbilities()
        {
            if (_unit.Abilities == null)
                return Enumerable.Empty<IAbilityData>();

            return _unit.Abilities
                .Visible
                .Where(a => a.Data?.IsAvailableForCast ?? false)
                .Select(a => new PathfinderAbilityAdapter(a.Data));
        }

        public bool HasAbility(string abilityGuid)
        {
            if (_unit.Abilities == null || string.IsNullOrEmpty(abilityGuid))
                return false;

            return _unit.Abilities.RawFacts
                .Any(a => a.Blueprint != null && a.Blueprint.AssetGuid.ToString() == abilityGuid);
        }

        // === 원본 객체 접근 ===
        public object GetNativeUnit() => _unit;

        /// <summary>
        /// UnitEntityData로 직접 캐스팅
        /// </summary>
        public UnitEntityData NativeUnit => _unit;
    }
}
