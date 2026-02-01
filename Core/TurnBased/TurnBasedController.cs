using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using TurnBased.Controllers;
using CompanionAI_Pathfinder.Core.DecisionEngine;
using CompanionAI_Pathfinder.Execution;
using CompanionAI_Pathfinder.GameInterface;
using CompanionAI_Pathfinder.Scoring;

namespace CompanionAI_Pathfinder.Core.TurnBased
{
    /// <summary>
    /// ★ v0.2.115: 계획 기반 턴제 컨트롤러
    ///
    /// 핵심 원칙:
    /// 1. 턴 시작 시 전체 계획 수립 (TurnPlanBuilder)
    /// 2. 상황 변화 감지 시 리플랜 (TurnPlanValidator)
    /// 3. 계획에 따라 순차적 실행
    /// 4. Commands.Empty 체크로 명령 완료 대기
    /// </summary>
    public class TurnBasedController
    {
        #region Singleton

        private static TurnBasedController _instance;
        public static TurnBasedController Instance => _instance ?? (_instance = new TurnBasedController());

        #endregion

        #region Constants

        /// <summary>명령 대기 타임아웃 (프레임)</summary>
        private const int COMMAND_WAIT_TIMEOUT = 300;  // 약 5초

        /// <summary>턴당 최대 스텝 수 (안전장치)</summary>
        private const int MAX_STEPS_PER_TURN = 10;

        /// <summary>연속 실패 최대 횟수</summary>
        private const int MAX_CONSECUTIVE_FAILURES = 5;

        #endregion

        #region State

        /// <summary>유닛별 턴 계획</summary>
        private readonly Dictionary<string, TurnPlan> _turnPlans = new Dictionary<string, TurnPlan>();

        /// <summary>유닛별 턴 상태</summary>
        private readonly Dictionary<string, TurnStateInfo> _turnStates = new Dictionary<string, TurnStateInfo>();

        /// <summary>기존 TurnState (UnifiedDecisionEngine용 - 폴백)</summary>
        private readonly Dictionary<string, TurnState> _legacyTurnStates = new Dictionary<string, TurnState>();

        /// <summary>마지막 처리 라운드</summary>
        private int _lastProcessedRound = -1;

        #endregion

        #region Inner Classes

        private class TurnStateInfo
        {
            public string UnitId { get; set; }
            public int CombatRound { get; set; }
            public int StepCount { get; set; }
            public int WaitCount { get; set; }
            public int ConsecutiveFailures { get; set; }
            public bool IsFinished { get; set; }
            public int TurnStartFrame { get; set; }
            public HashSet<string> FailedAbilities { get; } = new HashSet<string>();

            public TurnStateInfo(string unitId, int round)
            {
                UnitId = unitId;
                CombatRound = round;
                TurnStartFrame = UnityEngine.Time.frameCount;
            }
        }

        #endregion

        #region Main Entry Point

        /// <summary>
        /// ★ 메인 진입점 - 계획 기반 턴 처리
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
                // 1. 라운드 변경 체크
                CheckRoundChange();

                // 2. 턴 상태 가져오기/생성
                var stateInfo = GetOrCreateTurnState(unit);

                // 이미 종료된 턴
                if (stateInfo.IsFinished)
                {
                    Main.Verbose($"[TBController] {unitName}: Turn already finished");
                    return ExecutionResult.EndTurn("Turn finished");
                }

                // 안전장치
                if (stateInfo.StepCount >= MAX_STEPS_PER_TURN)
                {
                    Main.Log($"[TBController] {unitName}: Max steps reached");
                    MarkTurnFinished(stateInfo);
                    return ExecutionResult.EndTurn("Max steps");
                }

                if (stateInfo.ConsecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    Main.Log($"[TBController] {unitName}: Too many failures");
                    MarkTurnFinished(stateInfo);
                    return ExecutionResult.EndTurn("Too many failures");
                }

