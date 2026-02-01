// ★ v0.2.116: 능력 타겟팅 실패 시 기본 공격 폴백 추가
using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Utility;
using TurnBased.Controllers;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Core;

namespace CompanionAI_Pathfinder.Execution
{
    /// <summary>
    /// 행동 실행기 - 계획된 행동을 실행
    /// Pathfinder 명령 시스템 사용
    /// </summary>
    public class ActionExecutor
    {
        #region Singleton

        private static ActionExecutor _instance;
        public static ActionExecutor Instance => _instance ?? (_instance = new ActionExecutor());

        private ActionExecutor() { }

        #endregion

        /// <summary>
        /// 계획된 행동 실행
        /// </summary>
        public ExecutionResult Execute(PlannedAction action, UnitEntityData unit)
        {
            if (action == null)
            {
                return ExecutionResult.EndTurn("No action");
            }

            if (unit == null)
            {
                return ExecutionResult.Failure("Unit is null");
            }

            Main.Verbose($"[Executor] Executing: {action}");

            try
            {
                switch (action.Type)
                {
                    case ActionType.Buff:
                    case ActionType.Attack:
                    case ActionType.Heal:
                    case ActionType.Debuff:
                    case ActionType.Support:
                    case ActionType.Special:
                        return ExecuteAbility(action, unit);

                    case ActionType.Move:
                        return ExecuteMove(action, unit);

                    case ActionType.EndTurn:
                        return ExecutionResult.EndTurn(action.Reason);

                    default:
                        return ExecutionResult.Failure($"Unknown action type: {action.Type}");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[Executor] Error executing {action.Type}: {ex.Message}");
                return ExecutionResult.Failure($"Execution error: {ex.Message}");
            }
        }

        /// <summary>
        /// 능력 실행
        /// ★ v0.2.22: BasicAttack 지원 (Ability=null 시 UnitAttack 사용)
        /// </summary>
        private ExecutionResult ExecuteAbility(PlannedAction action, UnitEntityData unit)
        {
            var ability = action.Ability;
            var target = action.Target;

            if (target == null)
            {
                return ExecutionResult.Failure("Target is null");
            }

            // ★ v0.2.22: BasicAttack 처리 (Ability가 null인 Attack 타입)
            if (ability == null)
            {
                if (action.Type == ActionType.Attack && target.Unit != null)
                {
                    return ExecuteBasicAttack(target.Unit, unit);
                }
                return ExecutionResult.Failure("Ability is null");
            }

            // 능력 사용 가능 여부 확인
            if (!ability.IsAvailable)
            {
                Main.Log($"[Executor] Ability unavailable: {ability.Name}");
                return ExecutionResult.Failure($"Ability unavailable: {ability.Name}");
            }

            // 타겟 유효성 확인
            if (!ability.CanTarget(target))
            {
                Main.Log($"[Executor] Cannot target: {ability.Name} -> {GetTargetName(target)}");

                // ★ v0.2.116: 공격 타입이고 유닛 타겟이면 기본 공격으로 폴백
                if (action.Type == ActionType.Attack && target.Unit != null)
                {
                    Main.Log($"[Executor] Falling back to basic attack: {unit.CharacterName} -> {target.Unit.CharacterName}");
                    return ExecuteBasicAttack(target.Unit, unit);
                }

                return ExecutionResult.Failure($"Cannot target: {GetTargetName(target)}");
            }

            // MultiTarget 능력 처리
            if (action.AllTargets != null && action.AllTargets.Count > 0)
            {
                return ExecuteMultiTargetAbility(ability, action.AllTargets, unit);
            }

            // 일반 능력 실행
            return ExecuteSingleTargetAbility(ability, target, unit);
        }

        /// <summary>
        /// 단일 타겟 능력 실행
        /// ★ v0.2.82: 명령 발행 결과 검증 추가
        /// ★ v0.2.89: 상세 명령 상태 로깅 추가
        /// ★ v0.2.92: 게임 AI 시스템 호환 - AiAction 설정
        /// </summary>
        private ExecutionResult ExecuteSingleTargetAbility(AbilityData ability, TargetWrapper target, UnitEntityData unit)
        {
            try
            {
                // ★ v0.2.84: 디버그 - 명령 발행 전 상태 확인
                var tbController = Game.Instance?.TurnBasedCombatController;
                var currentTurn = tbController?.CurrentTurn;
                bool isCurrentUnit = unit.IsCurrentUnit();
                string riderName = currentTurn?.Rider?.CharacterName ?? "null";
                string turnStatus = currentTurn?.Status.ToString() ?? "null";

                Main.Log($"[Executor] PRE-RUN: {unit.CharacterName}");
                Main.Log($"[Executor]   IsCurrentUnit={isCurrentUnit}, Rider={riderName}, Status={turnStatus}");

                // ★ v0.2.82: 발행 전 상태 확인
                bool wasEmpty = unit.Commands.Empty;
                bool wasHasUnfinished = unit.Commands.HasUnfinished();

                // Pathfinder 명령 시스템으로 실행
                var command = new UnitUseAbility(ability, target);

                // ★ v0.2.92: 게임 AI 시스템과 호환
                // 게임의 AiBrainController.SelectAction()과 동일한 방식으로 설정
                var aiAction = ability.DefaultAiAction;
                if (aiAction != null)
                {
                    command.AiAction = aiAction;
                    Main.Verbose($"[Executor] Set AiAction: {aiAction.DebugName}");
                }

                // ★ v0.2.92: NextCommandTime 설정 (게임 AI와 동일)
                // Free/Swift 아닌 경우 0.3초 + 랜덤 지연
                if (command.Type != UnitCommand.CommandType.Free && command.Type != UnitCommand.CommandType.Swift)
                {
                    unit.CombatState.AIData.NextCommandTime = UnityEngine.Time.time + 0.3f + UnityEngine.Random.Range(0f, 0.1f);
                }

                unit.Commands.Run(command);

                // ★ v0.2.89: 상세 명령 상태 로깅
                bool isInRunningCommands = currentTurn?.IsRunningCommand(command) ?? false;
                Main.Log($"[Executor] POST-RUN: Empty={unit.Commands.Empty}, HasUnfinished={unit.Commands.HasUnfinished()}, InRunning={isInRunningCommands}");
                Main.Log($"[Executor]   Command: Started={command.IsStarted}, Running={command.IsRunning}, Finished={command.IsFinished}, Acted={command.IsActed}");

                // ★ v0.2.89: 슬롯 상태 로깅
                int slotIdx = 0;
                foreach (var cmd in unit.Commands.Raw)
                {
                    if (cmd != null)
                    {
                        bool isSameCmd = ReferenceEquals(cmd, command);
                        Main.Log($"[Executor]   Slot[{slotIdx}]: {cmd.GetType().Name} (Started={cmd.IsStarted}, Finished={cmd.IsFinished}) {(isSameCmd ? "← OUR CMD" : "")}");
                    }
                    slotIdx++;
                }

                // ★ v0.2.82: 명령이 실제로 등록됐는지 검증
                if (unit.Commands.Empty && wasEmpty)
                {
                    Main.Log($"[Executor] Command NOT registered: {ability.Name} -> {GetTargetName(target)}");
                    Main.Log($"[Executor] Possible cause: CanRunCommand() returned false (CantUseStandardActions or not conscious)");
                    return ExecutionResult.Failure($"Command not registered: {ability.Name}");
                }

                Main.Log($"[Executor] Cast: {ability.Name} -> {GetTargetName(target)} (Command registered)");
                return ExecutionResult.CastAbility(ability, target);
            }
            catch (Exception ex)
            {
                Main.Error($"[Executor] Failed to cast {ability.Name}: {ex.Message}");
                return ExecutionResult.Failure($"Cast failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 멀티 타겟 능력 실행
        /// </summary>
        private ExecutionResult ExecuteMultiTargetAbility(AbilityData ability, List<TargetWrapper> targets, UnitEntityData unit)
        {
            try
            {
                // 첫 번째 타겟으로 실행 (Pathfinder에서 멀티타겟 처리는 게임 엔진이 담당)
                if (targets.Count == 0)
                {
                    return ExecutionResult.Failure("No targets for multi-target ability");
                }

                var primaryTarget = targets[0];
                var command = new UnitUseAbility(ability, primaryTarget);
                unit.Commands.Run(command);

                Main.Log($"[Executor] Cast MultiTarget: {ability.Name} ({targets.Count} targets)");
                return ExecutionResult.CastAbilityMultiTarget(ability, targets);
            }
            catch (Exception ex)
            {
                Main.Error($"[Executor] Failed to cast multi-target {ability.Name}: {ex.Message}");
                return ExecutionResult.Failure($"Multi-target cast failed: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.22: 기본 공격 실행 (UnitAttack)
        /// ★ v0.2.82: 명령 발행 결과 검증 추가
        /// ★ v0.2.92: 게임 AI 시스템 호환 - NextCommandTime 설정
        /// </summary>
        private ExecutionResult ExecuteBasicAttack(UnitEntityData target, UnitEntityData unit)
        {
            try
            {
                bool wasEmpty = unit.Commands.Empty;

                var command = new UnitAttack(target);

                // ★ v0.2.92: NextCommandTime 설정 (게임 AI와 동일)
                unit.CombatState.AIData.NextCommandTime = UnityEngine.Time.time + 0.3f + UnityEngine.Random.Range(0f, 0.1f);

                unit.Commands.Run(command);

                // ★ v0.2.82: 명령 등록 검증
                if (unit.Commands.Empty && wasEmpty)
                {
                    string targetName = target.CharacterName;
                    Main.Log($"[Executor] BasicAttack NOT registered: {unit.CharacterName} -> {targetName}");
                    return ExecutionResult.Failure($"BasicAttack not registered");
                }

                Main.Log($"[Executor] BasicAttack: {unit.CharacterName} -> {target.CharacterName} (Command registered)");
                return ExecutionResult.BasicAttack(target.CharacterName);
            }
            catch (Exception ex)
            {
                Main.Error($"[Executor] BasicAttack failed: {ex.Message}");
                return ExecutionResult.Failure($"Basic attack failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 이동 실행
        /// ★ v0.2.30: BattlefieldGrid 위치 검증 추가
        /// ★ v0.2.82: 명령 발행 결과 검증 추가
        /// ★ v0.2.93: approachRadius 문제 수정 - 0f 대신 적절한 값 사용
        /// </summary>
        private ExecutionResult ExecuteMove(PlannedAction action, UnitEntityData unit)
        {
            if (!action.MoveDestination.HasValue)
            {
                return ExecutionResult.Failure("Move destination is null");
            }

            var destination = action.MoveDestination.Value;

            try
            {
                // ★ v0.2.30: BattlefieldGrid로 목표 위치 사전 검증
                if (!BattlefieldGrid.Instance.ValidateTargetPosition(unit, destination))
                {
                    Main.Log($"[Executor] Move target invalid: ({destination.x:F1}, {destination.z:F1}) - not walkable or occupied");
                    return ExecutionResult.Failure("Move target invalid: not walkable or occupied");
                }

                bool wasEmpty = unit.Commands.Empty;

                // ★ v0.2.93: 첫 번째 생성자 사용 (MaxApproachRadius=1000000f)
                // approachRadius=0f는 정확히 목적지에 도달해야 해서 IsUnitCloseEnough()=false 발생
                // 첫 번째 생성자는 ApproachRadius를 설정하지 않아 MaxApproachRadius만 적용됨
                var command = new UnitMoveTo(destination);

                // ★ v0.2.92: NextCommandTime 설정 (게임 AI와 동일)
                // Move 명령은 Standard가 아니므로 짧은 지연
                unit.CombatState.AIData.NextCommandTime = UnityEngine.Time.time + 0.1f;

                unit.Commands.Run(command);

                // ★ v0.2.82: 명령 등록 검증
                if (unit.Commands.Empty && wasEmpty)
                {
                    Main.Log($"[Executor] Move NOT registered: ({destination.x:F1}, {destination.z:F1})");
                    return ExecutionResult.Failure("Move command not registered");
                }

                Main.Log($"[Executor] Move to: ({destination.x:F1}, {destination.z:F1}) (Command registered)");
                return ExecutionResult.MoveTo(destination);
            }
            catch (Exception ex)
            {
                Main.Error($"[Executor] Failed to move: {ex.Message}");
                return ExecutionResult.Failure($"Move failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 유닛이 명령 실행 중인지 확인
        /// ★ v0.2.83: 로그 트레이더 모드와 동일하게 Commands.Empty 기반으로 변경
        ///
        /// 게임 분석 결과 (UnitCommandController.cs, TurnController.cs):
        /// - Commands.Run() → OnRun() → StartInTbm() → m_RunningCommands에 추가
        /// - 다음 프레임: UnitCommandController.TickOnUnit() → TickCommand()
        /// - TickCommandTurnBased() 조건 통과 → ShouldStartCommand() → command.Start()
        /// - command.IsRunning이 되면 command.Tick() 실행
        ///
        /// v0.2.81의 문제점:
        /// - IsStarted=false인 명령을 무시 → 다음 틱에서 새 명령으로 덮어쓰기
        /// - UnitCommandController가 Start()를 호출할 기회가 없음
        ///
        /// 해결: 슬롯에 미완료 명령이 있으면 무조건 대기 (게임이 처리할 때까지)
        /// 로그 트레이더 CombatAPI.IsCommandQueueEmpty()와 동일한 로직 사용
        /// </summary>
        public bool IsExecutingCommand(UnitEntityData unit)
        {
            if (unit?.Commands == null) return false;

            // ★ v0.2.83: 슬롯에 미완료 명령이 있으면 게임이 처리할 때까지 대기
            // 게임의 UnitCommandController가 Start() → Tick()을 처리함
            foreach (var cmd in unit.Commands.Raw)
            {
                if (cmd == null) continue;
                if (cmd.IsFinished) continue;

                // ★ v0.2.83: IsStarted 여부와 관계없이 미완료 명령이 있으면 대기
                Main.Verbose($"[Executor] Pending command: {cmd.GetType().Name} (Started={cmd.IsStarted}, Running={cmd.IsRunning}, Finished={cmd.IsFinished})");
                return true;
            }

            return false;
        }

        /// <summary>
        /// ★ v0.2.81: 디버그용 - 현재 명령 상태 로깅
        /// </summary>
        public void LogCommandState(UnitEntityData unit, string context)
        {
            if (unit?.Commands == null)
            {
                Main.Log($"[Executor] {context}: Commands is null");
                return;
            }

            var commands = unit.Commands;
            Main.Log($"[Executor] {context}: HasUnfinished={commands.HasUnfinished()}, Empty={commands.Empty}");

            int idx = 0;
            foreach (var cmd in commands.Raw)
            {
                if (cmd != null)
                {
                    Main.Log($"  [{idx}] {cmd.GetType().Name}: Started={cmd.IsStarted}, Running={cmd.IsRunning}, Finished={cmd.IsFinished}, Acted={cmd.IsActed}");
                }
                idx++;
            }
        }

        /// <summary>
        /// 현재 명령 완료 대기
        /// </summary>
        public bool WaitForCommandCompletion(UnitEntityData unit)
        {
            return !IsExecutingCommand(unit);
        }

        /// <summary>
        /// 타겟 이름 추출 (로깅용)
        /// </summary>
        private string GetTargetName(TargetWrapper target)
        {
            if (target == null) return "null";

            if (target.Unit != null)
            {
                return target.Unit.CharacterName;
            }

            // Point 타겟
            var point = target.Point;
            if (point.sqrMagnitude > 0.001f)
            {
                return $"Point({point.x:F1}, {point.z:F1})";
            }

            return "unknown";
        }

        /// <summary>
        /// 유닛의 모든 명령 취소
        /// </summary>
        public void CancelAllCommands(UnitEntityData unit)
        {
            try
            {
                unit?.Commands?.InterruptAll();
                Main.Verbose($"[Executor] Commands cancelled for {unit?.CharacterName}");
            }
            catch (Exception ex)
            {
                Main.Error($"[Executor] Failed to cancel commands: {ex.Message}");
            }
        }
    }
}
