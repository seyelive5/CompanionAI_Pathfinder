using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Core.TurnBased
{
    /// <summary>
    /// ★ v0.2.114: 턴 계획 유효성 검증기
    /// 상황 변화 감지 및 리플랜 필요 여부 판단
    /// </summary>
    public static class TurnPlanValidator
    {
        #region Constants

        /// <summary>HP 변화 임계값 (퍼센트)</summary>
        private const float HP_CHANGE_THRESHOLD = 20f;

        /// <summary>위치 변화 임계값 (미터)</summary>
        private const float POSITION_CHANGE_THRESHOLD = 5f;

        /// <summary>적 수 변화 임계값</summary>
        private const int ENEMY_COUNT_CHANGE_THRESHOLD = 2;

        #endregion

        #region Main Validation

        /// <summary>
        /// 계획이 여전히 유효한지 검증
        /// </summary>
        /// <param name="plan">현재 계획</param>
        /// <param name="unit">유닛</param>
        /// <param name="enemies">현재 적 목록</param>
        /// <param name="invalidReason">무효화 이유 (out)</param>
        /// <returns>계획 유효 여부</returns>
        public static bool ValidatePlan(
            TurnPlan plan,
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            out string invalidReason)
        {
            invalidReason = null;

            if (plan == null || !plan.IsValid)
            {
                invalidReason = "Plan is null or already invalid";
                return false;
            }

            if (plan.IsComplete)
            {
                // 완료된 계획은 유효 (처리할 게 없음)
                return true;
            }

            var snapshot = plan.Snapshot;
            if (snapshot == null)
            {
                invalidReason = "No snapshot";
                return false;
            }

            // 1. 내 HP 급격한 변화
            float currentHP = unit.HPLeft / (float)unit.MaxHP * 100f;
            float hpChange = Mathf.Abs(currentHP - snapshot.MyHPPercent);
            if (hpChange >= HP_CHANGE_THRESHOLD)
            {
                invalidReason = $"HP changed significantly: {snapshot.MyHPPercent:F0}% → {currentHP:F0}%";
                return false;
            }

            // 2. 타겟이 죽었는지 확인
            var currentStep = plan.CurrentStep;
            if (currentStep != null && currentStep.TargetUnit != null)
            {
                if (currentStep.TargetUnit.HPLeft <= 0)
                {
                    invalidReason = $"Target {currentStep.TargetUnit.CharacterName} is dead";
                    return false;
                }
            }

            // 3. 적 수 급격한 변화
            int currentEnemyCount = enemies?.Count ?? 0;
            int enemyDiff = Mathf.Abs(currentEnemyCount - snapshot.EnemyCount);
            if (enemyDiff >= ENEMY_COUNT_CHANGE_THRESHOLD)
            {
                invalidReason = $"Enemy count changed: {snapshot.EnemyCount} → {currentEnemyCount}";
                return false;
            }

            // 4. 위치가 크게 변했는지 (강제 이동 등)
            float posDiff = Vector3.Distance(unit.Position, snapshot.MyPosition);
            if (posDiff >= POSITION_CHANGE_THRESHOLD)
            {
                invalidReason = $"Position changed by {posDiff:F1}m";
                return false;
            }

            // 5. 현재 스텝의 능력이 더 이상 사용 불가인지
            if (currentStep?.Ability != null)
            {
                if (!currentStep.Ability.IsAvailable)
                {
                    invalidReason = $"Ability {currentStep.Ability.Name} no longer available";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 현재 스텝이 실행 가능한지 검증
        /// </summary>
        public static bool ValidateStep(
            TurnPlanStep step,
            UnitEntityData unit,
            TurnState turnState,
            out string invalidReason)
        {
            invalidReason = null;

            if (step == null)
            {
                invalidReason = "Step is null";
                return false;
            }

            // 1. 액션 가용성 확인
            switch (step.ActionCost)
            {
                case ActionCostType.Standard:
                    if (!(turnState?.HasStandardAction ?? true))
                    {
                        invalidReason = "No standard action";
                        return false;
                    }
                    break;
                case ActionCostType.Move:
                    if (!(turnState?.HasMoveAction ?? true))
                    {
                        invalidReason = "No move action";
                        return false;
                    }
                    break;
                case ActionCostType.Swift:
                    if (!(turnState?.HasSwiftAction ?? true))
                    {
                        invalidReason = "No swift action";
                        return false;
                    }
                    break;
            }

            // 2. 타겟 유효성
            if (step.TargetUnit != null && step.TargetUnit.HPLeft <= 0)
            {
                invalidReason = "Target is dead";
                return false;
            }

            // 3. 능력 사용 가능 여부
            if (step.Ability != null && !step.Ability.IsAvailable)
            {
                invalidReason = "Ability not available";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 리플랜이 필요한지 판단
        /// </summary>
        public static bool ShouldReplan(TurnPlan plan, UnitEntityData unit, List<UnitEntityData> enemies)
        {
            if (plan == null || !plan.IsValid)
                return true;

            if (plan.ReplanCount >= TurnPlan.MAX_REPLAN_COUNT)
            {
                Main.Log($"[Validator] Max replan count reached ({plan.ReplanCount})");
                return false; // 더 이상 리플랜 안함
            }

            if (!ValidatePlan(plan, unit, enemies, out string reason))
            {
                Main.Log($"[Validator] Replan needed: {reason}");
                return true;
            }

            return false;
        }

        #endregion

        #region Step Transition

        /// <summary>
        /// 스텝 완료 후 다음 스텝으로 전환 가능한지
        /// </summary>
        public static bool CanAdvanceToNextStep(TurnPlan plan, TurnState turnState)
        {
            if (plan == null || plan.IsComplete)
                return false;

            var nextIndex = plan.CurrentStepIndex + 1;
            if (nextIndex >= plan.Steps.Count)
                return false;

            var nextStep = plan.Steps[nextIndex];
            return ValidateStep(nextStep, null, turnState, out _);
        }

        /// <summary>
        /// 스텝 실패 처리
        /// </summary>
        public static void HandleStepFailure(TurnPlanStep step, string reason)
        {
            step.AttemptCount++;
            Main.Log($"[Validator] Step failed (attempt {step.AttemptCount}): {reason}");

            if (step.AttemptCount >= TurnPlanStep.MAX_ATTEMPTS)
            {
                step.Status = StepStatus.Failed;
                Main.Log($"[Validator] Step marked as failed after {step.AttemptCount} attempts");
            }
        }

        #endregion
    }
}
