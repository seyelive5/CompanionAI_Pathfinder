// ★ v0.2.32: CombatDataCache - 공유 전투 데이터 캐시
// 연구문서 섹션 4.3 (Blackboard 캐싱) 기반 구현
// 모든 유닛이 공유하여 중복 연산 제거 (6× → 1×)
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Core
{
    /// <summary>
    /// ★ v0.2.32: 공유 전투 데이터 캐시
    ///
    /// 문제: 6명의 유닛이 각자 GetEnemies()/GetAllies() 호출
    ///       → 같은 연산을 6번 중복 수행
    ///
    /// 해결: 프레임당 1회만 계산하고 결과를 공유
    ///       → 6× 연산 감소
    /// </summary>
    public class CombatDataCache
    {
        #region Singleton

        private static CombatDataCache _instance;
        public static CombatDataCache Instance => _instance ??= new CombatDataCache();

        private CombatDataCache() { }

        #endregion

        #region Constants

        /// <summary>캐시 유효 기간 (초)</summary>
        private const float CACHE_DURATION = 1.0f;

        /// <summary>적 감지 최대 거리</summary>
        private const float MAX_ENEMY_DISTANCE = 50f;

        #endregion

        #region Cached Data

        /// <summary>전투 중인 모든 적</summary>
        public List<UnitEntityData> AllEnemies { get; private set; } = new List<UnitEntityData>();

        /// <summary>전투 중인 모든 아군</summary>
        public List<UnitEntityData> AllAllies { get; private set; } = new List<UnitEntityData>();

        /// <summary>마지막 갱신 시간</summary>
        public float LastUpdateTime { get; private set; }

        /// <summary>전투 활성화 여부</summary>
        public bool IsCombatActive { get; private set; }

        /// <summary>아군 중심점 (전선 계산용)</summary>
        public Vector3 AllyCentroid { get; private set; }

        /// <summary>적 중심점 (전선 계산용)</summary>
        public Vector3 EnemyCentroid { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// 캐시 갱신 (필요한 경우에만)
        /// 이 메서드는 매우 자주 호출되지만, 실제 계산은 CACHE_DURATION마다만 수행
        /// </summary>
        public void RefreshIfNeeded()
        {
            float currentTime = Time.time;

            // 캐시가 유효하면 스킵
            if (currentTime - LastUpdateTime < CACHE_DURATION)
                return;

            RefreshInternal();
            LastUpdateTime = currentTime;
        }

        /// <summary>
        /// 강제 갱신 (전투 시작 시)
        /// </summary>
        public void ForceRefresh()
        {
            RefreshInternal();
            LastUpdateTime = Time.time;
        }

        /// <summary>
        /// 캐시 초기화 (전투 종료 시)
        /// </summary>
        public void Clear()
        {
            AllEnemies.Clear();
            AllAllies.Clear();
            IsCombatActive = false;
            LastUpdateTime = 0f;
            Main.Verbose("[CombatDataCache] Cleared");
        }

        /// <summary>
        /// 특정 유닛 주변의 적 목록 반환 (거리 필터링)
        /// </summary>
        public List<UnitEntityData> GetEnemiesNear(UnitEntityData unit, float maxDistance = MAX_ENEMY_DISTANCE)
        {
            RefreshIfNeeded();

            if (unit == null || AllEnemies.Count == 0)
                return new List<UnitEntityData>();

            Vector3 unitPos = unit.Position;
            float maxDistSq = maxDistance * maxDistance;

            return AllEnemies
                .Where(e => e != null && e.HPLeft > 0 &&
                           (e.Position - unitPos).sqrMagnitude < maxDistSq)
                .ToList();
        }

        /// <summary>
        /// 특정 유닛의 아군 목록 반환 (자신 제외)
        /// </summary>
        public List<UnitEntityData> GetAlliesExcept(UnitEntityData unit)
        {
            RefreshIfNeeded();

            if (unit == null)
                return AllAllies;

            return AllAllies
                .Where(a => a != null && a != unit && a.HPLeft > 0)
                .ToList();
        }

        #endregion

        #region Internal Methods

        private void RefreshInternal()
        {
            try
            {
                if (Game.Instance?.State?.Units == null)
                {
                    IsCombatActive = false;
                    return;
                }

                // 새 리스트 생성 (기존 리스트 재사용 대신)
                var newEnemies = new List<UnitEntityData>();
                var newAllies = new List<UnitEntityData>();

                Vector3 allySum = Vector3.zero;
                Vector3 enemySum = Vector3.zero;
                int allyCount = 0;
                int enemyCount = 0;

                foreach (var unit in Game.Instance.State.Units)
                {
                    if (unit == null) continue;
                    if (unit.Descriptor?.State?.IsDead == true) continue;

                    // 아군 (플레이어 팩션)
                    if (unit.IsPlayerFaction)
                    {
                        newAllies.Add(unit);
                        allySum += unit.Position;
                        allyCount++;

                        // 아군이 전투 중이면 전투 활성화
                        if (unit.CombatState?.IsInCombat == true)
                            IsCombatActive = true;
                    }
                    // 적 (전투 중인 적대 유닛)
                    else if (unit.CombatState?.IsInCombat == true)
                    {
                        // 플레이어 파티와 적대 관계 확인
                        bool isEnemy = false;
                        foreach (var ally in newAllies)
                        {
                            if (ally.IsEnemy(unit))
                            {
                                isEnemy = true;
                                break;
                            }
                        }

                        if (isEnemy)
                        {
                            newEnemies.Add(unit);
                            enemySum += unit.Position;
                            enemyCount++;
                        }
                    }
                }

                // 결과 저장
                AllEnemies = newEnemies;
                AllAllies = newAllies;

                // 중심점 계산
                AllyCentroid = allyCount > 0 ? allySum / allyCount : Vector3.zero;
                EnemyCentroid = enemyCount > 0 ? enemySum / enemyCount : Vector3.zero;

                Main.Verbose($"[CombatDataCache] Refreshed: {newAllies.Count} allies, {newEnemies.Count} enemies, Combat={IsCombatActive}");
            }
            catch (Exception ex)
            {
                Main.Error($"[CombatDataCache] RefreshInternal error: {ex.Message}");
            }
        }

        #endregion

        #region Debug

        public override string ToString()
        {
            return $"[CombatDataCache] Allies={AllAllies.Count}, Enemies={AllEnemies.Count}, Combat={IsCombatActive}, LastUpdate={LastUpdateTime:F1}";
        }

        #endregion
    }
}
