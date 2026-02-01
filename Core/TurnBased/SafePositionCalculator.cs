using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.GameInterface;

namespace CompanionAI_Pathfinder.Core.TurnBased
{
    /// <summary>
    /// ★ v0.2.114: 안전 위치 계산기
    /// 적 사거리 밖, 아군 근처 등 역할에 맞는 위치 계산
    /// </summary>
    public static class SafePositionCalculator
    {
        #region Constants

        /// <summary>안전 여유 거리</summary>
        private const float SAFETY_MARGIN = 2f;

        /// <summary>아군 근처 유지 거리</summary>
        private const float ALLY_PROXIMITY = 8f;

        /// <summary>위치 샘플링 개수</summary>
        private const int POSITION_SAMPLES = 12;

        /// <summary>최대 이동 거리 (한 Move Action)</summary>
        private const float MAX_MOVE_DISTANCE = 9f;

        #endregion

        #region Main Methods

        /// <summary>
        /// 적 사거리 밖 안전 위치 계산 (원거리/서포트용)
        /// </summary>
        public static Vector3? GetSafeRetreatPosition(
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            List<UnitEntityData> allies,
            float moveDistance)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            Vector3 myPos = unit.Position;
            float maxDist = Mathf.Min(moveDistance, MAX_MOVE_DISTANCE);

            // 적들의 최대 공격 거리 계산
            float maxEnemyRange = GetMaxEnemyAttackRange(enemies);

            // 후보 위치들 생성
            var candidates = new List<(Vector3 pos, float score)>();

            for (int i = 0; i < POSITION_SAMPLES; i++)
            {
                float angle = i * (360f / POSITION_SAMPLES) * Mathf.Deg2Rad;
                Vector3 direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

                // 다양한 거리에서 샘플링
                foreach (float distRatio in new[] { 0.5f, 0.75f, 1.0f })
                {
                    Vector3 candidatePos = myPos + direction * (maxDist * distRatio);

                    // 점수 계산
                    float score = ScoreRetreatPosition(candidatePos, unit, enemies, allies, maxEnemyRange);

                    if (score > 0)
                    {
                        candidates.Add((candidatePos, score));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                Main.Verbose($"[SafePos] {unit.CharacterName}: No safe retreat positions found");
                return null;
            }

            // 최고 점수 위치 반환
            var best = candidates.OrderByDescending(c => c.score).First();
            Main.Verbose($"[SafePos] {unit.CharacterName}: Retreat to {best.pos} (score={best.score:F1})");
            return best.pos;
        }

        /// <summary>
        /// 탱커용 - 적 밀집 지역으로 접근 위치 계산
        /// </summary>
        public static Vector3? GetEngagePosition(
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            float moveDistance)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            Vector3 myPos = unit.Position;
            float maxDist = Mathf.Min(moveDistance, MAX_MOVE_DISTANCE);

            // 적 중심점 계산
            Vector3 enemyCenter = GetEnemyClusterCenter(enemies);

            // 적 중심을 향해 이동
            Vector3 direction = (enemyCenter - myPos).normalized;
            float distToCenter = Vector3.Distance(myPos, enemyCenter);

            // 이동 가능 거리만큼 이동
            float actualMove = Mathf.Min(maxDist, distToCenter - 1.5f); // 1.5m 근접 유지
            if (actualMove <= 0)
            {
                // 이미 적 근처
                return null;
            }

            Vector3 targetPos = myPos + direction * actualMove;
            Main.Verbose($"[SafePos] {unit.CharacterName}: Engage toward {targetPos} (dist={actualMove:F1}m)");
            return targetPos;
        }

        /// <summary>
        /// 공격 위치 계산 - 이동 후 공격 가능한 최적 위치
        /// </summary>
        public static Vector3? GetAttackPosition(
            UnitEntityData unit,
            UnitEntityData target,
            float attackRange,
            float moveDistance)
        {
            if (unit == null || target == null)
                return null;

            Vector3 myPos = unit.Position;
            Vector3 targetPos = target.Position;
            float currentDist = Vector3.Distance(myPos, targetPos);

            // 이미 사거리 내
            if (currentDist <= attackRange)
            {
                return null; // 이동 필요 없음
            }

            // 필요 이동 거리
            float neededMove = currentDist - attackRange + 0.5f; // 약간의 여유

            if (neededMove > moveDistance)
            {
                // 이동 거리 부족 - 최대한 접근
                Vector3 direction = (targetPos - myPos).normalized;
                return myPos + direction * moveDistance;
            }

            // 사거리 내 최적 위치로 이동
            Vector3 moveDir = (targetPos - myPos).normalized;
            Vector3 optimalPos = myPos + moveDir * neededMove;

            Main.Verbose($"[SafePos] {unit.CharacterName}: Attack position {optimalPos} (move={neededMove:F1}m)");
            return optimalPos;
        }

