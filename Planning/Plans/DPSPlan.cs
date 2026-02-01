using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Planning.Plans
{
    /// <summary>
    /// DPS 전략
    /// ★ v0.2.95: SequenceOptimizer 통합 - 능동적 판단
    /// "후퇴 → 공격" vs "직접 공격" vs "스킵" 비교
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

            // Phase 2: 공격 전 버프 (첫 행동)
            if (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn)
            {
                var buffAction = PlanBuff(situation, ref hasStandardAction);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }

            // ★ v0.2.95: Phase 3 - 시퀀스 최적화 (핵심 변경)
            // "후퇴 → 공격" vs "직접 공격" vs "스킵" 능동적 비교
            if (hasStandardAction && situation.HasLivingEnemies)
            {
                var optimizedActions = PlanOptimizedAttack(situation, ref hasStandardAction, ref hasMoveAction);
                if (optimizedActions != null && optimizedActions.Count > 0)
                {
                    actions.AddRange(optimizedActions);
                }
            }

            // Phase 4: 공격 불가 시 이동 (옵티마이저가 스킵 안 했을 때만)
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

        /// <summary>
        /// ★ v0.2.95: 시퀀스 최적화 기반 공격 계획
        /// 여러 옵션을 비교하여 최적의 행동 시퀀스 선택
        /// </summary>
        private List<PlannedAction> PlanOptimizedAttack(Situation situation, ref bool hasStandardAction, ref bool hasMoveAction)
        {
            // 타겟 선택 (낮은 HP 우선)
            var target = FindLowHPEnemy(situation, 30f) ?? FindWeakestEnemy(situation) ?? situation.BestTarget ?? situation.NearestEnemy;
            if (target == null)
                return null;

            // 사용 가능한 공격 능력
            var attacks = situation.AvailableAttacks?.ToList();
            if (attacks == null || attacks.Count == 0)
                return null;

            // Role 설정
            var role = situation.CharacterSettings?.Role ?? AIRole.DPS;

            // ★ SequenceOptimizer로 최적 시퀀스 찾기
            var bestSequence = SequenceOptimizer.GetOptimalAttackSequence(
                situation, attacks, target, role, "DPS-Seq");

            // null = 스킵 결정 또는 최적화 실패
            if (bestSequence == null)
            {
                Main.Log($"[DPS] SequenceOptimizer: Skip or no valid sequence");
                return null;
            }

            // 시퀀스에서 행동 추출
            var result = new List<PlannedAction>();
            foreach (var action in bestSequence.Actions)
            {
                if (action.Type == ActionType.Move)
                {
                    if (hasMoveAction)
                    {
                        result.Add(action);
                        hasMoveAction = false;
                    }
                }
                else if (action.Type == ActionType.Attack)
                {
                    if (hasStandardAction)
                    {
                        result.Add(action);
                        hasStandardAction = false;
                    }
                }
            }

            return result;
        }
    }
}
