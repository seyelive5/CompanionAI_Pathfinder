// ★ v0.2.53: TrapAvoidancePatch - 플레이어 이동 명령에 대한 함정 회피 패치
// UnitMoveTo 명령을 인터셉트하여 함정 회피 적용
using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// ★ v0.2.53: 플레이어 이동 명령에 함정 회피 적용
    /// UnitCommands.Run을 패치하여 이동 전 함정 체크
    /// </summary>
    [HarmonyPatch]
    public static class TrapAvoidancePatch
    {
        /// <summary>
        /// 패치 활성화 여부
        /// </summary>
        public static bool PatchEnabled { get; set; } = true;

        /// <summary>
        /// 전투 외 상황에서도 함정 회피 적용
        /// </summary>
        public static bool ApplyOutOfCombat { get; set; } = true;

        // UnitCommands.m_Owner 필드 캐시
        private static FieldInfo _ownerField;

        /// <summary>
        /// UnitCommands에서 m_Owner 필드 가져오기
        /// </summary>
        private static UnitEntityData GetOwner(UnitCommands commands)
        {
            if (_ownerField == null)
            {
                _ownerField = AccessTools.Field(typeof(UnitCommands), "m_Owner");
            }
            return _ownerField?.GetValue(commands) as UnitEntityData;
        }

        /// <summary>
        /// UnitCommands.Run 패치 - 이동 명령 인터셉트
        /// </summary>
        [HarmonyPatch(typeof(Kingmaker.UnitLogic.Commands.UnitCommands), "Run", new Type[] { typeof(UnitCommand) })]
        [HarmonyPrefix]
        public static bool UnitCommands_Run_Prefix(
            Kingmaker.UnitLogic.Commands.UnitCommands __instance,
            ref UnitCommand cmd)
        {
            if (!PatchEnabled || !TrapAwarenessController.Enabled)
                return true;

            try
            {
                // UnitMoveTo 명령인지 확인
                if (cmd is UnitMoveTo moveCmd)
                {
                    var unit = GetOwner(__instance);
                    if (unit == null) return true;

                    string unitName = unit.CharacterName ?? "Unknown";

                    // 전투 외 상황 체크
                    bool inCombat = unit.CombatState?.IsInCombat ?? false;
                    if (!inCombat && !ApplyOutOfCombat)
                        return true;

                    // 플레이어 파티원만
                    if (!unit.IsPlayerFaction)
                        return true;

                    // 목적지 가져오기
                    Vector3 destination = moveCmd.Target;
                    if (destination == Vector3.zero)
                        return true;

                    Main.Verbose($"[TrapPatch] {unitName}: Move command to {destination}");

                    // 안전 체크
                    if (!TrapAwarenessController.CheckMovementSafety(unit, destination, out Vector3 safeDestination))
                    {
                        // 목적지가 다르면 새 명령으로 교체
                        if (Vector3.Distance(destination, safeDestination) > 0.5f)
                        {
                            Main.Log($"[TrapPatch] {unitName}: Avoiding trap, redirecting to safe position");

                            // 새 이동 명령 생성
                            var safeMove = new UnitMoveTo(safeDestination);
                            safeMove.MovementDelay = moveCmd.MovementDelay;

                            // 원래 명령을 안전한 명령으로 교체
                            cmd = safeMove;
                        }
                        else
                        {
                            Main.Log($"[TrapPatch] {unitName}: Trap detected on path, but no safe alternative found");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[TrapPatch] UnitCommands.Run prefix error: {ex.Message}");
            }

            return true;  // 원래 명령 (또는 교체된 명령) 실행
        }

        /// <summary>
        /// 가장 가까운 안전한 위치 찾기 (위험한 위치 주변 탐색)
        /// </summary>
        public static Vector3 FindNearestSafePosition(Vector3 dangerousPosition)
        {
            // 8방향으로 안전한 위치 탐색
            float[] distances = { 2f, 3f, 4f, 5f };
            Vector3[] directions = {
                Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
                (Vector3.forward + Vector3.right).normalized,
                (Vector3.forward + Vector3.left).normalized,
                (Vector3.back + Vector3.right).normalized,
                (Vector3.back + Vector3.left).normalized
            };

            foreach (float dist in distances)
            {
                foreach (var dir in directions)
                {
                    Vector3 candidate = dangerousPosition + dir * dist;

                    if (!TrapAwarenessController.IsPositionDangerous(candidate))
                    {
                        // NavMesh 위인지 확인
                        if (UnityEngine.AI.NavMesh.SamplePosition(candidate, out var hit, 1f, UnityEngine.AI.NavMesh.AllAreas))
                        {
                            return hit.position;
                        }
                    }
                }
            }

            // 안전한 위치를 찾지 못함
            return dangerousPosition;
        }
    }
}
