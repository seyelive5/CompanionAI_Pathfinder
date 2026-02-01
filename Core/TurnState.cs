using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using TurnBased.Controllers;

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
        public int CombatRound { get; private set; }

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

        #region ★ v0.2.108: Command Execution Tracking

        /// <summary>
        /// 명령 실행 대기 중 여부
        /// ProcessTurn에서 명령을 내린 후 true로 설정,
        /// 명령 완료 시 false로 설정
        /// </summary>
        public bool IsAwaitingCommandCompletion { get; set; }

        /// <summary>명령 대기 시작 프레임</summary>
        public int CommandWaitStartFrame { get; set; }

        /// <summary>명령 대기 최대 프레임 수 (무한 대기 방지)</summary>
        private const int MaxCommandWaitFrames = 300;  // 약 5초

        /// <summary>명령 대기 시간 초과 여부</summary>
        public bool IsCommandWaitTimeout =>
            IsAwaitingCommandCompletion &&
            (UnityEngine.Time.frameCount - CommandWaitStartFrame) > MaxCommandWaitFrames;

        /// <summary>명령 실행 시작 기록</summary>
        public void StartAwaitingCommand()
        {
            IsAwaitingCommandCompletion = true;
            CommandWaitStartFrame = UnityEngine.Time.frameCount;
        }

        /// <summary>명령 완료 기록</summary>
        public void FinishAwaitingCommand()
        {
            IsAwaitingCommandCompletion = false;
        }

        #endregion

        #region ★ v0.2.99: Failed Ability Tracking

        /// <summary>이번 턴에 실패한 능력 GUID 목록 (무한 재시도 방지)</summary>
        private HashSet<string> _failedAbilities = new HashSet<string>();

        /// <summary>마지막으로 선택된 능력 GUID (연속 선택 감지용)</summary>
        public string LastSelectedAbilityGuid { get; private set; }

        /// <summary>마지막 능력 연속 선택 횟수</summary>
        public int ConsecutiveAbilitySelectionCount { get; private set; }

        /// <summary>이번 턴에 실패한 능력인지 확인</summary>
        public bool IsAbilityFailed(string abilityGuid)
        {
            if (string.IsNullOrEmpty(abilityGuid)) return false;
            return _failedAbilities.Contains(abilityGuid);
        }

        /// <summary>능력 실패 기록 (이번 턴에 다시 시도하지 않음)</summary>
        public void RecordAbilityFailure(string abilityGuid, string reason = null)
        {
            if (string.IsNullOrEmpty(abilityGuid)) return;
            _failedAbilities.Add(abilityGuid);
            Main.Log($"[TurnState] ★ Ability marked as failed: {abilityGuid} ({reason ?? "unknown reason"})");
        }

        /// <summary>실패한 능력 수</summary>
        public int FailedAbilityCount => _failedAbilities.Count;

        /// <summary>
        /// 능력 선택 기록 (연속 선택 감지용)
        /// 같은 능력이 연속 3회 이상 선택되고 진행 안 되면 실패로 간주
        /// </summary>
        public bool RecordAbilitySelection(string abilityGuid)
        {
            if (string.IsNullOrEmpty(abilityGuid))
            {
                LastSelectedAbilityGuid = null;
                ConsecutiveAbilitySelectionCount = 0;
                return false;
            }

            if (LastSelectedAbilityGuid == abilityGuid)
            {
                ConsecutiveAbilitySelectionCount++;

                // 3회 연속 같은 능력 선택 = 진행 안 됨 → 실패로 기록
                if (ConsecutiveAbilitySelectionCount >= 3)
                {
                    RecordAbilityFailure(abilityGuid, "Consecutive selection without progress");
                    return true;  // 실패 플래그
                }
            }
            else
            {
                LastSelectedAbilityGuid = abilityGuid;
                ConsecutiveAbilitySelectionCount = 1;
            }

            return false;
        }

        /// <summary>마지막 선택 초기화 (명령이 성공적으로 시작된 경우)</summary>
        public void ClearLastSelection()
        {
            LastSelectedAbilityGuid = null;
            ConsecutiveAbilitySelectionCount = 0;
        }

        #endregion

        #region Constructor

        public TurnState(UnitEntityData unit)
        {
            Unit = unit;
            UnitId = unit?.UniqueId ?? "unknown";
            TurnStartFrame = UnityEngine.Time.frameCount;

            // ★ v0.2.78: 게임 API에서 초기 상태 동기화
            SyncFromGameState(unit);
        }

        #endregion

        #region Methods

        /// <summary>
        /// ★ v0.2.78: 게임 API에서 액션 상태 및 라운드 정보 동기화
        /// 턴제 모드와 실시간 모드 각각에 맞게 처리
        /// </summary>
        public void SyncFromGameState(UnitEntityData unit)
        {
            if (unit == null)
            {
                // 폴백: 기본값
                HasStandardAction = true;
                HasMoveAction = true;
                HasSwiftAction = true;
                CombatRound = 0;
                return;
            }

            try
            {
                // 턴제 전투 모드 체크
                bool isTurnBased = CombatController.IsInTurnBasedCombat();

                if (isTurnBased)
                {
                    // ★ 턴제 모드: 게임 API의 액션 메서드 사용
                    HasStandardAction = unit.HasStandardAction();
                    HasMoveAction = unit.HasMoveAction();
                    HasSwiftAction = unit.HasSwiftAction();

                    // 라운드 번호 동기화
                    var tbController = Game.Instance?.TurnBasedCombatController;
                    CombatRound = tbController?.RoundNumber ?? 0;

                    Main.Verbose($"[TurnState] Synced (Turn-Based): {unit.CharacterName} - " +
                                $"Std={HasStandardAction}, Move={HasMoveAction}, Swift={HasSwiftAction}, Round={CombatRound}");
                }
                else
                {
                    // ★ 실시간 모드: 쿨다운 기반 체크
                    var cooldown = unit.CombatState?.Cooldown;
                    if (cooldown != null)
                    {
                        HasStandardAction = cooldown.StandardAction <= 0f;
                        HasMoveAction = cooldown.MoveAction <= 0f;
                        HasSwiftAction = cooldown.SwiftAction <= 0f;
                    }
                    else
                    {
                        // 쿨다운 정보 없으면 기본값
                        HasStandardAction = true;
                        HasMoveAction = true;
                        HasSwiftAction = true;
                    }

                    // 실시간 모드: 라운드 개념 없음 (전투 시작부터 추정)
                    CombatRound = 1;

                    Main.Verbose($"[TurnState] Synced (Real-Time): {unit.CharacterName} - " +
                                $"Std={HasStandardAction}, Move={HasMoveAction}, Swift={HasSwiftAction}");
                }
            }
            catch (System.Exception ex)
            {
                Main.Verbose($"[TurnState] SyncFromGameState error: {ex.Message}");
                // 에러 시 기본값
                HasStandardAction = true;
                HasMoveAction = true;
                HasSwiftAction = true;
                CombatRound = 0;
            }
        }

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
