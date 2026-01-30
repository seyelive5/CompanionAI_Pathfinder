// ★ v0.2.49: AoE 위험 지역 분석기
// ★ v0.2.59: NavMesh 검증 추가
// 유닛이 적대적 지역 효과(Grease, Entangle, 화염 지역 등) 안에 있는지 감지
// 이탈 필요 여부 및 안전한 방향 계산
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints.Classes.Spells;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.View;
using UnityEngine;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// ★ v0.2.49: AoE 위험 지역 분석
    ///
    /// 감지 대상:
    /// - 적이 시전한 지역 효과 (화염, 산성, 독 등)
    /// - 이동 방해 효과 (Grease, Entangle, Web 등)
    /// - 지속 피해 지역 (Cloudkill, Blade Barrier 등)
    /// </summary>
    public static class AoeDangerAnalyzer
    {
        #region Constants

        // 위험 수준 임계값
        private const float DANGER_THRESHOLD_ESCAPE = 0.5f;  // 이 이상이면 탈출 시도
        private const float DANGER_THRESHOLD_AVOID = 0.3f;   // 이 이상이면 진입 회피

        // 탈출 방향 계산용
        private const float ESCAPE_DISTANCE = 5f;  // 기본 탈출 거리 (미터)
        private const int DIRECTION_SAMPLES = 8;    // 탈출 방향 샘플 수

        #endregion

        #region Data Classes

        /// <summary>
        /// 유닛의 AoE 위험 분석 결과
        /// </summary>
        public class DangerAnalysis
        {
            public UnitEntityData Unit { get; set; }
            public bool IsInDanger { get; set; }
            public float TotalDangerLevel { get; set; }  // 0.0 ~ 1.0+
            public List<DangerousEffect> DangerousEffects { get; } = new();
            public Vector3? SuggestedEscapePosition { get; set; }
            public bool ShouldEscape => TotalDangerLevel >= DANGER_THRESHOLD_ESCAPE;
        }

        /// <summary>
        /// 개별 위험 효과 정보
        /// </summary>
        public class DangerousEffect
        {
            public AreaEffectEntityData AreaEffect { get; set; }
            public string Name { get; set; }
            public float DangerLevel { get; set; }
            public SpellDescriptor Descriptors { get; set; }
            public bool IsDamaging { get; set; }
            public bool IsMovementImpairing { get; set; }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 유닛의 현재 AoE 위험 상황 분석
        /// </summary>
        public static DangerAnalysis AnalyzeUnit(UnitEntityData unit)
        {
            var result = new DangerAnalysis { Unit = unit };

            if (unit == null || !unit.IsInGame)
                return result;

            try
            {
                // 게임의 모든 지역 효과 검사
                var areaEffects = Game.Instance?.State?.AreaEffects;
                if (areaEffects == null)
                    return result;

                foreach (var aoe in areaEffects)
                {
                    if (aoe == null || !aoe.IsInGame)
                        continue;

                    // 유닛이 이 AoE 안에 있는지 확인
                    if (!IsUnitInsideEffect(unit, aoe))
                        continue;

                    // 이 AoE가 유닛에게 위험한지 판단
                    var danger = EvaluateEffectDanger(unit, aoe);
                    if (danger != null && danger.DangerLevel > 0)
                    {
                        result.DangerousEffects.Add(danger);
                        result.TotalDangerLevel += danger.DangerLevel;
                    }
                }

                result.IsInDanger = result.DangerousEffects.Count > 0;

                // 탈출이 필요하면 안전한 위치 계산
                if (result.ShouldEscape)
                {
                    result.SuggestedEscapePosition = CalculateEscapePosition(unit, result.DangerousEffects);
                }

                if (result.IsInDanger)
                {
                    Main.Log($"[AoeDanger] {unit.CharacterName}: Danger={result.TotalDangerLevel:F2}, " +
                        $"Effects={result.DangerousEffects.Count}, ShouldEscape={result.ShouldEscape}");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[AoeDanger] Error analyzing {unit?.CharacterName}: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 특정 위치가 위험 지역인지 확인 (이동 계획용)
        /// </summary>
        public static bool IsPositionDangerous(UnitEntityData unit, Vector3 position)
        {
            if (unit == null)
                return false;

            try
            {
                var areaEffects = Game.Instance?.State?.AreaEffects;
                if (areaEffects == null)
                    return false;

                foreach (var aoe in areaEffects)
                {
                    if (aoe == null || !aoe.IsInGame)
                        continue;

                    // 위치가 AoE 범위 안인지 확인
                    if (!IsPositionInsideEffect(position, aoe))
                        continue;

                    // 유닛에게 위험한 AoE인지 확인
                    if (IsEffectHarmfulToUnit(unit, aoe))
                        return true;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[AoeDanger] Error checking position: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// 모든 위험 지역 효과 목록 반환 (디버그/UI용)
        /// </summary>
        public static List<AreaEffectEntityData> GetAllDangerousEffects(UnitEntityData forUnit)
        {
            var result = new List<AreaEffectEntityData>();

            try
            {
                var areaEffects = Game.Instance?.State?.AreaEffects;
                if (areaEffects == null)
                    return result;

                foreach (var aoe in areaEffects)
                {
                    if (aoe == null || !aoe.IsInGame)
                        continue;

                    if (IsEffectHarmfulToUnit(forUnit, aoe))
                        result.Add(aoe);
                }
            }
            catch { }

            return result;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 유닛이 지역 효과 안에 있는지 확인
        /// </summary>
        private static bool IsUnitInsideEffect(UnitEntityData unit, AreaEffectEntityData aoe)
        {
            try
            {
                // 게임 API 사용: InGameUnitsInside
                var unitsInside = aoe.InGameUnitsInside;
                return unitsInside?.Contains(unit) ?? false;
            }
            catch
            {
                // 폴백: 거리 기반 체크
                return IsPositionInsideEffect(unit.Position, aoe);
            }
        }

        /// <summary>
        /// 위치가 지역 효과 범위 안인지 확인
        /// </summary>
        private static bool IsPositionInsideEffect(Vector3 position, AreaEffectEntityData aoe)
        {
            try
            {
                // AoE 중심과의 거리 계산
                float distance = Vector3.Distance(position, (aoe.View?.transform?.position) ?? aoe.Position);

                // Blueprint에서 반경 가져오기
                var blueprint = aoe.Blueprint;
                if (blueprint == null)
                    return false;

                // Size 필드에서 반경 추정 (Shape에 따라 다름)
                float radius = blueprint.Size.Meters;

                return distance <= radius;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// AoE가 유닛에게 위험한지 판단
        /// </summary>
        private static bool IsEffectHarmfulToUnit(UnitEntityData unit, AreaEffectEntityData aoe)
        {
            try
            {
                var context = aoe.Context;
                if (context == null)
                    return false;

                // 시전자 확인
                var caster = context.MaybeCaster;

                // 아군이 시전한 효과는 일반적으로 안전
                if (caster != null && caster.IsPlayerFaction == unit.IsPlayerFaction)
                {
                    // 단, AffectEnemies와 동시에 AffectAllies가 false면 안전
                    var blueprint = aoe.Blueprint;
                    if (blueprint != null && !blueprint.AffectEnemies)
                        return false;

                    // 아군 타겟 가능하고 적 타겟 가능하면 위험할 수 있음
                    // (Fireball, Cloudkill 등)
                    if (blueprint != null && blueprint.AffectEnemies && blueprint.CanTargetAllies)
                    {
                        // SpellDescriptor로 판단
                        var desc = context.SpellDescriptor;
                        if (IsDamagingDescriptor(desc) || IsMovementImpairingDescriptor(desc))
                            return true;
                    }

                    return false;
                }

                // 적이 시전한 효과
                if (caster != null && caster.IsPlayerFaction != unit.IsPlayerFaction)
                {
                    var blueprint = aoe.Blueprint;

                    // 적 전용 효과면 체크
                    if (blueprint != null && blueprint.AffectEnemies && !blueprint.CanTargetAllies)
                        return false;  // 적에게만 영향 → 아군에게 안전

                    // 아군에게 영향을 주는 적의 AoE → 위험
                    if (blueprint != null && blueprint.CanTargetAllies)
                        return true;

                    // 모두에게 영향 (AffectEnemies && AffectAllies 둘 다 true 또는 기본값)
                    return true;
                }

                // 시전자 없음 (환경 효과 등) - SpellDescriptor로 판단
                var descriptor = context.SpellDescriptor;
                return IsDamagingDescriptor(descriptor) || IsMovementImpairingDescriptor(descriptor);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 지역 효과의 위험도 평가
        /// </summary>
        private static DangerousEffect EvaluateEffectDanger(UnitEntityData unit, AreaEffectEntityData aoe)
        {
            if (!IsEffectHarmfulToUnit(unit, aoe))
                return null;

            var result = new DangerousEffect
            {
                AreaEffect = aoe,
                Name = aoe.Blueprint?.name ?? "Unknown"
            };

            try
            {
                var context = aoe.Context;
                var descriptor = context?.SpellDescriptor ?? SpellDescriptor.None;
                result.Descriptors = descriptor;

                // 피해 유형 확인
                result.IsDamaging = IsDamagingDescriptor(descriptor);
                result.IsMovementImpairing = IsMovementImpairingDescriptor(descriptor);

                // 위험도 계산
                float dangerLevel = 0f;

                // 직접 피해 효과 (Fire, Acid, Electricity 등)
                if (result.IsDamaging)
                    dangerLevel += 0.6f;

                // 이동 방해 (Entangle, Grease, Web)
                if (result.IsMovementImpairing)
                    dangerLevel += 0.4f;

                // 특수 위험 효과
                if ((descriptor & SpellDescriptor.Death) != 0)
                    dangerLevel += 0.8f;
                if ((descriptor & SpellDescriptor.Poison) != 0)
                    dangerLevel += 0.3f;
                if ((descriptor & SpellDescriptor.Stun) != 0)
                    dangerLevel += 0.5f;
                if ((descriptor & SpellDescriptor.Paralysis) != 0)
                    dangerLevel += 0.6f;
                if ((descriptor & SpellDescriptor.Fear) != 0)
                    dangerLevel += 0.3f;

                // 최대 1.0으로 제한
                result.DangerLevel = Mathf.Clamp01(dangerLevel);

                Main.Verbose($"[AoeDanger] Effect '{result.Name}': Danger={result.DangerLevel:F2}, " +
                    $"Desc={descriptor}, Damaging={result.IsDamaging}, MovImpair={result.IsMovementImpairing}");
            }
            catch (Exception ex)
            {
                Main.Error($"[AoeDanger] Error evaluating effect: {ex.Message}");
                result.DangerLevel = 0.3f;  // 알 수 없는 효과는 중간 위험도
            }

            return result;
        }

        /// <summary>
        /// 안전한 탈출 위치 계산
        /// ★ v0.2.59: NavMesh 검증 추가
        /// </summary>
        private static Vector3? CalculateEscapePosition(UnitEntityData unit, List<DangerousEffect> dangers)
        {
            if (unit == null || dangers == null || dangers.Count == 0)
                return null;

            try
            {
                Vector3 unitPos = unit.Position;
                Vector3 bestPosition = unitPos;
                float bestScore = float.MinValue;

                // 여러 방향 샘플링
                for (int i = 0; i < DIRECTION_SAMPLES; i++)
                {
                    float angle = (360f / DIRECTION_SAMPLES) * i;
                    Vector3 direction = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                    Vector3 testPosition = unitPos + direction * ESCAPE_DISTANCE;

                    // ★ v0.2.59: NavMesh 위치 검증
                    if (!IsPositionValid(unitPos, testPosition))
                        continue;  // 이동 불가능한 위치 스킵

                    // 이 위치의 안전도 계산
                    float safetyScore = CalculatePositionSafety(testPosition, dangers);

                    if (safetyScore > bestScore)
                    {
                        bestScore = safetyScore;
                        bestPosition = testPosition;
                    }
                }

                // 현재 위치보다 나은 곳이 있으면 반환
                float currentSafety = CalculatePositionSafety(unitPos, dangers);
                if (bestScore > currentSafety + 0.1f)
                {
                    Main.Verbose($"[AoeDanger] Escape position found: {bestPosition} (safety={bestScore:F2} vs current={currentSafety:F2})");
                    return bestPosition;
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[AoeDanger] Error calculating escape: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// ★ v0.2.59: 위치가 유효하고 도달 가능한지 확인
        /// </summary>
        private static bool IsPositionValid(Vector3 from, Vector3 to)
        {
            try
            {
                // 1. 목표 위치가 NavMesh 위에 있는지 확인
                if (!ObstacleAnalyzer.IsPointInsideNavMesh(to))
                {
                    // NavMesh 밖이면 가장 가까운 유효 위치로 스냅
                    var nearest = ObstacleAnalyzer.GetNearestNode(to, null);
                    if (nearest.node == null)
                        return false;

                    // 스냅된 위치가 원래 위치에서 너무 멀면 무효
                    if (Vector3.Distance(to, nearest.position) > 2f)
                        return false;
                }

                // 2. 경로가 막혀있는지 확인
                Vector3 blocked = ObstacleAnalyzer.TraceAlongNavmesh(from, to);
                float pathBlocked = Vector3.Distance(blocked, to);

                // 목표 지점과 막힌 지점 차이가 1m 이상이면 경로 막힘
                if (pathBlocked > 1f)
                    return false;

                return true;
            }
            catch
            {
                return true;  // 오류 시 기존 동작 유지
            }
        }

        /// <summary>
        /// 특정 위치의 안전도 계산 (높을수록 안전)
        /// </summary>
        private static float CalculatePositionSafety(Vector3 position, List<DangerousEffect> dangers)
        {
            float safety = 1.0f;

            foreach (var danger in dangers)
            {
                var aoe = danger.AreaEffect;
                if (aoe == null)
                    continue;

                try
                {
                    Vector3 aoeCenter = (aoe.View?.transform?.position) ?? aoe.Position;
                    float distance = Vector3.Distance(position, aoeCenter);

                    // AoE 반경
                    float radius = aoe.Blueprint?.Size.Meters ?? 5f;

                    if (distance < radius)
                    {
                        // AoE 안에 있으면 위험도만큼 안전도 감소
                        safety -= danger.DangerLevel;
                    }
                    else
                    {
                        // AoE 밖이면 거리에 따라 약간의 안전도 보너스
                        float distanceBonus = Mathf.Clamp01((distance - radius) / radius) * 0.2f;
                        safety += distanceBonus;
                    }
                }
                catch { }
            }

            return safety;
        }

        /// <summary>
        /// 피해 관련 SpellDescriptor 확인
        /// </summary>
        private static bool IsDamagingDescriptor(SpellDescriptor descriptor)
        {
            // 피해 유형 디스크립터들
            return (descriptor & (
                SpellDescriptor.Fire |
                SpellDescriptor.Cold |
                SpellDescriptor.Acid |
                SpellDescriptor.Electricity |
                SpellDescriptor.Sonic |
                SpellDescriptor.Force |
                SpellDescriptor.Death
            )) != 0;
        }

        /// <summary>
        /// 이동 방해 SpellDescriptor 확인
        /// </summary>
        private static bool IsMovementImpairingDescriptor(SpellDescriptor descriptor)
        {
            return (descriptor & (
                SpellDescriptor.MovementImpairing |
                SpellDescriptor.Ground |
                SpellDescriptor.Paralysis |
                SpellDescriptor.Stun
            )) != 0;
        }

        #endregion
    }
}
