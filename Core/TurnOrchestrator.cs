using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using TurnBased.Controllers;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Planning;
using CompanionAI_Pathfinder.Execution;
using CompanionAI_Pathfinder.Core.DecisionEngine;
using CompanionAI_Pathfinder.Scoring;

namespace CompanionAI_Pathfinder.Core
{
    /// <summary>
    /// 턴 오케스트레이터 - 모든 AI 결정의 단일 제어점
    /// Pathfinder: WotR 버전
    ///
    /// 핵심 원칙:
    /// 1. TurnPlanner가 턴 시작 시 전체 계획 수립
    /// 2. 계획에 따라 순차적으로 행동 실행
    /// 3. 게임 AI는 실행만, 결정은 우리가
    /// </summary>
    public class TurnOrchestrator
    {
        #region Singleton

        private static TurnOrchestrator _instance;
        public static TurnOrchestrator Instance => _instance ?? (_instance = new TurnOrchestrator());

        #endregion

        #region Dependencies

        private readonly SituationAnalyzer _analyzer;
        private readonly ActionExecutor _executor;

        #endregion

        #region State

        /// <summary>현재 턴 상태 (유닛별)</summary>
        private readonly Dictionary<string, TurnState> _turnStates = new Dictionary<string, TurnState>();

        /// <summary>턴 종료된 유닛</summary>
        private readonly HashSet<string> _finishedUnits = new HashSet<string>();

        /// <summary>현재 라운드</summary>
        private int _currentRound = -1;

        #endregion

        #region Constructor

        private TurnOrchestrator()
        {
            _analyzer = new SituationAnalyzer();
            _executor = ActionExecutor.Instance;
        }

        #endregion

        #region Main Entry Point

