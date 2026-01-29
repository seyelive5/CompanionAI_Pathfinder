// ★ v0.2.30: TeamBlackboard - 팀 전술 정보 공유 시스템 (Pathfinder WotR 버전)
// RT 모드 v3.5.7에서 포팅 - Blackboard 패턴
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.Core
{
    /// <summary>
    /// ★ v0.2.30: 팀 전술 정보 공유 시스템 (Blackboard 패턴)
    ///
    /// 각 유닛의 상황과 계획을 공유하여 조율된 팀 전술을 가능하게 함.
    /// - 타겟 공유: DPS들이 같은 적 집중 공격
    /// - 전술 신호: 팀 HP에 따라 Attack/Defend/Retreat 전환
    /// - 역할 조율: Tank 도발 시 DPS가 후방 공격
    /// </summary>
    public class TeamBlackboard
    {
        #region Singleton

        private static TeamBlackboard _instance;
        public static TeamBlackboard Instance => _instance ??= new TeamBlackboard();

        private TeamBlackboard() { }

        #endregion

        #region Fields

        /// <summary>유닛별 상황 캐시 (UniqueId → Situation)</summary>
        private readonly Dictionary<string, Situation> _unitSituations = new Dictionary<string, Situation>();

        /// <summary>유닛별 계획 캐시 (UniqueId → TurnPlan)</summary>
        private readonly Dictionary<string, TurnPlan> _unitPlans = new Dictionary<string, TurnPlan>();

        /// <summary>현재 라운드 (캐시 무효화용)</summary>
        private int _currentRound = -1;

        #endregion

        #region Kill/Damage Tracking

        /// <summary>현재 라운드 킬 카운트</summary>
        private int _roundKillCount = 0;

        /// <summary>현재 라운드 가한 피해량</summary>
        private float _roundDamageDealt = 0f;

        /// <summary>현재 라운드 받은 피해량</summary>
        private float _roundDamageTaken = 0f;

        /// <summary>전투 전체 킬 카운트</summary>
        private int _combatKillCount = 0;

        /// <summary>전투 전체 가한 피해량</summary>
        private float _combatDamageDealt = 0f;

        /// <summary>킬 모멘텀 (0.0 ~ 1.0) - 최근 킬 성과 반영</summary>
        public float KillMomentum { get; private set; } = 0f;

        /// <summary>데미지 비율 (0.0 ~ 1.0) - 가한 피해 / (가한 + 받은)</summary>
        public float DamageRatio { get; private set; } = 0.5f;

        #endregion

        #region Action Reservation System

        /// <summary>도발 예약된 적 ID 집합</summary>
        private readonly HashSet<string> _reservedTauntTargets = new HashSet<string>();

        /// <summary>힐 예약된 아군 ID 집합</summary>
        private readonly HashSet<string> _reservedHealTargets = new HashSet<string>();

        /// <summary>버프 예약된 아군 ID 집합</summary>
        private readonly HashSet<string> _reservedBuffTargets = new HashSet<string>();

        #endregion

        #region Team Tactical State

        /// <summary>팀 공유 타겟 (가장 많이 지정된 적)</summary>
        public UnitEntityData SharedTarget { get; private set; }

        /// <summary>팀 평균 HP (%)</summary>
        public float AverageAllyHP { get; private set; }

        /// <summary>위험 상태 아군 수 (HP < 50%)</summary>
        public int LowHPAlliesCount { get; private set; }

        /// <summary>치명적 상태 아군 수 (HP < 30%)</summary>
        public int CriticalHPAlliesCount { get; private set; }

        /// <summary>현재 팀 전술 신호</summary>
        public TacticalSignal CurrentTactic { get; private set; } = TacticalSignal.Attack;

        /// <summary>팀 신뢰도 (0.0=절망 ~ 1.0=압도)</summary>
        public float TeamConfidence { get; private set; } = 0.5f;

        /// <summary>
        /// 신뢰도 값을 상태로 변환
        /// Heroic/Confident/Neutral/Worried/Panicked 상태 분류
        /// </summary>
        public ConfidenceState GetConfidenceState()
        {
            if (TeamConfidence > 0.8f) return ConfidenceState.Heroic;
            if (TeamConfidence > 0.6f) return ConfidenceState.Confident;
            if (TeamConfidence > 0.4f) return ConfidenceState.Neutral;
            if (TeamConfidence > 0.2f) return ConfidenceState.Worried;
            return ConfidenceState.Panicked;
        }

        /// <summary>전투 활성화 여부</summary>
        public bool IsCombatActive { get; private set; }

        /// <summary>등록된 유닛 수</summary>
        public int RegisteredUnitCount => _unitSituations.Count;

        #endregion

        #region Combat Lifecycle

        /// <summary>
        /// 전투 시작 시 호출 - Blackboard 초기화
        /// </summary>
        public void InitializeCombat()
        {
            Clear();
            IsCombatActive = true;
            _currentRound = 1;
            Main.Log("[TeamBlackboard] Combat initialized");
        }

        /// <summary>
        /// 전투 종료 시 호출 - 모든 데이터 정리
        /// </summary>
        public void Clear()
        {
            _unitSituations.Clear();
            _unitPlans.Clear();
            SharedTarget = null;
            AverageAllyHP = 100f;
            LowHPAlliesCount = 0;
            CriticalHPAlliesCount = 0;
            CurrentTactic = TacticalSignal.Attack;
            TeamConfidence = 0.5f;
            IsCombatActive = false;
            _currentRound = -1;

            // Kill/Damage 추적 초기화
            _roundKillCount = 0;
            _roundDamageDealt = 0f;
            _roundDamageTaken = 0f;
            _combatKillCount = 0;
            _combatDamageDealt = 0f;
            KillMomentum = 0f;
            DamageRatio = 0.5f;

            // 역할 예약 초기화
            ClearReservations();

            // ★ v0.2.31: InfluenceMap 캐시도 무효화
            BattlefieldInfluenceMap.InvalidateCache();

            Main.Verbose("[TeamBlackboard] Cleared");
        }

        /// <summary>
        /// 라운드 시작 시 호출 - 라운드별 통계 리셋 + 모멘텀 계산
        /// </summary>
        public void OnRoundStart(int roundNumber)
        {
            // 이전 라운드 킬 기반 모멘텀 계산 (킬당 0.25, 최대 1.0)
            if (_currentRound > 0)
            {
                KillMomentum = Math.Min(1f, _roundKillCount * 0.25f);
                Main.Verbose($"[TeamBlackboard] Round {_currentRound} end: Kills={_roundKillCount}, Momentum={KillMomentum:F2}");
            }

            // 데미지 비율 업데이트
            float totalDamage = _combatDamageDealt + _roundDamageTaken + 1f; // +1 to avoid div/0
            DamageRatio = _combatDamageDealt / totalDamage;

            // 라운드 카운터 리셋
            _currentRound = roundNumber;
            _roundKillCount = 0;
            _roundDamageDealt = 0f;
            _roundDamageTaken = 0f;

            // 새 라운드 시작 시 역할 예약 초기화
            ClearReservations();

            Main.Log($"[TeamBlackboard] Round {roundNumber} started. Combat kills={_combatKillCount}, DmgRatio={DamageRatio:F2}");
        }

        /// <summary>
        /// 킬 기록
        /// </summary>
        public void RecordKill(UnitEntityData enemy)
        {
            if (enemy == null) return;

            _roundKillCount++;
            _combatKillCount++;

            // 킬 시 즉시 모멘텀 보너스 (+0.15)
            KillMomentum = Math.Min(1f, KillMomentum + 0.15f);

            Main.Log($"[TeamBlackboard] Kill recorded: {enemy.CharacterName}. Round kills={_roundKillCount}, Momentum={KillMomentum:F2}");

            // 팀 상태 재평가
            UpdateTeamAssessment();
        }

        /// <summary>
        /// 가한 피해량 기록
        /// </summary>
        public void RecordDamageDealt(float damage)
        {
            if (damage <= 0) return;

            _roundDamageDealt += damage;
            _combatDamageDealt += damage;

            Main.Verbose($"[TeamBlackboard] Damage dealt: {damage:F0}. Round total={_roundDamageDealt:F0}");
        }

        /// <summary>
        /// 받은 피해량 기록
        /// </summary>
        public void RecordDamageTaken(float damage)
        {
            if (damage <= 0) return;

            _roundDamageTaken += damage;

            Main.Verbose($"[TeamBlackboard] Damage taken: {damage:F0}. Round total={_roundDamageTaken:F0}");
        }

        #endregion

        #region Registration Methods

        /// <summary>
        /// 유닛의 상황 분석 결과 등록
        /// </summary>
        public void RegisterUnitSituation(string unitId, Situation situation)
        {
            if (string.IsNullOrEmpty(unitId) || situation == null) return;

            _unitSituations[unitId] = situation;
            Main.Verbose($"[TeamBlackboard] Registered situation for {situation.Unit?.CharacterName}");
        }

        /// <summary>
        /// 유닛의 턴 계획 등록 + 팀 상태 업데이트
        /// </summary>
        public void RegisterUnitPlan(string unitId, TurnPlan plan)
        {
            if (string.IsNullOrEmpty(unitId) || plan == null) return;

            _unitPlans[unitId] = plan;

            // 계획 등록 시마다 팀 상태 재계산
            UpdateTeamAssessment();

            Main.Verbose($"[TeamBlackboard] Registered plan for {unitId}, Tactic={CurrentTactic}");
        }

        #endregion

        #region Team Assessment

        /// <summary>마지막 팀 상태 업데이트 시간</summary>
        private float _lastAssessmentTime;

        /// <summary>팀 상태 업데이트 최소 간격 (초)</summary>
        /// <summary>★ v0.2.32: 0.5→1.5초로 증가 (성능 최적화)</summary>
        private const float ASSESSMENT_INTERVAL = 1.5f;

        /// <summary>
        /// 팀 전체 상태 평가 및 전술 신호 결정
        /// ★ v0.2.31: 스로틀링 적용 - 0.5초마다만 업데이트
        /// </summary>
        public void UpdateTeamAssessment()
        {
            if (_unitSituations.Count == 0) return;

            // ★ v0.2.31: 스로틀링 - 너무 자주 호출되면 스킵
            float currentTime = UnityEngine.Time.time;
            if (currentTime - _lastAssessmentTime < ASSESSMENT_INTERVAL)
            {
                return;
            }
            _lastAssessmentTime = currentTime;

            // 1. 팀 HP 계산
            CalculateTeamHP();

            // 2. 공유 타겟 결정
            DetermineSharedTarget();

            // 3. 전술 신호 결정
            DetermineTacticalSignal();

            // 4. 팀 신뢰도 계산
            CalculateConfidence();

            Main.Verbose($"[TeamBlackboard] Team: AvgHP={AverageAllyHP:F0}%, " +
                $"LowHP={LowHPAlliesCount}, Critical={CriticalHPAlliesCount}, " +
                $"Tactic={CurrentTactic}, Confidence={TeamConfidence:F2} ({GetConfidenceState()}), " +
                $"Target={SharedTarget?.CharacterName ?? "None"}");
        }

        private void CalculateTeamHP()
        {
            float totalHP = 0f;
            int allyCount = 0;
            int lowHPCount = 0;
            int criticalCount = 0;

            foreach (var kvp in _unitSituations)
            {
                var situation = kvp.Value;
                if (situation?.Unit == null) continue;

                float hp = situation.HPPercent;
                totalHP += hp;
                allyCount++;

                if (hp < 50f) lowHPCount++;
                if (hp < 30f) criticalCount++;
            }

            AverageAllyHP = allyCount > 0 ? totalHP / allyCount : 100f;
            LowHPAlliesCount = lowHPCount;
            CriticalHPAlliesCount = criticalCount;
        }

        private void DetermineSharedTarget()
        {
            // 각 유닛의 BestTarget을 집계하여 가장 많이 지정된 적 선택
            var targetCounts = new Dictionary<UnitEntityData, int>();

            foreach (var kvp in _unitSituations)
            {
                var target = kvp.Value?.BestTarget;
                if (target == null || target.HPLeft <= 0) continue;

                if (!targetCounts.ContainsKey(target))
                    targetCounts[target] = 0;
                targetCounts[target]++;
            }

            if (targetCounts.Count == 0)
            {
                SharedTarget = null;
                return;
            }

            // 가장 많이 선택된 타겟 (동률 시 HP 낮은 적 우선)
            SharedTarget = targetCounts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => GetHPPercent(kvp.Key))
                .First().Key;
        }

        private void DetermineTacticalSignal()
        {
            // 전술 결정 로직:
            // - Attack: 팀이 건강하고 위험 아군이 적음
            // - Defend: 일부 아군이 위험하지만 전투 가능
            // - Retreat: 팀이 위험 상태

            if (AverageAllyHP >= 60f && LowHPAlliesCount <= 1)
            {
                CurrentTactic = TacticalSignal.Attack;
            }
            else if (AverageAllyHP >= 40f && CriticalHPAlliesCount <= 1)
            {
                CurrentTactic = TacticalSignal.Defend;
            }
            else
            {
                CurrentTactic = TacticalSignal.Retreat;
            }
        }

        /// <summary>
        /// 팀 신뢰도 계산
        /// Confidence = AllyHP(30%) + EnemyDamage(25%) + NumberAdvantage(20%) + KillMomentum(15%) + DamageRatio(10%)
        /// </summary>
        private void CalculateConfidence()
        {
            if (_unitSituations.Count == 0)
            {
                TeamConfidence = 0.5f;
                return;
            }

            // 1. 아군 HP 비율 (0-1)
            float allyHPFactor = AverageAllyHP / 100f;

            // 2. 적 피해 비율 (0-1) - 적 HP가 낮을수록 높음
            float avgEnemyHP = GetAverageEnemyHP();
            float enemyDamageFactor = (100f - avgEnemyHP) / 100f;

            // 3. 수적 우위 (0-1)
            int allyCount = _unitSituations.Count;
            int enemyCount = GetTotalEnemyCount();
            float numberFactor = Math.Min(1f, Math.Max(0f, (float)allyCount / (enemyCount + 0.1f) * 0.5f));

            // 4. 킬 모멘텀 (0-1)
            float momentumFactor = KillMomentum;

            // 5. 데미지 비율 (0-1)
            float damageRatioFactor = DamageRatio;

            // 가중치 합산 (30% + 25% + 20% + 15% + 10% = 100%)
            TeamConfidence = Math.Min(1f, Math.Max(0f,
                allyHPFactor * 0.30f +
                enemyDamageFactor * 0.25f +
                numberFactor * 0.20f +
                momentumFactor * 0.15f +
                damageRatioFactor * 0.10f
            ));

            Main.Verbose($"[TeamBlackboard] Confidence={TeamConfidence:F2} ({GetConfidenceState()}) " +
                $"(AllyHP={allyHPFactor:F2}, EnemyDmg={enemyDamageFactor:F2}, Numbers={numberFactor:F2}, " +
                $"Momentum={momentumFactor:F2}, DmgRatio={damageRatioFactor:F2})");
        }

        private float GetAverageEnemyHP()
        {
            var enemies = _unitSituations.Values
                .Where(s => s?.Enemies != null && s.Enemies.Count > 0)
                .SelectMany(s => s.Enemies)
                .Where(e => e != null && e.HPLeft > 0)
                .Distinct()
                .ToList();

            if (enemies.Count == 0) return 50f;
            return enemies.Average(e => GetHPPercent(e));
        }

        private int GetTotalEnemyCount()
        {
            return _unitSituations.Values
                .Where(s => s?.Enemies != null && s.Enemies.Count > 0)
                .SelectMany(s => s.Enemies)
                .Where(e => e != null && e.HPLeft > 0)
                .Distinct()
                .Count();
        }

        /// <summary>
        /// HP 퍼센트 계산 (Pathfinder WotR)
        /// </summary>
        private float GetHPPercent(UnitEntityData unit)
        {
            if (unit == null) return 0f;
            int maxHP = unit.MaxHP;
            if (maxHP <= 0) return 0f;
            return (float)unit.HPLeft / maxHP * 100f;
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// 특정 유닛의 캐시된 상황 조회
        /// </summary>
        public Situation GetUnitSituation(string unitId)
        {
            return _unitSituations.TryGetValue(unitId, out var situation) ? situation : null;
        }

        /// <summary>
        /// 특정 유닛의 캐시된 계획 조회
        /// </summary>
        public TurnPlan GetUnitPlan(string unitId)
        {
            return _unitPlans.TryGetValue(unitId, out var plan) ? plan : null;
        }

        /// <summary>
        /// 팀에 힐러가 있는지 확인
        /// </summary>
        public bool HasActiveHealer()
        {
            foreach (var kvp in _unitSituations)
            {
                var situation = kvp.Value;
                if (situation?.AvailableHeals?.Count > 0 && situation.HPPercent > 30f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 가장 위험한 아군 조회 (힐 우선순위)
        /// </summary>
        public UnitEntityData GetMostWoundedAlly()
        {
            UnitEntityData mostWounded = null;
            float lowestHP = float.MaxValue;

            foreach (var kvp in _unitSituations)
            {
                var situation = kvp.Value;
                if (situation?.Unit == null) continue;

                if (situation.HPPercent < lowestHP)
                {
                    lowestHP = situation.HPPercent;
                    mostWounded = situation.Unit;
                }
            }

            return mostWounded;
        }

        /// <summary>
        /// 특정 적을 타겟으로 지정한 아군 수
        /// </summary>
        public int CountAlliesTargeting(UnitEntityData enemy)
        {
            if (enemy == null) return 0;

            int count = 0;
            foreach (var kvp in _unitSituations)
            {
                if (kvp.Value?.BestTarget == enemy)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 팀 공유 타겟 강제 설정 (Tank 도발 등)
        /// </summary>
        public void SetSharedTarget(UnitEntityData target)
        {
            if (target == null || target.HPLeft <= 0) return;
            SharedTarget = target;
            Main.Log($"[TeamBlackboard] Shared target set: {target.CharacterName}");
        }

        /// <summary>
        /// 현재 전술 신호가 후퇴인지 확인
        /// </summary>
        public bool ShouldRetreat()
        {
            return CurrentTactic == TacticalSignal.Retreat;
        }

        /// <summary>
        /// 현재 전술 신호가 방어인지 확인
        /// </summary>
        public bool ShouldDefend()
        {
            return CurrentTactic == TacticalSignal.Defend || CurrentTactic == TacticalSignal.Retreat;
        }

        #endregion

        #region Action Reservation API

        /// <summary>
        /// 도발 대상 예약 (중복 도발 방지)
        /// </summary>
        /// <param name="target">도발할 적</param>
        /// <returns>예약 성공 여부 (이미 예약된 경우 false)</returns>
        public bool ReserveTaunt(UnitEntityData target)
        {
            if (target == null) return false;

            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            if (_reservedTauntTargets.Contains(id))
            {
                Main.Verbose($"[Blackboard] Taunt already reserved: {target.CharacterName}");
                return false;
            }

            _reservedTauntTargets.Add(id);
            Main.Log($"[Blackboard] Taunt reserved: {target.CharacterName}");
            return true;
        }

        /// <summary>
        /// 힐 대상 예약 (중복 힐 방지)
        /// </summary>
        /// <param name="target">힐할 아군</param>
        /// <returns>예약 성공 여부 (이미 예약된 경우 false)</returns>
        public bool ReserveHeal(UnitEntityData target)
        {
            if (target == null) return false;

            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            if (_reservedHealTargets.Contains(id))
            {
                Main.Verbose($"[Blackboard] Heal already reserved: {target.CharacterName}");
                return false;
            }

            _reservedHealTargets.Add(id);
            Main.Log($"[Blackboard] Heal reserved: {target.CharacterName}");
            return true;
        }

        /// <summary>
        /// 버프 대상 예약 (중복 버프 방지)
        /// </summary>
        public bool ReserveBuff(UnitEntityData target, string buffName)
        {
            if (target == null) return false;

            string id = (target.UniqueId ?? target.CharacterName ?? "unknown") + "_" + buffName;
            if (_reservedBuffTargets.Contains(id))
            {
                Main.Verbose($"[Blackboard] Buff already reserved: {target.CharacterName} - {buffName}");
                return false;
            }

            _reservedBuffTargets.Add(id);
            Main.Log($"[Blackboard] Buff reserved: {target.CharacterName} - {buffName}");
            return true;
        }

        /// <summary>
        /// 도발 예약 여부 확인
        /// </summary>
        public bool IsTauntReserved(UnitEntityData target)
        {
            if (target == null) return false;
            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            return _reservedTauntTargets.Contains(id);
        }

        /// <summary>
        /// 힐 예약 여부 확인
        /// </summary>
        public bool IsHealReserved(UnitEntityData target)
        {
            if (target == null) return false;
            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            return _reservedHealTargets.Contains(id);
        }

        /// <summary>
        /// 버프 예약 여부 확인
        /// </summary>
        public bool IsBuffReserved(UnitEntityData target, string buffName)
        {
            if (target == null) return false;
            string id = (target.UniqueId ?? target.CharacterName ?? "unknown") + "_" + buffName;
            return _reservedBuffTargets.Contains(id);
        }

        /// <summary>
        /// 모든 예약 초기화 (라운드 시작 시)
        /// </summary>
        public void ClearReservations()
        {
            int tauntCount = _reservedTauntTargets.Count;
            int healCount = _reservedHealTargets.Count;
            int buffCount = _reservedBuffTargets.Count;

            _reservedTauntTargets.Clear();
            _reservedHealTargets.Clear();
            _reservedBuffTargets.Clear();

            if (tauntCount > 0 || healCount > 0 || buffCount > 0)
            {
                Main.Verbose($"[Blackboard] Reservations cleared: {tauntCount} taunts, {healCount} heals, {buffCount} buffs");
            }
        }

        #endregion

        #region Debug

        public override string ToString()
        {
            return $"[TeamBlackboard] Units={_unitSituations.Count}, " +
                   $"AvgHP={AverageAllyHP:F0}%, Tactic={CurrentTactic}, Confidence={TeamConfidence:F2}, " +
                   $"Target={SharedTarget?.CharacterName ?? "None"}";
        }

        /// <summary>
        /// 디버그용 상태 출력
        /// </summary>
        public string GetDebugInfo()
        {
            return $"[TeamBlackboard] Units={_unitSituations.Count}, AvgHP={AverageAllyHP:F0}%, " +
                   $"Tactic={CurrentTactic}, Confidence={TeamConfidence:F2} ({GetConfidenceState()}), " +
                   $"SharedTarget={SharedTarget?.CharacterName ?? "None"}, " +
                   $"Kills={_combatKillCount}, Momentum={KillMomentum:F2}";
        }

        #endregion
    }

    /// <summary>
    /// 팀 전술 신호
    /// </summary>
    public enum TacticalSignal
    {
        /// <summary>공격적 - 버프 스킵, 즉시 공격</summary>
        Attack,

        /// <summary>방어적 - 힐/버프 우선, 신중한 공격</summary>
        Defend,

        /// <summary>철수 - 힐/후퇴 우선, 생존 최우선</summary>
        Retreat
    }

    /// <summary>
    /// 팀 신뢰도 상태
    /// TeamConfidence 값에 따른 전술 상태 분류
    /// </summary>
    public enum ConfidenceState
    {
        /// <summary>영웅적 (>0.8) - 측면 공격, 공격적 포지셔닝, 적극 추격</summary>
        Heroic,

        /// <summary>자신감 (0.6~0.8) - 지속 공격, 이니셔티브 유지</summary>
        Confident,

        /// <summary>중립 (0.4~0.6) - 현 위치 유지, 기회주의적 공격</summary>
        Neutral,

        /// <summary>우려 (0.2~0.4) - 후퇴 고려, 방어적 행동, 엄폐 우선</summary>
        Worried,

        /// <summary>공황 (≤0.2) - 즉시 엄폐/후퇴, 생존 최우선</summary>
        Panicked
    }
}
