using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
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
                // 1. 라운드 변경 감지
                CheckRoundChange();

                // 2. 이미 턴 종료한 유닛 스킵
                if (_finishedUnits.Contains(unitId))
                {
                    Main.Verbose($"[Orchestrator] {unitName}: Already finished this turn");
                    return ExecutionResult.EndTurn("Turn already finished");
                }

                // 3. 명령 실행 중이면 대기
                if (_executor.IsExecutingCommand(unit))
                {
                    Main.Verbose($"[Orchestrator] {unitName}: Waiting for command completion");
                    return ExecutionResult.Waiting("Command executing");
                }

                // 4. 턴 상태 가져오기/생성
                var turnState = GetOrCreateTurnState(unit);

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

                // 7. ActionCandidate → PlannedAction 변환 및 실행
                var plannedAction = bestAction.ToPlannedAction();
                var result = _executor.Execute(plannedAction, unit);

                // 8. 실행 결과 기록
                turnState.RecordAction(plannedAction, result.Type == ResultType.CastAbility || result.Type == ResultType.MoveTo);
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

        #endregion
    }
}
