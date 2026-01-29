using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;

namespace CompanionAI_Pathfinder.Core
{
    /// <summary>
    /// 계획된 단일 행동
    /// TurnPlanner가 생성하고, ActionExecutor가 실행
    /// </summary>
    public class PlannedAction
    {
        /// <summary>행동 유형</summary>
        public ActionType Type { get; set; }

        /// <summary>사용할 능력 (Attack, Buff, Heal 등)</summary>
        public AbilityData Ability { get; set; }

        /// <summary>능력의 타겟</summary>
        public TargetWrapper Target { get; set; }

        /// <summary>이동 목적지 (Move 타입일 때)</summary>
        public Vector3? MoveDestination { get; set; }

        /// <summary>
        /// 이 행동의 비용
        /// Pathfinder: Standard/Move/Swift Action 시스템
        /// </summary>
        public float ActionCost { get; set; }

        /// <summary>이 행동을 선택한 이유</summary>
        public string Reason { get; set; }

        /// <summary>우선순위 (낮을수록 먼저 실행)</summary>
        public int Priority { get; set; }

        /// <summary>
        /// MultiTarget 능력용 타겟 리스트
        /// 2개 이상의 Point 타겟이 필요한 능력에 사용
        /// </summary>
        public List<TargetWrapper> AllTargets { get; set; }

        /// <summary>실행 완료 여부</summary>
        public bool IsExecuted { get; set; }

        /// <summary>실행 결과</summary>
        public bool? WasSuccessful { get; set; }

        #region Factory Methods

        public static PlannedAction Buff(AbilityData ability, UnitEntityData self, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Buff,
                Ability = ability,
                Target = new TargetWrapper(self),
                ActionCost = cost,
                Reason = reason,
                Priority = 10
            };
        }

        public static PlannedAction Attack(AbilityData ability, UnitEntityData target, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = new TargetWrapper(target),
                ActionCost = cost,
                Reason = reason,
                Priority = 50
            };
        }

        public static PlannedAction Heal(AbilityData ability, UnitEntityData target, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Heal,
                Ability = ability,
                Target = new TargetWrapper(target),
                ActionCost = cost,
                Reason = reason,
                Priority = 1
            };
        }

        public static PlannedAction Move(Vector3 destination, string reason)
        {
            return new PlannedAction
            {
                Type = ActionType.Move,
                MoveDestination = destination,
                ActionCost = 0,  // Move action
                Reason = reason,
                Priority = 20
            };
        }

        public static PlannedAction Debuff(AbilityData ability, UnitEntityData target, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Debuff,
                Ability = ability,
                Target = new TargetWrapper(target),
                ActionCost = cost,
                Reason = reason,
                Priority = 30
            };
        }

        public static PlannedAction Support(AbilityData ability, UnitEntityData target, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Support,
                Ability = ability,
                Target = new TargetWrapper(target),
                ActionCost = cost,
                Reason = reason,
                Priority = 15
            };
        }

        /// <summary>
        /// 위치 타겟 버프 (전방/보조/후방 구역 등)
        /// </summary>
        public static PlannedAction PositionalBuff(AbilityData ability, Vector3 position, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Support,
                Ability = ability,
                Target = new TargetWrapper(position),
                ActionCost = cost,
                Reason = reason,
                Priority = 12
            };
        }

        /// <summary>
        /// 위치 타겟 공격 (AOE 등)
        /// </summary>
        public static PlannedAction PositionalAttack(AbilityData ability, Vector3 position, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = new TargetWrapper(position),
                ActionCost = cost,
                Reason = reason,
                Priority = 25
            };
        }

        /// <summary>
        /// MultiTarget 공격 (2개 Point 필요)
        /// </summary>
        public static PlannedAction MultiTargetAttack(AbilityData ability, List<TargetWrapper> allTargets, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = allTargets?.Count > 0 ? allTargets[0] : null,
                AllTargets = allTargets,
                ActionCost = cost,
                Reason = reason,
                Priority = 25
            };
        }

        /// <summary>
        /// 위치 타겟 힐 (AOE 힐 등)
        /// </summary>
        public static PlannedAction PositionalHeal(AbilityData ability, Vector3 position, string reason, float cost = 0)
        {
            return new PlannedAction
            {
                Type = ActionType.Heal,
                Ability = ability,
                Target = new TargetWrapper(position),
                ActionCost = cost,
                Reason = reason,
                Priority = 2
            };
        }

        public static PlannedAction EndTurn(string reason = "No more actions available")
        {
            return new PlannedAction
            {
                Type = ActionType.EndTurn,
                Reason = reason,
                Priority = 100
            };
        }

        #endregion

        public override string ToString()
        {
            if (Type == ActionType.EndTurn)
            {
                return $"[EndTurn] ({Reason})";
            }

            if (Type == ActionType.Move)
            {
                return $"[Move] -> {MoveDestination?.ToString() ?? "unknown"} ({Reason})";
            }

            string targetName = Target?.Unit?.CharacterName ?? "point";
            return $"[{Type}] {Ability?.Name ?? "?"} -> {targetName} ({Reason})";
        }
    }

    /// <summary>
    /// 행동 유형
    /// </summary>
    public enum ActionType
    {
        /// <summary>자기 버프</summary>
        Buff,

        /// <summary>이동</summary>
        Move,

        /// <summary>공격</summary>
        Attack,

        /// <summary>힐링 (자신 또는 아군)</summary>
        Heal,

        /// <summary>아군 지원 버프</summary>
        Support,

        /// <summary>적 디버프</summary>
        Debuff,

        /// <summary>특수 능력</summary>
        Special,

        /// <summary>턴 종료</summary>
        EndTurn
    }
}