        /// <summary>
        /// 근접 딜러용 - 탱커 뒤 위치 계산
        /// </summary>
        public static Vector3? GetPositionBehindTank(
            UnitEntityData unit,
            UnitEntityData tank,
            List<UnitEntityData> enemies,
            float moveDistance)
        {
            if (unit == null || tank == null || enemies == null || enemies.Count == 0)
                return null;

            Vector3 myPos = unit.Position;
            Vector3 tankPos = tank.Position;

            // 적 중심 계산
            Vector3 enemyCenter = GetEnemyClusterCenter(enemies);

            // 탱커 기준 적 반대 방향
            Vector3 awayFromEnemies = (tankPos - enemyCenter).normalized;

            // 탱커 뒤쪽 위치 (탱커에서 3m 뒤)
            Vector3 behindTank = tankPos + awayFromEnemies * 3f;

            // 이동 가능한지 확인
            float distToBehind = Vector3.Distance(myPos, behindTank);
            if (distToBehind > moveDistance)
            {
                // 최대한 그 방향으로
                Vector3 direction = (behindTank - myPos).normalized;
                behindTank = myPos + direction * moveDistance;
            }

            Main.Verbose($"[SafePos] {unit.CharacterName}: Position behind tank at {behindTank}");
            return behindTank;
        }

        #endregion

        #region Scoring

        /// <summary>
        /// 후퇴 위치 점수 계산
        /// </summary>
        private static float ScoreRetreatPosition(
            Vector3 pos,
            UnitEntityData unit,
            List<UnitEntityData> enemies,
            List<UnitEntityData> allies,
            float maxEnemyRange)
        {
            float score = 100f;

            // 1. 적 사거리 밖인지 (-50 ~ +50)
            foreach (var enemy in enemies)
            {
                float distToEnemy = Vector3.Distance(pos, enemy.Position);
                float enemyRange = GetUnitAttackRange(enemy);

                if (distToEnemy < enemyRange + SAFETY_MARGIN)
                {
                    // 사거리 내 - 큰 페널티
                    score -= 50f * (1f - distToEnemy / (enemyRange + SAFETY_MARGIN));
                }
                else
                {
                    // 사거리 밖 - 보너스
                    score += 10f;
                }
            }

            // 2. 아군 근처인지 (+20)
            if (allies != null && allies.Count > 0)
            {
                float minAllyDist = allies.Min(a => Vector3.Distance(pos, a.Position));
                if (minAllyDist < ALLY_PROXIMITY)
                {
                    score += 20f;
                }
            }

            // 3. 현재 위치에서 너무 멀지 않은지 (-10 per 5m)
            float moveDistance = Vector3.Distance(unit.Position, pos);
            score -= (moveDistance / 5f) * 10f;

            // 4. 맵 경계 체크 (간단히)
            if (Mathf.Abs(pos.x) > 100f || Mathf.Abs(pos.z) > 100f)
            {
                score -= 100f;
            }

            return score;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 적들의 최대 공격 거리
        /// </summary>
        private static float GetMaxEnemyAttackRange(List<UnitEntityData> enemies)
        {
            float maxRange = 0f;
            foreach (var enemy in enemies)
            {
                float range = GetUnitAttackRange(enemy);
                if (range > maxRange)
                    maxRange = range;
            }
            return maxRange;
        }

        /// <summary>
        /// 유닛의 공격 거리
        /// </summary>
        private static float GetUnitAttackRange(UnitEntityData unit)
        {
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null)
                {
                    return weapon.AttackRange.Meters + unit.Corpulence;
                }
                return 2f + unit.Corpulence; // 기본 근접
            }
            catch
            {
                return 2f;
            }
        }

        /// <summary>
        /// 적 밀집 중심점 계산
        /// </summary>
        private static Vector3 GetEnemyClusterCenter(List<UnitEntityData> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (var enemy in enemies)
            {
                sum += enemy.Position;
            }
            return sum / enemies.Count;
        }

        /// <summary>
        /// 가장 가까운 적 찾기
        /// </summary>
        public static UnitEntityData GetClosestEnemy(UnitEntityData unit, List<UnitEntityData> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return null;

            return enemies.OrderBy(e => Vector3.Distance(unit.Position, e.Position)).First();
        }

        /// <summary>
        /// 탱커 찾기 (파티 내)
        /// </summary>
        public static UnitEntityData FindTankInParty(UnitEntityData unit, List<UnitEntityData> allies)
        {
            if (allies == null || allies.Count == 0)
                return null;

            // 설정에서 탱커 역할인 유닛 찾기
            foreach (var ally in allies)
            {
                var settings = Settings.ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == Settings.AIRole.Tank)
                {
                    return ally;
                }
            }

            // 없으면 HP가 가장 높은 근접 캐릭터
            return allies
                .Where(a => a.Body?.PrimaryHand?.MaybeWeapon?.Blueprint?.IsMelee == true)
                .OrderByDescending(a => a.MaxHP)
                .FirstOrDefault();
        }

        #endregion
    }
}
