// ★ v0.2.30: MovementAPI - 이동 위치 평가 및 최적 위치 찾기 (Pathfinder WotR 버전)
// ★ v0.2.65: LOS 체크 추가 - 좁은 지형에서 왔다갔다 현상 수정
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// 이동 API - 위치 평가 및 최적 위치 찾기
    /// Pathfinder WotR 호환 간소화 버전
    /// </summary>
    public static class MovementAPI
    {
        #region Constants

        private const float SAMPLE_RADIUS = 12f;      // 위치 샘플링 최대 반경 (★ v0.2.31: 15→12 축소)
        private const int SAMPLES_PER_RING = 6;       // 각 링당 샘플 수 (★ v0.2.31: 8→6 축소)
        private const int NUM_RINGS = 3;              // 링 수 (★ v0.2.31: 5→3 축소)
        private const float MIN_MELEE_RANGE = 1.5f;   // 근접 최소 거리
        private const float MAX_MELEE_RANGE = 3f;     // 근접 최대 거리
        private const float RANGED_MIN_SAFE = 6f;     // 원거리 최소 안전 거리
        private const float RANGED_OPTIMAL = 10f;     // 원거리 최적 거리

        #endregion

        #region Position Score

        /// <summary>
        /// 위치 점수 - 다양한 요소 기반 평가
        /// </summary>
        public class PositionScore
        {
            public Vector3 Position { get; set; }

            public float DistanceScore { get; set; }      // 목표 거리 기반
            public float ThreatScore { get; set; }        // InfluenceMap 위협
            public float ControlBonus { get; set; }       // 아군 통제 보너스
            public float SafetyScore { get; set; }        // 안전 지역 보너스
            public float LosScore { get; set; }           // 시야선 점수

            public float TotalScore => DistanceScore - ThreatScore + ControlBonus + SafetyScore + LosScore;

            public bool IsWalkable { get; set; }
            public bool HasLosToEnemy { get; set; }
            public int HittableEnemyCount { get; set; }

            public override string ToString() =>
                $"Pos({Position.x:F1},{Position.z:F1}) Score={TotalScore:F1} " +
                $"[Dist:{DistanceScore:F1} Thr:{ThreatScore:F1} Ctrl:{ControlBonus:F1}]";
        }

        public enum MovementGoal
        {
            FindCover,
            MaintainDistance,
            ApproachEnemy,
            AttackPosition,
            Retreat,
            RangedAttackPosition,
            MeleeAttackPosition
        }

        #endregion

        #region Position Generation

        /// <summary>
        /// 유닛 주변의 후보 위치 생성
        /// </summary>
        private static List<Vector3> GenerateCandidatePositions(Vector3 center, float maxRadius)
        {
            var positions = new List<Vector3>();

            // 현재 위치도 후보에 포함
            positions.Add(center);

            // 동심원 형태로 위치 샘플링
            for (int ring = 1; ring <= NUM_RINGS; ring++)
            {
                float radius = (maxRadius / NUM_RINGS) * ring;
                int samples = SAMPLES_PER_RING;

                for (int i = 0; i < samples; i++)
                {
                    float angle = (360f / samples) * i;
                    float rad = angle * Mathf.Deg2Rad;

                    Vector3 offset = new Vector3(
                        Mathf.Cos(rad) * radius,
                        0,
                        Mathf.Sin(rad) * radius
                    );

                    positions.Add(center + offset);
                }
            }

            return positions;
        }

        /// <summary>
        /// 적 주변의 공격 위치 생성 (근접용)
        /// </summary>
        private static List<Vector3> GenerateMeleePositions(UnitEntityData enemy)
        {
            var positions = new List<Vector3>();

            // 적 주변 8방향
            for (int i = 0; i < 8; i++)
            {
                float angle = (360f / 8) * i;
                float rad = angle * Mathf.Deg2Rad;

                // 근접 거리에 위치
                float dist = MIN_MELEE_RANGE + 0.5f;
                Vector3 offset = new Vector3(
                    Mathf.Cos(rad) * dist,
                    0,
                    Mathf.Sin(rad) * dist
                );

                positions.Add(enemy.Position + offset);
            }

            return positions;
        }

        #endregion

        #region Position Evaluation

        /// <summary>
        /// 단일 위치 평가
        /// </summary>
        public static PositionScore EvaluatePosition(
            UnitEntityData unit,
            Vector3 position,
            List<UnitEntityData> enemies,
            MovementGoal goal,
            float targetDistance,
            BattlefieldInfluenceMap influenceMap)
        {
            var score = new PositionScore
            {
                Position = position
            };

            // 1. Walkability 체크
            score.IsWalkable = BattlefieldGrid.Instance.CanUnitStandOn(unit, position);
            if (!score.IsWalkable)
            {
                score.ThreatScore = float.MaxValue;
                return score;
            }

            // 2. 가장 가까운 적 거리 계산 + ★ v0.2.65: LOS 체크 추가
            float nearestEnemyDist = float.MaxValue;
            UnitEntityData nearestEnemy = null;
            int hittableCount = 0;
            int losBlockedCount = 0;  // LOS 차단된 적 수

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.HPLeft <= 0) continue;

                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < nearestEnemyDist)
                {
                    nearestEnemyDist = dist;
                    nearestEnemy = enemy;
                }

                // ★ v0.2.65: 공격 범위 내 적 + LOS 체크
                if (dist <= targetDistance)
                {
                    // 원거리 공격 위치 평가 시 LOS 체크 (3m 초과 거리만)
                    bool hasLos = true;
                    if (goal == MovementGoal.RangedAttackPosition && dist > 3f)
                    {
                        hasLos = LineOfSightChecker.HasLineOfSightFromPosition(unit, position, enemy);
                    }

                    if (hasLos)
                    {
                        hittableCount++;
                    }
                    else
                    {
                        losBlockedCount++;
                    }
                }
            }

            score.HittableEnemyCount = hittableCount;
            score.HasLosToEnemy = hittableCount > 0;

            // ★ v0.2.65: LOS 차단된 적만 있으면 페널티
            if (losBlockedCount > 0 && hittableCount == 0)
            {
                score.LosScore = -20f;  // 모든 적이 LOS 차단됨
            }

            // 3. 거리 기반 점수 (목표에 따라 다름)
            score.DistanceScore = CalculateDistanceScore(goal, nearestEnemyDist, targetDistance);

            // 4. InfluenceMap 기반 위협/통제 점수
            if (influenceMap != null && influenceMap.IsValid)
            {
                float threat = influenceMap.GetThreatAt(position);
                float control = influenceMap.GetControlAt(position);

                // 위협 정규화 (0-30 범위)
                score.ThreatScore = Mathf.Min(30f, threat * 3f);

                // 아군 통제 보너스
                score.ControlBonus = Mathf.Min(15f, control * 2f);

                // 안전 지역 보너스
                if (influenceMap.IsSafeZone(position))
                {
                    score.SafetyScore = 10f;
                }

                // 전선 거리 기반 조정 (역할에 따라)
                float frontlineDist = influenceMap.GetFrontlineDistance(position);

                // 후퇴 시: 전선 뒤(음수)가 좋음
                if (goal == MovementGoal.Retreat)
                {
                    score.SafetyScore += Mathf.Max(0, -frontlineDist * 0.5f);
                }
                // 접근 시: 전선 앞(양수)이 좋음
                else if (goal == MovementGoal.ApproachEnemy || goal == MovementGoal.MeleeAttackPosition)
                {
                    score.DistanceScore += Mathf.Max(0, frontlineDist * 0.3f);
                }
            }

            // 5. 시야선 점수 (공격 가능한 적이 있으면 보너스)
            if (hittableCount > 0)
            {
                score.LosScore = 10f + hittableCount * 2f;
            }

            return score;
        }

        /// <summary>
        /// 목표에 따른 거리 점수 계산
        /// </summary>
        private static float CalculateDistanceScore(MovementGoal goal, float distance, float targetDistance)
        {
            switch (goal)
            {
                case MovementGoal.Retreat:
                case MovementGoal.FindCover:
                    // 멀수록 좋음 (최대 30점)
                    return Mathf.Min(30f, distance * 2f);

                case MovementGoal.ApproachEnemy:
                    // 가까울수록 좋음 (최대 30점)
                    return Mathf.Max(0f, 30f - distance * 2f);

                case MovementGoal.MeleeAttackPosition:
                    // 근접 범위 내가 최적
                    if (distance >= MIN_MELEE_RANGE && distance <= MAX_MELEE_RANGE)
                        return 30f;
                    else if (distance < MIN_MELEE_RANGE)
                        return 20f - (MIN_MELEE_RANGE - distance) * 10f;
                    else
                        return Mathf.Max(0f, 20f - (distance - MAX_MELEE_RANGE) * 3f);

                case MovementGoal.RangedAttackPosition:
                    // 원거리 최적 거리
                    float minSafe = RANGED_MIN_SAFE;

                    if (distance < minSafe)
                    {
                        // 너무 가까움 - 페널티
                        return -20f + distance * 3f;
                    }
                    else if (distance <= targetDistance)
                    {
                        // 최적 범위
                        return 25f;
                    }
                    else
                    {
                        // 범위 밖 - 감점
                        return Mathf.Max(0f, 15f - (distance - targetDistance) * 2f);
                    }

                case MovementGoal.MaintainDistance:
                    // 목표 거리 유지
                    float diff = Mathf.Abs(distance - targetDistance);
                    return Mathf.Max(0f, 25f - diff * 2f);

                default:
                    return 0f;
            }
        }

        #endregion

        #region Best Position Finding

        /// <summary>
        /// 원거리 공격 최적 위치 찾기
        /// ★ v0.2.65: LOS 체크 강화 - 공격 가능한 위치만 반환
        /// </summary>
        public static PositionScore FindRangedAttackPosition(
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            float weaponRange,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            var candidates = GenerateCandidatePositions(unit.Position, SAMPLE_RADIUS);
            var scores = new List<PositionScore>();

            foreach (var pos in candidates)
            {
                var score = EvaluatePosition(
                    unit, pos, enemies,
                    MovementGoal.RangedAttackPosition,
                    weaponRange,
                    influenceMap);

                if (score.IsWalkable)
                    scores.Add(score);
            }

            if (scores.Count == 0)
                return null;

            // ★ v0.2.65: LOS가 확보된 위치만 필터링
            var positionsWithLos = scores.Where(s => s.HittableEnemyCount > 0).ToList();

            if (positionsWithLos.Count > 0)
            {
                // LOS 확보된 위치 중 최적 선택
                var best = positionsWithLos.OrderByDescending(s => s.TotalScore).First();
                Main.Verbose($"[MovementAPI] FindRangedAttackPosition (LOS OK): {best}");
                return best;
            }

            // ★ v0.2.65: LOS 확보된 위치가 없으면 더 넓은 범위 탐색
            Main.Verbose($"[MovementAPI] No LOS position found in {SAMPLE_RADIUS}m, trying extended search");

            // 확장 탐색: 적 방향으로 더 이동
            var closestEnemy = enemies.OrderBy(e => Vector3.Distance(unit.Position, e.Position)).FirstOrDefault();
            if (closestEnemy != null)
            {
                // 적 방향으로 이동하되, 적당한 거리 유지
                Vector3 dirToEnemy = (closestEnemy.Position - unit.Position).normalized;
                float currentDist = Vector3.Distance(unit.Position, closestEnemy.Position);

                // 무기 사거리 - 2m 정도의 위치로 접근
                float targetDist = Mathf.Max(weaponRange * 0.7f, RANGED_MIN_SAFE);
                float moveDist = Mathf.Min(currentDist - targetDist, SAMPLE_RADIUS);

                if (moveDist > 2f)
                {
                    Vector3 approachPos = unit.Position + dirToEnemy * moveDist;
                    var approachScore = EvaluatePosition(
                        unit, approachPos, enemies,
                        MovementGoal.RangedAttackPosition,
                        weaponRange,
                        influenceMap);

                    if (approachScore.IsWalkable && approachScore.HittableEnemyCount > 0)
                    {
                        Main.Verbose($"[MovementAPI] Found approach position with LOS: {approachScore}");
                        return approachScore;
                    }
                }
            }

            // 폴백: LOS 없는 위치도 없으면 null 반환 (이동 안 함)
            // 이전 동작: LOS 없어도 거리 기반으로 이동 → 왔다갔다 현상 발생
            Main.Log($"[MovementAPI] No valid ranged position with LOS found - staying put to avoid oscillation");
            return null;
        }

        /// <summary>
        /// 근접 공격 최적 위치 찾기
        /// </summary>
        public static PositionScore FindMeleeAttackPosition(
            UnitEntityData unit,
            UnitEntityData target,
            List<UnitEntityData> enemies,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || target == null)
                return null;

            var candidates = GenerateMeleePositions(target);
            var scores = new List<PositionScore>();

            foreach (var pos in candidates)
            {
                var score = EvaluatePosition(
                    unit, pos, enemies,
                    MovementGoal.MeleeAttackPosition,
                    MAX_MELEE_RANGE,
                    influenceMap);

                if (score.IsWalkable)
                    scores.Add(score);
            }

            if (scores.Count == 0)
            {
                // 폴백: 적에게 직접 접근
                var approachPos = target.Position + (unit.Position - target.Position).normalized * MIN_MELEE_RANGE;
                return new PositionScore
                {
                    Position = approachPos,
                    DistanceScore = 10f,
                    IsWalkable = BattlefieldGrid.Instance.CanUnitStandOn(unit, approachPos)
                };
            }

            var best = scores.OrderByDescending(s => s.TotalScore).FirstOrDefault();

            if (best != null)
            {
                Main.Verbose($"[MovementAPI] FindMeleeAttackPosition: {best}");
            }

            return best;
        }

        /// <summary>
        /// 후퇴 위치 찾기
        /// </summary>
        public static PositionScore FindRetreatPosition(
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            float minSafeDistance,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            var candidates = GenerateCandidatePositions(unit.Position, SAMPLE_RADIUS);
            var scores = new List<PositionScore>();

            foreach (var pos in candidates)
            {
                var score = EvaluatePosition(
                    unit, pos, enemies,
                    MovementGoal.Retreat,
                    minSafeDistance,
                    influenceMap);

                if (score.IsWalkable)
                    scores.Add(score);
            }

            if (scores.Count == 0)
                return null;

            // 가장 가까운 적에서 가장 먼 위치 선택 (위협도 고려)
            var best = scores
                .OrderByDescending(s => s.TotalScore)
                .FirstOrDefault();

            if (best != null)
            {
                Main.Verbose($"[MovementAPI] FindRetreatPosition: {best}");
            }

            return best;
        }

        /// <summary>
        /// 적에게 접근하는 최적 위치 찾기
        /// </summary>
        public static PositionScore FindApproachPosition(
            UnitEntityData unit,
            UnitEntityData target,
            List<UnitEntityData> enemies,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || target == null)
                return null;

            // 적 방향으로 이동
            Vector3 direction = (target.Position - unit.Position).normalized;
            float currentDist = Vector3.Distance(unit.Position, target.Position);

            // 이동 가능한 최대 거리 (약 6m)
            float moveDistance = Mathf.Min(6f, currentDist - MIN_MELEE_RANGE);

            if (moveDistance <= 0)
            {
                // 이미 충분히 가까움
                return new PositionScore
                {
                    Position = unit.Position,
                    DistanceScore = 20f,
                    IsWalkable = true
                };
            }

            Vector3 newPos = unit.Position + direction * moveDistance;

            var score = EvaluatePosition(
                unit, newPos, enemies,
                MovementGoal.ApproachEnemy,
                0f,
                influenceMap);

            if (score.IsWalkable)
            {
                Main.Verbose($"[MovementAPI] FindApproachPosition: {score}");
                return score;
            }

            // 폴백: 안전 구역에서 가장 가까운 위치
            if (influenceMap != null)
            {
                var safeZone = influenceMap.GetNearestSafeZone(unit.Position);
                if (safeZone.HasValue)
                {
                    return new PositionScore
                    {
                        Position = safeZone.Value,
                        DistanceScore = 10f,
                        SafetyScore = 10f,
                        IsWalkable = true
                    };
                }
            }

            return null;
        }

        #endregion
    }
}
