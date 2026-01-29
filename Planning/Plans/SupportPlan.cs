using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Utility;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.Planning.Plans
{
    /// <summary>
    /// Support 전략
    /// 힐 우선, 버프, 디버프, 안전 공격, 후퇴
    /// </summary>
    public class SupportPlan : BasePlan
    {
        protected override string RoleName => "Support";

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var actions = new List<PlannedAction>();

            // Pathfinder 액션 시스템: Standard/Move/Swift
            bool hasStandardAction = turnState?.HasStandardAction ?? situation.HasStandardAction;
            bool hasMoveAction = turnState?.HasMoveAction ?? situation.HasMoveAction;
            bool hasSwiftAction = turnState?.HasSwiftAction ?? situation.HasSwiftAction;

            // Phase 1: 긴급 자기 힐
            var selfHealAction = PlanEmergencyHeal(situation, ref hasStandardAction);
            if (selfHealAction != null)
            {
                actions.Add(selfHealAction);
                return new TurnPlan(actions, TurnPriority.Emergency, "Support emergency self-heal");
            }

            // Phase 2: 아군 힐 (HP < 60%)
            if (hasStandardAction)
            {
                var allyHealAction = PlanAllyHeal(situation, ref hasStandardAction, 60f);
                if (allyHealAction != null)
                {
                    actions.Add(allyHealAction);
                }
            }

            // Phase 3: 자기 버프
            if (hasStandardAction && !situation.HasBuffedThisTurn)
            {
                var selfBuffAction = PlanBuff(situation, ref hasStandardAction);
                if (selfBuffAction != null)
                {
                    actions.Add(selfBuffAction);
                }
            }

            // Phase 4: 아군 버프
            if (hasStandardAction)
            {
                var allyBuffAction = PlanAllyBuff(situation, ref hasStandardAction);
                if (allyBuffAction != null)
                {
                    actions.Add(allyBuffAction);
                }
            }

            // Phase 5: 디버프
            if (hasStandardAction && situation.NearestEnemy != null)
            {
                var debuffAction = PlanDebuff(situation, ref hasStandardAction, situation.NearestEnemy);
                if (debuffAction != null)
                {
                    actions.Add(debuffAction);
                }
            }

            // Phase 6: 안전 공격 (원거리)
            if (hasStandardAction && situation.HasHittableEnemies && situation.PrefersRanged)
            {
                var attackAction = PlanSafeRangedAttack(situation, ref hasStandardAction);
                if (attackAction != null)
                {
                    actions.Add(attackAction);
                }
            }

            // Phase 7: 후퇴 (위험 시)
            if (ShouldRetreat(situation) && hasMoveAction)
            {
                var retreatAction = PlanRetreat(situation, ref hasMoveAction);
                if (retreatAction != null)
                {
                    actions.Add(retreatAction);
                }
            }

            // 턴 종료
            if (actions.Count == 0)
            {
                actions.Add(PlannedAction.EndTurn("Support maintaining position"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"Support: {DetermineReasoning(actions, situation)}";

            return new TurnPlan(actions, priority, reasoning);
        }

        /// <summary>
        /// 안전한 원거리 공격 계획
        /// </summary>
        private PlannedAction PlanSafeRangedAttack(Situation situation, ref bool hasStandardAction)
        {
            if (!hasStandardAction)
                return null;

            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
                return null;

            // 타겟 선택
            var target = situation.BestTarget ?? situation.NearestEnemy;
            if (target == null)
                return null;

            var targetWrapper = new TargetWrapper(target);

            // 원거리 공격 우선 (Touch 이외)
            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack?.Blueprint == null) continue;

                // Touch 범위는 근접이므로 스킵
                if (attack.Blueprint.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch)
                    continue;

                if (attack.CanTarget(targetWrapper))
                {
                    hasStandardAction = false;
                    Main.Log($"[Support] Safe attack: {attack.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(attack, target, $"Safe attack on {target.CharacterName}", 1f);
                }
            }

            return null;
        }
    }
}
