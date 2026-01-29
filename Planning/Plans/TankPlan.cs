using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.Planning.Plans
{
    /// <summary>
    /// Tank 전략
    /// 방어 자세 우선, 도발, 전선 유지, 적에게 접근
    /// </summary>
    public class TankPlan : BasePlan
    {
        protected override string RoleName => "Tank";

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
                return new TurnPlan(actions, TurnPriority.Emergency, "Tank emergency heal");
            }

            // Phase 2: 방어 버프 (첫 행동)
            // Tank는 방어적 버프 우선
            if (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn)
            {
                var buffAction = PlanDefensiveBuff(situation, ref hasStandardAction);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }

            // Phase 3: 적에게 접근 (공격 범위 밖이면)
            float meleeEngageDistance = 3f;
            if (!situation.HasHittableEnemies && situation.HasLivingEnemies &&
                situation.NearestEnemyDistance > meleeEngageDistance && hasMoveAction)
            {
                var moveAction = PlanMoveToEnemy(situation, ref hasMoveAction);
                if (moveAction != null)
                {
                    actions.Add(moveAction);
                    Main.Log($"[Tank] Engaging melee range");
                }
            }

            // Phase 4: 공격 (가장 가까운 적)
            if (hasStandardAction && situation.HasHittableEnemies)
            {
                var attackAction = PlanAttack(situation, ref hasStandardAction, situation.NearestEnemy);
                if (attackAction != null)
                {
                    actions.Add(attackAction);
                }
            }

            // Phase 5: 추가 이동 (적이 아직 멀면)
            if (hasMoveAction && situation.HasLivingEnemies && situation.NearestEnemyDistance > meleeEngageDistance)
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
                actions.Add(PlannedAction.EndTurn("Tank holding position"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"Tank: {DetermineReasoning(actions, situation)}";

            return new TurnPlan(actions, priority, reasoning);
        }

        /// <summary>
        /// 방어적 버프 계획 (Tank 특화)
        /// </summary>
        private PlannedAction PlanDefensiveBuff(Situation situation, ref bool hasStandardAction)
        {
            // 기본 버프 메서드 사용
            // 향후: 방어 자세 스킬 우선 선택 로직 추가 가능
            return PlanBuff(situation, ref hasStandardAction);
        }
    }
}
