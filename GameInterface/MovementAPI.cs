// ★ v0.2.30: MovementAPI - 이동 위치 평가 및 최적 위치 찾기 (Pathfinder WotR 버전)
// ★ v0.2.65: LOS 체크 추가 - 좁은 지형에서 왔다갔다 현상 수정
// ★ v0.2.98: 동적 이동 반경 계산 - 실제 유닛 이동 속도 기반
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using TurnBased.Controllers;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// 이동 API - 위치 평가 및 최적 위치 찾기
    /// Pathfinder WotR 호환 간소화 버전
    /// ★ v0.2.98: 동적 이동 반경 - 하드코딩된 상수 대신 실제 유닛 속도 사용
    /// </summary>
    public static class MovementAPI
    {
        #region Constants

        // ★ v0.2.98: SAMPLE_RADIUS는 이제 폴백용으로만 사용
        private const float DEFAULT_SAMPLE_RADIUS = 12f;  // 폴백 반경 (속도 계산 실패 시)
        private const int SAMPLES_PER_RING = 6;           // 각 링당 샘플 수
        private const int NUM_RINGS = 3;                  // 링 수
        private const float MIN_MELEE_RANGE = 1.5f;       // 근접 최소 거리
        private const float MAX_MELEE_RANGE = 3f;         // 근접 최대 거리
        private const float RANGED_MIN_SAFE = 6f;         // 원거리 최소 안전 거리
        private const float RANGED_OPTIMAL = 10f;         // 원거리 최적 거리

        #endregion

        #region ★ v0.2.98: Dynamic Movement Calculation

        /// <summary>
        /// ★ v0.2.98: 유닛의 실제 이동 가능 거리 계산
        /// 턴제: 1 Move Action = 속도 * 3초
        /// 실시간: 속도 * 2초 (약간의 여유)
        /// </summary>
        public static float GetMaxMoveDistance(UnitEntityData unit)
        {
            if (unit == null)
                return DEFAULT_SAMPLE_RADIUS;

            try
            {
                // 기본 이동 속도 (미터/초)
                float speedMps = unit.CurrentSpeedMps;
                if (speedMps <= 0)
                {
                    // 폴백: ModifiedValue 사용
                    speedMps = unit.Stats?.Speed?.ModifiedValue ?? 30f;
                    speedMps = speedMps / 5f;  // feet/round → m/s 대략 변환
                }

                bool isTurnBased = CombatController.IsInTurnBasedCombat();

                float maxDistance;
                if (isTurnBased)
                {
                    // ★ 턴제: 1 Move Action = 속도 * 3초
                    // Pathfinder에서 1 라운드 = 6초, Move Action = 라운드의 절반
                    maxDistance = speedMps * 3f;
                }
                else
                {
                    // ★ 실시간: 쿨다운 기반으로 약 2초 분량
                    maxDistance = speedMps * 2f;
                }

                // 최소/최대 제한
                maxDistance = Mathf.Clamp(maxDistance, 3f, 20f);

                Main.Verbose($"[MovementAPI] GetMaxMoveDistance: {unit.CharacterName} speed={speedMps:F1}m/s, " +
                           $"mode={(isTurnBased ? "TB" : "RT")}, maxDist={maxDistance:F1}m");

                return maxDistance;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[MovementAPI] GetMaxMoveDistance error: {ex.Message}");
                return DEFAULT_SAMPLE_RADIUS;
            }
        }

        /// <summary>
        /// ★ v0.2.98: 유닛의 근접 공격 도달 범위 계산
        /// Corpulence(몸 크기) + 무기 Reach 포함
        /// </summary>
        public static float GetMeleeReachDistance(UnitEntityData unit)
        {
            if (unit == null)
                return MAX_MELEE_RANGE;

            try
            {
                // 무기의 AttackRange 사용 (게임 API)
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null)
                {
                    var attackRange = weapon.AttackRange;
                    if (attackRange.Meters > 0)
                    {
                        // Corpulence(몸 크기) 보너스 추가
                        float corpulence = unit.Corpulence;
                        return attackRange.Meters + corpulence + 0.5f;  // 여유
                    }
                }

                // 폴백: Descriptor에서 Reach 계산
                if (unit.Descriptor != null)
                {
                    var reachRange = unit.Descriptor.GetWeaponRange(null);
                    if (reachRange.Meters > 0)
                    {
                        return reachRange.Meters + unit.Corpulence + 0.5f;
                    }
                }

                return MAX_MELEE_RANGE;
            }
            catch
            {
                return MAX_MELEE_RANGE;
            }
        }

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
        /// ★ v0.2.98: maxRadius 파라미터로 동적 반경 지원
        /// </summary>
        private static List<Vector3> GenerateCandidatePositions(Vector3 center, float maxRadius)
        {
            var positions = new List<Vector3>();

            // 현재 위치도 후보에 포함
            positions.Add(center);

            // ★ v0.2.98: maxRadius가 너무 작으면 링 수 조절
            int rings = NUM_RINGS;
            if (maxRadius < 6f)
                rings = 2;
            if (maxRadius < 3f)
                rings = 1;

            // 동심원 형태로 위치 샘플링
            for (int ring = 1; ring <= rings; ring++)
            {
                float radius = (maxRadius / rings) * ring;
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

            Main.Verbose($"[MovementAPI] GenerateCandidatePositions: radius={maxRadius:F1}m, rings={rings}, total={positions.Count}");
            return positions;
        }

        /// <summary>
        /// 적 주변의 공격 위치 생성 (근접용)
        /// ★ v0.2.98: meleeReach 파라미터로 동적 거리 지원
        /// </summary>
        private static List<Vector3> GenerateMeleePositions(UnitEntityData enemy, float meleeReach = 2f)
        {
            var positions = new List<Vector3>();

            // 적의 크기 고려
            float enemySize = enemy?.Corpulence ?? 0.5f;

            // 적 주변 8방향
            for (int i = 0; i < 8; i++)
            {
                float angle = (360f / 8) * i;
                float rad = angle * Mathf.Deg2Rad;

                // ★ v0.2.98: 근접 도달 범위에서 약간 안쪽 위치 (확실히 공격 가능하도록)
                float dist = Mathf.Max(1.5f, meleeReach * 0.8f + enemySize);
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
        /// ★ v0.2.98: 동적 이동 반경 사용 - 실제 이동 가능 거리 기반
        /// </summary>
        public static PositionScore FindRangedAttackPosition(
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            float weaponRange,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            // ★ v0.2.98: 실제 이동 가능 거리 계산
            float maxMoveDistance = GetMaxMoveDistance(unit);
            var candidates = GenerateCandidatePositions(unit.Position, maxMoveDistance);
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
            // ★ v0.2.98: 실제 이동 가능 거리 사용
            Main.Verbose($"[MovementAPI] No LOS position found in {maxMoveDistance:F1}m, trying extended search");

            // 확장 탐색: 적 방향으로 더 이동
            var closestEnemy = enemies.OrderBy(e => Vector3.Distance(unit.Position, e.Position)).FirstOrDefault();
            if (closestEnemy != null)
            {
                // 적 방향으로 이동하되, 적당한 거리 유지
                Vector3 dirToEnemy = (closestEnemy.Position - unit.Position).normalized;
                float currentDist = Vector3.Distance(unit.Position, closestEnemy.Position);

                // 무기 사거리 - 2m 정도의 위치로 접근
                float targetDist = Mathf.Max(weaponRange * 0.7f, RANGED_MIN_SAFE);
                // ★ v0.2.98: 실제 이동 가능 거리 제한
                float moveDist = Mathf.Min(currentDist - targetDist, maxMoveDistance);

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
        /// ★ v0.2.98: 동적 근접 도달 범위 사용
        /// </summary>
        public static PositionScore FindMeleeAttackPosition(
            UnitEntityData unit,
            UnitEntityData target,
            List<UnitEntityData> enemies,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || target == null)
                return null;

            // ★ v0.2.98: 실제 근접 도달 범위 계산
            float meleeReach = GetMeleeReachDistance(unit);
            var candidates = GenerateMeleePositions(target, meleeReach);
            var scores = new List<PositionScore>();

            foreach (var pos in candidates)
            {
                var score = EvaluatePosition(
                    unit, pos, enemies,
                    MovementGoal.MeleeAttackPosition,
                    meleeReach,
                    influenceMap);

                if (score.IsWalkable)
                    scores.Add(score);
            }

            if (scores.Count == 0)
            {
                // ★ v0.2.98: 폴백 - 실제 근접 도달 범위 사용
                var approachPos = target.Position + (unit.Position - target.Position).normalized * (meleeReach * 0.8f);
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
        /// ★ v0.2.98: 동적 이동 반경 사용
        /// </summary>
        public static PositionScore FindRetreatPosition(
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            float minSafeDistance,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            // ★ v0.2.98: 실제 이동 가능 거리 계산
            float maxMoveDistance = GetMaxMoveDistance(unit);
            var candidates = GenerateCandidatePositions(unit.Position, maxMoveDistance);
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
        /// ★ v0.2.98: 동적 이동 거리 사용 - 실제 유닛 속도 기반
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

            // ★ v0.2.98: 실제 이동 가능 거리 계산
            float maxMoveDistance = GetMaxMoveDistance(unit);
            float meleeReach = GetMeleeReachDistance(unit);
            float moveDistance = Mathf.Min(maxMoveDistance, currentDist - meleeReach);

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
