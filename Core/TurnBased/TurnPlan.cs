using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Core.TurnBased
{
    /// <summary>
    /// ★ v0.2.114: 턴 전체 계획
    /// 턴 시작 시 수립되어 순차적으로 실행됨
    /// </summary>
    public class TurnPlan
    {
        #region Properties

        /// <summary>계획 소유 유닛 ID</summary>
        public string UnitId { get; set; }

        /// <summary>계획 생성 시 라운드</summary>
        public int CombatRound { get; set; }

        /// <summary>계획 생성 시 프레임</summary>
        public int CreatedFrame { get; set; }

        /// <summary>계획된 스텝들</summary>
        public List<TurnPlanStep> Steps { get; } = new List<TurnPlanStep>();

        /// <summary>현재 실행 중인 스텝 인덱스</summary>
        public int CurrentStepIndex { get; set; } = 0;

        /// <summary>계획이 유효한지</summary>
        public bool IsValid { get; set; } = true;

        /// <summary>계획이 완료되었는지</summary>
        public bool IsComplete => CurrentStepIndex >= Steps.Count;

        /// <summary>현재 스텝</summary>
        public TurnPlanStep CurrentStep =>
            CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;

        /// <summary>계획 수립 시 상황 스냅샷</summary>
        public PlanSnapshot Snapshot { get; set; }

        /// <summary>리플랜 횟수</summary>
        public int ReplanCount { get; set; } = 0;

        /// <summary>최대 리플랜 횟수</summary>
        public const int MAX_REPLAN_COUNT = 3;

        #endregion

        #region Constructor

        public TurnPlan(string unitId, int combatRound)
        {
            UnitId = unitId;
            CombatRound = combatRound;
            CreatedFrame = UnityEngine.Time.frameCount;
        }

        #endregion

        #region Methods

        /// <summary>다음 스텝으로 진행</summary>
        public void AdvanceStep()
        {
            CurrentStepIndex++;
        }

        /// <summary>스텝 추가</summary>
        public void AddStep(TurnPlanStep step)
        {
            Steps.Add(step);
        }

        /// <summary>계획 무효화</summary>
        public void Invalidate(string reason)
        {
            IsValid = false;
            Main.Log($"[TurnPlan] Invalidated: {reason}");
        }

        /// <summary>디버그 로그</summary>
        public void LogPlan(string unitName)
        {
            Main.Log($"[TurnPlan] {unitName}: {Steps.Count} steps planned (Replan#{ReplanCount})");
            for (int i = 0; i < Steps.Count; i++)
            {
                var step = Steps[i];
                string marker = i == CurrentStepIndex ? "→" : " ";
                Main.Log($"  {marker}[{i}] {step.StepType}: {step.Description}");
            }
        }

        #endregion
    }

    /// <summary>
    /// 계획 스텝 타입
    /// </summary>
    public enum PlanStepType
    {
        /// <summary>자가 버프</summary>
        SelfBuff,

        /// <summary>팀원 버프</summary>
        AllyBuff,

        /// <summary>이동 (공격 전)</summary>
        MoveToAttack,

        /// <summary>이동 (안전 위치)</summary>
        MoveToSafety,

        /// <summary>이동 (적 접근 - 탱커용)</summary>
        MoveToEngage,

        /// <summary>공격</summary>
        Attack,

        /// <summary>디버프</summary>
        Debuff,

        /// <summary>턴 종료</summary>
        EndTurn
    }

    /// <summary>
    /// ★ v0.2.114: 개별 계획 스텝
    /// </summary>
    public class TurnPlanStep
    {
        /// <summary>스텝 타입</summary>
        public PlanStepType StepType { get; set; }

        /// <summary>사용할 능력 (있으면)</summary>
        public Kingmaker.UnitLogic.Abilities.AbilityData Ability { get; set; }

        /// <summary>타겟 유닛 (있으면)</summary>
        public UnitEntityData TargetUnit { get; set; }

        /// <summary>타겟 위치 (이동용)</summary>
        public Vector3? TargetPosition { get; set; }

        /// <summary>필요한 액션 타입</summary>
        public ActionCostType ActionCost { get; set; }

        /// <summary>스텝 설명</summary>
        public string Description { get; set; }

        /// <summary>스텝 상태</summary>
        public StepStatus Status { get; set; } = StepStatus.Pending;

        /// <summary>실행 시도 횟수</summary>
        public int AttemptCount { get; set; } = 0;

        /// <summary>최대 시도 횟수</summary>
        public const int MAX_ATTEMPTS = 3;

        /// <summary>실행 실패 가능 (스킵 가능)</summary>
        public bool IsOptional { get; set; } = false;
    }

    /// <summary>
    /// 스텝 실행 상태
    /// </summary>
    public enum StepStatus
    {
        Pending,
        Executing,
        Completed,
        Failed,
        Skipped
    }

    /// <summary>
    /// 액션 비용 타입
    /// </summary>
    public enum ActionCostType
    {
        Free,
        Swift,
        Move,
        Standard,
        FullRound
    }

    /// <summary>
    /// ★ v0.2.114: 계획 수립 시 상황 스냅샷
    /// 상황 변화 감지에 사용
    /// </summary>
    public class PlanSnapshot
    {
        /// <summary>내 HP 퍼센트</summary>
        public float MyHPPercent { get; set; }

        /// <summary>타겟 HP 퍼센트 (있으면)</summary>
        public float? TargetHPPercent { get; set; }

        /// <summary>타겟 ID (있으면)</summary>
        public string TargetId { get; set; }

        /// <summary>내 위치</summary>
        public Vector3 MyPosition { get; set; }

        /// <summary>적 수</summary>
        public int EnemyCount { get; set; }

        /// <summary>교전 중인 적 수 (내 근접)</summary>
        public int EngagedEnemyCount { get; set; }

        /// <summary>스냅샷 생성</summary>
        public static PlanSnapshot Create(UnitEntityData unit, UnitEntityData target, int enemyCount, int engagedCount)
        {
            return new PlanSnapshot
            {
                MyHPPercent = unit.HPLeft / (float)unit.MaxHP * 100f,
                TargetHPPercent = target != null ? target.HPLeft / (float)target.MaxHP * 100f : (float?)null,
                TargetId = target?.UniqueId,
                MyPosition = unit.Position,
                EnemyCount = enemyCount,
                EngagedEnemyCount = engagedCount
            };
        }
    }
}
