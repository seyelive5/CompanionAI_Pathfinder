// ★ v0.2.53: TrapAwarenessController - 함정 인식 및 자동 회피 시스템
// 감지된 함정의 트리거 영역을 파악하고, 이동 시 자동으로 우회
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View.MapObjects;
using Kingmaker.View.MapObjects.Traps;
using Kingmaker.View.MapObjects.SriptZones;
using UnityEngine;
using UnityEngine.AI;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// ★ v0.2.53: 함정 인식 및 자동 회피
    ///
    /// 기능:
    /// 1. 현재 영역의 감지된 함정 수집
    /// 2. 이동 경로가 함정 영역을 지나는지 체크
    /// 3. 안전한 우회 경로 계산
    /// </summary>
    public static class TrapAwarenessController
    {
        #region Settings

        /// <summary>
        /// 함정 회피 활성화 여부
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// 감지되지 않은 함정도 회피할지 (치트 모드)
        /// </summary>
        public static bool AvoidUndetectedTraps { get; set; } = false;

        /// <summary>
        /// 함정 영역 주변 안전 여유 거리
        /// </summary>
        public static float SafetyMargin { get; set; } = 1.0f;

        /// <summary>
        /// 반경 정보가 없는 함정의 기본 트리거 반경
        /// </summary>
        public static float DefaultTrapRadius { get; set; } = 2.5f;

        #endregion

        #region Cache

        private static readonly Dictionary<string, CachedTrapInfo> _trapCache = new();
        private static float _lastCacheUpdate = 0f;
        private const float CACHE_DURATION = 2.0f;

        public class CachedTrapInfo
        {
            public TrapObjectData TrapData;
            public Vector3 Position;
            public float TriggerRadius;
            public ScriptZone TriggerZone;
            public bool IsPerceived;
        }

        #endregion

        #region Public API

        /// <summary>
        /// 현재 영역의 모든 활성 함정 정보 가져오기
        /// </summary>
        public static List<CachedTrapInfo> GetActiveTraps()
        {
            UpdateTrapCache();
            return _trapCache.Values.ToList();
        }

        /// <summary>
        /// 위치가 함정 위험 영역 내인지 체크
        /// </summary>
        public static bool IsPositionDangerous(Vector3 position)
        {
            if (!Enabled) return false;

            UpdateTrapCache();

            foreach (var trap in _trapCache.Values)
            {
                if (!trap.TrapData.TrapActive) continue;
                if (!AvoidUndetectedTraps && !trap.IsPerceived) continue;

                // ScriptZone 기반 체크
                if (trap.TriggerZone != null)
                {
                    if (trap.TriggerZone.ContainsPosition(position))
                        return true;
                }
                // 반경 기반 체크 (폴백 - radius=0이면 기본값 사용)
                else
                {
                    float effectiveRadius = trap.TriggerRadius > 0 ? trap.TriggerRadius : DefaultTrapRadius;
                    float dist = Vector3.Distance(position, trap.Position);
                    if (dist < effectiveRadius + SafetyMargin)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 경로가 함정을 지나는지 체크
        /// </summary>
        /// <param name="start">시작 위치</param>
        /// <param name="end">목적지</param>
        /// <param name="dangerousTrap">위험한 함정 정보 (출력)</param>
        /// <returns>true = 위험한 경로</returns>
        public static bool IsPathDangerous(Vector3 start, Vector3 end, out CachedTrapInfo dangerousTrap)
        {
            dangerousTrap = null;
            if (!Enabled) return false;

            UpdateTrapCache();

            Vector3 direction = (end - start).normalized;
            float distance = Vector3.Distance(start, end);

            // 경로를 따라 샘플링하여 체크
            int samples = Mathf.Max(5, Mathf.CeilToInt(distance / 1.0f));

            foreach (var trap in _trapCache.Values)
            {
                if (!trap.TrapData.TrapActive) continue;
                if (!AvoidUndetectedTraps && !trap.IsPerceived) continue;

                for (int i = 0; i <= samples; i++)
                {
                    float t = (float)i / samples;
                    Vector3 samplePoint = Vector3.Lerp(start, end, t);

                    bool inDanger = false;

                    // ScriptZone 기반 체크
                    if (trap.TriggerZone != null)
                    {
                        inDanger = trap.TriggerZone.ContainsPosition(samplePoint);
                    }
                    // 반경 기반 체크 (폴백 - radius=0이면 기본값 사용)
                    else
                    {
                        float effectiveRadius = trap.TriggerRadius > 0 ? trap.TriggerRadius : DefaultTrapRadius;
                        float dist = Vector3.Distance(samplePoint, trap.Position);
                        inDanger = dist < effectiveRadius + SafetyMargin;
                    }

                    if (inDanger)
                    {
                        dangerousTrap = trap;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 안전한 우회 경로 계산
        /// </summary>
        /// <param name="start">시작 위치</param>
        /// <param name="end">목적지</param>
        /// <param name="safePath">안전한 경로 (출력)</param>
        /// <returns>true = 안전한 경로 찾음</returns>
        public static bool TryFindSafePath(Vector3 start, Vector3 end, out Vector3[] safePath)
        {
            safePath = null;

            if (!Enabled)
            {
                safePath = new[] { start, end };
                return true;
            }

            // 직선 경로가 안전한지 먼저 체크
            if (!IsPathDangerous(start, end, out _))
            {
                safePath = new[] { start, end };
                return true;
            }

            // NavMesh를 이용한 우회 경로 계산
            try
            {
                NavMeshPath navPath = new NavMeshPath();

                // 함정 영역을 피하는 경로 계산
                // NavMesh.CalculatePath는 장애물을 자동으로 피함
                // 하지만 함정은 NavMesh 장애물이 아니므로 수동 처리 필요

                if (NavMesh.CalculatePath(start, end, NavMesh.AllAreas, navPath))
                {
                    // NavMesh 경로도 함정을 피하는지 확인
                    Vector3[] corners = navPath.corners;
                    bool pathIsSafe = true;

                    for (int i = 0; i < corners.Length - 1; i++)
                    {
                        if (IsPathDangerous(corners[i], corners[i + 1], out _))
                        {
                            pathIsSafe = false;
                            break;
                        }
                    }

                    if (pathIsSafe)
                    {
                        safePath = corners;
                        return true;
                    }

                    // NavMesh 경로도 위험하면 수동 우회 시도
                    return TryFindManualDetour(start, end, out safePath);
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TrapAwareness] NavMesh path error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// 캐시 초기화 (영역 변경 시 호출)
        /// </summary>
        public static void ClearCache()
        {
            _trapCache.Clear();
            _lastCacheUpdate = 0f;
            Main.Verbose("[TrapAwareness] Cache cleared");
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// 함정 캐시 업데이트
        /// </summary>
        private static void UpdateTrapCache()
        {
            float currentTime = Time.time;
            if (currentTime - _lastCacheUpdate < CACHE_DURATION)
                return;

            _lastCacheUpdate = currentTime;
            _trapCache.Clear();

            try
            {
                var currentArea = Game.Instance?.CurrentlyLoadedArea;
                if (currentArea == null) return;

                // MapObjectEntityData에서 함정 찾기
                var allMapObjects = Game.Instance?.State?.MapObjects;
                if (allMapObjects == null) return;

                foreach (var mapObj in allMapObjects)
                {
                    if (mapObj?.View == null) continue;

                    // TrapObjectView 확인
                    var trapView = mapObj.View as TrapObjectView;
                    if (trapView == null) continue;

                    var trapData = trapView.Data;
                    if (trapData == null) continue;

                    // 활성 함정만
                    if (!trapData.TrapActive) continue;

                    string trapId = mapObj.UniqueId;

                    // ★ v0.2.53 FIX: 트리거 영역 정보를 올바르게 수집
                    ScriptZone triggerZone = null;
                    float triggerRadius = 0f;

                    // 1. ScriptZone 기반 트리거 - Settings에서 직접 가져오기
                    if (trapView.Settings?.ScriptZoneTrigger != null)
                    {
                        triggerZone = trapView.Settings.ScriptZoneTrigger;
                    }

                    // 2. ProximityRadius - DisableTrapInteractionPart에서 가져오기
                    if (trapView.UseProximityRadius)
                    {
                        // TrapObjectData.ProximityRadius는 DisableTrapInteractionPart.Settings.ProximityRadius에서 옴
                        if (trapData.ProximityRadius.HasValue)
                        {
                            triggerRadius = trapData.ProximityRadius.Value;
                        }
                        else
                        {
                            // 기본값 사용
                            triggerRadius = DefaultTrapRadius;
                        }
                    }

                    // 감지 여부 확인
                    bool isPerceived = mapObj.IsPerceptionCheckPassed;

                    _trapCache[trapId] = new CachedTrapInfo
                    {
                        TrapData = trapData,
                        Position = trapView.transform.position,
                        TriggerRadius = triggerRadius,
                        TriggerZone = triggerZone,
                        IsPerceived = isPerceived
                    };

                    string radiusInfo = triggerZone != null ? "ScriptZone" :
                        (triggerRadius > 0 ? $"radius={triggerRadius:F1}" : $"default={DefaultTrapRadius:F1}");

                    if (isPerceived)
                    {
                        Main.Log($"[TrapAwareness] Detected trap: {trapId} at {trapView.transform.position}, {radiusInfo}");
                    }
                    else
                    {
                        Main.Verbose($"[TrapAwareness] Hidden trap: {trapId} at {trapView.transform.position}, {radiusInfo} (not perceived)");
                    }
                }

                Main.Log($"[TrapAwareness] Cache updated: {_trapCache.Count} active traps");
            }
            catch (Exception ex)
            {
                Main.Error($"[TrapAwareness] Cache update error: {ex.Message}");
            }
        }

        /// <summary>
        /// 수동 우회 경로 계산 (함정 주변을 돌아가기)
        /// </summary>
        private static bool TryFindManualDetour(Vector3 start, Vector3 end, out Vector3[] safePath)
        {
            safePath = null;

            try
            {
                // 위험한 함정 찾기
                if (!IsPathDangerous(start, end, out var trap) || trap == null)
                {
                    safePath = new[] { start, end };
                    return true;
                }

                Vector3 trapCenter = trap.Position;
                float avoidRadius = (trap.TriggerRadius > 0 ? trap.TriggerRadius : 3f) + SafetyMargin + 1f;

                // 함정을 피해 좌/우로 우회 시도
                Vector3 toEnd = (end - start).normalized;
                Vector3 perpendicular = Vector3.Cross(toEnd, Vector3.up).normalized;

                // 오른쪽 우회
                Vector3 rightDetour = trapCenter + perpendicular * avoidRadius;
                if (NavMesh.SamplePosition(rightDetour, out NavMeshHit hitRight, 5f, NavMesh.AllAreas))
                {
                    rightDetour = hitRight.position;

                    if (!IsPathDangerous(start, rightDetour, out _) &&
                        !IsPathDangerous(rightDetour, end, out _))
                    {
                        safePath = new[] { start, rightDetour, end };
                        Main.Log($"[TrapAwareness] Found right detour around trap");
                        return true;
                    }
                }

                // 왼쪽 우회
                Vector3 leftDetour = trapCenter - perpendicular * avoidRadius;
                if (NavMesh.SamplePosition(leftDetour, out NavMeshHit hitLeft, 5f, NavMesh.AllAreas))
                {
                    leftDetour = hitLeft.position;

                    if (!IsPathDangerous(start, leftDetour, out _) &&
                        !IsPathDangerous(leftDetour, end, out _))
                    {
                        safePath = new[] { start, leftDetour, end };
                        Main.Log($"[TrapAwareness] Found left detour around trap");
                        return true;
                    }
                }

                Main.Verbose($"[TrapAwareness] Could not find safe detour");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TrapAwareness] Manual detour error: {ex.Message}");
            }

            return false;
        }

        #endregion

        #region Movement Integration

        /// <summary>
        /// 이동 명령 전 안전 체크
        /// RealTimeController나 이동 명령 패치에서 호출
        /// </summary>
        /// <param name="unit">이동할 유닛</param>
        /// <param name="destination">목적지</param>
        /// <param name="safeDestination">안전한 목적지 (출력)</param>
        /// <returns>true = 원래 목적지 안전, false = 우회 필요</returns>
        public static bool CheckMovementSafety(UnitEntityData unit, Vector3 destination, out Vector3 safeDestination)
        {
            safeDestination = destination;

            if (!Enabled || unit == null)
                return true;

            Vector3 start = unit.Position;

            // 직선 경로가 안전한지
            if (!IsPathDangerous(start, destination, out var dangerousTrap))
                return true;

            // 우회 경로 찾기
            if (TryFindSafePath(start, destination, out var safePath) && safePath.Length > 1)
            {
                // 첫 번째 웨이포인트를 임시 목적지로
                safeDestination = safePath.Length > 2 ? safePath[1] : safePath[safePath.Length - 1];

                Main.Log($"[TrapAwareness] {unit.CharacterName}: Avoiding trap, detour via {safeDestination}");
                return false;
            }

            // 우회 불가 - 함정 직전에서 멈추기
            if (dangerousTrap != null)
            {
                Vector3 toTrap = (dangerousTrap.Position - start).normalized;
                float stopDistance = dangerousTrap.TriggerRadius > 0
                    ? dangerousTrap.TriggerRadius + SafetyMargin
                    : 3f + SafetyMargin;

                safeDestination = dangerousTrap.Position - toTrap * stopDistance;

                Main.Log($"[TrapAwareness] {unit.CharacterName}: Cannot avoid trap, stopping before it");
                return false;
            }

            return true;
        }

        #endregion
    }
}