        /// <summary>
        /// 턴제 모드에서 호출되는 메인 진입점
        /// CustomBrainPatch.TickBrain_Prefix에서 호출
        /// ★ v0.2.22: UnifiedDecisionEngine 통합
        /// ★ v0.2.85: TurnController.Status 체크 추가 - Acting일 때만 명령 발행
        /// </summary>
        public ExecutionResult ProcessTurn(UnitEntityData unit)
        {
            if (unit == null)
            {
                return ExecutionResult.Failure("Unit is null");
            }

            string unitId = unit.UniqueId;
            string unitName = unit.CharacterName;

            try
            {
                // ★ v0.2.85: TurnController.Status 체크
                // ★ v0.2.86: Preparing 상태도 허용 - IsDirectlyControllable 유닛은 Preparing에서 플레이어 입력 대기
                //
                // 게임 동작 분석:
                // - IsDirectlyControllable 유닛 → Status = Preparing (플레이어 입력 대기)
                // - AI 유닛 → Status = Acting (ForceTick 호출)
                // - 우리 동료는 IsDirectlyControllable이므로 Preparing 상태가 됨
                // - Scrolling만 제외 (카메라 이동 중)
                var tbController = Game.Instance?.TurnBasedCombatController;
                var currentTurn = tbController?.CurrentTurn;

                if (currentTurn != null)
                {
                    var turnStatus = currentTurn.Status;

                    // Scrolling 상태에서만 대기 (카메라 이동 완료 대기)
                    // Preparing, Acting, Ending 모두 명령 발행 가능
                    if (turnStatus == TurnController.TurnStatus.Scrolling)
                    {
                        Main.Verbose($"[Orchestrator] {unitName}: Waiting for camera (current={turnStatus})");
                        return ExecutionResult.Waiting($"Turn status: {turnStatus}");
                    }

                    Main.Verbose($"[Orchestrator] {unitName}: Status={turnStatus}, proceeding with decision");
                }

                // 1. 라운드 변경 감지
                CheckRoundChange();

                // 2. 이미 턴 종료한 유닛 스킵
                if (_finishedUnits.Contains(unitId))
                {
                    Main.Verbose($"[Orchestrator] {unitName}: Already finished this turn");
                    return ExecutionResult.EndTurn("Turn already finished");
                }

                // ★ v0.2.81: 턴 시작 시 (첫 호출) 명령 상태 처리
                bool isFirstCall = !_turnStates.ContainsKey(unitId);

                // 3. 명령 실행 중이면 대기 (단, 턴 시작 시에는 특별 처리)
                if (_executor.IsExecutingCommand(unit))
                {
                    if (isFirstCall)
                    {
                        // ★ v0.2.81: 턴 시작 시 로깅 + 잔여 명령 처리
                        Main.Log($"[Orchestrator] {unitName}: Commands found at turn start - logging state");
                        _executor.LogCommandState(unit, $"{unitName} turn start");

                        // 턴 시작 시 잔여 명령은 이전 턴/게임 시스템에서 남은 것
                        // AI 명령만 취소하고 진행 (플레이어가 수동으로 지시한 명령은 유지)
                        if (unit.Commands != null && unit.Commands.HasAiCommand())
                        {
                            Main.Log($"[Orchestrator] {unitName}: Clearing stale AI commands at turn start");
                            unit.Commands.InterruptAiCommands();

                            // 명령 취소 후 재확인
                            if (!_executor.IsExecutingCommand(unit))
                            {
                                Main.Log($"[Orchestrator] {unitName}: Commands cleared, proceeding");
                                // 아래로 계속 진행
                            }
                            else
                            {
                                // 아직 명령 있음 - 다음 틱에 재시도
                                return ExecutionResult.Waiting("Clearing commands");
                            }
                        }
                        else
                        {
                            // AI 명령 아님 - 대기
                            return ExecutionResult.Waiting("Non-AI command executing at turn start");
                        }
                    }
                    else
                    {
                        // 이미 턴 진행 중 - 명령 완료 대기
                        Main.Verbose($"[Orchestrator] {unitName}: Waiting for command completion");
                        return ExecutionResult.Waiting("Command executing");
                    }
                }

                // 4. 턴 상태 가져오기/생성
                var turnState = GetOrCreateTurnState(unit);

                // ★ v0.2.108: 명령 실행 대기 중인지 확인
                if (turnState.IsAwaitingCommandCompletion)
                {
                    // 명령이 완료됐는지 확인
                    bool hasUnfinishedCmd = unit.Commands.HasUnfinished();
                    bool isExecuting = _executor.IsExecutingCommand(unit);

                    if (hasUnfinishedCmd || isExecuting)
                    {
                        // 타임아웃 체크
                        if (turnState.IsCommandWaitTimeout)
                        {
                            Main.Log($"[Orchestrator] {unitName}: ★ Command wait TIMEOUT - forcing completion");
                            turnState.FinishAwaitingCommand();
                            // 아래로 계속 진행
                        }
                        else
                        {
                            // 아직 명령 실행 중 - 대기
                            Main.Verbose($"[Orchestrator] {unitName}: Waiting for command completion (HasUnfinished={hasUnfinishedCmd})");
                            return ExecutionResult.Waiting("Awaiting command completion");
                        }
                    }
                    else
                    {
                        // 명령 완료됨
                        Main.Verbose($"[Orchestrator] {unitName}: Command completed - proceeding");
                        turnState.FinishAwaitingCommand();
                    }
                }

                // ★ v0.2.78: 게임 상태에서 액션 가용성 동기화
                // 턴제 모드에서 이미 사용한 액션을 정확히 반영
                turnState.SyncFromGameState(unit);

                // 5. 최대 행동 수 도달 시 종료
                if (turnState.HasReachedMaxActions)
                {
                    Main.Log($"[Orchestrator] {unitName}: Max actions reached - ending turn");
                    MarkTurnFinished(unitId);
                    return ExecutionResult.EndTurn("Max actions reached");
                }

                // ★ v0.2.22: UnifiedDecisionEngine으로 최적 행동 결정
                var bestAction = UnifiedDecisionEngine.Instance.DecideAction(unit, turnState);

                // 6. EndTurn 행동
                if (bestAction == null || bestAction.ActionType == CandidateType.EndTurn)
                {
                    Main.Log($"[Orchestrator] {unitName}: EndTurn - {bestAction?.Reason ?? "No action"}");
                    MarkTurnFinished(unitId);
                    return ExecutionResult.EndTurn(bestAction?.Reason ?? "No action");
                }

                // ★ v0.2.99: 연속 선택 감지 (무한 재시도 방지)
                string selectedAbilityGuid = bestAction.Ability?.Blueprint?.AssetGuid.ToString();
                if (!string.IsNullOrEmpty(selectedAbilityGuid))
                {
                    bool markedAsFailed = turnState.RecordAbilitySelection(selectedAbilityGuid);
                    if (markedAsFailed)
                    {
                        // 3회 연속 같은 능력 선택 → 실패로 기록됨 → 다시 결정 요청
                        Main.Log($"[Orchestrator] {unitName}: ★ Ability {bestAction.Ability?.Name} marked as failed (consecutive selection)");
                        // 다음 틱에서 다른 능력 선택하도록 함
                        return ExecutionResult.Waiting("Ability failed - will retry with different action");
                    }
                }

                // 7. ActionCandidate → PlannedAction 변환 및 실행
                var plannedAction = bestAction.ToPlannedAction();
                var result = _executor.Execute(plannedAction, unit);

                // ★ v0.2.99: 실행 성공 시 선택 카운터 초기화
                bool executionSuccess = result.Type == ResultType.CastAbility || result.Type == ResultType.MoveTo;
                if (executionSuccess)
                {
                    turnState.ClearLastSelection();

                    // ★ v0.2.108: 명령 실행 성공 시 대기 상태로 전환
                    // 다음 틱에서 명령 완료를 확인한 후 다음 행동 결정
                    turnState.StartAwaitingCommand();
                    Main.Log($"[Orchestrator] {unitName}: {bestAction.ActionType} (Score={bestAction.FinalScore:F1}) - {result.Reason}");
                    Main.Verbose($"[Orchestrator] {unitName}: Started awaiting command completion");

                    // 8. 실행 결과 기록
                    turnState.RecordAction(plannedAction, true);

                    // Waiting 반환하여 다음 틱에서 명령 완료 확인
                    return ExecutionResult.Waiting("Command started - awaiting completion");
                }

                // 8. 실행 결과 기록 (실패한 경우)
                turnState.RecordAction(plannedAction, false);
                Main.Log($"[Orchestrator] {unitName}: {bestAction.ActionType} (Score={bestAction.FinalScore:F1}) - {result.Reason}");

                // 9. 결과에 따라 계속 또는 종료
                if (result.Type == ResultType.EndTurn)
                {
                    MarkTurnFinished(unitId);
                }

                return result;
            }
            catch (Exception ex)
            {
                Main.Error($"[Orchestrator] {unitName}: Critical error - {ex.Message}");
                Main.Error($"[Orchestrator] Stack: {ex.StackTrace}");
                return ExecutionResult.EndTurn($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Turn State Management

        /// <summary>
        /// 유닛의 턴 상태 가져오기 (없으면 생성)
        /// </summary>
        private TurnState GetOrCreateTurnState(UnitEntityData unit)
        {
            string unitId = unit.UniqueId;

            if (!_turnStates.TryGetValue(unitId, out var state))
            {
                state = new TurnState(unit);
                _turnStates[unitId] = state;
                Main.Verbose($"[Orchestrator] Created new TurnState for {unit.CharacterName}");
            }

            return state;
        }

        /// <summary>
        /// 유닛의 턴 종료 표시
        /// </summary>
        private void MarkTurnFinished(string unitId)
        {
            _finishedUnits.Add(unitId);
        }

        /// <summary>
        /// 라운드 변경 감지 및 처리
        /// </summary>
        private void CheckRoundChange()
        {
            try
            {
                int gameRound = GetCurrentRound();
                if (gameRound != _currentRound)
                {
                    Main.Log($"[Orchestrator] Round changed: {_currentRound} -> {gameRound}");
                    _currentRound = gameRound;
                    OnNewRound();
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[Orchestrator] CheckRoundChange error: {ex.Message}");
            }
        }

        /// <summary>
        /// 새 라운드 시작 처리
        /// </summary>
        private void OnNewRound()
        {
            // 모든 턴 상태 초기화
            _turnStates.Clear();
            _finishedUnits.Clear();
            Main.Log($"[Orchestrator] New round - all turn states cleared");
        }

        /// <summary>
        /// 현재 게임 라운드 가져오기
        /// </summary>
        private int GetCurrentRound()
        {
            try
            {
                // Pathfinder 턴제 시스템에서 라운드 가져오기
                var combat = Game.Instance?.TurnBasedCombatController;
                if (combat != null)
                {
                    return combat.RoundNumber;
                }
            }
            catch { }
            return 0;
        }

        #endregion

        #region Public API

        /// <summary>
        /// 특정 유닛의 턴 상태 초기화 (수동)
        /// </summary>
        public void ResetTurnState(string unitId)
        {
            _turnStates.Remove(unitId);
            _finishedUnits.Remove(unitId);
        }

        /// <summary>
        /// 모든 턴 상태 초기화
        /// </summary>
        public void ResetAllTurnStates()
        {
            _turnStates.Clear();
            _finishedUnits.Clear();
            _currentRound = -1;
        }

        /// <summary>
        /// 유닛이 이번 턴에 행동했는지
        /// </summary>
        public bool HasActedThisTurn(string unitId)
        {
            return _turnStates.ContainsKey(unitId) && _turnStates[unitId].ActionCount > 0;
        }

        /// <summary>
        /// 유닛의 이번 턴이 종료되었는지
        /// </summary>
        public bool IsTurnFinished(string unitId)
        {
            return _finishedUnits.Contains(unitId);
        }

        /// <summary>
        /// ★ v0.2.99: 유닛의 TurnState 가져오기 (외부 접근용)
        /// AttackScorer 등에서 실패한 능력 체크에 사용
        /// </summary>
        public TurnState GetTurnState(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return null;
            _turnStates.TryGetValue(unitId, out var state);
            return state;
        }

        /// <summary>
        /// ★ v0.2.99: 능력 실패 기록 (무한 재시도 방지)
        /// </summary>
        public void RecordAbilityFailure(string unitId, string abilityGuid, string reason)
        {
            if (string.IsNullOrEmpty(unitId) || string.IsNullOrEmpty(abilityGuid)) return;

            if (_turnStates.TryGetValue(unitId, out var state))
            {
                state.RecordAbilityFailure(abilityGuid, reason);
            }
        }

        /// <summary>
        /// ★ v0.2.99: 이번 턴에 실패한 능력인지 확인
        /// </summary>
        public bool IsAbilityFailedThisTurn(string unitId, string abilityGuid)
        {
            if (string.IsNullOrEmpty(unitId) || string.IsNullOrEmpty(abilityGuid)) return false;

            if (_turnStates.TryGetValue(unitId, out var state))
            {
                return state.IsAbilityFailed(abilityGuid);
            }
            return false;
        }

        #endregion
    }
}
