// ★ v0.2.30: BattlefieldGrid - 전장 그리드 시스템 (Pathfinder WotR 호환 버전)
// 게임의 pathfinding 시스템을 활용하여 이동 가능 여부 판단
using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 전장 그리드 - 게임의 실제 맵 구조 활용
    ///
    /// Pathfinder WotR 호환 버전:
    /// - AstarPath를 통한 walkability 체크
    /// - 유닛 점유 체크
    /// - 그리드 기반이 아닌 위치 기반 체크로 단순화
    /// </summary>
    public class BattlefieldGrid
    {
        #region Singleton

        private static BattlefieldGrid _instance;
        public static BattlefieldGrid Instance => _instance ??= new BattlefieldGrid();

        #endregion

        #region State

        /// <summary>그리드 유효 여부</summary>
        private bool _isValid;

        /// <summary>마지막 초기화 시간</summary>
        private float _lastInitTime;

        /// <summary>모든 유닛 캐시 (전투 참여 중)</summary>
        private List<UnitEntityData> _combatUnits;

        /// <summary>★ v0.2.31: Walkability 캐시 (성능 최적화)</summary>
        private Dictionary<long, bool> _walkabilityCache = new Dictionary<long, bool>();

        /// <summary>캐시 최대 크기 - ★ v0.2.32: 200→500으로 증가</summary>
        private const int MAX_CACHE_SIZE = 500;

        #endregion

        #region Initialization

        /// <summary>
        /// 초기화 확인 및 필요 시 초기화
        /// </summary>
        public void EnsureInitialized()
        {
            // 5초마다 유닛 목록 갱신
            if (_isValid && Time.time - _lastInitTime < 5f)
                return;

            try
            {
                // AstarPath 존재 확인
                if (AstarPath.active == null)
                {
                    Main.Verbose("[BattlefieldGrid] AstarPath not active");
                    _isValid = false;
                    return;
                }

                // 전투 유닛 수집
                _combatUnits = new List<UnitEntityData>();
                foreach (var unit in Game.Instance.State.Units)
                {
                    if (unit != null && unit.IsInCombat)
                        _combatUnits.Add(unit);
                }

                _isValid = true;
                _lastInitTime = Time.time;

                if (_combatUnits.Count > 0)
                {
                    Main.Verbose($"[BattlefieldGrid] Initialized with {_combatUnits.Count} combat units");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[BattlefieldGrid] EnsureInitialized failed: {ex.Message}");
                _isValid = false;
            }
        }

        /// <summary>
        /// 전투 종료 시 정리
        /// </summary>
        public void Clear()
        {
            _combatUnits?.Clear();
            _walkabilityCache?.Clear();
            _isValid = false;
            Main.Verbose("[BattlefieldGrid] Cleared");
        }

        #endregion

        #region Walkability Checks

        /// <summary>
        /// 위치를 캐시 키로 변환 (1m 단위로 양자화)
        /// </summary>
        private long GetCacheKey(Vector3 worldPos)
        {
            int x = Mathf.RoundToInt(worldPos.x);
            int z = Mathf.RoundToInt(worldPos.z);
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>
        /// 월드 좌표가 Walkable인지 확인
        /// ★ v0.2.31: 캐싱 적용하여 성능 최적화
        /// </summary>
        public bool IsWalkable(Vector3 worldPos)
        {
            if (!_isValid)
                return true; // 폴백: 게임에 맡김

            try
            {
                // 캐시 체크
                long key = GetCacheKey(worldPos);
                if (_walkabilityCache.TryGetValue(key, out bool cached))
                {
                    return cached;
                }

                // AstarPath를 통해 가장 가까운 walkable 노드 찾기
                var nnInfo = AstarPath.active.GetNearest(worldPos, NNConstraint.Default);
                bool walkable = nnInfo.node != null && nnInfo.node.Walkable;

                // 캐시 업데이트 (크기 제한)
                if (_walkabilityCache.Count > MAX_CACHE_SIZE)
                {
                    _walkabilityCache.Clear();
                }
                _walkabilityCache[key] = walkable;

                return walkable;
            }
            catch
            {
                return true; // 오류 시 폴백
            }
        }

        /// <summary>
        /// 특정 위치가 다른 유닛에 의해 점유되었는지 확인
        /// </summary>
        public bool IsOccupiedByOther(Vector3 worldPos, UnitEntityData unit)
        {
            if (_combatUnits == null)
                return false;

            float checkRadius = 1.5f; // 점유 체크 반경

            foreach (var other in _combatUnits)
            {
                if (other == null || other == unit)
                    continue;

                if (other.HPLeft <= 0)
                    continue;

                float dist = Vector3.Distance(worldPos, other.Position);
                if (dist < checkRadius)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 유닛이 특정 위치에 설 수 있는지 확인 (종합 검증)
        /// </summary>
        public bool CanUnitStandOn(UnitEntityData unit, Vector3 worldPos)
        {
            if (!_isValid)
                return true; // 폴백

            // 1. Walkable 체크
            if (!IsWalkable(worldPos))
                return false;

            // 2. 점유 체크
            if (IsOccupiedByOther(worldPos, unit))
                return false;

            return true;
        }

        #endregion

        #region Position Validation

        /// <summary>
        /// 타겟 위치 검증 (이동 전 사전 검증)
        /// </summary>
        public bool ValidateTargetPosition(UnitEntityData unit, Vector3 targetPos)
        {
            if (!_isValid)
                return true; // 폴백: 게임에 맡김

            // 1. Walkable 체크
            if (!IsWalkable(targetPos))
            {
                Main.Verbose($"[BattlefieldGrid] ValidateTargetPosition: Not walkable at ({targetPos.x:F1},{targetPos.z:F1})");
                return false;
            }

            // 2. 점유 체크
            if (IsOccupiedByOther(targetPos, unit))
            {
                Main.Verbose($"[BattlefieldGrid] ValidateTargetPosition: Occupied at ({targetPos.x:F1},{targetPos.z:F1})");
                return false;
            }

            return true;
        }

        #endregion

        #region Path Validation

        /// <summary>
        /// 두 위치 간 직선 경로가 연결되어 있는지 확인
        /// (walkable 노드 체크 기반)
        /// </summary>
        public bool IsPathClear(Vector3 from, Vector3 to)
        {
            if (!_isValid)
                return true;

            try
            {
                // 중간 지점들의 walkability 체크
                Vector3 direction = to - from;
                float distance = direction.magnitude;

                if (distance < 0.1f)
                    return true;

                // 중간 지점 샘플링
                int samples = Mathf.CeilToInt(distance / 2f);
                for (int i = 1; i < samples; i++)
                {
                    float t = (float)i / samples;
                    Vector3 checkPos = Vector3.Lerp(from, to, t);

                    if (!IsWalkable(checkPos))
                        return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// 특정 위치에서 가장 가까운 walkable 위치 찾기
        /// </summary>
        public Vector3 GetNearestWalkablePosition(Vector3 worldPos)
        {
            if (!_isValid)
                return worldPos;

            try
            {
                var nnInfo = AstarPath.active.GetNearest(worldPos, NNConstraint.Default);
                if (nnInfo.node != null && nnInfo.node.Walkable)
                {
                    return (Vector3)nnInfo.node.position;
                }
            }
            catch { }

            return worldPos;
        }

        /// <summary>
        /// 유닛 주변의 빈 위치 찾기
        /// </summary>
        public Vector3? FindEmptyPositionNear(UnitEntityData unit, Vector3 center, float minDist, float maxDist)
        {
            if (!_isValid)
                return null;

            // 8방향 체크
            Vector3[] directions = new Vector3[]
            {
                Vector3.forward,
                Vector3.back,
                Vector3.left,
                Vector3.right,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized
            };

            float[] distances = new float[] { minDist, (minDist + maxDist) / 2f, maxDist };

            foreach (float dist in distances)
            {
                foreach (var dir in directions)
                {
                    Vector3 testPos = center + dir * dist;

                    if (CanUnitStandOn(unit, testPos))
                    {
                        return testPos;
                    }
                }
            }

            return null;
        }

        #endregion

        #region Properties

        /// <summary>그리드 초기화 완료 여부</summary>
        public bool IsValid => _isValid;

        /// <summary>전투 유닛 수</summary>
        public int CombatUnitCount => _combatUnits?.Count ?? 0;

        #endregion

        #region Debug

        /// <summary>
        /// 디버그용 상태 출력
        /// </summary>
        public string GetDebugInfo()
        {
            if (!_isValid)
                return "[BattlefieldGrid] Not initialized";

            return $"[BattlefieldGrid] Valid, {CombatUnitCount} combat units";
        }

        #endregion
    }
}
