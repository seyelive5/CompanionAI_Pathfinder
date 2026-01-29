// ★ v0.2.30: BattlefieldInfluenceMap - 전장 영향력 맵 (RT 모드 v3.2.00 포팅)
// 적/아군의 위치 기반 영향력을 2D 그리드로 계산하여 O(1) 조회 제공
using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 전장 영향력 맵
    ///
    /// 적/아군의 위치 기반 영향력을 2D 그리드로 계산하여 O(1) 조회 제공.
    /// - ThreatAt(pos) = Σ (EnemyThreat[i] / Distance[i]²)
    /// - ControlAt(pos) = Σ (AllyStrength[i] / Distance[i]²)
    ///
    /// 전선(Frontline), 안전 구역, 팀 위협도 등 전술적 정보 제공.
    /// </summary>
    public class BattlefieldInfluenceMap
    {
        #region Constants

        /// <summary>그리드 셀 크기 (미터) - ★ v0.2.35: 1.5→4.0m로 증가 (성능 최적화)</summary>
        private const float CELL_SIZE = 4.0f;

        /// <summary>영향력 최대 거리 (미터)</summary>
        private const float MAX_INFLUENCE_DISTANCE = 20.0f;

        /// <summary>최소 거리 (0 나눗셈 방지)</summary>
        private const float MIN_DISTANCE = 1.0f;

        /// <summary>안전 구역 위협 임계값</summary>
        private const float SAFE_ZONE_THRESHOLD = 0.5f;

        /// <summary>최대 그리드 크기 - ★ v0.2.35: 100→30으로 축소 (성능 최적화)</summary>
        private const int MAX_GRID_SIZE = 30;

        #endregion

        #region Fields

        // 그리드 데이터
        private float[,] _threatGrid;      // 적 위협 밀도
        private float[,] _controlGrid;     // 아군 통제 영역

        // 그리드 메타데이터
        private Vector3 _gridOrigin;       // 그리드 원점 (최소 좌표)
        private int _gridWidth;            // X 방향 셀 수
        private int _gridHeight;           // Z 방향 셀 수
        private bool _isValid;             // 유효한 맵 여부

        // 유닛 캐시
        private List<UnitEntityData> _enemies;
        private List<UnitEntityData> _allies;

        #endregion

        #region Properties

        /// <summary>전선 위치 (적/아군 경계)</summary>
        public Vector3 Frontline { get; private set; }

        /// <summary>안전 구역 목록 (위협이 낮은 위치)</summary>
        public List<Vector3> SafeZones { get; private set; } = new List<Vector3>();

        /// <summary>전체 팀 위협도 (0-1)</summary>
        public float TeamThreatLevel { get; private set; }

        /// <summary>적 중심점</summary>
        public Vector3 EnemyCentroid { get; private set; }

        /// <summary>아군 중심점</summary>
        public Vector3 AllyCentroid { get; private set; }

        /// <summary>전선 방향 (아군→적)</summary>
        public Vector3 FrontlineDirection { get; private set; }

        /// <summary>맵이 유효한지 (계산 완료)</summary>
        public bool IsValid => _isValid;

        #endregion

        #region Caching (★ v0.2.31 Performance)

        /// <summary>캐시된 맵</summary>
        private static BattlefieldInfluenceMap _cachedMap;

        /// <summary>마지막 계산 시간</summary>
        private static float _lastComputeTime;

        /// <summary>캐시 유효 기간 (초) - ★ v0.2.32: 0.8→2.0초로 증가</summary>
        private const float CACHE_DURATION = 2.0f;

        /// <summary>캐시된 적/아군 수 (변경 감지용)</summary>
        private static int _cachedEnemyCount;
        private static int _cachedAllyCount;

        #endregion

        #region Factory

        /// <summary>
        /// 영향력 맵 계산 (캐싱 적용)
        /// ★ v0.2.31: 성능 최적화 - 0.8초간 캐시 유지
        /// </summary>
        public static BattlefieldInfluenceMap Compute(
            List<UnitEntityData> enemies,
            List<UnitEntityData> allies)
        {
            float currentTime = UnityEngine.Time.time;
            int enemyCount = enemies?.Count ?? 0;
            int allyCount = allies?.Count ?? 0;

            // 캐시 유효성 체크
            bool cacheValid = _cachedMap != null &&
                              _cachedMap._isValid &&
                              (currentTime - _lastComputeTime) < CACHE_DURATION &&
                              _cachedEnemyCount == enemyCount &&
                              _cachedAllyCount == allyCount;

            if (cacheValid)
            {
                return _cachedMap;
            }

            // 새로 계산
            var map = new BattlefieldInfluenceMap();
            map.ComputeInternal(enemies, allies);

            // 캐시 업데이트
            _cachedMap = map;
            _lastComputeTime = currentTime;
            _cachedEnemyCount = enemyCount;
            _cachedAllyCount = allyCount;

            return map;
        }

        /// <summary>캐시 무효화 (전투 종료 시)</summary>
        public static void InvalidateCache()
        {
            _cachedMap = null;
            _lastComputeTime = 0f;
        }

        #endregion

        #region Core Computation

        private void ComputeInternal(List<UnitEntityData> enemies, List<UnitEntityData> allies)
        {
            _enemies = enemies ?? new List<UnitEntityData>();
            _allies = allies ?? new List<UnitEntityData>();

            if (_enemies.Count == 0 && _allies.Count == 0)
            {
                _isValid = false;
                return;
            }

            try
            {
                // 1. 전장 경계 계산
                CalculateBounds(out Vector3 min, out Vector3 max);

                // 2. 그리드 초기화
                InitializeGrid(min, max);

                // 3. 영향력 계산
                ComputeInfluence();

                // 4. 전술 정보 계산
                ComputeTacticalInfo();

                _isValid = true;

                Main.Verbose($"[InfluenceMap] Computed: {_gridWidth}x{_gridHeight}, " +
                    $"Enemies={_enemies.Count}, Allies={_allies.Count}, ThreatLevel={TeamThreatLevel:F2}");
            }
            catch (Exception ex)
            {
                Main.Error($"[InfluenceMap] Compute failed: {ex.Message}");
                _isValid = false;
            }
        }

        private void CalculateBounds(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, 0, float.MaxValue);
            max = new Vector3(float.MinValue, 0, float.MinValue);

            // 모든 유닛 위치에서 경계 계산
            foreach (var unit in _enemies)
            {
                if (unit == null) continue;
                var pos = unit.Position;
                min.x = Mathf.Min(min.x, pos.x);
                min.z = Mathf.Min(min.z, pos.z);
                max.x = Mathf.Max(max.x, pos.x);
                max.z = Mathf.Max(max.z, pos.z);
            }

            foreach (var unit in _allies)
            {
                if (unit == null) continue;
                var pos = unit.Position;
                min.x = Mathf.Min(min.x, pos.x);
                min.z = Mathf.Min(min.z, pos.z);
                max.x = Mathf.Max(max.x, pos.x);
                max.z = Mathf.Max(max.z, pos.z);
            }

            // 여유 공간 추가 (영향력 범위 고려)
            min.x -= MAX_INFLUENCE_DISTANCE;
            min.z -= MAX_INFLUENCE_DISTANCE;
            max.x += MAX_INFLUENCE_DISTANCE;
            max.z += MAX_INFLUENCE_DISTANCE;
        }

        private void InitializeGrid(Vector3 min, Vector3 max)
        {
            _gridOrigin = min;
            _gridWidth = Mathf.CeilToInt((max.x - min.x) / CELL_SIZE) + 1;
            _gridHeight = Mathf.CeilToInt((max.z - min.z) / CELL_SIZE) + 1;

            // ★ v0.2.35: 성능 최적화 - MAX_GRID_SIZE로 제한 (이전: 100×100 = 10,000셀)
            _gridWidth = Mathf.Min(_gridWidth, MAX_GRID_SIZE);
            _gridHeight = Mathf.Min(_gridHeight, MAX_GRID_SIZE);

            _threatGrid = new float[_gridWidth, _gridHeight];
            _controlGrid = new float[_gridWidth, _gridHeight];
        }

        private void ComputeInfluence()
        {
            // ★ v0.2.35: 성능 최적화
            // - IsWalkable 체크 제거 (셀당 비용이 높음)
            // - sqrMagnitude 사용 (루트 연산 회피)
            float maxDistSq = MAX_INFLUENCE_DISTANCE * MAX_INFLUENCE_DISTANCE;

            // 각 셀에 대해 영향력 계산
            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridHeight; z++)
                {
                    Vector3 cellPos = GetCellWorldPosition(x, z);

                    // 적 위협 계산
                    float threat = 0f;
                    foreach (var enemy in _enemies)
                    {
                        if (enemy == null) continue;
                        float distSq = (cellPos - enemy.Position).sqrMagnitude;
                        if (distSq < maxDistSq)
                        {
                            float enemyThreat = GetUnitThreatValue(enemy);
                            // 역제곱 감소 (거리가 2배 → 영향력 1/4)
                            float effectiveDistSq = Mathf.Max(distSq, MIN_DISTANCE * MIN_DISTANCE);
                            threat += enemyThreat / effectiveDistSq;
                        }
                    }
                    _threatGrid[x, z] = threat;

                    // 아군 통제력 계산
                    float control = 0f;
                    foreach (var ally in _allies)
                    {
                        if (ally == null) continue;
                        float distSq = (cellPos - ally.Position).sqrMagnitude;
                        if (distSq < maxDistSq)
                        {
                            float allyStrength = GetUnitStrengthValue(ally);
                            float effectiveDistSq = Mathf.Max(distSq, MIN_DISTANCE * MIN_DISTANCE);
                            control += allyStrength / effectiveDistSq;
                        }
                    }
                    _controlGrid[x, z] = control;
                }
            }
        }

        private void ComputeTacticalInfo()
        {
            // 1. 중심점 계산
            ComputeCentroids();

            // 2. 전선 계산 (적/아군 중심 사이의 중간점)
            ComputeFrontline();

            // 3. 안전 구역 탐색
            FindSafeZones();

            // 4. 팀 위협도 계산
            ComputeTeamThreatLevel();
        }

        private void ComputeCentroids()
        {
            Vector3 enemySum = Vector3.zero;
            int enemyCount = 0;
            foreach (var enemy in _enemies)
            {
                if (enemy == null) continue;
                enemySum += enemy.Position;
                enemyCount++;
            }
            EnemyCentroid = enemyCount > 0 ? enemySum / enemyCount : Vector3.zero;

            Vector3 allySum = Vector3.zero;
            int allyCount = 0;
            foreach (var ally in _allies)
            {
                if (ally == null) continue;
                allySum += ally.Position;
                allyCount++;
            }
            AllyCentroid = allyCount > 0 ? allySum / allyCount : Vector3.zero;
        }

        /// <summary>
        /// Contact Line 기반 전선 계산
        /// 각 아군의 최근접 적과의 중간점들을 평균하여 실제 교전 위치 기반 전선 산출
        /// </summary>
        private void ComputeFrontline()
        {
            var contactPoints = new List<Vector3>();

            // 1. 각 아군-최근접 적 쌍의 접촉점 계산
            foreach (var ally in _allies)
            {
                if (ally == null) continue;

                UnitEntityData nearestEnemy = null;
                float nearestDist = float.MaxValue;

                foreach (var enemy in _enemies)
                {
                    if (enemy == null) continue;
                    float dist = Vector3.Distance(ally.Position, enemy.Position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestEnemy = enemy;
                    }
                }

                // 접촉 거리 20m 이내면 접촉 지점으로 인정
                if (nearestEnemy != null && nearestDist < 20f)
                {
                    Vector3 contactPoint = (ally.Position + nearestEnemy.Position) / 2f;
                    contactPoints.Add(contactPoint);
                }
            }

            // 2. 접촉 지점들의 평균 = 전선
            if (contactPoints.Count > 0)
            {
                Vector3 sum = Vector3.zero;
                foreach (var point in contactPoints)
                    sum += point;
                Frontline = sum / contactPoints.Count;
            }
            else
            {
                // 폴백: 기존 중간점 방식 (접촉이 없으면)
                Frontline = (EnemyCentroid + AllyCentroid) / 2f;
            }

            // 3. 전선 방향 계산 (아군→적)
            Vector3 direction = EnemyCentroid - AllyCentroid;
            FrontlineDirection = direction.magnitude > 0.01f ? direction.normalized : Vector3.forward;
        }

        private void FindSafeZones()
        {
            SafeZones.Clear();

            // ★ v0.2.35: 아군 중심점 근처에서 위협이 낮은 셀 탐색
            // IsWalkable 체크 제거 (성능 최적화)
            float maxDistSq = MAX_INFLUENCE_DISTANCE * MAX_INFLUENCE_DISTANCE;

            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridHeight; z++)
                {
                    if (_threatGrid[x, z] < SAFE_ZONE_THRESHOLD && _controlGrid[x, z] > 0)
                    {
                        Vector3 cellPos = GetCellWorldPosition(x, z);

                        // 아군 중심에서 너무 멀지 않은 위치만 (sqrMagnitude 사용)
                        float distSq = (cellPos - AllyCentroid).sqrMagnitude;
                        if (distSq < maxDistSq)
                        {
                            SafeZones.Add(cellPos);
                        }
                    }
                }
            }

            // 가장 안전한 10개 위치로 제한
            if (SafeZones.Count > 10)
            {
                SafeZones.Sort((a, b) =>
                {
                    float threatA = GetThreatAt(a);
                    float threatB = GetThreatAt(b);
                    return threatA.CompareTo(threatB);
                });
                SafeZones = SafeZones.GetRange(0, 10);
            }
        }

        private void ComputeTeamThreatLevel()
        {
            if (_allies.Count == 0)
            {
                TeamThreatLevel = 1.0f;
                return;
            }

            // 각 아군 위치의 평균 위협도
            float totalThreat = 0f;
            int count = 0;

            foreach (var ally in _allies)
            {
                if (ally == null) continue;
                totalThreat += GetThreatAt(ally.Position);
                count++;
            }

            // 정규화 (0-1 범위)
            float avgThreat = count > 0 ? totalThreat / count : 0f;
            TeamThreatLevel = Mathf.Clamp01(avgThreat / 10f);  // 10 = 높은 위협 기준값
        }

        #endregion

        #region Unit Value Calculation

        private float GetUnitThreatValue(UnitEntityData enemy)
        {
            if (enemy == null) return 0f;

            try
            {
                float baseThreat = 1.0f;

                // HP 비율에 따른 위협도 (죽어가는 적은 덜 위협적)
                float hpPercent = GetHPPercent(enemy);
                float hpFactor = hpPercent / 100f;
                baseThreat *= (0.5f + 0.5f * hpFactor);

                // 무기 타입에 따른 위협도
                bool hasRanged = HasRangedWeapon(enemy);
                if (hasRanged)
                {
                    baseThreat *= 1.3f;  // 원거리 적은 더 넓은 영역에 위협
                }

                return baseThreat;
            }
            catch
            {
                return 1.0f;
            }
        }

        private float GetUnitStrengthValue(UnitEntityData ally)
        {
            if (ally == null) return 0f;

            try
            {
                float baseStrength = 1.0f;

                // HP 비율에 따른 통제력
                float hpPercent = GetHPPercent(ally);
                float hpFactor = hpPercent / 100f;
                baseStrength *= (0.3f + 0.7f * hpFactor);

                return baseStrength;
            }
            catch
            {
                return 1.0f;
            }
        }

        private float GetHPPercent(UnitEntityData unit)
        {
            try
            {
                if (unit?.Stats?.HitPoints == null) return 100f;
                float current = unit.HPLeft;
                float max = unit.Stats.HitPoints.ModifiedValue;
                if (max <= 0) return 100f;
                return (current / max) * 100f;
            }
            catch
            {
                return 100f;
            }
        }

        private bool HasRangedWeapon(UnitEntityData unit)
        {
            try
            {
                var primaryWeapon = unit?.Body?.PrimaryHand?.Weapon;
                if (primaryWeapon != null)
                {
                    return primaryWeapon.Blueprint?.IsRanged ?? false;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Grid Helpers

        private Vector3 GetCellWorldPosition(int x, int z)
        {
            return new Vector3(
                _gridOrigin.x + x * CELL_SIZE + CELL_SIZE / 2f,
                0,
                _gridOrigin.z + z * CELL_SIZE + CELL_SIZE / 2f
            );
        }

        private bool WorldToGrid(Vector3 worldPos, out int x, out int z)
        {
            x = Mathf.FloorToInt((worldPos.x - _gridOrigin.x) / CELL_SIZE);
            z = Mathf.FloorToInt((worldPos.z - _gridOrigin.z) / CELL_SIZE);

            return x >= 0 && x < _gridWidth && z >= 0 && z < _gridHeight;
        }

        #endregion

        #region Public Query Methods

        /// <summary>
        /// 특정 위치의 적 위협도 조회 (O(1))
        /// </summary>
        public float GetThreatAt(Vector3 position)
        {
            if (!_isValid) return 0f;

            if (WorldToGrid(position, out int x, out int z))
            {
                return _threatGrid[x, z];
            }

            // 그리드 외부: 실시간 계산
            return CalculateThreatAtPosition(position);
        }

        /// <summary>
        /// 특정 위치의 아군 통제력 조회 (O(1))
        /// </summary>
        public float GetControlAt(Vector3 position)
        {
            if (!_isValid) return 0f;

            if (WorldToGrid(position, out int x, out int z))
            {
                return _controlGrid[x, z];
            }

            // 그리드 외부: 실시간 계산
            return CalculateControlAtPosition(position);
        }

        /// <summary>
        /// 전선까지의 거리
        /// 양수 = 적 방향 (전선 너머), 음수 = 아군 방향 (전선 뒤)
        /// </summary>
        public float GetFrontlineDistance(Vector3 position)
        {
            if (!_isValid) return 0f;
            if (FrontlineDirection.magnitude < 0.01f) return 0f;

            // 전선에서 위치까지의 투영 거리 (FrontlineDirection 사용)
            Vector3 toPos = position - Frontline;
            float dist = Vector3.Dot(toPos, FrontlineDirection);

            return dist;  // 양수=적 방향, 음수=아군 방향
        }

        /// <summary>
        /// 위치가 안전 구역인지 확인
        /// </summary>
        public bool IsSafeZone(Vector3 position, float tolerance = 3.0f)
        {
            foreach (var safe in SafeZones)
            {
                if (Vector3.Distance(position, safe) < tolerance)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 가장 가까운 안전 구역
        /// </summary>
        public Vector3? GetNearestSafeZone(Vector3 position)
        {
            if (SafeZones.Count == 0) return null;

            Vector3 nearest = SafeZones[0];
            float nearestDist = Vector3.Distance(position, nearest);

            for (int i = 1; i < SafeZones.Count; i++)
            {
                float dist = Vector3.Distance(position, SafeZones[i]);
                if (dist < nearestDist)
                {
                    nearest = SafeZones[i];
                    nearestDist = dist;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 위치의 영향력 균형 (Control - Threat, 양수=아군 우세)
        /// </summary>
        public float GetInfluenceBalance(Vector3 position)
        {
            return GetControlAt(position) - GetThreatAt(position);
        }

        /// <summary>
        /// 전술 점수 (종합)
        /// 값이 높을수록 좋은 위치
        /// </summary>
        public float GetCombinedScore(Vector3 position)
        {
            if (!_isValid) return 0.5f;

            float threat = GetThreatAt(position);

            // 위협도 정규화 (0-1 범위로, 10 = 높은 위협 기준)
            float normalizedThreat = Mathf.Clamp01(threat / 10f);

            // 낮은 위협 = 높은 점수
            float tacticalScore = 1f - normalizedThreat;

            return tacticalScore;
        }

        #endregion

        #region Fallback Calculations (그리드 외부용)

        private float CalculateThreatAtPosition(Vector3 position)
        {
            float threat = 0f;
            foreach (var enemy in _enemies)
            {
                if (enemy == null) continue;
                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < MAX_INFLUENCE_DISTANCE)
                {
                    float enemyThreat = GetUnitThreatValue(enemy);
                    float effectiveDist = Mathf.Max(dist, MIN_DISTANCE);
                    threat += enemyThreat / (effectiveDist * effectiveDist);
                }
            }
            return threat;
        }

        private float CalculateControlAtPosition(Vector3 position)
        {
            float control = 0f;
            foreach (var ally in _allies)
            {
                if (ally == null) continue;
                float dist = Vector3.Distance(position, ally.Position);
                if (dist < MAX_INFLUENCE_DISTANCE)
                {
                    float allyStrength = GetUnitStrengthValue(ally);
                    float effectiveDist = Mathf.Max(dist, MIN_DISTANCE);
                    control += allyStrength / (effectiveDist * effectiveDist);
                }
            }
            return control;
        }

        #endregion

        #region Debug

        public override string ToString()
        {
            if (!_isValid) return "[InfluenceMap] Invalid";

            return $"[InfluenceMap] Grid={_gridWidth}x{_gridHeight}, " +
                   $"Enemies={_enemies?.Count ?? 0}, Allies={_allies?.Count ?? 0}, " +
                   $"ThreatLevel={TeamThreatLevel:F2}, SafeZones={SafeZones.Count}";
        }

        #endregion
    }
}
