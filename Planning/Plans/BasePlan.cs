using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.GameInterface;

namespace CompanionAI_Pathfinder.Planning.Plans
{
    /// <summary>
    /// 모든 Role Plan의 기본 클래스
    /// Pathfinder용 단순화 버전
    /// </summary>
    public abstract class BasePlan
    {
        #region Constants

        protected const float HP_EMERGENCY_THRESHOLD = 30f;
        protected const float HP_HEAL_THRESHOLD = 60f;
        protected const int MAX_ATTACKS_PER_PLAN = 3;

        #endregion

        #region Abstract Methods

        /// <summary>
        /// Role별 턴 계획 생성
        /// </summary>
        public abstract TurnPlan CreatePlan(Situation situation, TurnState turnState);

        /// <summary>
        /// Role 이름 (로깅용)
        /// </summary>
        protected abstract string RoleName { get; }

        #endregion

        #region Emergency Heal

        /// <summary>
        /// 긴급 자기 힐 (HP < 30%)
        /// </summary>
        protected PlannedAction PlanEmergencyHeal(Situation situation, ref bool hasStandardAction)
        {
            if (situation.HPPercent > HP_EMERGENCY_THRESHOLD)
                return null;

            if (!hasStandardAction)
                return null;

            if (situation.AvailableHeals == null || situation.AvailableHeals.Count == 0)
                return null;

            var self = situation.Unit;
            var selfTarget = new TargetWrapper(self);

            foreach (var heal in situation.AvailableHeals)
            {
                if (heal?.Blueprint == null) continue;

                // 자기 힐 가능 여부
                if (!heal.Blueprint.CanTargetSelf) continue;

                if (heal.CanTarget(selfTarget))
                {
                    hasStandardAction = false;
                    Main.Log($"[{RoleName}] Emergency heal: {heal.Name}");
                    return PlannedAction.Heal(heal, self, "Emergency self heal (HP critical)", 1f);
                }
            }

            return null;
        }

        #endregion

        #region Buff Planning

        /// <summary>
        /// 선제 버프 계획
        /// </summary>
        protected PlannedAction PlanBuff(Situation situation, ref bool hasStandardAction)
        {
            if (!hasStandardAction)
                return null;

            if (situation.AvailableBuffs == null || situation.AvailableBuffs.Count == 0)
                return null;

            var self = situation.Unit;
            var selfTarget = new TargetWrapper(self);

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff?.Blueprint == null) continue;

                // 자기 버프 가능 여부
                if (!buff.Blueprint.CanTargetSelf) continue;

                if (buff.CanTarget(selfTarget))
                {
                    hasStandardAction = false;
                    Main.Log($"[{RoleName}] Buff: {buff.Name}");
                    return PlannedAction.Buff(buff, self, "Pre-combat buff", 1f);
                }
            }

