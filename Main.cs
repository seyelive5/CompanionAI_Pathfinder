using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using Kingmaker.AI;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_Pathfinder.GameInterface;
using CompanionAI_Pathfinder.Settings;
using TurnBased.Controllers;

namespace CompanionAI_Pathfinder
{
    /// <summary>
    /// CompanionAI Pathfinder 모드 진입점
    /// Unity Mod Manager에서 호출됨
    /// </summary>
    public class Main
    {
        public static bool Enabled { get; private set; }
        public static UnityModManager.ModEntry ModEntry { get; private set; }
        public static Harmony HarmonyInstance { get; private set; }
        public static ModSettings Settings => ModSettings.Instance;

        // 통계
        public static int TickCount { get; set; } = 0;
        public static int ProcessedUnits { get; set; } = 0;
        public static bool TickBrainPatched { get; set; } = false;
        public static string PatchStatus { get; set; } = "대기 중";

        /// <summary>
        /// 모드 로드 시 호출됨
        /// </summary>
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;

            try
            {
                Log("CompanionAI Pathfinder 초기화 중...");

                // 설정 로드
                ModSettings.Load(modEntry);

                // Harmony 패치 적용
                HarmonyInstance = new Harmony(modEntry.Info.Id);

                // 자동 패치 (attribute 기반)
                HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

                // ★ 수동 패치: AiBrainController.TickBrain (private static 메서드)
                PatchTickBrainManually();

                // 패치 확인
                var patchedMethods = HarmonyInstance.GetPatchedMethods();
                int patchCount = 0;
                foreach (var method in patchedMethods)
                {
                    Log($"  패치됨: {method.DeclaringType?.Name}.{method.Name}");
                    patchCount++;
                }
                Log($"총 {patchCount}개 메서드 패치 완료");

                // 콜백 등록
                modEntry.OnToggle = OnToggle;
                modEntry.OnGUI = OnGUI;
                modEntry.OnSaveGUI = OnSaveGUI;

                // v0.2.2: 전투 이벤트 핸들러 구독
                CombatEventHandler.Subscribe();

                // ★ v0.2.38: Pre-Buff Controller 초기화
                PreBuffController.Initialize();

                Enabled = true;
                Log($"CompanionAI Pathfinder v{modEntry.Info.Version} 로드 완료!");

                return true;
            }
            catch (Exception ex)
            {
                Error($"모드 로드 실패: {ex}");
                return false;
            }
        }

