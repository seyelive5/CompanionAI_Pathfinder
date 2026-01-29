using UnityEngine;

namespace CompanionAI_Pathfinder.Abstraction
{
    /// <summary>
    /// 능력/스펠에 대한 추상화 인터페이스
    /// Pathfinder: AbilityData를 래핑
    /// </summary>
    public interface IAbilityData
    {
        // === 식별 ===
        string Guid { get; }
        string Name { get; }

        // === 사용 가능 여부 ===
        bool IsAvailable { get; }
        bool NeedsTarget { get; }
        bool IsFullRoundAction { get; }

        // === 범위 ===
        float Range { get; }
        bool IsRanged { get; }
        bool IsMelee { get; }
        bool IsAoE { get; }
        float AoERadius { get; }

        // === 분류 ===
        AbilityCategory Category { get; }
        bool IsHostile { get; }
        bool IsBeneficial { get; }

        // === 리소스 ===
        bool HasCharges { get; }
        int RemainingCharges { get; }
        int SpellLevel { get; }

        // === 원본 객체 접근 ===
        object GetNativeAbility();
    }

    /// <summary>
    /// 능력 카테고리 분류
    /// </summary>
    public enum AbilityCategory
    {
        Unknown,
        Attack,         // 직접 공격
        Heal,           // 치료
        Buff,           // 아군 버프
        Debuff,         // 적 디버프
        Summon,         // 소환
        Movement,       // 이동 관련
        Utility,        // 기타 유틸리티
        Channel,        // 채널 에너지
        Metamagic       // 메타매직
    }
}
