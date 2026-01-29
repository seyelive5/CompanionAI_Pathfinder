namespace CompanionAI_Pathfinder.Abstraction
{
    /// <summary>
    /// 전투 상태에 대한 추상화 인터페이스
    /// </summary>
    public interface ICombatState
    {
        // === 전투 모드 ===
        CombatMode CurrentMode { get; }
        bool IsTurnBased { get; }
        bool IsRealTime { get; }

        // === 턴 정보 (턴제 모드에서만 유효) ===
        IGameUnit CurrentTurnUnit { get; }
        int CurrentRound { get; }
        float TimeInRound { get; }

        // === 전투 상태 ===
        bool IsInCombat { get; }
        int EnemyCount { get; }
        int AllyCount { get; }
    }

    /// <summary>
    /// 전투 모드 열거형
    /// </summary>
    public enum CombatMode
    {
        None,           // 전투 외
        RealTime,       // 실시간 전투
        TurnBased       // 턴제 전투
    }
}
