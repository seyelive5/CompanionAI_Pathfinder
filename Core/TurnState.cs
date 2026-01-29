using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;

namespace CompanionAI_Pathfinder.Core
{
    /// <summary>
    /// 현재 턴의 상태를 추적
    /// 한 유닛의 턴 동안 유지되는 모든 상태 정보
    /// </summary>
    public class TurnState
    {
        #region Constants

        /// <summary>
        /// 턴당 최대 행동 수 (최후의 안전장치)
        /// 게임의 자연스러운 제한(액션 시스템)을 따름
        /// </summary>
        public const int MaxActionsPerTurn = 9999;

        #endregion

        #region Identity

        /// <summary>이 턴의 유닛</summary>
        public UnitEntityData Unit { get; }

        /// <summary>유닛 고유 ID</summary>
        public string UnitId { get; }

        /// <summary>턴 시작 프레임</summary>
        public int TurnStartFrame { get; }

        /// <summary>이 턴이 시작된 전투 라운드</summary>
        public int CombatRound { get; }

        #endregion

        #region Plan

        /// <summary>현재 턴 계획</summary>
        public TurnPlan Plan { get; set; }

        #endregion

        #region Executed Actions

        /// <summary>이번 턴에 실행된 행동들</summary>
        public List<PlannedAction> ExecutedActions { get; } = new List<PlannedAction>();

        /// <summary>이번 턴 행동 횟수</summary>
        public int ActionCount => ExecutedActions.Count;

        #endregion

        #region State Flags

        /// <summary>이동 완료 여부</summary>
        public bool HasMovedThisTurn { get; set; }

        /// <summary>공격 완료 여부 (첫 공격)</summary>
        public bool HasAttackedThisTurn { get; set; }

        /// <summary>버프 사용 여부</summary>
        public bool HasBuffedThisTurn { get; set; }

        /// <summary>힐 사용 여부</summary>
        public bool HasHealedThisTurn { get; set; }

        /// <summary>첫 번째 행동 완료 여부</summary>
        public bool HasPerformedFirstAction { get; set; }

        /// <summary>이번 턴 이동 횟수 (다중 이동 지원)</summary>
        public int MoveCount { get; set; }

        /// <summary>공격 후 추가 이동 허용 (이동→공격 완료 시 추가 이동 가능)</summary>
        public bool AllowPostAttackMove => HasMovedThisTurn && HasAttackedThisTurn;

        /// <summary>추격 이동 허용 (이동했지만 공격 못함 - 적이 너무 멀어서)</summary>
        public bool AllowChaseMove => HasMovedThisTurn && !HasAttackedThisTurn;

        #endregion

        #region Action Resources (Pathfinder Action System)

        /// <summary>Standard Action 사용 가능 여부</summary>
        public bool HasStandardAction { get; set; } = true;

        /// <summary>Move Action 사용 가능 여부</summary>
        public bool HasMoveAction { get; set; } = true;

        /// <summary>Swift Action 사용 가능 여부</summary>
        public bool HasSwiftAction { get; set; } = true;

        /// <summary>Full-Round Action 사용 가능 여부</summary>
        public bool HasFullRoundAction => HasStandardAction && HasMoveAction;

        #endregion

        #region Safety

        /// <summary>연속 실패 횟수</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>대기 횟수 (무한 대기 방지)</summary>
        public int WaitCount { get; set; }

        /// <summary>최대 액션 도달 여부</summary>
        public bool HasReachedMaxActions => ActionCount >= MaxActionsPerTurn;

        #endregion

        #region Constructor

        public TurnState(UnitEntityData unit)
        {
            Unit = unit;
            UnitId = unit?.UniqueId ?? "unknown";
            TurnStartFrame = UnityEngine.Time.frameCount;

            // Pathfinder 턴제 라운드 추적
            // TODO: TurnBased 컨트롤러에서 라운드 가져오기
            CombatRound = 0;

            // 초기 액션 리소스 설정
            HasStandardAction = true;
            HasMoveAction = true;
            HasSwiftAction = true;
        }

        #endregion

        #region Methods

        /// <summary>
        /// 행동 실행 기록
        /// </summary>
        public void RecordAction(PlannedAction action, bool success)
        {
            action.IsExecuted = true;
            action.WasSuccessful = success;
            ExecutedActions.Add(action);

            if (success)
            {
                ConsecutiveFailures = 0;

                // 상태 플래그 업데이트
                switch (action.Type)
                {
                    case ActionType.Move:
                        HasMovedThisTurn = true;
                        MoveCount++;
                        HasMoveAction = false;
                        break;
                    case ActionType.Attack:
                        HasAttackedThisTurn = true;
                        HasPerformedFirstAction = true;
                        HasStandardAction = false;
                        break;
                    case ActionType.Buff:
                        HasBuffedThisTurn = true;
                        // 버프 종류에 따라 Swift 또는 Standard 소모
                        // TODO: 능력 메타데이터로 판단
                        break;
                    case ActionType.Heal:
                        HasHealedThisTurn = true;
                        HasStandardAction = false;
                        break;
                    case ActionType.Debuff:
                    case ActionType.Support:
                    case ActionType.Special:
                        HasPerformedFirstAction = true;
                        HasStandardAction = false;
                        break;
                }
            }
            else
            {
                ConsecutiveFailures++;
            }

            Main.Verbose($"[TurnState] Action #{ActionCount}: {action} -> {(success ? "SUCCESS" : "FAILED")}");
        }

        /// <summary>
        /// 특정 능력을 이번 턴에 사용했는지 확인
        /// </summary>
        public bool HasUsedAbility(string abilityGuid)
        {
            foreach (var action in ExecutedActions)
            {
                if (action.WasSuccessful == true &&
                    action.Ability?.Blueprint != null &&
                    action.Ability.Blueprint.AssetGuid.ToString() == abilityGuid)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 디버그 정보 출력
        /// </summary>
        public override string ToString()
        {
            return $"[TurnState] {Unit?.CharacterName}: " +
                   $"Actions={ActionCount}, Std={HasStandardAction}, Move={HasMoveAction}, Swift={HasSwiftAction}, " +
                   $"Moved={HasMovedThisTurn}, Attacked={HasAttackedThisTurn}";
        }

        #endregion
    }
}
