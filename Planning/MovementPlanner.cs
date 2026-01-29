// ★ v0.2.30: MovementPlanner - 이동 계획 오케스트레이터 (Pathfinder WotR 버전)
// RT 모드 v3.5.7에서 포팅 - 전술 시스템 통합
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.GameInterface;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Planning
{
    /// <summary>
    /// ★ v0.2.30: 이동 관련 계획 담당
    /// - 이동, 후퇴, 안전 이동 결정
    /// - MovementAPI, TeamBlackboard, InfluenceMap 통합
    /// </summary>
    public static class MovementPlanner
    {
        #region Main Entry Points

        /// <summary>
        /// 이동 계획 (공통화)
        /// 모든 Role에서 사용 - 적에게 도달 못하면 이동
        /// </summary>
        /// <param name="situation">현재 상황</param>
        /// <param name="roleName">역할 이름 (로깅용)</param>
        /// <param name="forceMove">공격 실패 시 이동 강제</param>
        /// <returns>이동 행동 또는 null</returns>
        public static MoveDecision PlanMove(Situation situation, string roleName, bool forceMove = false)
        {
            // forceMove=true면 HasHittableEnemies 체크 스킵
            // 원거리 캐릭터가 위험 거리 내에 있으면 후퇴 이동 허용
            if (!forceMove && situation.HasHittableEnemies)
            {
                // 원거리가 위험하면 이동 허용 (공격 가능해도 후퇴 필요)
                bool isRangedInDanger = situation.PrefersRanged && situation.IsInDanger;
                if (!isRangedInDanger)
                    return null;
                Main.Verbose($"[{roleName}] Ranged in danger - allowing movement despite hittable enemies");
            }

            if (!situation.HasLivingEnemies) return null;
            if (situation.NearestEnemy == null) return null;

            // Blackboard에서 전술적 타겟 결정
            var tacticalTarget = GetTacticalMoveTarget(situation);
            float tacticalTargetDistance = tacticalTarget != null
                ? Vector3.Distance(situation.Unit.Position, tacticalTarget.Position)
                : situation.NearestEnemyDistance;

            Main.Log($"[{roleName}] TacticalTarget={tacticalTarget?.CharacterName ?? "null"}, Distance={tacticalTargetDistance:F1}m");

            // 이동 계획
            return PlanMoveToEnemy(situation, tacticalTarget, roleName);
        }

        /// <summary>
        /// 후퇴 계획 (원거리 캐릭터가 적과 너무 가까울 때)
        /// </summary>
        public static MoveDecision PlanRetreat(Situation situation, string roleName)
        {
            if (situation.HasMovedThisTurn) return null;
            if (!situation.CanMove) return null;

            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // 현재 위치가 이미 안전 거리 이상이면 후퇴 불필요
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
            {
                Main.Verbose($"[{roleName}] Already safe, no retreat needed");
                return null;
            }

            // MovementAPI로 후퇴 위치 찾기
            var retreatScore = MovementAPI.FindRetreatPosition(
                unit,
                situation.Enemies,
                situation.MinSafeDistance,
                situation.InfluenceMap
            );

            if (retreatScore == null)
            {
                Main.Verbose($"[{roleName}] No reachable retreat position");
                return null;
            }

            Main.Log($"[{roleName}] Retreat to ({retreatScore.Position.x:F1},{retreatScore.Position.z:F1}) " +
                $"Score={retreatScore.TotalScore:F1}");

            return new MoveDecision
            {
                Destination = retreatScore.Position,
                Reason = $"Retreat from {nearestEnemy.CharacterName}",
                Score = retreatScore.TotalScore
            };
        }

        /// <summary>
        /// 후퇴 필요 여부 확인
        /// </summary>
        public static bool ShouldRetreat(Situation situation)
        {
            if (!situation.PrefersRanged)
                return false;

            return situation.NearestEnemyDistance < situation.MinSafeDistance;
        }

        #endregion

        #region Tactical Target

        /// <summary>
        /// 전술적 이동 타겟 결정
        /// Blackboard의 SharedTarget이 있으면 우선, 없으면 BestTarget 또는 NearestEnemy
        /// </summary>
        private static UnitEntityData GetTacticalMoveTarget(Situation situation)
        {
            // 1. Blackboard의 SharedTarget 확인
            var sharedTarget = TeamBlackboard.Instance?.SharedTarget;
            if (sharedTarget != null && sharedTarget.HPLeft > 0 && situation.Enemies.Contains(sharedTarget))
            {
                Main.Log($"[MovementPlanner] ★ Using SharedTarget: {sharedTarget.CharacterName}");
                return sharedTarget;
            }

            // 2. BestTarget 확인 (Situation에서 이미 계산됨)
            if (situation.BestTarget != null && situation.BestTarget.HPLeft > 0)
            {
                Main.Log($"[MovementPlanner] Using BestTarget: {situation.BestTarget.CharacterName}");
                return situation.BestTarget;
            }

            // 3. 폴백: NearestEnemy
            return situation.NearestEnemy;
        }

        #endregion

        #region Movement Planning

        /// <summary>
        /// 적에게 이동 계획
        /// </summary>
        private static MoveDecision PlanMoveToEnemy(Situation situation, UnitEntityData tacticalTarget, string roleName)
        {
            // 이번 턴 이미 이동했으면 스킵 (추가 이동 허용 조건 제외)
            if (situation.HasMovedThisTurn && !situation.AllowChaseMove && !situation.AllowPostAttackMove)
            {
                Main.Verbose($"[{roleName}] Already moved this turn");
                return null;
            }

            if (!situation.CanMove) return null;
            if (situation.NearestEnemy == null) return null;

            var unit = situation.Unit;
            var target = tacticalTarget ?? situation.NearestEnemy;

            if (situation.PrefersRanged)
            {
                // 원거리: 안전한 공격 위치 찾기
                return PlanRangedPosition(situation, target, roleName);
            }
            else
            {
                // 근접: 적에게 접근 위치 찾기
                return PlanMeleePosition(situation, target, roleName);
            }
        }

        /// <summary>
        /// 원거리 공격 위치 계획
        /// </summary>
        private static MoveDecision PlanRangedPosition(Situation situation, UnitEntityData target, string roleName)
        {
            var unit = situation.Unit;
            float weaponRange = situation.WeaponRange;

            // MovementAPI로 최적 원거리 위치 찾기
            var bestPosition = MovementAPI.FindRangedAttackPosition(
                unit,
                situation.Enemies,
                weaponRange,
                situation.InfluenceMap
            );

            if (bestPosition == null)
            {
                // 폴백: 적에게 접근
                var approachPosition = MovementAPI.FindApproachPosition(
                    unit, target, situation.Enemies, situation.InfluenceMap);

                if (approachPosition != null)
                {
                    Main.Log($"[{roleName}] No attack position, approach to ({approachPosition.Position.x:F1},{approachPosition.Position.z:F1})");
                    return new MoveDecision
                    {
                        Destination = approachPosition.Position,
                        Reason = $"Approach {target.CharacterName}",
                        Score = approachPosition.TotalScore
                    };
                }

                Main.Verbose($"[{roleName}] No safe ranged position found");
                return null;
            }

            // 현재 위치와 거의 같으면 이동 불필요
            float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
            if (moveDistance < 1f)
            {
                Main.Verbose($"[{roleName}] Already at optimal position");
                return null;
            }

            Main.Log($"[{roleName}] Ranged position: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                $"Score={bestPosition.TotalScore:F1}, Hittable={bestPosition.HittableEnemyCount}");

            return new MoveDecision
            {
                Destination = bestPosition.Position,
                Reason = "Safe attack position",
                Score = bestPosition.TotalScore
            };
        }

        /// <summary>
        /// 근접 공격 위치 계획
        /// </summary>
        private static MoveDecision PlanMeleePosition(Situation situation, UnitEntityData target, string roleName)
        {
            var unit = situation.Unit;

            // MovementAPI로 근접 공격 위치 찾기
            var bestPosition = MovementAPI.FindMeleeAttackPosition(
                unit,
                target,
                situation.Enemies,
                situation.InfluenceMap
            );

            if (bestPosition == null)
            {
                // 폴백: 적 위치 직접 사용
                Main.Verbose($"[{roleName}] No melee position found, falling back to target position");

                // 적 방향으로 이동
                Vector3 direction = (target.Position - unit.Position).normalized;
                Vector3 destination = unit.Position + direction * 6f;

                // BattlefieldGrid 검증
                if (BattlefieldGrid.Instance.ValidateTargetPosition(unit, destination))
                {
                    return new MoveDecision
                    {
                        Destination = destination,
                        Reason = $"Approach {target.CharacterName}",
                        Score = 10f
                    };
                }

                return null;
            }

            // 현재 위치와 거의 같으면 이동 불필요
            float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
            if (moveDistance < 1f)
            {
                Main.Verbose($"[{roleName}] Already at melee position");
                return null;
            }

            Main.Log($"[{roleName}] Melee position: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                $"Score={bestPosition.TotalScore:F1}");

            return new MoveDecision
            {
                Destination = bestPosition.Position,
                Reason = $"Melee position near {target.CharacterName}",
                Score = bestPosition.TotalScore
            };
        }

        #endregion

        #region Post-Action Movement

        /// <summary>
        /// 행동 완료 후 안전 후퇴 (원거리 캐릭터)
        /// </summary>
        public static MoveDecision PlanPostActionSafeRetreat(Situation situation, string roleName)
        {
            if (!situation.CanMove) return null;
            if (!situation.PrefersRanged) return null;

            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // 현재 위치가 이미 안전 거리 이상이면 이동 불필요
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
            {
                Main.Verbose($"[{roleName}] Already safe after action, no retreat needed");
                return null;
            }

            // MovementAPI로 후퇴 위치 찾기
            var retreatScore = MovementAPI.FindRetreatPosition(
                unit,
                situation.Enemies,
                situation.MinSafeDistance,
                situation.InfluenceMap
            );

            if (retreatScore == null)
            {
                Main.Verbose($"[{roleName}] No reachable safe retreat position");
                return null;
            }

            // 최적 위치가 현재 위치보다 충분히 좋은지 확인
            float currentDistToEnemy = situation.NearestEnemyDistance;
            float newDistToEnemy = Vector3.Distance(retreatScore.Position, nearestEnemy.Position);

            // 이동 후 거리가 현재보다 최소 2m 이상 멀어지지 않으면 이동 가치 없음
            if (newDistToEnemy < currentDistToEnemy + 2f)
            {
                Main.Verbose($"[{roleName}] Retreat not worth it (current={currentDistToEnemy:F1}m, after={newDistToEnemy:F1}m)");
                return null;
            }

            return new MoveDecision
            {
                Destination = retreatScore.Position,
                Reason = $"Safe retreat from {nearestEnemy.CharacterName}",
                Score = retreatScore.TotalScore
            };
        }

        #endregion
    }

    /// <summary>
    /// 이동 결정 결과
    /// </summary>
    public class MoveDecision
    {
        public Vector3 Destination { get; set; }
        public string Reason { get; set; }
        public float Score { get; set; }

        public override string ToString()
        {
            return $"Move to ({Destination.x:F1},{Destination.z:F1}) - {Reason} (Score={Score:F1})";
        }
    }
}
