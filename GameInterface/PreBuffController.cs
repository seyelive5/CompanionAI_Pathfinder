// ★ v0.2.63: Smart Pre-Combat Buff System
// 스마트 타겟팅 + 우선순위 기반 버프
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// ★ v0.2.38: MonoBehaviour for Update loop (전투 외 상황에서도 Tick 호출)
    /// </summary>
    public class PreBuffUpdater : MonoBehaviour
    {
        private static PreBuffUpdater _instance;

        public static void Create()
        {
            if (_instance != null) return;

            var go = new GameObject("CompanionAI_PreBuffUpdater");
            _instance = go.AddComponent<PreBuffUpdater>();
            DontDestroyOnLoad(go);
            Main.Log("[PreBuff] Updater MonoBehaviour created");
        }

        public static void Destroy()
        {
            if (_instance != null)
            {
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        private void Update()
        {
            PreBuffController.Instance?.Tick();
        }
    }

    /// <summary>
    /// ★ v0.2.63: Smart Pre-Combat Buff Controller
    /// - 스마트 타겟팅 (역할 기반)
    /// - 우선순위 기반 버프
    /// - 자원 효율적 사용
    /// </summary>
    public class PreBuffController : IDisposable
    {
        #region Singleton

        private static PreBuffController _instance;
        public static PreBuffController Instance => _instance;

        #endregion

        #region Constants

        private const float BUFF_INTERVAL = 0.5f;  // 버프 간 간격 (초)
        private const int MAX_BUFFS_PER_SESSION = 50;  // 한 번에 최대 버프 수

        #endregion

        #region State

        private bool _isBuffing = false;
        private Queue<BuffRequest> _buffQueue = new Queue<BuffRequest>();
        private float _lastBuffTime = 0f;
        private int _totalBuffsApplied = 0;
        private int _totalBuffsSkipped = 0;

        #endregion

        #region Initialization

        public static void Initialize()
        {
            if (_instance != null)
            {
                Main.Log("[PreBuff] Already initialized");
                return;
            }

            _instance = new PreBuffController();
            _instance.Setup();

            // ★ v0.2.38: MonoBehaviour 생성하여 Update에서 Tick 호출
            PreBuffUpdater.Create();
        }

        private void Setup()
        {
            Main.Log("[PreBuff] Smart Pre-Buff System initialized (v0.2.63)");
        }

        public void Dispose()
        {
            try
            {
                PreBuffUpdater.Destroy();
                _instance = null;
                Main.Log("[PreBuff] Disposed");
            }
            catch (Exception ex)
            {
                Main.Error($"[PreBuff] Dispose error: {ex.Message}");
            }
        }

        #endregion

        #region Public Trigger

        /// <summary>
        /// UI에서 호출 - 파티 버프 실행
        /// </summary>
        public static void TriggerPartyBuff()
        {
            Instance?.StartPartyBuff();
        }

        #endregion

        #region Buff Logic

        /// <summary>
        /// 파티 전체 버프 시작
        /// </summary>
        public void StartPartyBuff()
        {
            if (_isBuffing)
            {
                Main.Log("[PreBuff] Already buffing, please wait...");
                return;
            }

            // 전투 중이면 무시
            if (IsPartyInCombat())
            {
                Main.Log("[PreBuff] Party is in combat, cannot pre-buff");
                return;
            }

            try
            {
                _buffQueue.Clear();
                _totalBuffsApplied = 0;
                _totalBuffsSkipped = 0;

                CollectSmartBuffRequests();

                if (_buffQueue.Count == 0)
                {
                    Main.Log("[PreBuff] No buffs to apply (all buffs already active or no valid buffs)");
                    return;
                }

                Main.Log($"[PreBuff] ★ Smart buffing: {_buffQueue.Count} buff(s) queued");
                _isBuffing = true;
                _lastBuffTime = Time.time;
            }
            catch (Exception ex)
            {
                Main.Error($"[PreBuff] StartPartyBuff error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.63: 스마트 버프 요청 수집
        /// </summary>
        private void CollectSmartBuffRequests()
        {
            var party = GetPartyMembers();
            if (party.Count == 0)
            {
                Main.Log("[PreBuff] No valid party members");
                return;
            }

            var allRequests = new List<BuffRequest>();

            // 각 캐스터별로 분석
            foreach (var caster in party)
            {
                if (caster == null || caster.HPLeft <= 0)
                    continue;

                // 스마트 분석
                var analyses = PreBuffAnalyzer.AnalyzeAllPreBuffs(caster, party);

                // 로그: 분석 결과 요약
                int critical = analyses.Count(a => a.Priority == PreBuffPriority.Critical);
                int high = analyses.Count(a => a.Priority == PreBuffPriority.High);
                int medium = analyses.Count(a => a.Priority == PreBuffPriority.Medium);

                if (analyses.Count > 0)
                {
                    Main.Verbose($"[PreBuff] {caster.CharacterName}: {analyses.Count} buffs analyzed " +
                                 $"(Critical={critical}, High={high}, Medium={medium})");
                }

                // 버프 요청 생성
                var requests = PreBuffAnalyzer.GenerateBuffRequests(caster, analyses, MAX_BUFFS_PER_SESSION);
                allRequests.AddRange(requests);
            }

            // 우선순위 순 정렬
            allRequests.Sort((a, b) =>
            {
                // 1. Priority 내림차순
                int priorityA = (int)(a.Analysis?.Priority ?? PreBuffPriority.Skip);
                int priorityB = (int)(b.Analysis?.Priority ?? PreBuffPriority.Skip);
                int priorityCompare = priorityB.CompareTo(priorityA);
                if (priorityCompare != 0) return priorityCompare;

                // 2. CombatValue 내림차순
                float valueA = a.Analysis?.CombatValue ?? 0;
                float valueB = b.Analysis?.CombatValue ?? 0;
                return valueB.CompareTo(valueA);
            });

            // 중복 제거 (같은 버프를 같은 대상에게)
            var seen = new HashSet<string>();
            foreach (var request in allRequests)
            {
                if (_buffQueue.Count >= MAX_BUFFS_PER_SESSION)
                    break;

                string key = $"{request.Ability?.Blueprint?.AssetGuid}_{request.Target?.UniqueId}";
                if (seen.Contains(key))
                {
                    _totalBuffsSkipped++;
                    continue;
                }
                seen.Add(key);

                _buffQueue.Enqueue(request);
            }

            Main.Log($"[PreBuff] Final queue: {_buffQueue.Count} buffs (skipped {_totalBuffsSkipped} duplicates)");
        }

        /// <summary>
        /// 매 프레임 호출 - 버프 큐 처리
        /// </summary>
        public void Tick()
        {
            // 버프 큐 처리
            if (!_isBuffing || _buffQueue.Count == 0)
            {
                if (_isBuffing && _buffQueue.Count == 0)
                {
                    _isBuffing = false;
                    Main.Log($"[PreBuff] ★ Buff session completed: {_totalBuffsApplied} buffs applied");
                }
                return;
            }

            // 간격 체크
            if (Time.time - _lastBuffTime < BUFF_INTERVAL)
                return;

            // 다음 버프 처리
            ProcessNextBuff();
        }

        /// <summary>
        /// 다음 버프 처리
        /// </summary>
        private void ProcessNextBuff()
        {
            while (_buffQueue.Count > 0)
            {
                var request = _buffQueue.Dequeue();

                // 유효성 재확인
                if (!IsValidRequest(request))
                {
                    _totalBuffsSkipped++;
                    continue;
                }

                // 시전자가 바쁜지 확인
                if (!request.Caster.Commands.Empty)
                {
                    // 다시 큐에 넣고 나중에 재시도
                    _buffQueue.Enqueue(request);
                    _lastBuffTime = Time.time;
                    return;
                }

                // 버프 시전
                ExecuteBuff(request);
                _lastBuffTime = Time.time;
                _totalBuffsApplied++;
                return;
            }
        }

        /// <summary>
        /// 버프 요청 유효성 확인
        /// </summary>
        private bool IsValidRequest(BuffRequest request)
        {
            if (request.Caster == null || request.Caster.HPLeft <= 0)
                return false;
            if (request.Target == null || request.Target.HPLeft <= 0)
                return false;
            if (request.Ability == null)
                return false;
            if (!request.Ability.IsAvailableForCast)
                return false;

            // 이미 적용된 버프 체크
            if (request.Analysis?.AppliedBuff != null)
            {
                if (AbilityClassifier.IsBuffAlreadyApplied(request.Ability, request.Target))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 버프 실행
        /// </summary>
        private void ExecuteBuff(BuffRequest request)
        {
            try
            {
                var ability = request.Ability;
                var target = new TargetWrapper(request.Target);

                var command = new UnitUseAbility(ability, target);
                request.Caster.Commands.Run(command);

                // 상세 로그
                string priorityStr = request.Analysis?.Priority.ToString() ?? "?";
                string valueStr = request.Analysis?.CombatValue.ToString("F2") ?? "?";

                Main.Log($"[PreBuff] {request.Caster.CharacterName} -> {request.Target.CharacterName}: " +
                         $"{ability.Name} [Priority={priorityStr}, Value={valueStr}]");
            }
            catch (Exception ex)
            {
                Main.Error($"[PreBuff] ExecuteBuff error: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private bool IsPartyInCombat()
        {
            try
            {
                foreach (var unit in Game.Instance.State.Units)
                {
                    if (unit == null || !unit.IsPlayerFaction)
                        continue;
                    if (unit.CombatState?.IsInCombat ?? false)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private List<UnitEntityData> GetPartyMembers()
        {
            var result = new List<UnitEntityData>();
            try
            {
                foreach (var unit in Game.Instance.State.Units)
                {
                    if (unit == null) continue;
                    if (!unit.IsPlayerFaction) continue;
                    if (unit.HPLeft <= 0) continue;

                    // 펫/소환수 필터링 (선택적)
                    // if (unit.Master != null) continue;

                    result.Add(unit);
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[PreBuff] GetPartyMembers error: {ex.Message}");
            }
            return result;
        }

        #endregion
    }
}