                // 3. ★ Commands.Empty 체크
                if (!GameAPI.IsReadyForNextAction(unit))
                {
                    stateInfo.WaitCount++;

                    if (stateInfo.WaitCount > COMMAND_WAIT_TIMEOUT)
                    {
                        Main.Log($"[TBController] {unitName}: ★ Wait timeout");
                        MarkTurnFinished(stateInfo);
                        return ExecutionResult.EndTurn("Wait timeout");
                    }

                    if (stateInfo.WaitCount % 30 == 0)
                    {
                        Main.Verbose($"[TBController] {unitName}: Waiting for command (frame {stateInfo.WaitCount})");
                    }

                    return ExecutionResult.Waiting("Command in progress");
                }

                stateInfo.WaitCount = 0;

                // 4. 액션 가용성 확인
                bool hasStandard = GameAPI.HasStandardAction(unit);
                bool hasMove = GameAPI.HasMoveAction(unit);
                bool hasSwift = GameAPI.HasSwiftAction(unit);

                if (!hasStandard && !hasMove && !hasSwift)
                {
                    Main.Log($"[TBController] {unitName}: No actions remaining");
                    MarkTurnFinished(stateInfo);
                    return ExecutionResult.EndTurn("No actions");
                }

                // 5. 계획 가져오기 또는 생성
                var plan = GetOrCreatePlan(unit, stateInfo);

                if (plan == null || !plan.IsValid)
                {
                    Main.Log($"[TBController] {unitName}: No valid plan");
                    MarkTurnFinished(stateInfo);
                    return ExecutionResult.EndTurn("No valid plan");
                }

                // 6. 계획 완료 체크
                if (plan.IsComplete)
                {
                    Main.Log($"[TBController] {unitName}: Plan complete");
                    MarkTurnFinished(stateInfo);
                    return ExecutionResult.EndTurn("Plan complete");
                }

                // 7. 상황 변화 감지 및 리플랜
                var enemies = GetEnemies(unit);
                if (TurnPlanValidator.ShouldReplan(plan, unit, enemies))
                {
                    if (plan.ReplanCount < TurnPlan.MAX_REPLAN_COUNT)
                    {
                        Main.Log($"[TBController] {unitName}: Replanning...");
                        plan = CreateNewPlan(unit, stateInfo);
                        if (plan == null || plan.IsComplete)
                        {
                            MarkTurnFinished(stateInfo);
                            return ExecutionResult.EndTurn("Replan failed");
                        }
                    }
                }

                // 8. 현재 스텝 실행
                var currentStep = plan.CurrentStep;
                if (currentStep == null)
                {
                    Main.Log($"[TBController] {unitName}: No current step");
                    MarkTurnFinished(stateInfo);
                    return ExecutionResult.EndTurn("No step");
                }

                // 스텝 유효성 검증
                var turnState = GetOrCreateLegacyTurnState(unit);
                turnState.SyncFromGameState(unit);

                if (!TurnPlanValidator.ValidateStep(currentStep, unit, turnState, out string stepInvalidReason))
                {
                    Main.Log($"[TBController] {unitName}: Step invalid - {stepInvalidReason}");

                    if (currentStep.IsOptional)
                    {
                        // 선택적 스텝은 스킵
                        currentStep.Status = StepStatus.Skipped;
                        plan.AdvanceStep();
                        return ExecutionResult.Waiting("Step skipped");
                    }
                    else
                    {
                        TurnPlanValidator.HandleStepFailure(currentStep, stepInvalidReason);
                        if (currentStep.Status == StepStatus.Failed)
                        {
                            plan.AdvanceStep();
                        }
                        stateInfo.ConsecutiveFailures++;
                        return ExecutionResult.Waiting("Step failed");
                    }
                }

                // 스텝 실행
                var result = ExecuteStep(currentStep, unit);

