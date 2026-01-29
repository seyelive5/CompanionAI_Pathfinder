// ★ v0.2.38: Pre-Combat Buff System
// 휴식 후 자동 버프 + 핫키 수동 버프
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic.Abilities;
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
    /// Pre-Combat Buff Controller
    /// - 핫키로 수동 파티 버프
    /// - 휴식 후 자동 버프 (IRestFinishedHandler)
    /// </summary>
    public class PreBuffController : IDisposable
    {
        #region Singleton

        private static PreBuffController _instance;
        public static PreBuffController Instance => _instance;

        #endregion

        #region Constants

        private const float BUFF_INTERVAL = 0.5f;  // 버프 간 간격 (초)

        #endregion

        #region State

        private bool _isInitialized = false;
        private bool _isBuffing = false;
        private Queue<BuffRequest> _buffQueue = new Queue<BuffRequest>();
        private float _lastBuffTime = 0f;

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
            try
            {
                _isInitialized = true;
                Main.Log("[PreBuff] Initialized successfully (use UI button or TriggerPartyBuff())");
            }
            catch (Exception ex)
            {
                Main.Error($"[PreBuff] Initialization failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                PreBuffUpdater.Destroy();
                _isInitialized = false;
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
                CollectBuffRequests();

                if (_buffQueue.Count == 0)
                {
                    Main.Log("[PreBuff] No buffs to apply");
                    return;
                }

                Main.Log($"[PreBuff] Queued {_buffQueue.Count} buff(s) to apply");
                _isBuffing = true;
                _lastBuffTime = Time.time;
            }
            catch (Exception ex)
            {
                Main.Error($"[PreBuff] StartPartyBuff error: {ex.Message}");
            }
        }

        /// <summary>
        /// 버프 요청 수집 (모든 파티원의 영구/사전 버프)
        /// </summary>
        private void CollectBuffRequests()
        {
            var party = GetPartyMembers();

            foreach (var caster in party)
            {
                if (caster == null || caster.HPLeft <= 0)
                    continue;

                // 해당 캐릭터의 사용 가능한 버프 목록
                var buffs = GetAvailablePreBuffs(caster);

                foreach (var buff in buffs)
                {
                    // 각 파티원에게 적용할 버프 추가
                    foreach (var target in party)
                    {
                        if (target == null || target.HPLeft <= 0)
                            continue;

                        // 이미 적용된 버프는 스킵
                        if (AbilityClassifier.IsBuffAlreadyApplied(buff.Ability, target))
                            continue;

                        // 대상 지정 가능 여부 확인
                        if (!CanTargetUnit(buff.Ability, caster, target))
                            continue;

                        _buffQueue.Enqueue(new BuffRequest(caster, target, buff));
                    }
                }
            }
        }

        /// <summary>
        /// 사용 가능한 사전 버프 목록
        /// </summary>
        private List<AbilityClassification> GetAvailablePreBuffs(UnitEntityData unit)
        {
            return AbilityClassifier.ClassifyAllAbilities(unit)
                .Where(c => c.Timing == AbilityTiming.PermanentBuff ||
                            c.Timing == AbilityTiming.PreCombatBuff)
                .Where(c => c.Ability?.IsAvailableForCast ?? false)
                .Where(c => c.SpellLevel <= 2)  // 저레벨 버프만 (설정으로 변경 가능)
                .OrderByDescending(c => c.Timing == AbilityTiming.PermanentBuff ? 1 : 0)
                .ThenBy(c => c.SpellLevel)  // 낮은 레벨 우선
                .ToList();
        }

        /// <summary>
        /// 대상에게 능력 사용 가능 여부
        /// </summary>
        private bool CanTargetUnit(AbilityData ability, UnitEntityData caster, UnitEntityData target)
        {
            try
            {
                var bp = ability?.Blueprint;
                if (bp == null) return false;

                // 자기 자신만 대상 가능
                if (bp.CanTargetSelf && !bp.CanTargetFriends && caster == target)
                    return true;

                // 아군 대상 가능
                if (bp.CanTargetFriends)
                    return true;

                // 자기 자신 대상 가능
                if (bp.CanTargetSelf && caster == target)
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
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
                    Main.Log("[PreBuff] Buff queue completed");
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
                    continue;

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
            if (request.Buff?.Ability == null)
                return false;
            if (!request.Buff.Ability.IsAvailableForCast)
                return false;
            if (AbilityClassifier.IsBuffAlreadyApplied(request.Buff.Ability, request.Target))
                return false;

            return true;
        }

        /// <summary>
        /// 버프 실행
        /// </summary>
        private void ExecuteBuff(BuffRequest request)
        {
            try
            {
                var ability = request.Buff.Ability;
                var target = new TargetWrapper(request.Target);

                var command = new UnitUseAbility(ability, target);
                request.Caster.Commands.Run(command);

                Main.Log($"[PreBuff] {request.Caster.CharacterName} -> {request.Target.CharacterName}: {ability.Name}");
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
                    // TODO: 펫/탈것 필터링 (나중에 구현)

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

        #region Buff Request Structure

        private struct BuffRequest
        {
            public UnitEntityData Caster;
            public UnitEntityData Target;
            public AbilityClassification Buff;

            public BuffRequest(UnitEntityData caster, UnitEntityData target, AbilityClassification buff)
            {
                Caster = caster;
                Target = target;
                Buff = buff;
            }
        }

        #endregion
    }
}
