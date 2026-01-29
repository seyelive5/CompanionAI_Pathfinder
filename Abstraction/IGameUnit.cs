using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_Pathfinder.Abstraction
{
    /// <summary>
    /// 게임 유닛에 대한 추상화 인터페이스
    /// Pathfinder: UnitEntityData를 래핑
    /// </summary>
    public interface IGameUnit
    {
        // === 식별 ===
        string UniqueId { get; }
        string CharacterName { get; }

        // === 위치 ===
        Vector3 Position { get; }
        float Orientation { get; }

        // === 생명력 ===
        int CurrentHP { get; }
        int MaxHP { get; }
        float HPPercent { get; }
        bool IsAlive { get; }
        bool IsConscious { get; }

        // === 진영 ===
        bool IsPlayerFaction { get; }
        bool IsEnemy(IGameUnit other);
        bool IsAlly(IGameUnit other);

        // === 전투 상태 ===
        bool IsInCombat { get; }
        bool HasPendingCommands { get; }
        bool CanAct { get; }

        // === 능력/스펠 ===
        IEnumerable<IAbilityData> GetUsableAbilities();
        bool HasAbility(string abilityGuid);

        // === 원본 객체 접근 ===
        object GetNativeUnit();
    }
}
