using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using TurnBased.Controllers;
using CompanionAI_Pathfinder.Abstraction;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Core.TurnBased;
using CompanionAI_Pathfinder.Core.DecisionEngine;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// AiBrainController에 대한 Harmony 패치
    /// AI 결정을 가로채서 커스텀 AI 로직 실행
    ///
    /// ★ v0.2.113: 아키텍처 개선
    /// - 턴제: TurnControllerTick_Postfix만 사용 → TurnBasedController
    /// - 실시간: TickBrain_Prefix → RealTimeController
    /// - TickBrain_Prefix는 턴제에서 호출 안 됨 (게임 로직)
    /// </summary>
    public static class CustomBrainPatch
    {
        private static int _tickCounter = 0;
        private static int _lastPreBuffFrame = 0;

        /// <summary>
        /// AiBrainController.TickBrain() 패치
        /// ★ v0.2.113: 실시간 전투 전용
        ///
        /// 게임 분석 결과:
        /// - 턴제 전투: AiBrainController.Tick()에서 바로 return (ForceTick 안 함)
        /// - 실시간 전투: Tick() → ForceTick() → TickBrain()
        ///
        /// 따라서 턴제에서는 이 메서드가 호출되지 않음!
        /// 턴제 AI는 TurnControllerTick_Postfix에서 처리
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

                string unitName = unit?.Descriptor?.CharacterName ?? "Unknown";

                // 아군만 제어
                if (!ShouldControlUnit(unit))
                {
                    return true;
                }

                Main.ProcessedUnits++;

                // ★ v0.2.113: 턴제/실시간 분기
                if (CombatController.IsInTurnBasedCombat())
                {
                    // ★ 턴제 모드에서는 TurnControllerTick_Postfix가 처리
                    // 이 코드는 실제로 호출되지 않아야 함 (게임이 턴제에서 TickBrain 안 함)
                    // 혹시 호출되면 무시
                    Main.Verbose($"[TickBrain] {unitName}: 턴제 모드 - TurnControllerTick에서 처리");
                    return false;
                }
                else
                {
                    // ★ 실시간 모드 - 게임 AI 완전 대체
                    float nextTime = unit.CombatState?.AIData?.NextCommandTime ?? 0f;
                    if (nextTime >= UnityEngine.Time.time)
                    {
                        return false;  // 명령 쿨다운 중
                    }

                    // 능력 시전 중 체크
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
                                    break;
                                }
                            }
                        }
                    }

                    if (hasAbilityInProgress)
                    {
                        return false;  // 능력 시전 완료 대기
                    }

                    // RealTimeController에서 AI 결정 수행
                    RealTimeController.Instance.ProcessUnit(unit);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"TickBrain 패치 오류: {ex.Message}\n{ex.StackTrace}");
                return true;
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
        /// 턴 시작 시 호출 (수동 패치 - Main.PatchCombatControllerManually)
        /// 이 시점에서는 Status=Scrolling이므로 실제 AI 호출은 Tick에서 수행
        /// </summary>
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
                Main.Log($"턴 시작: {unitName} (Status={__instance.CurrentTurn?.Status})");

                if (!CustomBrainPatch.ShouldControlUnit(currentUnit))
                {
                    Main.Verbose($"유닛 제어 대상 아님: {unitName}");
                    return;
                }

                Main.Log($"★ AI 제어 대상: {unitName} - TurnControllerTick에서 처리 예정");
                // ★ v0.2.86: StartTurn 시점에는 Status=Scrolling이므로 호출하지 않음
                // TurnControllerTick_Postfix에서 Preparing/Acting 상태일 때 호출
            }
            catch (Exception ex)
            {
                Main.Error($"StartTurn 패치 오류: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// ★ v0.2.113: TurnController.Tick() Postfix
        /// 완전히 재작성 - TurnBasedController 사용
        ///
        /// 핵심 원칙:
        /// 1. Commands.Empty 체크 (RT v3.5.7과 동일)
        /// 2. TurnBasedController.ProcessTurn() 호출
        /// 3. 단순화된 흐름
        /// </summary>
        public static void TurnControllerTick_Postfix(TurnController __instance)
        {
            try
            {
                if (!Main.Enabled) return;

                var rider = __instance.Rider;
                if (rider == null) return;

                // 우리가 제어하는 유닛만
                if (!CustomBrainPatch.ShouldControlUnit(rider)) return;

                // IsDirectlyControllable 유닛만 (게임이 AI를 호출하지 않는 유닛)
                if (!rider.IsDirectlyControllable) return;

                var status = __instance.Status;
                string unitName = rider.Descriptor?.CharacterName ?? "Unknown";

                // Scrolling 상태에서는 대기 (카메라 이동 중)
                if (status == TurnController.TurnStatus.Scrolling)
                {
                    return;
                }

                // Ended 상태에서는 대기 (턴 종료됨)
                if (status == TurnController.TurnStatus.Ended)
                {
                    return;
                }

                // ★ v0.2.113: TurnBasedController 호출
                var result = TurnBasedController.Instance.ProcessTurn(rider);

                // 결과 처리
                if (result != null)
                {
                    if (result.Type == Core.ResultType.EndTurn)
                    {
                        Main.Log($"[TurnTick] {unitName}: EndTurn - {result.Reason}");

                        // ★ Acting으로 전환 후 턴 종료 시도
                        if (status != TurnController.TurnStatus.Acting)
                        {
                            ForceStatusToActing(__instance);
                        }

                        if (__instance.CanEndTurnAndNoActing())
                        {
                            __instance.ForceToEnd(true);
                        }
                    }
                    else if (result.Type == Core.ResultType.Waiting)
                    {
                        // ★ 명령이 발행된 경우 Acting으로 전환
                        // 이래야 명령이 tick됨
                        if (status != TurnController.TurnStatus.Acting)
                        {
                            bool hasCommands = rider.Commands.HasUnfinished();
                            if (hasCommands)
                            {
                                Main.Verbose($"[TurnTick] {unitName}: Commands pending - transitioning to Acting");
                                ForceStatusToActing(__instance);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Error($"TurnControllerTick 패치 오류: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// ★ v0.2.88: TurnController.Status를 Acting으로 강제 전환
        /// ★ v0.2.90: 리플렉션 필드 접근으로 변경 (Traverse가 실패할 수 있음)
        /// </summary>
        private static void ForceStatusToActing(TurnController controller)
        {
            try
            {
                // 방법 1: Harmony Traverse (auto-property setter)
                var traverse = HarmonyLib.Traverse.Create(controller);
                traverse.Property("Status").SetValue(TurnController.TurnStatus.Acting);

                // 검증
                if (controller.Status == TurnController.TurnStatus.Acting)
                {
                    Main.Verbose($"[TurnTick] Traverse.Property worked - Status is now Acting");
                    return;
                }

                Main.Log($"[TurnTick] Traverse.Property failed, trying backing field...");

                // 방법 2: 백킹 필드 직접 접근
                var backingField = typeof(TurnController).GetField("<Status>k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (backingField != null)
                {
                    backingField.SetValue(controller, TurnController.TurnStatus.Acting);
                    Main.Log($"[TurnTick] Set via backing field - Status = {controller.Status}");
                    return;
                }

                Main.Log($"[TurnTick] Backing field not found, trying all private fields...");

                // 방법 3: Status라는 이름의 모든 필드 검색
                var fields = typeof(TurnController).GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                foreach (var field in fields)
                {
                    if (field.Name.Contains("Status") && field.FieldType == typeof(TurnController.TurnStatus))
                    {
                        field.SetValue(controller, TurnController.TurnStatus.Acting);
                        Main.Log($"[TurnTick] Set via field '{field.Name}' - Status = {controller.Status}");
                        return;
                    }
                }

                Main.Error($"[TurnTick] Could not find any way to set Status!");
            }
            catch (Exception ex)
            {
                Main.Error($"[TurnTick] Failed to force Status: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 전투 시작/종료 감지 패치
    /// </summary>
    public static class CombatPatches
    {
        /// <summary>
        /// 턴제 전투 활성화 시 호출 (수동 패치)
        /// </summary>
        public static void Activate_Postfix()
        {
            Main.Log("★★★ 턴제 전투 시작됨 ★★★");
        }

        /// <summary>
        /// 턴제 전투 비활성화 시 호출 (수동 패치)
        /// ★ v0.2.113: TurnBasedController 사용
        /// </summary>
        public static void Deactivate_Postfix()
        {
            Main.Log("★★★ 턴제 전투 종료됨 ★★★");
            // ★ v0.2.113: TurnBasedController 상태 초기화
            TurnBasedController.Instance.ResetAll();

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
