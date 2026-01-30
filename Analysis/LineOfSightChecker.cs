// ★ v0.2.50: Line of Sight 체크 유틸리티
// ability.CanTarget()은 LOS를 체크하지 않으므로 별도 구현 필요
// UnitEntityData.HasLOS() 메서드 사용
using System;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// Line of Sight (시야) 체크 유틸리티
    /// 게임의 UnitEntityData.HasLOS() 메서드를 사용
    /// </summary>
    public static class LineOfSightChecker
    {
        /// <summary>
        /// 두 유닛 사이에 시야가 확보되어 있는지 확인
        /// UnitEntityData.CheckLOS() 사용 - hasLOSWithoutDistance로 순수 장애물만 체크
        /// (HasLOS()는 비전 범위도 체크해서 부정확함)
        /// </summary>
        /// <param name="from">공격자</param>
        /// <param name="to">타겟</param>
        /// <returns>true = 시야 확보됨, false = 장애물에 막힘</returns>
        public static bool HasLineOfSight(UnitEntityData from, UnitEntityData to)
        {
            if (from == null || to == null)
                return false;

            try
            {
                // ★ v0.2.50: CheckLOS 사용 - hasLOSWithoutDistance로 순수 장애물만 체크
                // HasLOS()는 VisionRangeMeters도 체크해서 무기 사거리와 맞지 않음
                from.CheckLOS(to.Position, from.Position, out bool hasLOS, out bool hasLOSWithoutDistance);

                if (!hasLOSWithoutDistance)
                {
                    Main.Verbose($"[LOSChecker] {from.CharacterName} -> {to.CharacterName}: BLOCKED by obstacle");
                }

                return hasLOSWithoutDistance;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[LOSChecker] Error checking LOS: {ex.Message}");
                // 에러 발생 시 기본적으로 통과 (false positive보다 나음)
                return true;
            }
        }

        /// <summary>
        /// 특정 위치에서 타겟까지 시야가 확보되어 있는지 확인
        /// (이동 후 공격 가능 여부 체크용)
        /// UnitEntityData.CheckLOS(point, overridePosition) 사용
        /// </summary>
        public static bool HasLineOfSightFromPosition(UnitEntityData from, Vector3 fromPosition, UnitEntityData to)
        {
            if (from == null || to == null)
                return false;

            try
            {
                // CheckLOS(point, overridePosition)로 순수 장애물만 체크
                from.CheckLOS(to.Position, fromPosition, out bool hasLOS, out bool hasLOSWithoutDistance);
                return hasLOSWithoutDistance;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[LOSChecker] Error checking LOS from position: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 원거리 공격이 가능한지 종합 체크 (거리 + LOS)
        /// </summary>
        /// <param name="from">공격자</param>
        /// <param name="to">타겟</param>
        /// <param name="weaponRange">무기 사거리</param>
        /// <returns>true = 공격 가능</returns>
        public static bool CanRangedAttack(UnitEntityData from, UnitEntityData to, float weaponRange)
        {
            if (from == null || to == null)
                return false;

            float distance = Vector3.Distance(from.Position, to.Position);

            // 사거리 체크
            if (distance > weaponRange + 1f)  // 약간의 여유
                return false;

            // LOS 체크
            return HasLineOfSight(from, to);
        }
    }
}
