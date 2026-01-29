// ★ v0.2.30: BattlefieldGrid 위치 검증 추가
using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Utility;
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
        /// </summary>
        private ExecutionResult ExecuteSingleTargetAbility(AbilityData ability, TargetWrapper target, UnitEntityData unit)
        {
            try
            {
                // Pathfinder 명령 시스템으로 실행
                var command = new UnitUseAbility(ability, target);
                unit.Commands.Run(command);

                Main.Log($"[Executor] Cast: {ability.Name} -> {GetTargetName(target)}");
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
        /// UnifiedDecisionEngine에서 BasicAttack 타입으로 요청 시 호출
        /// </summary>
        private ExecutionResult ExecuteBasicAttack(UnitEntityData target, UnitEntityData unit)
        {
            try
            {
                var command = new UnitAttack(target);
                unit.Commands.Run(command);

                string targetName = target.CharacterName;
                Main.Log($"[Executor] BasicAttack: {unit.CharacterName} -> {targetName}");
                return ExecutionResult.BasicAttack(targetName);
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

                // Pathfinder 이동 명령
                var command = new UnitMoveTo(destination, 0f);
                unit.Commands.Run(command);

                Main.Log($"[Executor] Move to: ({destination.x:F1}, {destination.z:F1})");
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
        /// v0.2.3: HasUnfinished() 사용으로 더 정확한 체크
        /// </summary>
        public bool IsExecutingCommand(UnitEntityData unit)
        {
            if (unit?.Commands == null) return false;

            // v0.2.3: HasUnfinished()가 더 정확함 - 완료되었지만 아직 정리되지 않은 명령 처리
            // Empty는 슬롯이 null인지만 체크, HasUnfinished는 실제 완료 여부 체크
            return unit.Commands.HasUnfinished();
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
