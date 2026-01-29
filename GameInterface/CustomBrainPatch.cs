using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using TurnBased.Controllers;
using CompanionAI_Pathfinder.Abstraction;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Core.DecisionEngine;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// AiBrainController에 대한 Harmony 패치
    /// AI 결정을 가로채서 커스텀 AI 로직 실행
    /// </summary>
    public static class CustomBrainPatch
    {
        private static int _tickCounter = 0;
        private static int _lastPreBuffFrame = 0;  // ★ v0.2.38: PreBuff tick tracking

        /// <summary>
        /// AiBrainController.TickBrain() 패치
        /// 실시간 전투에서 AI 결정 인터셉트
        /// 주의: 수동 패치로 적용됨 (Main.PatchTickBrainManually)
        ///
        /// v0.2.21: 게임 AI 완전 대체 - 명령 직접 발행 + NextCommandTime 관리
        /// </summary>
        public static bool TickBrain_Prefix(UnitEntityData unit)
        {
            try
            {
                Main.TickCount++;
                _tickCounter++;

                // ★ v0.2.38: PreBuffController tick (프레임당 1회)
                int currentFrame = UnityEngine.Time.frameCount;
                if (currentFrame != _lastPreBuffFrame)
                {
                    _lastPreBuffFrame = currentFrame;
                    PreBuffController.Instance?.Tick();
                }

                // 모드가 비활성화되어 있으면 원본 실행
                if (!Main.Enabled)
                    return true;

                // 유닛 정보 로깅
                string unitName = unit?.Descriptor?.CharacterName ?? "Unknown";
                bool isPlayerFaction = unit?.IsPlayerFaction ?? false;
                bool isControllable = unit?.IsDirectlyControllable ?? false;

                // 아군만 제어
                if (!ShouldControlUnit(unit))
                {
                    // 첫 번째 스킵 이유 로깅
                    if (_tickCounter <= 10)
                    {
                        Main.Verbose($"유닛 스킵: {unitName} (PlayerFaction={isPlayerFaction}, Controllable={isControllable})");
                    }
                    return true;
                }

                Main.ProcessedUnits++;

                // 전투 모드에 따라 처리
                if (CombatController.IsInTurnBasedCombat())
                {
                    Main.Verbose($"턴제 모드 - {unitName}: TurnOrchestrator 호출");
                    // 턴제 모드: TurnOrchestrator가 처리
                    TurnOrchestrator.Instance.ProcessTurn(unit);
                    return false;
                }
                else
                {
                    // v0.2.21: 실시간 모드 - 게임 AI 완전 대체
                    // 1. 게임의 NextCommandTime 체크를 여기서 수행 (게임 AI 로직과 동일)
                    float nextTime = unit.CombatState?.AIData?.NextCommandTime ?? 0f;
                    if (nextTime >= UnityEngine.Time.time)
                    {
                        // 아직 명령 쿨다운 중 - 게임 AI도 대기하므로 우리도 대기
                        return false;  // 게임 AI 스킵 (우리도 아무것도 안 함)
                    }

                    // 2. 진행 중인 능력 시전 체크 (기본 공격은 무시)
                    bool hasAbilityInProgress = false;
                    if (unit.Commands?.Raw != null)
                    {
                        foreach (var cmd in unit.Commands.Raw)
                        {
                            if (cmd == null || cmd.IsFinished) continue;
                            if (cmd is Kingmaker.UnitLogic.Commands.UnitUseAbility useAbility)
                            {
                                if (useAbility.IsStarted && !useAbility.IsActed)
                                {
                                    hasAbilityInProgress = true;
                                    Main.Verbose($"[TickBrain] {unitName}: UnitUseAbility 시전 중 - {useAbility.Ability?.Name}");
                                    break;
                                }
                            }
                        }
                    }

                    if (hasAbilityInProgress)
                    {
                        return false;  // 능력 시전 완료 대기
                    }

                    // 3. RealTimeController에서 AI 결정 수행
                    // ★ v0.2.29: 로그 스팸 감소 - ProcessUnit 내부에서 실제 처리 시에만 로그 출력
                    RealTimeController.Instance.ProcessUnit(unit);
                    return false; // 게임 AI 스킵
                }
            }
            catch (Exception ex)
            {
                Main.Error($"TickBrain 패치 오류: {ex.Message}\n{ex.StackTrace}");
                return true; // 오류 시 원본 실행
            }
        }

        /// <summary>
        /// 유닛을 제어해야 하는지 확인
        /// </summary>
        public static bool ShouldControlUnit(UnitEntityData unit)
        {
            if (unit == null)
                return false;

            // 죽었거나 의식 없음
            if (unit.HPLeft <= 0)
                return false;

            // 플레이어 팩션만 제어
            if (!unit.IsPlayerFaction)
                return false;

            var settings = ModSettings.Instance;
            if (settings == null)
                return false;

            // 주인공 여부 확인
            bool isMainCharacter = unit.IsMainCharacter;

            // 주인공 제어 여부
            if (isMainCharacter)
            {
                if (!settings.ControlMainCharacter)
                    return false;
            }
            else
            {
                // 동료 제어 여부
                if (!settings.ControlCompanions)
                    return false;
            }

            // 캐릭터별 AI 활성화 여부 확인
            string unitId = unit.UniqueId;
            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            var charSettings = settings.GetOrCreateSettings(unitId, unitName);
            if (!charSettings.EnableCustomAI)
                return false;

            return true;
        }
    }

    /// <summary>
    /// 턴제 전투 관련 패치
    /// </summary>
    public static class TurnBasedPatches
    {
        /// <summary>
        /// 턴 시작 시 호출
        /// </summary>
        [HarmonyPatch(typeof(CombatController), "StartTurn")]
        [HarmonyPostfix]
        public static void StartTurn_Postfix(CombatController __instance)
        {
            try
            {
                Main.Log("=== StartTurn 패치 호출됨 ===");

                if (!Main.Enabled)
                {
                    Main.Verbose("모드 비활성화 상태");
                    return;
                }

                var currentUnit = __instance.CurrentTurn?.Rider;
                if (currentUnit == null)
                {
                    Main.Verbose("CurrentTurn.Rider가 null");
                    return;
                }

                string unitName = currentUnit.Descriptor?.CharacterName ?? "Unknown";
                Main.Log($"턴 시작: {unitName}");

                if (!CustomBrainPatch.ShouldControlUnit(currentUnit))
                {
                    Main.Verbose($"유닛 제어 대상 아님: {unitName}");
                    return;
                }

                Main.Log($"★ AI 제어 대상: {unitName}");

                // TurnOrchestrator로 턴 처리
                var result = TurnOrchestrator.Instance.ProcessTurn(currentUnit);
                Main.Log($"턴 처리 결과: {result?.Reason ?? "unknown"}");
            }
            catch (Exception ex)
            {
                Main.Error($"StartTurn 패치 오류: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// 전투 시작/종료 감지 패치
    /// </summary>
    public static class CombatPatches
    {
        [HarmonyPatch(typeof(CombatController), "Activate")]
        [HarmonyPostfix]
        public static void Activate_Postfix()
        {
            Main.Log("★★★ 턴제 전투 시작됨 ★★★");
        }

        [HarmonyPatch(typeof(CombatController), "Deactivate")]
        [HarmonyPostfix]
        public static void Deactivate_Postfix()
        {
            Main.Log("★★★ 턴제 전투 종료됨 ★★★");
            // 전투 종료 시 TurnOrchestrator 상태 초기화
            TurnOrchestrator.Instance.ResetAllTurnStates();

            // ★ v0.2.23: Clear pending action tracker
            PendingActionTracker.Instance.Clear();

            // v0.2.2: 전투 종료 후 영구 버프 자동 적용
            try
            {
                Main.Log("[CombatEnd] Applying permanent buffs to party...");
                RealTimeController.Instance.ApplyPermanentBuffsToParty();
            }
            catch (Exception ex)
            {
                Main.Error($"[CombatEnd] Permanent buff application failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 전투 모드 전환 감지 패치
    /// </summary>
    public static class CombatModePatches
    {
        private static CombatMode _lastMode = CombatMode.None;

        /// <summary>
        /// 모드 변경 감지 및 이벤트 발생
        /// </summary>
        public static void CheckModeChange()
        {
            var currentMode = PathfinderCombatStateAdapter.Instance.CurrentMode;
            if (currentMode != _lastMode)
            {
                Main.Log($"전투 모드 변경: {_lastMode} → {currentMode}");
                _lastMode = currentMode;
                OnModeChanged?.Invoke(currentMode);
            }
        }

        public static event Action<CombatMode> OnModeChanged;
    }

    /// <summary>
    /// v0.2.2: 전투 이벤트 핸들러
    /// 유닛이 전투에서 빠져나올 때 영구 버프 적용
    /// </summary>
    public class CombatEventHandler : IInCombatHandler
    {
        private static CombatEventHandler _instance;
        private static bool _isSubscribed = false;

        public static void Subscribe()
        {
            if (_isSubscribed) return;

            _instance = new CombatEventHandler();
            EventBus.Subscribe(_instance);
            _isSubscribed = true;
            Main.Log("[CombatEventHandler] Subscribed to combat events");
        }

        public static void Unsubscribe()
        {
            if (!_isSubscribed || _instance == null) return;

            EventBus.Unsubscribe(_instance);
            _isSubscribed = false;
            _instance = null;
            Main.Log("[CombatEventHandler] Unsubscribed from combat events");
        }

        /// <summary>
        /// 유닛이 전투에서 빠져나올 때 호출됨
        /// </summary>
        public void HandleObjectLeaveCombat(UnitEntityData unit)
        {
            if (!Main.Enabled) return;
            if (unit == null) return;

            // 플레이어 팩션만 처리
            if (!unit.IsPlayerFaction) return;

            string unitName = unit.Descriptor?.CharacterName ?? "Unknown";
            Main.Log($"[CombatEventHandler] {unitName} left combat");

            try
            {
                // v0.2.10: 전투 종료 시 모든 AI 명령 취소
                // Move 명령뿐 아니라 Attack(접근 포함), UseAbility 등 모든 명령이 이동을 유발할 수 있음
                if (unit.Commands != null && unit.Commands.HasUnfinished())
                {
                    Main.Log($"[CombatEventHandler] {unitName}: Cancelling all commands (left combat)");
                    unit.Commands.InterruptAll();
                }

                // v0.2.2: queueOnBusy=true로 호출하여 명령 실행 중이면 대기열에 추가
                RealTimeController.Instance.ApplyPermanentBuffsOutOfCombat(unit, queueOnBusy: true);
            }
            catch (Exception ex)
            {
                Main.Error($"[CombatEventHandler] Failed for {unitName}: {ex.Message}");
            }
        }
    }
}
