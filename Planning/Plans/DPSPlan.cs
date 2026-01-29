using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.Planning.Plans
{
    /// <summary>
    /// DPS 전략
    /// 공격 우선, 약한 적 마무리, 버프 후 공격
    /// </summary>
    public class DPSPlan : BasePlan
    {
        protected override string RoleName => "DPS";

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var actions = new List<PlannedAction>();

            // Pathfinder 액션 시스템: Standard/Move/Swift
            bool hasStandardAction = turnState?.HasStandardAction ?? situation.HasStandardAction;
            bool hasMoveAction = turnState?.HasMoveAction ?? situation.HasMoveAction;
            bool hasSwiftAction = turnState?.HasSwiftAction ?? situation.HasSwiftAction;

            // Phase 1: 긴급 자기 힐
            var healAction = PlanEmergencyHeal(situation, ref hasStandardAction);
            if (healAction != null)
            {
                actions.Add(healAction);
                return new TurnPlan(actions, TurnPriority.Emergency, "DPS emergency heal");
            }

            // Phase 2: 원거리 캐릭터 후퇴 (위험 시)
            if (ShouldRetreat(situation))
            {
                var retreatAction = PlanRetreat(situation, ref hasMoveAction);
                if (retreatAction != null)
                {
                    actions.Add(retreatAction);
                }
            }

            // Phase 3: 공격 전 버프 (첫 행동)
            if (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn)
            {
                var buffAction = PlanBuff(situation, ref hasStandardAction);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }

            // Phase 4: 마무리 공격 (낮은 HP 적 우선)
            var lowHPEnemy = FindLowHPEnemy(situation, 30f);
            if (lowHPEnemy != null && hasStandardAction)
            {
                var finisherAction = PlanAttack(situation, ref hasStandardAction, lowHPEnemy);
                if (finisherAction != null)
                {
                    actions.Add(finisherAction);
                    Main.Log($"[DPS] Finisher attack on {lowHPEnemy.CharacterName}");
                }
            }

            // Phase 5: 일반 공격 (가장 약한 적)
            if (hasStandardAction && situation.HasHittableEnemies)
            {
                var weakestEnemy = FindWeakestEnemy(situation);
                var attackAction = PlanAttack(situation, ref hasStandardAction, weakestEnemy);
                if (attackAction != null)
                {
                    actions.Add(attackAction);
                }
            }

            // Phase 6: 공격 불가 시 이동
            if (!situation.HasHittableEnemies && situation.HasLivingEnemies && hasMoveAction)
            {
                var moveAction = PlanMoveToEnemy(situation, ref hasMoveAction);
                if (moveAction != null)
                {
                    actions.Add(moveAction);
                }
            }

            // 턴 종료
            if (actions.Count == 0)
            {
                actions.Add(PlannedAction.EndTurn("DPS no targets"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"DPS: {DetermineReasoning(actions, situation)}";

            return new TurnPlan(actions, priority, reasoning);
        }
    }
}