            return null;
        }

        #endregion

        #region Attack Planning

        /// <summary>
        /// 공격 계획
        /// </summary>
        protected PlannedAction PlanAttack(Situation situation, ref bool hasStandardAction, UnitEntityData preferTarget = null)
        {
            if (!hasStandardAction)
                return null;

            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
                return null;

            // 타겟 선택
            var target = preferTarget ?? situation.BestTarget ?? situation.NearestEnemy;
            if (target == null)
                return null;

            var targetWrapper = new TargetWrapper(target);

            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack?.Blueprint == null) continue;

                if (attack.CanTarget(targetWrapper))
                {
                    hasStandardAction = false;
                    Main.Log($"[{RoleName}] Attack: {attack.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(attack, target, $"Attack {target.CharacterName}", 1f);
                }
            }

            return null;
        }

        /// <summary>
        /// 약한 적 찾기 (HP 기준)
        /// </summary>
        protected UnitEntityData FindWeakestEnemy(Situation situation)
        {
            if (situation.HittableEnemies == null || situation.HittableEnemies.Count == 0)
                return null;

            return situation.HittableEnemies
                .Where(e => e != null)
                .Where(e => e.Descriptor?.State?.IsDead != true)
                .OrderBy(e => GetHPPercent(e))
                .FirstOrDefault();
        }

        /// <summary>
        /// 낮은 HP 적 찾기 (threshold 미만)
        /// </summary>
        protected UnitEntityData FindLowHPEnemy(Situation situation, float threshold)
        {
            if (situation.HittableEnemies == null || situation.HittableEnemies.Count == 0)
                return null;

            return situation.HittableEnemies
                .Where(e => e != null)
                .Where(e => e.Descriptor?.State?.IsDead != true)
                .Where(e => GetHPPercent(e) < threshold)
                .OrderBy(e => GetHPPercent(e))
                .FirstOrDefault();
        }

        #endregion

        #region Movement Planning

        /// <summary>
        /// 적에게 이동 계획
        /// ★ v0.2.94: MovementPlanner 통합 - InfluenceMap/BattlefieldGrid 사용
        /// Range 캐릭터는 안전한 사격 위치로, 근접은 근접 공격 위치로
        /// </summary>
        protected PlannedAction PlanMoveToEnemy(Situation situation, ref bool hasMoveAction)
        {
            if (!hasMoveAction)
                return null;

            if (!situation.CanMove)
                return null;

            var target = situation.NearestEnemy;
            if (target == null)
                return null;

            // ★ v0.2.94: MovementPlanner 사용 - Role/상황에 따른 전술적 이동
            var moveDecision = MovementPlanner.PlanMove(situation, RoleName, forceMove: true);

            if (moveDecision != null)
            {
                hasMoveAction = false;
                Main.Log($"[{RoleName}] Tactical move: {moveDecision.Reason}");
                return PlannedAction.Move(moveDecision.Destination, moveDecision.Reason);
            }

            // MovementPlanner가 실패한 경우에만 폴백 (단순 이동)
            // ★ v0.2.94: Range 캐릭터는 적에게 접근하지 않음!
            if (situation.PrefersRanged)
            {
                Main.Log($"[{RoleName}] Range character - no safe position, skipping move");
                return null;  // Range는 안전한 위치가 없으면 이동 안함
            }

            // 근접만 단순 접근 (최후의 수단)
            Vector3 direction = (target.Position - situation.Unit.Position).normalized;
            float moveDistance = 6f;
            Vector3 destination = situation.Unit.Position + direction * moveDistance;

            // BattlefieldGrid 검증
            if (!BattlefieldGrid.Instance.ValidateTargetPosition(situation.Unit, destination))
            {
                Main.Log($"[{RoleName}] Fallback move position invalid");
                return null;
            }

            hasMoveAction = false;
            Main.Log($"[{RoleName}] Fallback move toward: {target.CharacterName}");
            return PlannedAction.Move(destination, $"Move toward {target.CharacterName}");
        }

        /// <summary>
        /// 후퇴 계획 (원거리용)
        /// ★ v0.2.94: MovementPlanner.PlanRetreat 사용
        /// </summary>
        protected PlannedAction PlanRetreat(Situation situation, ref bool hasMoveAction)
        {
            if (!hasMoveAction)
                return null;

            if (!situation.CanMove)
                return null;

            if (!situation.IsInDanger)
                return null;

            // ★ v0.2.94: MovementPlanner 사용 - 전술적 후퇴 위치 계산
            var retreatDecision = MovementPlanner.PlanRetreat(situation, RoleName);

            if (retreatDecision != null)
            {
                hasMoveAction = false;
                Main.Log($"[{RoleName}] Tactical retreat: {retreatDecision.Reason}");
                return PlannedAction.Move(retreatDecision.Destination, retreatDecision.Reason);
            }

            // 폴백: 단순 후퇴 (적 반대 방향)
            var threat = situation.NearestEnemy;
            if (threat == null)
                return null;

            Vector3 direction = (situation.Unit.Position - threat.Position).normalized;
            float moveDistance = 6f;
            Vector3 destination = situation.Unit.Position + direction * moveDistance;

            // BattlefieldGrid 검증
            if (!BattlefieldGrid.Instance.ValidateTargetPosition(situation.Unit, destination))
            {
                Main.Log($"[{RoleName}] Fallback retreat position invalid");
                return null;
            }

            hasMoveAction = false;
            Main.Log($"[{RoleName}] Fallback retreat from: {threat.CharacterName}");
            return PlannedAction.Move(destination, $"Retreat from {threat.CharacterName}");
        }

        /// <summary>
        /// 후퇴 필요 여부 (원거리 캐릭터용)
        /// ★ v0.2.94: MovementPlanner.ShouldRetreat 사용 (더 정교한 판단)
        /// </summary>
        protected bool ShouldRetreat(Situation situation)
        {
            return MovementPlanner.ShouldRetreat(situation);
        }

        #endregion

        #region Ally Heal/Buff

        /// <summary>
        /// 아군 힐 계획
        /// </summary>
        protected PlannedAction PlanAllyHeal(Situation situation, ref bool hasStandardAction, float threshold = HP_HEAL_THRESHOLD)
        {
            if (!hasStandardAction)
                return null;

            if (situation.AvailableHeals == null || situation.AvailableHeals.Count == 0)
                return null;

            // 부상 아군 찾기
            var woundedAlly = FindWoundedAlly(situation, threshold);
            if (woundedAlly == null)
                return null;

            var targetWrapper = new TargetWrapper(woundedAlly);

            foreach (var heal in situation.AvailableHeals)
            {
                if (heal?.Blueprint == null) continue;

                if (heal.CanTarget(targetWrapper))
                {
                    hasStandardAction = false;
                    Main.Log($"[{RoleName}] Heal ally: {heal.Name} -> {woundedAlly.CharacterName}");
                    return PlannedAction.Heal(heal, woundedAlly, $"Heal {woundedAlly.CharacterName}", 1f);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v0.2.36: 향상된 부상 아군 찾기
        /// HP%, 역할, 위협 상황을 종합적으로 고려
        /// </summary>
        protected UnitEntityData FindWoundedAlly(Situation situation, float threshold)
        {
            if (situation.Allies == null || situation.Allies.Count == 0)
                return null;

            // TargetScorer의 향상된 힐링 대상 선택 사용
            return TargetScorer.SelectBestAllyForHealing(situation.Allies, situation, threshold);
        }

        /// <summary>
        /// 아군 버프 계획
        /// </summary>
        protected PlannedAction PlanAllyBuff(Situation situation, ref bool hasStandardAction)
        {
            if (!hasStandardAction)
                return null;

            if (situation.AvailableBuffs == null || situation.AvailableBuffs.Count == 0)
                return null;

            // 아군 목록 (자신 제외)
            var allies = situation.Allies?.Where(a => a != null && a != situation.Unit).ToList();
            if (allies == null || allies.Count == 0)
                return null;

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff?.Blueprint == null) continue;
                if (!buff.Blueprint.CanTargetFriends) continue;

                foreach (var ally in allies)
                {
                    var targetWrapper = new TargetWrapper(ally);
                    if (buff.CanTarget(targetWrapper))
                    {
                        hasStandardAction = false;
                        Main.Log($"[{RoleName}] Buff ally: {buff.Name} -> {ally.CharacterName}");
                        return PlannedAction.Support(buff, ally, $"Buff {ally.CharacterName}", 1f);
                    }
                }
            }

            return null;
        }

        #endregion

        #region Debuff Planning

        /// <summary>
        /// 디버프 계획
        /// </summary>
        protected PlannedAction PlanDebuff(Situation situation, ref bool hasStandardAction, UnitEntityData target = null)
        {
            if (!hasStandardAction)
                return null;

            if (situation.AvailableDebuffs == null || situation.AvailableDebuffs.Count == 0)
                return null;

            // v0.2.18: 세이브 인식 디버프 계획 - (디버프, 타겟) 최적 쌍 선택
            AbilityData bestDebuff = null;
            UnitEntityData bestTarget = null;
            float bestScore = float.MinValue;

            foreach (var debuff in situation.AvailableDebuffs)
            {
                if (debuff?.Blueprint == null) continue;

                UnitEntityData debuffTarget = target;
                float score = 0f;

                // 세이브 타입이 있으면 최적 타겟 찾기
                if (situation.DebuffSaveTypes.TryGetValue(debuff, out var saveType))
                {
                    var (saveTarget, saveScore) = TargetScorer.SelectBestDebuffTarget(
                        situation.Enemies, saveType, situation);
                    if (saveTarget != null)
                    {
                        debuffTarget = saveTarget;
                        score = saveScore;
                    }
                }

                debuffTarget = debuffTarget ?? situation.BestTarget ?? situation.NearestEnemy;
                if (debuffTarget == null) continue;

                var wrapper = new TargetWrapper(debuffTarget);
                if (!debuff.CanTarget(wrapper)) continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDebuff = debuff;
                    bestTarget = debuffTarget;
                }
            }

            // 세이브 매칭 실패 시 폴백
            if (bestDebuff == null)
            {
                target = target ?? situation.BestTarget ?? situation.NearestEnemy;
                if (target == null) return null;
                var targetWrapper = new TargetWrapper(target);

                foreach (var debuff in situation.AvailableDebuffs)
                {
                    if (debuff?.Blueprint == null) continue;
                    if (debuff.CanTarget(targetWrapper))
                    {
                        bestDebuff = debuff;
                        bestTarget = target;
                        break;
                    }
                }
            }

            if (bestDebuff == null || bestTarget == null)
                return null;

            hasStandardAction = false;
            Main.Log($"[{RoleName}] Debuff: {bestDebuff.Name} -> {bestTarget.CharacterName}");
            return PlannedAction.Debuff(bestDebuff, bestTarget, $"Debuff {bestTarget.CharacterName}", 1f);
        }

        #endregion

        #region Priority/Reasoning

        /// <summary>
        /// 계획 우선순위 결정
        /// </summary>
        protected TurnPriority DeterminePriority(List<PlannedAction> actions, Situation situation)
        {
            if (actions == null || actions.Count == 0)
                return TurnPriority.EndTurn;

            // 긴급 힐이 포함되면 Emergency
            if (actions.Any(a => a.Type == ActionType.Heal && situation.HPPercent < HP_EMERGENCY_THRESHOLD))
                return TurnPriority.Emergency;

            // 공격이 포함되면 DirectAttack
            if (actions.Any(a => a.Type == ActionType.Attack))
                return TurnPriority.DirectAttack;

            // 버프 포함되면 BuffedAttack
            if (actions.Any(a => a.Type == ActionType.Buff))
                return TurnPriority.BuffedAttack;

            // 이동 포함되면 MoveAndAttack
            if (actions.Any(a => a.Type == ActionType.Move))
                return TurnPriority.MoveAndAttack;

            return TurnPriority.Support;
        }

        /// <summary>
        /// 계획 이유 설명 생성
        /// </summary>
        protected string DetermineReasoning(List<PlannedAction> actions, Situation situation)
        {
            if (actions == null || actions.Count == 0)
                return "No actions planned";

            var actionTypes = actions.Select(a => a.Type).Distinct().ToList();
            var parts = new List<string>();

            if (actionTypes.Contains(ActionType.Heal)) parts.Add("Heal");
            if (actionTypes.Contains(ActionType.Buff)) parts.Add("Buff");
            if (actionTypes.Contains(ActionType.Attack)) parts.Add("Attack");
            if (actionTypes.Contains(ActionType.Move)) parts.Add("Move");
            if (actionTypes.Contains(ActionType.Debuff)) parts.Add("Debuff");

            return string.Join(" + ", parts);
        }

        #endregion

        #region Helper Methods

        protected float GetHPPercent(UnitEntityData unit)
        {
            try
            {
                if (unit?.Stats?.HitPoints == null) return 100f;
                float current = unit.Stats.HitPoints.ModifiedValue;
                float max = unit.Stats.HitPoints.BaseValue;
                if (max <= 0) return 100f;
                return (current / max) * 100f;
            }
            catch { return 100f; }
        }

        #endregion
    }
}