        /// <summary>
        /// AiBrainController.TickBrain 수동 패치
        /// private static 메서드이므로 AccessTools 사용
        /// </summary>
        private static void PatchTickBrainManually()
        {
            try
            {
                // private static 메서드 찾기
                var targetMethod = AccessTools.Method(
                    typeof(AiBrainController),
                    "TickBrain",
                    new Type[] { typeof(UnitEntityData) }
                );

                if (targetMethod == null)
                {
                    PatchStatus = "실패: TickBrain 메서드 없음";
                    Error("TickBrain 메서드를 찾을 수 없습니다!");

                    // 모든 메서드 나열해서 디버깅
                    Log("AiBrainController의 모든 메서드:");
                    foreach (var m in typeof(AiBrainController).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        Log($"  - {m.Name} ({(m.IsStatic ? "static" : "instance")}, {(m.IsPrivate ? "private" : "public")})");
                    }
                    return;
                }

                Log($"TickBrain 메서드 발견: {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");

                // Prefix 메서드 찾기
                var prefixMethod = AccessTools.Method(
                    typeof(GameInterface.CustomBrainPatch),
                    "TickBrain_Prefix"
                );

                if (prefixMethod == null)
                {
                    PatchStatus = "실패: Prefix 메서드 없음";
                    Error("TickBrain_Prefix 메서드를 찾을 수 없습니다!");
                    return;
                }

                // 패치 적용
                HarmonyInstance.Patch(
                    targetMethod,
                    prefix: new HarmonyMethod(prefixMethod)
                );

                TickBrainPatched = true;
                PatchStatus = "성공";
                Log("★ TickBrain 수동 패치 성공!");

                // ★ v0.2.78: CombatController 패치 추가
                PatchCombatControllerManually();
            }
            catch (Exception ex)
            {
                PatchStatus = $"실패: {ex.Message}";
                Error($"TickBrain 수동 패치 실패: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// ★ v0.2.78: CombatController 메서드들 수동 패치
        /// 턴제 전투 시작/종료 감지를 위해 필요
        /// </summary>
        private static void PatchCombatControllerManually()
        {
            try
            {
                // 1. StartTurn(UnitEntityData unit) 패치
                var startTurnMethod = AccessTools.Method(
                    typeof(CombatController),
                    "StartTurn",
                    new Type[] { typeof(UnitEntityData) }
                );

                if (startTurnMethod != null)
                {
                    var startTurnPostfix = AccessTools.Method(
                        typeof(TurnBasedPatches),
                        "StartTurn_Postfix"
                    );

                    if (startTurnPostfix != null)
                    {
                        HarmonyInstance.Patch(
                            startTurnMethod,
                            postfix: new HarmonyMethod(startTurnPostfix)
                        );
                        Log("  패치됨: CombatController.StartTurn");
                    }
                    else
                    {
                        Error("StartTurn_Postfix 메서드를 찾을 수 없습니다!");
                    }
                }
                else
                {
                    Error("CombatController.StartTurn 메서드를 찾을 수 없습니다!");
                }

                // 2. Activate() 패치
                var activateMethod = AccessTools.Method(
                    typeof(CombatController),
                    "Activate"
                );

                if (activateMethod != null)
                {
                    var activatePostfix = AccessTools.Method(
                        typeof(CombatPatches),
                        "Activate_Postfix"
                    );

                    if (activatePostfix != null)
                    {
                        HarmonyInstance.Patch(
                            activateMethod,
                            postfix: new HarmonyMethod(activatePostfix)
                        );
                        Log("  패치됨: CombatController.Activate");
                    }
                }
                else
                {
                    Error("CombatController.Activate 메서드를 찾을 수 없습니다!");
                }

                // 3. Deactivate() 패치
                var deactivateMethod = AccessTools.Method(
                    typeof(CombatController),
                    "Deactivate"
                );

                if (deactivateMethod != null)
                {
                    var deactivatePostfix = AccessTools.Method(
                        typeof(CombatPatches),
                        "Deactivate_Postfix"
                    );

                    if (deactivatePostfix != null)
                    {
                        HarmonyInstance.Patch(
                            deactivateMethod,
                            postfix: new HarmonyMethod(deactivatePostfix)
                        );
                        Log("  패치됨: CombatController.Deactivate");
                    }
                }
                else
                {
                    Error("CombatController.Deactivate 메서드를 찾을 수 없습니다!");
                }

                // ★ v0.2.86: TurnController.Tick() 패치 추가
                // IsDirectlyControllable 유닛에게 AI를 호출하기 위해 필요
                PatchTurnControllerTickManually();

                Log("★ CombatController 수동 패치 완료!");
            }
            catch (Exception ex)
            {
                Error($"CombatController 수동 패치 실패: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// ★ v0.2.86: TurnController.Tick() 수동 패치
        /// 게임은 IsDirectlyControllable 유닛에게 ForceTick()을 호출하지 않음
        /// 우리가 Preparing/Acting 상태에서 직접 AI를 호출해야 함
        /// </summary>
        private static void PatchTurnControllerTickManually()
        {
            try
            {
                var tickMethod = AccessTools.Method(
                    typeof(TurnController),
                    "Tick"
                );

                if (tickMethod != null)
                {
                    var tickPostfix = AccessTools.Method(
                        typeof(TurnBasedPatches),
                        "TurnControllerTick_Postfix"
                    );

                    if (tickPostfix != null)
                    {
                        HarmonyInstance.Patch(
                            tickMethod,
                            postfix: new HarmonyMethod(tickPostfix)
                        );
                        Log("  패치됨: TurnController.Tick");
                    }
                    else
                    {
                        Error("TurnControllerTick_Postfix 메서드를 찾을 수 없습니다!");
                    }
                }
                else
                {
                    Error("TurnController.Tick 메서드를 찾을 수 없습니다!");
                }
            }
            catch (Exception ex)
            {
                Error($"TurnController.Tick 패치 실패: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 모드 활성화/비활성화 토글
        /// </summary>
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;

            if (value)
            {
                Log("CompanionAI 활성화됨");
                CombatEventHandler.Subscribe();
            }
            else
            {
                Log("CompanionAI 비활성화됨");
                CombatEventHandler.Unsubscribe();
            }

            return true;
        }

        /// <summary>
        /// 설정 GUI 렌더링
        /// </summary>
        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            UI.MainUI.OnGUI();
        }

        /// <summary>
        /// 설정 저장
        /// </summary>
        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            ModSettings.Save();
        }

        // === 로깅 ===
        public static void Log(string message)
        {
            ModEntry?.Logger?.Log(message);
        }

        public static void Warning(string message)
        {
            ModEntry?.Logger?.Warning(message);
        }

        public static void Error(string message)
        {
            ModEntry?.Logger?.Error(message);
        }

        /// <summary>
        /// 상세 로깅 (EnableDebugLogging이 true일 때만)
        /// </summary>
        public static void Verbose(string message)
        {
            if (Settings?.EnableDebugLogging ?? true)
            {
                ModEntry?.Logger?.Log($"[V] {message}");
            }
        }
    }
}
