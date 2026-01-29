using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Utility;
using CompanionAI_Pathfinder.Abstraction;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Core.DecisionEngine;
using CompanionAI_Pathfinder.Scoring;
using UnityEngine;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// Real-time combat AI controller
    /// v0.2.0: 계획 시스템 통합 - SituationAnalyzer, TargetScorer, Role-based 결정
    /// </summary>
    public class RealTimeController
    {
        #region Constants

        // Decision interval (seconds)
        private const float DECISION_INTERVAL = 0.5f;

        // ★ v0.2.32: 위상 분산 - 유닛별 최대 오프셋 (0~0.45초 분산)
        private const float MAX_PHASE_OFFSET = 0.45f;
        private const int PHASE_DIVISIONS = 10;  // 10개 위상으로 분산

        // ★ v0.2.32: 시간 예산 - 프레임당 최대 AI 처리 시간 (ms)
        private const float MAX_AI_TIME_MS = 3.0f;

        // Max engagement distance (meters)
        private const float MAX_ENGAGEMENT_DISTANCE = 30f;

        // RangePreference distance constants
        private const float MELEE_RANGE = 2f;
        private const float RANGED_MIN_DIST = 6f;
        private const float RANGED_OPTIMAL_DIST = 10f;
        private const float RETREAT_DISTANCE = 5f;

        // HP thresholds
        private const float HP_EMERGENCY_THRESHOLD = 30f;
        private const float HP_HEAL_ALLY_THRESHOLD = 50f;

        #endregion

        #region State

        // Last decision time per unit
        private readonly Dictionary<string, float> _lastDecisionTime = new Dictionary<string, float>();

        // ★ v0.2.32: 유닛별 위상 오프셋 (Phase Staggering)
        // 캐시: 유닛 ID → 오프셋 값
        private readonly Dictionary<string, float> _unitPhaseOffsets = new Dictionary<string, float>();

        // ★ v0.2.32: 시간 예산 시스템
        // 프레임당 AI 처리 시간 제한용 스톱워치
        private readonly Stopwatch _frameBudget = new Stopwatch();
        private int _lastBudgetFrame = -1;  // 마지막으로 스톱워치 리셋한 프레임

        // ★ v0.2.32: 처리 못한 유닛 대기열
        private readonly Queue<string> _pendingProcessUnits = new Queue<string>();

        // v0.2.1: Ability cooldown tracking (prevent free ability spam)
        // Key: "unitId:abilityGuid", Value: last use time
        private readonly Dictionary<string, float> _abilityCooldowns = new Dictionary<string, float>();
        private const float FREE_ABILITY_COOLDOWN = 6f; // 6초 쿨다운 (무료 능력)
        // v0.2.6: DEMORALIZE_COOLDOWN 제거됨 - Demoralize는 완전 비활성화

        // v0.2.2: Pending buff queue (units that left combat with commands in progress)
        private readonly HashSet<string> _pendingBuffUnits = new HashSet<string>();
        private const float PENDING_BUFF_RETRY_INTERVAL = 1.0f;
        private readonly Dictionary<string, float> _pendingBuffLastCheck = new Dictionary<string, float>();

        // Statistics
        private int _processCount = 0;
        private int _attackCount = 0;
        private int _moveCount = 0;
        private int _healCount = 0;
        private int _buffCount = 0;
        private int _debuffCount = 0;

        // Dependencies
        private readonly SituationAnalyzer _analyzer;

        #endregion

        #region Singleton

        private static RealTimeController _instance;
        public static RealTimeController Instance => _instance ?? (_instance = new RealTimeController());

        private RealTimeController()
        {
            _analyzer = new SituationAnalyzer();
        }

        #endregion

        #region Main Entry Point

        /// <summary>
        /// Process unit's real-time AI
        /// ★ v0.2.30: TeamBlackboard 통합
        /// ★ v0.2.32: 위상 분산 + 시간 예산 시스템
        /// </summary>
        public void ProcessUnit(UnitEntityData unit)
        {
            if (unit == null)
                return;

            // ★ v0.2.32: 시간 예산 체크 - 프레임당 처리 시간 제한
            if (!CheckAndUpdateTimeBudget())
            {
                // 시간 예산 초과 - 대기열에 추가
                if (unit.UniqueId != null && !_pendingProcessUnits.Contains(unit.UniqueId))
                {
                    _pendingProcessUnits.Enqueue(unit.UniqueId);
                }
                return;
            }

            // ★ v0.2.32: CombatDataCache 갱신 (프레임당 1회)
            CombatDataCache.Instance.RefreshIfNeeded();

            // ★ v0.2.30: TeamBlackboard 전투 상태 업데이트
            UpdateTeamBlackboardCombatState(unit);

            // v0.2.2: 대기 중인 영구 버프 처리
            ProcessPendingBuffs();

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string unitId = unit.UniqueId;
            float currentTime = Time.time;

            // ★ v0.2.32: 위상 분산 적용된 Decision throttling
            float phaseOffset = GetUnitPhaseOffset(unitId);
            float effectiveInterval = DECISION_INTERVAL + phaseOffset;

            if (_lastDecisionTime.TryGetValue(unitId, out float lastTime))
            {
                if (currentTime - lastTime < effectiveInterval)
                    return;
            }

            _processCount++;
            Main.Verbose($"[RT] ProcessUnit: {unitName} (#{_processCount}, phase={phaseOffset:F2}s)");

            // v0.2.21: NextCommandTime과 능력 시전 체크는 CustomBrainPatch.TickBrain_Prefix에서 수행
            // 여기서는 중복 체크하지 않음

            // Check if can act
            if (!(unit.Descriptor?.State?.CanAct ?? false))
            {
                Main.Verbose($"[RT] {unitName}: Cannot act");
                return;
            }

            // Check combat state
            if (!(unit.CombatState?.IsInCombat ?? false))
            {
                // v0.2.2: 전투 외 상황에서 영구 버프 자동 적용
                ApplyPermanentBuffsOutOfCombat(unit);
                Main.Verbose($"[RT] {unitName}: Not in combat, checked permanent buffs");
                return;
            }

            try
            {
                ExecuteAIDecision(unit);
                _lastDecisionTime[unitId] = currentTime;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] ProcessUnit error ({unitName}): {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// ★ v0.2.32: 유닛별 위상 오프셋 계산
        /// 유닛 ID 해시를 기반으로 0~MAX_PHASE_OFFSET 범위의 오프셋 생성
        /// 모든 유닛이 동시에 업데이트되는 것을 방지
        /// </summary>
        private float GetUnitPhaseOffset(string unitId)
        {
            if (string.IsNullOrEmpty(unitId))
                return 0f;

            // 캐시 확인
            if (_unitPhaseOffsets.TryGetValue(unitId, out float cachedOffset))
                return cachedOffset;

            // 해시 기반 오프셋 계산
            int hash = unitId.GetHashCode();
            int phaseIndex = Math.Abs(hash) % PHASE_DIVISIONS;
            float offset = (phaseIndex / (float)PHASE_DIVISIONS) * MAX_PHASE_OFFSET;

            // 캐시 저장
            _unitPhaseOffsets[unitId] = offset;

            return offset;
        }

        /// <summary>
        /// ★ v0.2.32: 시간 예산 확인 및 업데이트
        /// 새 프레임이면 스톱워치 리셋, 예산 초과면 false 반환
        /// </summary>
        private bool CheckAndUpdateTimeBudget()
        {
            int currentFrame = Time.frameCount;

            // 새 프레임이면 스톱워치 리셋 + 대기열 처리
            if (currentFrame != _lastBudgetFrame)
            {
                _frameBudget.Restart();
                _lastBudgetFrame = currentFrame;

                // 이전 프레임에서 처리 못한 유닛 먼저 처리
                ProcessPendingUnitsQueue();
            }

            // 시간 예산 확인
            return _frameBudget.ElapsedMilliseconds < MAX_AI_TIME_MS;
        }

        /// <summary>
        /// ★ v0.2.32: 대기열 처리 - 이전 프레임에서 처리 못한 유닛
        /// </summary>
        private void ProcessPendingUnitsQueue()
        {
            if (_pendingProcessUnits.Count == 0) return;

            int processed = 0;
            while (_pendingProcessUnits.Count > 0 && _frameBudget.ElapsedMilliseconds < MAX_AI_TIME_MS)
            {
                var unitId = _pendingProcessUnits.Dequeue();

                // 유닛 찾기
                var unit = Kingmaker.Game.Instance?.State?.Units?
                    .FirstOrDefault(u => u?.UniqueId == unitId);

                if (unit != null && unit.HPLeft > 0 && (unit.CombatState?.IsInCombat ?? false))
                {
                    try
                    {
                        ExecuteAIDecision(unit);
                        _lastDecisionTime[unitId] = Time.time;
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        Main.Verbose($"[RT] Pending queue process error: {ex.Message}");
                    }
                }
            }

            if (processed > 0)
            {
                Main.Verbose($"[RT] Processed {processed} pending units from queue");
            }
        }

        #endregion

        #region AI Decision

        /// <summary>
        /// Execute AI decision using Unified Decision Engine
        /// ★ v0.2.22: 역할별 로직 대신 통합 Utility Scoring 시스템 사용
        /// </summary>
        private void ExecuteAIDecision(UnitEntityData unit)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                // ★ v0.2.22: UnifiedDecisionEngine으로 최적 행동 결정
                var bestAction = UnifiedDecisionEngine.Instance.DecideAction(unit, (TurnState)null);

                if (bestAction == null || bestAction.ActionType == CandidateType.EndTurn)
                {
                    Main.Verbose($"[RT] {unitName}: No action (EndTurn)");
                    return;
                }

                // 행동 실행
                ExecuteActionCandidate(unit, bestAction);
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] {unitName}: Decision error - {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.22: ActionCandidate 실행
        /// </summary>
        private void ExecuteActionCandidate(UnitEntityData unit, ActionCandidate action)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                switch (action.ActionType)
                {
                    case CandidateType.AbilityAttack:
                        ExecuteAbilityAction(unit, action);
                        _attackCount++;
                        break;

                    case CandidateType.BasicAttack:
                        ExecuteBasicAttack(unit, action.Target);
                        _attackCount++;
                        break;

                    case CandidateType.Buff:
                        ExecuteAbilityAction(unit, action);
                        _buffCount++;
                        break;

                    case CandidateType.Heal:
                        ExecuteAbilityAction(unit, action);
                        _healCount++;
                        break;

                    case CandidateType.Debuff:
                        ExecuteAbilityAction(unit, action);
                        _debuffCount++;
                        break;

                    case CandidateType.Move:
                        if (action.MoveDestination.HasValue)
                        {
                            ExecuteMoveAction(unit, action.MoveDestination.Value, action.Reason);
                            _moveCount++;
                        }
                        break;

                    case CandidateType.EndTurn:
                    default:
                        // 아무 것도 안함
                        break;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] {unitName}: Execute error - {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.22: 능력 실행
        /// </summary>
        private void ExecuteAbilityAction(UnitEntityData unit, ActionCandidate action)
        {
            if (action.Ability == null || action.Target == null)
            {
                Main.Verbose($"[RT] Ability action missing ability or target");
                return;
            }

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string targetName = action.Target.Descriptor?.CharacterName ?? "Unknown";

            var targetWrapper = new TargetWrapper(action.Target);
            if (!action.Ability.CanTarget(targetWrapper))
            {
                Main.Verbose($"[RT] {unitName}: Cannot target {targetName} with {action.Ability.Name}");
                return;
            }

            try
            {
                var command = new UnitUseAbility(action.Ability, targetWrapper);
                unit.Commands.Run(command);
                SetNextCommandTime(unit, command);
                RecordAbilityUse(unit, action.Ability);

                // ★ v0.2.23: Register pending buff to prevent duplicate casting
                if (action.ActionType == CandidateType.Buff)
                {
                    PendingActionTracker.Instance.RegisterPendingBuff(action.Ability, action.Target, unit);
                }

                Main.Log($"[RT] ★ {unitName} -> {targetName}: {action.ActionType} ({action.Ability.Name}) Score={action.FinalScore:F1}");
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Ability execution failed: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.22: 기본 공격 실행
        /// </summary>
        private void ExecuteBasicAttack(UnitEntityData unit, UnitEntityData target)
        {
            if (target == null) return;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string targetName = target.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                var command = new UnitAttack(target);
                unit.Commands.Run(command);
                SetNextCommandTime(unit, command);

                Main.Log($"[RT] ★ {unitName} -> {targetName}: BasicAttack");
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Basic attack failed: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.30: 이동 실행 - BattlefieldGrid 위치 검증 추가
        /// </summary>
        private void ExecuteMoveAction(UnitEntityData unit, Vector3 destination, string reason)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                // ★ v0.2.30: BattlefieldGrid로 목표 위치 사전 검증
                if (!BattlefieldGrid.Instance.ValidateTargetPosition(unit, destination))
                {
                    Main.Log($"[RT] {unitName}: Move target invalid ({destination.x:F1},{destination.z:F1}) - not walkable or occupied");
                    return;
                }

                var command = new UnitMoveTo(destination, 0f);
                unit.Commands.Run(command);
                SetNextCommandTime(unit, command);

                Main.Log($"[RT] -> {unitName}: Move ({reason})");
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Move failed: {ex.Message}");
            }
        }

        #endregion

        #region Legacy Role-based Logic (Deprecated - kept for reference)

        #region Role-based Logic

        /// <summary>
        /// DPS: Focus on damage output
        /// Priority: Emergency Heal > Debuff > Attack > Buff > Move
        /// </summary>
        private void ExecuteDPSLogic(UnitEntityData unit, Situation situation, RangePreference preference)
        {
            string unitName = situation.Unit?.Descriptor?.CharacterName ?? "Unknown";

            // 1. Emergency self-heal
            if (situation.HPPercent < HP_EMERGENCY_THRESHOLD)
            {
                if (TryUseHealAbility(unit, situation, unit))
                {
                    Main.Log($"[RT] {unitName}: DPS emergency heal");
                    return;
                }
            }

            // v0.2.3: Emergency ally heal (DPS도 위급한 아군 힐)
            var criticalAlly = FindMostWoundedAlly(situation);
            if (criticalAlly != null && GetHPPercent(criticalAlly) < HP_EMERGENCY_THRESHOLD)
            {
                if (TryUseHealAbility(unit, situation, criticalAlly))
                {
                    string allyName = criticalAlly.Descriptor?.CharacterName ?? "Unknown";
                    Main.Log($"[RT] {unitName}: DPS emergency ally heal -> {allyName}");
                    return;
                }
            }

            // 2. Select best target using TargetScorer
            var target = SelectBestTarget(situation, AIRole.DPS);
            if (target == null)
            {
                // v0.2.8: 전투 중일 때만 이동, 그리고 가까운 적만
                if (situation.NearestEnemy != null && !situation.HasHittableEnemies &&
                    situation.NearestEnemyDistance <= MAX_ENGAGEMENT_DISTANCE)
                {
                    Main.Log($"[RT] {unitName}: No hittable targets, approaching nearest enemy (dist={situation.NearestEnemyDistance:F1}m)");
                    TryMoveTowardsEnemy(unit, situation.NearestEnemy);
                    return;
                }
                // No enemy: try buff
                if (TryUseBuffAbility(unit, situation))
                    return;
                return;
            }

            float dist = Vector3.Distance(unit.Position, target.Position);

            // 3. Try debuff on high-value target (if target HP > 70%)
            float targetHP = GetHPPercent(target);
            if (targetHP > 70f && situation.AvailableDebuffs?.Count > 0)
            {
                if (TryUseDebuffAbility(unit, situation, target))
                    return;
            }

            // 4. Attack or move based on range preference
            ExecuteRangeBasedAction(unit, situation, target, dist, preference);
        }

        /// <summary>
        /// Tank: Focus on survival and threat generation
        /// Priority: Emergency Heal > Buff > Taunt/Debuff > Attack > Move to front
        /// </summary>
        private void ExecuteTankLogic(UnitEntityData unit, Situation situation, RangePreference preference)
        {
            string unitName = situation.Unit?.Descriptor?.CharacterName ?? "Unknown";

            // 1. Emergency self-heal (higher threshold for tank)
            if (situation.HPPercent < 40f)
            {
                if (TryUseHealAbility(unit, situation, unit))
                {
                    Main.Log($"[RT] {unitName}: Tank emergency heal");
                    return;
                }
            }

            // 2. Self-buff for survival
            if (TryUseBuffAbility(unit, situation))
                return;

            // 3. Find nearest threat (prioritize enemies attacking allies)
            var target = SelectBestTarget(situation, AIRole.Tank);
            if (target == null)
            {
                // v0.2.8: Tank은 가까운 적이 있을 때만 전선으로 이동
                if (situation.NearestEnemy != null &&
                    situation.NearestEnemyDistance <= MAX_ENGAGEMENT_DISTANCE)
                {
                    Main.Log($"[RT] {unitName}: Tank advancing to front line (dist={situation.NearestEnemyDistance:F1}m)");
                    TryMoveTowardsEnemy(unit, situation.NearestEnemy);
                }
                return;
            }

            float dist = Vector3.Distance(unit.Position, target.Position);

            // 4. Taunt/debuff
            if (situation.AvailableDebuffs?.Count > 0)
            {
                if (TryUseDebuffAbility(unit, situation, target))
                    return;
            }

            // 5. Attack (tank always prefers melee)
            ExecuteRangeBasedAction(unit, situation, target, dist, RangePreference.Melee);
        }

        /// <summary>
        /// Support: Focus on healing and buffing allies
        /// Priority: Ally Emergency Heal > Self Heal > Ally Buff > Debuff > Attack > Stay back
        /// </summary>
        private void ExecuteSupportLogic(UnitEntityData unit, Situation situation, RangePreference preference)
        {
            string unitName = situation.Unit?.Descriptor?.CharacterName ?? "Unknown";

            // 1. Find wounded ally (below 50% HP)
            var woundedAlly = FindMostWoundedAlly(situation);
            if (woundedAlly != null)
            {
                float allyHP = GetHPPercent(woundedAlly);

                // Critical heal (ally HP < 30%)
                if (allyHP < HP_EMERGENCY_THRESHOLD)
                {
                    if (TryUseHealAbility(unit, situation, woundedAlly))
                    {
                        Main.Log($"[RT] {unitName}: Support critical ally heal");
                        return;
                    }
                }

                // Regular heal (ally HP < 50%)
                if (allyHP < HP_HEAL_ALLY_THRESHOLD)
                {
                    if (TryUseHealAbility(unit, situation, woundedAlly))
                    {
                        Main.Log($"[RT] {unitName}: Support ally heal");
                        return;
                    }
                }
            }

            // 2. Self-heal if needed
            if (situation.HPPercent < HP_HEAL_ALLY_THRESHOLD)
            {
                if (TryUseHealAbility(unit, situation, unit))
                    return;
            }

            // 3. Buff allies or self
            if (TryUseBuffAbility(unit, situation))
                return;

            // 4. Debuff enemies
            var target = SelectBestTarget(situation, AIRole.Support);
            if (target != null && situation.AvailableDebuffs?.Count > 0)
            {
                if (TryUseDebuffAbility(unit, situation, target))
                    return;
            }

            // 5. Attack (support prefers ranged)
            if (target != null)
            {
                float dist = Vector3.Distance(unit.Position, target.Position);
                ExecuteRangeBasedAction(unit, situation, target, dist, RangePreference.Ranged);
            }
            // v0.2.8: Support도 가까운 적이 있으면 적정 거리 유지하며 이동
            else if (situation.NearestEnemy != null && !situation.HasHittableEnemies &&
                     situation.NearestEnemyDistance <= MAX_ENGAGEMENT_DISTANCE)
            {
                // Support는 너무 멀면 사거리 내로 접근
                if (situation.NearestEnemyDistance > RANGED_OPTIMAL_DIST)
                {
                    Main.Log($"[RT] {unitName}: Support moving to optimal range (dist={situation.NearestEnemyDistance:F1}m)");
                    TryMoveToOptimalRange(unit, situation.NearestEnemy);
                }
            }
        }

        #endregion  // Legacy Role-based Logic

        #endregion

        #region Target Selection

        /// <summary>
        /// Select best target using TargetScorer
        /// v0.2.7: Relaxed combat state check for better enemy detection
        /// </summary>
        private UnitEntityData SelectBestTarget(Situation situation, AIRole role)
        {
            if (situation.Enemies == null || situation.Enemies.Count == 0)
                return null;

            // Use TargetScorer for intelligent selection
            float bestScore = float.MinValue;
            UnitEntityData bestTarget = null;

            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null) continue;
                if (enemy.HPLeft <= 0) continue;
                // v0.2.7: IsInCombat 체크 제거 - 적군 목록에 있으면 타겟 가능
                // 전투 상태가 아직 설정되지 않은 적도 타겟 가능하도록

                float dist = Vector3.Distance(situation.Unit.Position, enemy.Position);
                if (dist > MAX_ENGAGEMENT_DISTANCE) continue;

                // Static method call
                float score = TargetScorer.ScoreTarget(situation.Unit, enemy, role);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = enemy;
                }
            }

            // Set best target in situation for reference
            if (bestTarget != null)
            {
                situation.BestTarget = bestTarget;
            }

            return bestTarget;
        }

        /// <summary>
        /// Find most wounded ally (including self)
        /// </summary>
        private UnitEntityData FindMostWoundedAlly(Situation situation)
        {
            UnitEntityData mostWounded = null;
            float lowestHP = 100f;

            // Check self
            if (situation.HPPercent < lowestHP)
            {
                mostWounded = situation.Unit;
                lowestHP = situation.HPPercent;
            }

            // Check allies
            if (situation.Allies != null)
            {
                foreach (var ally in situation.Allies)
                {
                    if (ally == null) continue;
                    if (ally.HPLeft <= 0) continue;

                    float hp = GetHPPercent(ally);
                    if (hp < lowestHP)
                    {
                        mostWounded = ally;
                        lowestHP = hp;
                    }
                }
            }

            // Only return if actually wounded
            return lowestHP < HP_HEAL_ALLY_THRESHOLD ? mostWounded : null;
        }

        #endregion

        #region Ability Execution

        /// <summary>
        /// Try to use heal ability on target
        /// </summary>
        private bool TryUseHealAbility(UnitEntityData unit, Situation situation, UnitEntityData healTarget)
        {
            if (situation.AvailableHeals == null || situation.AvailableHeals.Count == 0)
                return false;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string targetName = healTarget.Descriptor?.CharacterName ?? "Unknown";
            var targetWrapper = new TargetWrapper(healTarget);

            foreach (var heal in situation.AvailableHeals)
            {
                if (heal == null || !heal.IsAvailable)
                    continue;

                // v0.2.11: 액션 쿨다운 체크
                if (ShouldSkipAbility(unit, heal))
                    continue;

                if (!heal.CanTarget(targetWrapper))
                    continue;

                try
                {
                    var command = new UnitUseAbility(heal, targetWrapper);
                    unit.Commands.Run(command);
                    SetNextCommandTime(unit, command);  // v0.2.21
                    _healCount++;
                    Main.Log($"[RT] * {unitName} -> {targetName} Heal ({heal.Name}) #{_healCount}");
                    return true;
                }
                catch (Exception ex)
                {
                    Main.Error($"[RT] Heal failed: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Try to use buff ability
        /// </summary>
        private bool TryUseBuffAbility(UnitEntityData unit, Situation situation)
        {
            if (situation.AvailableBuffs == null || situation.AvailableBuffs.Count == 0)
                return false;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            var selfTarget = new TargetWrapper(unit);

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff == null || !buff.IsAvailable)
                    continue;

                // v0.2.11: 액션 쿨다운 체크
                if (ShouldSkipAbility(unit, buff))
                    continue;

                // Try self-buff first
                if (buff.Blueprint?.CanTargetSelf == true && buff.CanTarget(selfTarget))
                {
                    // v0.2.3: Skip if buff already applied to self
                    if (AbilityClassifier.IsBuffAlreadyApplied(buff, unit))
                    {
                        Main.Verbose($"[RT] {unitName}: Buff {buff.Name} already applied to self, skipping");
                        continue;
                    }

                    try
                    {
                        var command = new UnitUseAbility(buff, selfTarget);
                        unit.Commands.Run(command);
                        SetNextCommandTime(unit, command);  // v0.2.21
                        _buffCount++;
                        Main.Log($"[RT] * {unitName} Buff ({buff.Name}) #{_buffCount}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Main.Error($"[RT] Buff failed: {ex.Message}");
                    }
                }

                // Try ally buff (for Support role)
                if (buff.Blueprint?.CanTargetFriends == true && situation.Allies != null)
                {
                    foreach (var ally in situation.Allies)
                    {
                        if (ally == null || ally == unit) continue;

                        // v0.2.3: Skip if buff already applied to ally
                        if (AbilityClassifier.IsBuffAlreadyApplied(buff, ally))
                        {
                            string allyName = ally.Descriptor?.CharacterName ?? "Unknown";
                            Main.Verbose($"[RT] {unitName}: Buff {buff.Name} already applied to {allyName}, skipping");
                            continue;
                        }

                        var allyTarget = new TargetWrapper(ally);
                        if (buff.CanTarget(allyTarget))
                        {
                            try
                            {
                                var command = new UnitUseAbility(buff, allyTarget);
                                unit.Commands.Run(command);
                                SetNextCommandTime(unit, command);  // v0.2.21
                                _buffCount++;
                                string allyName = ally.Descriptor?.CharacterName ?? "Unknown";
                                Main.Log($"[RT] * {unitName} -> {allyName} Buff ({buff.Name}) #{_buffCount}");
                                return true;
                            }
                            catch (Exception ex)
                            {
                                Main.Error($"[RT] Ally buff failed: {ex.Message}");
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Try to use debuff ability on target
        /// v0.2.1: Added cooldown check for spam prevention
        /// </summary>
        private bool TryUseDebuffAbility(UnitEntityData unit, Situation situation, UnitEntityData target)
        {
            if (situation.AvailableDebuffs == null || situation.AvailableDebuffs.Count == 0)
                return false;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";

            // v0.2.18: 세이브 인식 디버프 타겟 선택
            // 각 디버프별 최적 타겟을 찾아서 (디버프, 타겟) 쌍 중 최고를 선택
            AbilityData bestDebuff = null;
            UnitEntityData bestTarget = null;
            float bestScore = float.MinValue;

            foreach (var debuff in situation.AvailableDebuffs)
            {
                if (debuff == null || !debuff.IsAvailable)
                    continue;

                if (ShouldSkipAbility(unit, debuff))
                    continue;

                // 세이브 타입이 있으면 최적 타겟 선택, 없으면 기본 타겟 사용
                UnitEntityData debuffTarget = target;
                float score = 0f;

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

            // 세이브 인식 매칭 실패 시 기존 폴백: 순서대로 시도
            if (bestDebuff == null)
            {
                foreach (var debuff in situation.AvailableDebuffs)
                {
                    if (debuff == null || !debuff.IsAvailable) continue;
                    if (ShouldSkipAbility(unit, debuff)) continue;
                    if (target == null) continue;
                    var wrapper = new TargetWrapper(target);
                    if (!debuff.CanTarget(wrapper)) continue;
                    bestDebuff = debuff;
                    bestTarget = target;
                    break;
                }
            }

            if (bestDebuff == null || bestTarget == null)
                return false;

            try
            {
                string targetName = bestTarget.Descriptor?.CharacterName ?? "Unknown";
                var command = new UnitUseAbility(bestDebuff, new TargetWrapper(bestTarget));
                unit.Commands.Run(command);
                SetNextCommandTime(unit, command);  // v0.2.21
                _debuffCount++;
                RecordAbilityUse(unit, bestDebuff);
                Main.Log($"[RT] * {unitName} -> {targetName} Debuff ({bestDebuff.Name}) #{_debuffCount}");
                return true;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Debuff failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Execute range-based action (attack or movement)
        /// v0.2.15: 능력 우선 사용 로직 추가
        /// </summary>
        private void ExecuteRangeBasedAction(UnitEntityData unit, Situation situation, UnitEntityData target, float dist, RangePreference preference)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string targetName = target.Descriptor?.CharacterName ?? "Unknown";

            switch (preference)
            {
                case RangePreference.Melee:
                    if (dist <= MELEE_RANGE)
                    {
                        // v0.2.15: 능력 우선, 실패 시 기본 공격
                        if (!TryAbilityAttack(unit, situation, target))
                        {
                            TryAttackEnemy(unit, target);
                        }
                    }
                    else
                    {
                        TryMoveTowardsEnemy(unit, target);
                    }
                    break;

                case RangePreference.Ranged:
                    // v0.2.19: 공격 가능하면 공격 우선, 불가능하면 후퇴
                    // 이전: dist < 6m이면 무조건 후퇴 → 공격 기회 상실
                    // 이후: 공격 시도 후 실패하면 후퇴
                    if (dist <= MAX_ENGAGEMENT_DISTANCE)
                    {
                        // 능력 공격 또는 기본 공격 시도
                        if (TryAbilityAttack(unit, situation, target) || TryAttackEnemy(unit, target))
                        {
                            // 공격 성공 - 완료
                        }
                        else if (dist < RANGED_MIN_DIST)
                        {
                            // 공격 실패 + 너무 가까움 → 후퇴
                            TryMoveAwayFromEnemy(unit, target);
                        }
                        // 그 외: 공격 실패했지만 거리는 적당 → 대기
                    }
                    else
                    {
                        TryMoveToOptimalRange(unit, target);
                    }
                    break;

                case RangePreference.Mixed:
                default:
                    // v0.2.15: 능력 우선, 실패 시 기본 공격, 실패 시 이동
                    if (!TryAbilityAttack(unit, situation, target))
                    {
                        if (!TryAttackEnemy(unit, target))
                        {
                            TryMoveTowardsEnemy(unit, target);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Try to use ranged attack ability
        /// v0.2.1: Added cooldown check for spam prevention
        /// </summary>
        private bool TryRangedAttack(UnitEntityData unit, Situation situation, UnitEntityData target)
        {
            if (situation.AvailableAttacks == null)
                return false;

            var targetWrapper = new TargetWrapper(target);

            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack == null || !attack.IsAvailable)
                    continue;

                // v0.2.1: Skip if on cooldown
                if (ShouldSkipAbility(unit, attack))
                    continue;

                // Check if it's a ranged ability
                if (!IsRangedAbility(attack))
                    continue;

                if (!attack.CanTarget(targetWrapper))
                    continue;

                try
                {
                    string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
                    string targetName = target.Descriptor?.CharacterName ?? "Unknown";
                    var command = new UnitUseAbility(attack, targetWrapper);
                    unit.Commands.Run(command);
                    SetNextCommandTime(unit, command);  // v0.2.21
                    _attackCount++;

                    // v0.2.1: Record ability usage for cooldown tracking
                    RecordAbilityUse(unit, attack);

                    Main.Log($"[RT] * {unitName} -> {targetName} Ranged ({attack.Name}) #{_attackCount}");
                    return true;
                }
                catch (Exception ex)
                {
                    Main.Error($"[RT] Ranged attack failed: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Check if ability is ranged
        /// </summary>
        private bool IsRangedAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;

            var range = ability.Blueprint.Range;
            return range != Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch &&
                   range != Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal;
        }

        /// <summary>
        /// v0.2.17: 스코어링 기반 능력 선택
        /// 각 사용 가능한 공격 능력에 점수를 매기고 최적 선택
        /// </summary>
        private bool TryAbilityAttack(UnitEntityData unit, Situation situation, UnitEntityData target)
        {
            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
                return false;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string targetName = target.Descriptor?.CharacterName ?? "Unknown";
            var targetWrapper = new TargetWrapper(target);

            int preFilterCount = situation.AvailableAttacks.Count;

            // v0.2.17: 각 능력에 점수 부여 후 최적 선택
            AbilityData bestAbility = null;
            float bestScore = float.MinValue;

            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack == null) continue;

                // v0.2.19: 디버그 로그 추가
                if (ShouldSkipAbility(unit, attack))
                {
                    Main.Verbose($"[RT] {attack.Name}: skipped by ShouldSkipAbility");
                    continue;
                }
                if (!attack.CanTarget(targetWrapper))
                {
                    Main.Verbose($"[RT] {attack.Name}: cannot target {targetName}");
                    continue;
                }

                float score = ScoreAbility(attack, target, situation);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAbility = attack;
                }
            }

            Main.Verbose($"[RT] {unitName}: Scored {preFilterCount} attack abilities, best={bestAbility?.Name ?? "none"} (score={bestScore:F0})");

            if (bestAbility != null)
            {
                try
                {
                    var command = new UnitUseAbility(bestAbility, targetWrapper);
                    unit.Commands.Run(command);
                    SetNextCommandTime(unit, command);  // v0.2.21
                    _attackCount++;

                    RecordAbilityUse(unit, bestAbility);

                    string abilityType = IsRangedAbility(bestAbility) ? "Ranged" : "Melee";
                    Main.Log($"[RT] * {unitName} -> {targetName} {abilityType} Ability ({bestAbility.Name}) #{_attackCount}");
                    return true;
                }
                catch (Exception ex)
                {
                    Main.Error($"[RT] Ability attack failed: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// v0.2.17: 능력별 점수 계산
        /// 높은 점수 = 현재 상황에 더 적합한 능력
        /// </summary>
        private float ScoreAbility(AbilityData ability, UnitEntityData target, Situation situation)
        {
            float score = 50f;
            var bp = ability.Blueprint;
            if (bp == null) return 0f;

            var range = bp.Range;

            // 1. 리소스 보존 - 무제한 능력 우선 (무기 공격)
            bool isWeapon = range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon;
            bool hasLimitedResource = false;

            try
            {
                int castCount = Kingmaker.UnitLogic.Abilities.AbilityCastRateUtils.GetAvailableForCastCount(ability);
                if (castCount >= 0 && castCount <= 3)
                {
                    hasLimitedResource = true;
                    score -= 15f;  // 리소스 제한 = 아끼는 게 좋음
                }
                else if (castCount < 0 || castCount > 10)
                {
                    score += 5f;  // 무제한/풍부 = 자유롭게 사용
                }
            }
            catch { }

            // 2. 적 HP 기반 능력 선택
            float targetHP = 100f;
            try { targetHP = (target.HPLeft * 100f) / Math.Max(1, target.Stats.HitPoints.BaseValue); }
            catch { }

            if (targetHP <= 30f && isWeapon)
            {
                score += 10f;  // 빈사 적에겐 무기 공격으로 충분 (리소스 절약)
            }

            if (targetHP > 70f && !isWeapon && !hasLimitedResource)
            {
                score += 15f;  // 만피 적에겐 강한 주문 사용
            }

            // 3. 범위 적합성
            float dist = Vector3.Distance(situation.Unit.Position, target.Position);
            if (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch)
            {
                if (dist <= 2f) score += 10f;  // 터치 범위 내
                else score -= 20f;  // 터치인데 멀면 감점
            }
            else if (!isWeapon)
            {
                score += 10f;  // 원거리 주문은 기본 보너스
            }

            // 4. AoE 잠재력 - 적이 밀집되어 있으면 AoE 보너스
            try
            {
                float aoeRadius = bp.AoERadius.Meters;
                if (aoeRadius > 0 && situation.Enemies.Count > 1)
                {
                    int nearbyEnemies = 0;
                    foreach (var enemy in situation.Enemies)
                    {
                        if (enemy != null && Vector3.Distance(target.Position, enemy.Position) <= aoeRadius)
                            nearbyEnemies++;
                    }
                    if (nearbyEnemies > 1)
                        score += nearbyEnemies * 12f;  // 추가 적중마다 큰 보너스
                }
            }
            catch { }

            return score;
        }

        /// <summary>
        /// Try to attack enemy with basic attack
        /// </summary>
        private bool TryAttackEnemy(UnitEntityData unit, UnitEntityData enemy)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string enemyName = enemy.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                var attackCommand = new UnitAttack(enemy);
                unit.Commands.Run(attackCommand);
                SetNextCommandTime(unit, attackCommand);  // v0.2.21
                _attackCount++;
                Main.Log($"[RT] * {unitName} -> {enemyName} Attack #{_attackCount}");
                return true;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Attack failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Movement

        /// <summary>
        /// Move towards enemy
        /// </summary>
        private bool TryMoveTowardsEnemy(UnitEntityData unit, UnitEntityData enemy)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string enemyName = enemy.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                var direction = (enemy.Position - unit.Position).normalized;
                var targetPos = unit.Position + direction * 3f;

                var moveCommand = new UnitMoveTo(targetPos, 0f);
                unit.Commands.Run(moveCommand);
                SetNextCommandTime(unit, moveCommand);  // v0.2.21
                _moveCount++;
                Main.Verbose($"[RT] -> {unitName} moving to {enemyName} #{_moveCount}");
                return true;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Move failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Move away from enemy (retreat)
        /// </summary>
        private bool TryMoveAwayFromEnemy(UnitEntityData unit, UnitEntityData enemy)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string enemyName = enemy.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                var direction = (unit.Position - enemy.Position).normalized;
                var targetPos = unit.Position + direction * RETREAT_DISTANCE;

                var moveCommand = new UnitMoveTo(targetPos, 0f);
                unit.Commands.Run(moveCommand);
                SetNextCommandTime(unit, moveCommand);  // v0.2.21
                _moveCount++;
                Main.Log($"[RT] <- {unitName} retreating from {enemyName} #{_moveCount}");
                return true;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Retreat failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Move to optimal range (for ranged characters)
        /// </summary>
        private bool TryMoveToOptimalRange(UnitEntityData unit, UnitEntityData enemy)
        {
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";

            try
            {
                float currentDist = Vector3.Distance(unit.Position, enemy.Position);
                float moveDistance = currentDist - RANGED_OPTIMAL_DIST;

                if (moveDistance <= 0)
                    return false;

                var direction = (enemy.Position - unit.Position).normalized;
                var targetPos = unit.Position + direction * Mathf.Min(moveDistance, 5f);

                var moveCommand = new UnitMoveTo(targetPos, 0f);
                unit.Commands.Run(moveCommand);
                SetNextCommandTime(unit, moveCommand);  // v0.2.21
                _moveCount++;
                Main.Verbose($"[RT] -> {unitName} moving to optimal range #{_moveCount}");
                return true;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Optimal range move failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get HP percent for unit
        /// </summary>
        private float GetHPPercent(UnitEntityData unit)
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

        /// <summary>
        /// v0.2.21: 실시간 전투용 명령 체크 - 능력 시전만 블록
        /// 기본 공격(UnitAttack)과 이동(UnitMoveTo)은 블록하지 않음 (게임이 관리)
        /// 능력 시전(UnitUseAbility)만 완료까지 대기
        /// </summary>
        private bool HasNonInterruptibleCommand(UnitEntityData unit)
        {
            if (unit?.Commands?.Raw == null) return false;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";

            foreach (var cmd in unit.Commands.Raw)
            {
                if (cmd == null || cmd.IsFinished) continue;

                // v0.2.21: 능력 시전(UnitUseAbility)만 블록
                // 기본 공격(UnitAttack)과 이동(UnitMoveTo)은 중단 가능 - 새 명령으로 대체됨
                if (cmd is UnitUseAbility useAbility)
                {
                    // 시작했고 메인 액션(Acted) 완료 전이면 대기
                    if (useAbility.IsStarted && !useAbility.IsActed)
                    {
                        Main.Verbose($"[RT] {unitName}: Blocked by UnitUseAbility " +
                            $"(Started={useAbility.IsStarted}, Acted={useAbility.IsActed}, " +
                            $"Ability={useAbility.Ability?.Name ?? "null"})");
                        return true;
                    }
                }

                // UnitAttack, UnitMoveTo 등은 블록하지 않음
                // 새 명령을 내리면 게임이 자동으로 이전 명령을 중단하고 교체함
            }

            return false;
        }

        /// <summary>
        /// v0.2.19: 게임의 NextCommandTime 체크 (AiBrainController.TickBrain 참조)
        /// AI 명령 스팸 방지용 내부 쿨다운
        /// v0.2.20: 소환수/펫 - AIData 없으면 허용 (자체 쿨다운 사용)
        /// </summary>
        private bool IsNextCommandTimeReady(UnitEntityData unit)
        {
            try
            {
                // 게임 로직: unit.CombatState.AIData.NextCommandTime >= Time.time 이면 대기
                var aiData = unit.CombatState?.AIData;

                // v0.2.20: 소환수/펫은 AIData가 없을 수 있음
                if (aiData == null)
                {
                    Main.Verbose($"[RT] {unit.Descriptor?.CharacterName ?? "Unknown"}: AIData is null, allowing command");
                    return true;
                }

                var nextTime = aiData.NextCommandTime;
                bool ready = nextTime < Time.time;

                if (!ready)
                {
                    Main.Verbose($"[RT] {unit.Descriptor?.CharacterName ?? "Unknown"}: NextCommandTime={nextTime:F2}, Current={Time.time:F2}, wait {nextTime - Time.time:F2}s");
                }

                return ready;
            }
            catch { return true; }
        }

        /// <summary>
        /// Clear cache
        /// ★ v0.2.32: 위상 오프셋, 대기열, CombatDataCache도 초기화
        /// </summary>
        public void ClearCache()
        {
            _lastDecisionTime.Clear();
            _abilityCooldowns.Clear();

            // ★ v0.2.32: 위상 오프셋과 대기열 초기화
            _unitPhaseOffsets.Clear();
            _pendingProcessUnits.Clear();

            // ★ v0.2.32: CombatDataCache 초기화
            CombatDataCache.Instance.Clear();

            // ★ v0.2.30: TeamBlackboard도 초기화
            TeamBlackboard.Instance.Clear();
        }

        /// <summary>
        /// ★ v0.2.30: TeamBlackboard 전투 상태 업데이트
        /// 전투 시작 감지 및 초기화, 라운드 변경 감지
        /// </summary>
        private void UpdateTeamBlackboardCombatState(UnitEntityData unit)
        {
            try
            {
                bool unitInCombat = unit.CombatState?.IsInCombat ?? false;

                if (unitInCombat)
                {
                    // 전투 중인데 TeamBlackboard가 비활성 → 초기화
                    if (!TeamBlackboard.Instance.IsCombatActive)
                    {
                        TeamBlackboard.Instance.InitializeCombat();
                        Main.Log("[RT] TeamBlackboard initialized for combat");
                    }

                    // 팀 상태 주기적 업데이트 (모든 유닛 처리 시마다)
                    TeamBlackboard.Instance.UpdateTeamAssessment();
                }
                else
                {
                    // 전투 종료 감지 - 아무도 전투 중이지 않으면 정리
                    if (TeamBlackboard.Instance.IsCombatActive && TeamBlackboard.Instance.RegisteredUnitCount == 0)
                    {
                        // 모든 유닛이 전투 해제됐으면 정리
                        bool anyInCombat = false;
                        foreach (var u in Kingmaker.Game.Instance.State.Units)
                        {
                            if (u?.IsPlayerFaction == true && (u.CombatState?.IsInCombat ?? false))
                            {
                                anyInCombat = true;
                                break;
                            }
                        }

                        if (!anyInCombat)
                        {
                            TeamBlackboard.Instance.Clear();
                            Main.Log("[RT] TeamBlackboard cleared (combat ended)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[RT] UpdateTeamBlackboardCombatState error: {ex.Message}");
            }
        }

        /// <summary>
        /// v0.2.21: 명령 실행 후 NextCommandTime 설정
        /// 게임의 AiBrainController.TickBrain과 동일한 로직
        /// 이렇게 해야 게임 AI와 우리 AI가 같은 쿨다운 시스템을 공유함
        /// ★ v0.2.28: Free/Swift action에도 최소 딜레이 추가 (무한 루프 방지)
        /// </summary>
        private void SetNextCommandTime(UnitEntityData unit, UnitCommand command)
        {
            try
            {
                var aiData = unit?.CombatState?.AIData;
                if (aiData == null) return;

                // 게임 로직: Time.time + 명령 타입별 딜레이
                aiData.NextCommandTime = Time.time;

                if (command != null)
                {
                    if (command.Type != UnitCommand.CommandType.Free &&
                        command.Type != UnitCommand.CommandType.Swift)
                    {
                        // Standard/Move: 0.3~0.4초 딜레이
                        aiData.NextCommandTime += 0.3f + UnityEngine.Random.Range(0f, 0.1f);
                    }
                    else
                    {
                        // ★ v0.2.28: Free/Swift: 최소 0.15초 딜레이 (무한 루프 방지)
                        // 이전에는 딜레이 없어서 Free action 후 무한 호출됨
                        aiData.NextCommandTime += 0.15f;
                    }
                }

                Main.Verbose($"[RT] {unit.Descriptor?.CharacterName ?? "Unknown"}: NextCommandTime set to {aiData.NextCommandTime:F2} (cmdType={command?.Type})");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[RT] SetNextCommandTime error: {ex.Message}");
            }
        }

        #endregion

        #region v0.2.1 Ability Cooldown System

        /// <summary>
        /// Check if ability is on cooldown
        /// </summary>
        private bool IsAbilityOnCooldown(UnitEntityData unit, AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;

            string key = $"{unit.UniqueId}:{ability.Blueprint.AssetGuid}";

            if (!_abilityCooldowns.TryGetValue(key, out float lastUse))
                return false;

            float elapsed = Time.time - lastUse;
            float cooldown = GetAbilityCooldown(ability);

            return elapsed < cooldown;
        }

        /// <summary>
        /// Record ability usage for cooldown tracking
        /// </summary>
        private void RecordAbilityUse(UnitEntityData unit, AbilityData ability)
        {
            if (ability?.Blueprint == null) return;

            string key = $"{unit.UniqueId}:{ability.Blueprint.AssetGuid}";
            _abilityCooldowns[key] = Time.time;
        }

        /// <summary>
        /// Get cooldown time for ability
        /// v0.2.6: Demoralize 제거됨 (완전 비활성화)
        /// </summary>
        private float GetAbilityCooldown(AbilityData ability)
        {
            if (ability?.Blueprint == null) return 0f;

            // 무료 능력은 기본 쿨다운 적용
            var classification = AbilityClassifier.Classify(ability, null);
            if (classification.IsFreeToUse)
            {
                return FREE_ABILITY_COOLDOWN;
            }

            // 그 외 능력은 쿨다운 없음 (게임 자체 쿨다운 사용)
            return 0f;
        }

        /// <summary>
        /// Check if ability should be skipped (on cooldown or spam protection)
        /// v0.2.9: 타입 기반 Demoralize 블랙리스트 (스트링 매칭 금지)
        /// v0.2.11: 게임 액션 이코노미 쿨다운 체크 추가
        /// v0.2.19: 실시간 전투에서는 액션 이코노미 체크 제거 (턴제 전용)
        /// </summary>
        private bool ShouldSkipAbility(UnitEntityData unit, AbilityData ability)
        {
            if (ability?.Blueprint == null) return true;

            // v0.2.19: 실시간 전투에서는 HasCooldownForCommand 체크 제거
            // 이 체크는 턴제 전투의 Standard/Move/Swift 액션 이코노미용
            // 실시간에서는 게임 자체 글로벌 쿨다운 시스템이 능력 스팸을 방지함
            // 이전 v0.2.11: 이 체크가 실시간에서 모든 능력을 블록하는 버그 유발

            // v0.2.9: Demoralize 완전 비활성화 - 타입 기반 체크
            try
            {
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is Demoralize)
                        {
                            Main.Verbose($"[RT] BLOCKED: {ability.Name} (contains Demoralize action)");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[RT] ShouldSkipAbility type check error: {ex.Message}");
            }

            // Custom cooldown check (for free abilities)
            if (IsAbilityOnCooldown(unit, ability))
            {
                Main.Verbose($"[RT] Skipping {ability.Name}: on custom cooldown");
                return true;
            }

            return false;
        }

        #endregion

        #region Out-of-Combat Buffs

        /// <summary>
        /// Queue a unit for pending buff application
        /// v0.2.2: 명령 실행 중인 유닛은 대기열에 추가
        /// </summary>
        public void QueuePendingBuff(UnitEntityData unit)
        {
            if (unit == null) return;
            string unitId = unit.UniqueId;
            if (!_pendingBuffUnits.Contains(unitId))
            {
                _pendingBuffUnits.Add(unitId);
                Main.Log($"[RT] {unit.Descriptor?.CharacterName}: Queued for pending buff");
            }
        }

        /// <summary>
        /// Process pending buff queue (called from ProcessUnit)
        /// </summary>
        public void ProcessPendingBuffs()
        {
            if (_pendingBuffUnits.Count == 0) return;

            float currentTime = UnityEngine.Time.time;
            var unitsToRemove = new List<string>();

            foreach (var unitId in _pendingBuffUnits.ToList())
            {
                // 재시도 간격 체크
                if (_pendingBuffLastCheck.TryGetValue(unitId, out float lastCheck))
                {
                    if (currentTime - lastCheck < PENDING_BUFF_RETRY_INTERVAL)
                        continue;
                }
                _pendingBuffLastCheck[unitId] = currentTime;

                // 유닛 찾기
                var unit = Kingmaker.Game.Instance.State.Units
                    .FirstOrDefault(u => u.UniqueId == unitId);

                if (unit == null || unit.HPLeft <= 0)
                {
                    unitsToRemove.Add(unitId);
                    continue;
                }

                // 아직 명령 실행 중이면 대기
                if (!unit.Commands.Empty)
                    continue;

                // 전투 중이면 대기열에서 제거
                if (unit.CombatState?.IsInCombat ?? false)
                {
                    unitsToRemove.Add(unitId);
                    continue;
                }

                // 버프 적용 시도
                ApplyPermanentBuffsOutOfCombat(unit);
                unitsToRemove.Add(unitId);
            }

            foreach (var unitId in unitsToRemove)
            {
                _pendingBuffUnits.Remove(unitId);
                _pendingBuffLastCheck.Remove(unitId);
            }
        }

        /// <summary>
        /// Apply permanent buffs out of combat
        /// v0.2.2: 로깅 개선 + 대기열 지원
        /// </summary>
        public void ApplyPermanentBuffsOutOfCombat(UnitEntityData unit, bool queueOnBusy = false)
        {
            if (unit == null)
                return;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            string unitId = unit.UniqueId;

            if (!(unit.Descriptor?.State?.CanAct ?? false))
            {
                Main.Verbose($"[RT] {unitName}: Cannot act, skipping permanent buffs");
                return;
            }

            if (!unit.Commands.Empty)
            {
                Main.Verbose($"[RT] {unitName}: Commands in progress, skipping permanent buffs");
                if (queueOnBusy)
                {
                    QueuePendingBuff(unit);
                }
                return;
            }

            // 대기열에서 제거 (성공적으로 처리됨)
            _pendingBuffUnits.Remove(unitId);

            var unappliedPermanentBuffs = AbilityClassifier.GetUnappliedPermanentBuffs(unit);
            Main.Verbose($"[RT] {unitName}: Found {unappliedPermanentBuffs.Count} unapplied permanent buffs");

            if (unappliedPermanentBuffs.Count == 0)
                return;

            var classification = unappliedPermanentBuffs.First();
            var nativeAbility = classification.Ability;

            if (nativeAbility == null)
            {
                Main.Verbose($"[RT] {unitName}: Permanent buff ability is null");
                return;
            }

            if (!nativeAbility.IsAvailableForCast)
            {
                Main.Verbose($"[RT] {unitName}: Permanent buff ({nativeAbility.Name}) not available for cast");
                return;
            }

            var target = new TargetWrapper(unit);
            if (!nativeAbility.CanTarget(target))
            {
                Main.Verbose($"[RT] {unitName}: Permanent buff ({nativeAbility.Name}) cannot target self");
                return;
            }

            try
            {
                var useAbilityCommand = new UnitUseAbility(nativeAbility, target);
                unit.Commands.Run(useAbilityCommand);
                SetNextCommandTime(unit, useAbilityCommand);  // v0.2.21
                Main.Log($"[RT] ★ {unitName}: Out-of-combat permanent buff ({nativeAbility.Name})");
                _buffCount++;
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] Out-of-combat buff failed ({nativeAbility.Name}): {ex.Message}");
            }
        }

        /// <summary>
        /// Apply permanent buffs to entire party
        /// </summary>
        public void ApplyPermanentBuffsToParty()
        {
            try
            {
                foreach (var unit in Kingmaker.Game.Instance.State.Units)
                {
                    if (unit == null) continue;
                    if (!unit.IsPlayerFaction) continue;
                    if (unit.HPLeft <= 0) continue;
                    if (unit.CombatState?.IsInCombat ?? false) continue;

                    ApplyPermanentBuffsOutOfCombat(unit);
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[RT] ApplyPermanentBuffsToParty error: {ex.Message}");
            }
        }

        #endregion
    }
}