                if (result.Type == ResultType.CastAbility || result.Type == ResultType.MoveTo)
                {
                    // 성공
                    currentStep.Status = StepStatus.Completed;
                    plan.AdvanceStep();
                    stateInfo.StepCount++;
                    stateInfo.ConsecutiveFailures = 0;

                    Main.Log($"[TBController] {unitName}: ★ Step SUCCESS - {currentStep.Description}");
                    return ExecutionResult.Waiting("Step completed");
                }
                else if (result.Type == ResultType.EndTurn)
                {
                    MarkTurnFinished(stateInfo);
                    return result;
                }
                else
                {
                    // 실패
                    TurnPlanValidator.HandleStepFailure(currentStep, result.Reason);
                    stateInfo.ConsecutiveFailures++;

                    if (currentStep.Status == StepStatus.Failed)
                    {
                        plan.AdvanceStep();
                    }

                    Main.Log($"[TBController] {unitName}: Step FAILED - {result.Reason}");
                    return ExecutionResult.Waiting("Step failed - retry");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[TBController] {unitName}: Exception - {ex.Message}");
                Main.Error($"[TBController] Stack: {ex.StackTrace}");
                return ExecutionResult.EndTurn($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Plan Management

        private TurnPlan GetOrCreatePlan(UnitEntityData unit, TurnStateInfo stateInfo)
        {
            string unitId = unit.UniqueId;

            if (_turnPlans.TryGetValue(unitId, out var plan))
            {
                // 같은 라운드의 유효한 계획이면 재사용
                if (plan.CombatRound == stateInfo.CombatRound && plan.IsValid)
                {
                    return plan;
                }
            }

            // 새 계획 생성
            return CreateNewPlan(unit, stateInfo);
        }

        private TurnPlan CreateNewPlan(UnitEntityData unit, TurnStateInfo stateInfo)
        {
            string unitId = unit.UniqueId;
            string unitName = unit.CharacterName;

            // 기존 계획의 리플랜 횟수 가져오기
            int replanCount = 0;
            if (_turnPlans.TryGetValue(unitId, out var oldPlan))
            {
                replanCount = oldPlan.ReplanCount + 1;
            }

            // TurnState 준비
            var turnState = GetOrCreateLegacyTurnState(unit);
            turnState.SyncFromGameState(unit);

            // 계획 수립
            var plan = TurnPlanBuilder.Instance.BuildPlan(unit, turnState);
            plan.ReplanCount = replanCount;

            _turnPlans[unitId] = plan;

            Main.Log($"[TBController] {unitName}: Created new plan (Replan#{replanCount})");
            return plan;
        }

        #endregion

        #region Step Execution

        private ExecutionResult ExecuteStep(TurnPlanStep step, UnitEntityData unit)
        {
            step.Status = StepStatus.Executing;

            switch (step.StepType)
            {
                case PlanStepType.SelfBuff:
                case PlanStepType.AllyBuff:
                    return ExecuteBuffStep(step, unit);

                case PlanStepType.MoveToAttack:
                case PlanStepType.MoveToSafety:
                case PlanStepType.MoveToEngage:
                    return ExecuteMoveStep(step, unit);

                case PlanStepType.Attack:
                case PlanStepType.Debuff:
                    return ExecuteAttackStep(step, unit);

                case PlanStepType.EndTurn:
                    return ExecutionResult.EndTurn(step.Description);

                default:
                    return ExecutionResult.Failure($"Unknown step type: {step.StepType}");
            }
        }

        private ExecutionResult ExecuteBuffStep(TurnPlanStep step, UnitEntityData unit)
        {
            if (step.Ability == null)
                return ExecutionResult.Failure("No ability");

            var target = step.TargetUnit ?? unit;
            var plannedAction = PlannedAction.Buff(step.Ability, target, step.Description);

            return ActionExecutor.Instance.Execute(plannedAction, unit);
        }

        private ExecutionResult ExecuteMoveStep(TurnPlanStep step, UnitEntityData unit)
        {
            if (!step.TargetPosition.HasValue)
                return ExecutionResult.Failure("No target position");

            var plannedAction = PlannedAction.Move(step.TargetPosition.Value, step.Description);

            return ActionExecutor.Instance.Execute(plannedAction, unit);
        }

        private ExecutionResult ExecuteAttackStep(TurnPlanStep step, UnitEntityData unit)
        {
            if (step.Ability == null || step.TargetUnit == null)
                return ExecutionResult.Failure("No ability or target");

            // 타겟이 죽었는지 확인
            if (step.TargetUnit.HPLeft <= 0)
                return ExecutionResult.Failure("Target is dead");

            var plannedAction = PlannedAction.Attack(step.Ability, step.TargetUnit, step.Description);

            return ActionExecutor.Instance.Execute(plannedAction, unit);
        }

        #endregion

        #region State Management

        private TurnStateInfo GetOrCreateTurnState(UnitEntityData unit)
        {
            string unitId = unit.UniqueId;
            int currentRound = GameAPI.GetCurrentRound();

            if (_turnStates.TryGetValue(unitId, out var state))
            {
                if (state.CombatRound != currentRound)
                {
                    state = new TurnStateInfo(unitId, currentRound);
                    _turnStates[unitId] = state;
                    _turnPlans.Remove(unitId); // 새 라운드 = 새 계획
                    Main.Log($"[TBController] {unit.CharacterName}: New round ({currentRound})");
                }
            }
            else
            {
                state = new TurnStateInfo(unitId, currentRound);
                _turnStates[unitId] = state;
                Main.Log($"[TBController] {unit.CharacterName}: New turn (Round {currentRound})");
            }

            return state;
        }

        private TurnState GetOrCreateLegacyTurnState(UnitEntityData unit)
        {
            string unitId = unit.UniqueId;

            if (!_legacyTurnStates.TryGetValue(unitId, out var state) ||
                state.CombatRound != GameAPI.GetCurrentRound())
            {
                state = new TurnState(unit);
                _legacyTurnStates[unitId] = state;
            }

            return state;
        }

        private void MarkTurnFinished(TurnStateInfo state)
        {
            state.IsFinished = true;
        }

        private void CheckRoundChange()
        {
            int currentRound = GameAPI.GetCurrentRound();
            if (currentRound != _lastProcessedRound)
            {
                Main.Log($"[TBController] Round changed: {_lastProcessedRound} → {currentRound}");
                _lastProcessedRound = currentRound;
                OnNewRound();
            }
        }

        private void OnNewRound()
        {
            _turnStates.Clear();
            _turnPlans.Clear();
            _legacyTurnStates.Clear();
            Main.Log("[TBController] New round - all states cleared");
        }

        /// <summary>
        /// ★ v0.2.115: CombatDataCache 사용 - 전투 중인 적만 반환
        /// GameAPI.GetEnemies()는 게임 월드 전체 적을 반환하므로 사용 금지
        /// </summary>
        private List<UnitEntityData> GetEnemies(UnitEntityData unit)
        {
            // CombatDataCache 사용 (SituationAnalyzer와 동일한 소스)
            CombatDataCache.Instance.RefreshIfNeeded();
            return CombatDataCache.Instance.AllEnemies;
        }

        #endregion

        #region Public API

        public void ResetTurnState(string unitId)
        {
            _turnStates.Remove(unitId);
            _turnPlans.Remove(unitId);
            _legacyTurnStates.Remove(unitId);
        }

        public void ResetAll()
        {
            _turnStates.Clear();
            _turnPlans.Clear();
            _legacyTurnStates.Clear();
            _lastProcessedRound = -1;
            Main.Log("[TBController] All states reset");
        }

        public bool HasActedThisTurn(string unitId)
        {
            if (_turnStates.TryGetValue(unitId, out var state))
            {
                return state.StepCount > 0;
            }
            return false;
        }

        public bool IsTurnFinished(string unitId)
        {
            if (_turnStates.TryGetValue(unitId, out var state))
            {
                return state.IsFinished;
            }
            return false;
        }

        public void RecordAbilityFailure(string unitId, string abilityGuid, string reason)
        {
            if (_turnStates.TryGetValue(unitId, out var state))
            {
                state.FailedAbilities.Add(abilityGuid);
                Main.Log($"[TBController] Ability failure recorded: {abilityGuid} - {reason}");
            }
        }

        public bool IsAbilityFailedThisTurn(string unitId, string abilityGuid)
        {
            if (_turnStates.TryGetValue(unitId, out var state))
            {
                return state.FailedAbilities.Contains(abilityGuid);
            }
            return false;
        }

        /// <summary>
        /// 현재 유닛의 계획 가져오기 (디버그용)
        /// </summary>
        public TurnPlan GetCurrentPlan(string unitId)
        {
            _turnPlans.TryGetValue(unitId, out var plan);
            return plan;
        }

        #endregion
    }
}
