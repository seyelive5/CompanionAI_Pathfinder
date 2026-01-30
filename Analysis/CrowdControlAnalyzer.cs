// ★ v0.2.49: CC(Crowd Control) 상태 분석기
// 유닛이 CC 상태(기절, 마비, 공포 등)인지 감지
// 해제 능력/아이템 탐색 및 탈출 옵션 제공
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs;
using Kingmaker.Blueprints.Classes.Spells;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// ★ v0.2.49: CC 상태 분석 및 탈출 옵션 탐색
    ///
    /// 감지 대상:
    /// - 행동 불가: Stunned, Paralyzed, Held, Petrified
    /// - 이동 불가: Entangled, Prone, CantMove
    /// - 정신 영향: Confused, Frightened, Dazed, Nauseated
    ///
    /// 탈출 옵션:
    /// - 자가 해제 능력 (Freedom of Movement 등)
    /// - 아군 지원 대기 (Dispel, Remove Paralysis 등)
    /// - 이동 가능 시 위험 지역 탈출
    /// </summary>
    public static class CrowdControlAnalyzer
    {
        #region CC Severity

        /// <summary>
        /// CC 심각도 등급
        /// </summary>
        public enum CCSeverity
        {
            None = 0,       // CC 없음
            Minor = 1,      // 경미 (Shaken, Sickened)
            Moderate = 2,   // 중간 (Entangled, Dazed, Prone)
            Severe = 3,     // 심각 (Stunned, Frightened, Nauseated)
            Critical = 4    // 치명 (Paralyzed, Petrified, Helpless)
        }

        #endregion

        #region Data Classes

        /// <summary>
        /// CC 분석 결과
        /// </summary>
        public class CCAnalysis
        {
            public UnitEntityData Unit { get; set; }
            public bool IsUnderCC { get; set; }
            public CCSeverity MaxSeverity { get; set; }
            public List<ActiveCC> ActiveCCs { get; } = new();
            public List<EscapeOption> EscapeOptions { get; } = new();
            public bool CanAct { get; set; }
            public bool CanMove { get; set; }
            public bool NeedsAllyHelp { get; set; }
        }

        /// <summary>
        /// 활성 CC 정보
        /// </summary>
        public class ActiveCC
        {
            public UnitCondition Condition { get; set; }
            public string ConditionName { get; set; }
            public CCSeverity Severity { get; set; }
            public Buff SourceBuff { get; set; }  // CC를 유발하는 버프
            public bool CanSelfRemove { get; set; }
        }

        /// <summary>
        /// 탈출 옵션
        /// </summary>
        public class EscapeOption
        {
            public EscapeType Type { get; set; }
            public string Description { get; set; }
            public AbilityData Ability { get; set; }  // 해제 능력 (있을 경우)
            public float Priority { get; set; }       // 우선순위 (높을수록 좋음)
        }

        public enum EscapeType
        {
            SelfAbility,    // 자가 해제 능력 (Freedom of Movement)
            WaitForAlly,    // 아군 지원 대기
            FleeIfPossible, // 이동 가능 시 도주
            EndureTurn      // 턴 종료까지 버티기
        }

        #endregion

        #region CC Condition Mappings

        // CC 조건별 심각도 매핑
        private static readonly Dictionary<UnitCondition, CCSeverity> ConditionSeverity = new()
        {
            // Critical (행동/이동 완전 불가)
            { UnitCondition.Paralyzed, CCSeverity.Critical },
            { UnitCondition.Petrified, CCSeverity.Critical },
            { UnitCondition.Helpless, CCSeverity.Critical },
            { UnitCondition.Unconscious, CCSeverity.Critical },
            { UnitCondition.Sleeping, CCSeverity.Critical },

            // Severe (행동 크게 제한)
            { UnitCondition.Stunned, CCSeverity.Severe },
            { UnitCondition.Nauseated, CCSeverity.Severe },
            { UnitCondition.Frightened, CCSeverity.Severe },
            { UnitCondition.Confusion, CCSeverity.Severe },

            // Moderate (부분 제한)
            { UnitCondition.Dazed, CCSeverity.Moderate },
            { UnitCondition.Entangled, CCSeverity.Moderate },
            { UnitCondition.Prone, CCSeverity.Moderate },
            { UnitCondition.CantMove, CCSeverity.Moderate },
            { UnitCondition.MovementBan, CCSeverity.Moderate },
            { UnitCondition.Fatigued, CCSeverity.Moderate },

            // Minor (경미한 페널티)
            { UnitCondition.Shaken, CCSeverity.Minor },
            { UnitCondition.Sickened, CCSeverity.Minor },
            { UnitCondition.Exhausted, CCSeverity.Minor },
        };

        // CC 해제에 효과적인 SpellDescriptor들
        private static readonly SpellDescriptor[] RemovalDescriptors = new[]
        {
            SpellDescriptor.RestoreHP,  // 일부 상태 해제
            SpellDescriptor.Cure,       // 질병/독 등
        };

        #endregion

        #region Public Methods

        /// <summary>
        /// 유닛의 CC 상태 분석
        /// </summary>
        public static CCAnalysis AnalyzeUnit(UnitEntityData unit)
        {
            var result = new CCAnalysis { Unit = unit };

            if (unit == null || !unit.IsInGame)
                return result;

            try
            {
                var state = unit.Descriptor?.State;
                if (state == null)
                    return result;

                // 행동/이동 가능 여부
                result.CanAct = state.CanAct;
                result.CanMove = state.CanMove;

                // 모든 CC 조건 검사
                foreach (var kvp in ConditionSeverity)
                {
                    if (state.HasCondition(kvp.Key))
                    {
                        var cc = new ActiveCC
                        {
                            Condition = kvp.Key,
                            ConditionName = kvp.Key.ToString(),
                            Severity = kvp.Value,
                            SourceBuff = FindSourceBuff(unit, kvp.Key),
                            CanSelfRemove = CanSelfRemoveCondition(unit, kvp.Key)
                        };

                        result.ActiveCCs.Add(cc);

                        if (kvp.Value > result.MaxSeverity)
                            result.MaxSeverity = kvp.Value;
                    }
                }

                result.IsUnderCC = result.ActiveCCs.Count > 0;

                // 탈출 옵션 탐색
                if (result.IsUnderCC)
                {
                    FindEscapeOptions(unit, result);
                }

                // 로깅
                if (result.IsUnderCC)
                {
                    string ccList = string.Join(", ", result.ActiveCCs.Select(c => c.ConditionName));
                    Main.Log($"[CCAnalyzer] {unit.CharacterName}: Severity={result.MaxSeverity}, " +
                        $"CCs=[{ccList}], CanAct={result.CanAct}, CanMove={result.CanMove}");
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[CCAnalyzer] Error analyzing {unit?.CharacterName}: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 유닛이 특정 CC 상태인지 빠르게 확인
        /// </summary>
        public static bool HasCondition(UnitEntityData unit, UnitCondition condition)
        {
            try
            {
                return unit?.Descriptor?.State?.HasCondition(condition) ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 유닛이 행동 가능한지 확인
        /// </summary>
        public static bool CanUnitAct(UnitEntityData unit)
        {
            try
            {
                return unit?.Descriptor?.State?.CanAct ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 유닛이 이동 가능한지 확인
        /// </summary>
        public static bool CanUnitMove(UnitEntityData unit)
        {
            try
            {
                return unit?.Descriptor?.State?.CanMove ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 아군 중 CC 해제 가능한 유닛 찾기
        /// </summary>
        public static UnitEntityData FindAllyWithRemoval(UnitEntityData ccVictim, List<UnitEntityData> allies)
        {
            if (ccVictim == null || allies == null)
                return null;

            foreach (var ally in allies)
            {
                if (ally == null || ally == ccVictim || !ally.IsInGame)
                    continue;

                // 이 아군이 CC 해제 능력을 가지고 있는지 확인
                if (HasRemovalAbility(ally, ccVictim))
                    return ally;
            }

            return null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// CC를 유발하는 버프 찾기
        /// </summary>
        private static Buff FindSourceBuff(UnitEntityData unit, UnitCondition condition)
        {
            try
            {
                var buffs = unit?.Descriptor?.Buffs;
                if (buffs == null)
                    return null;

                // 모든 버프 검사
                foreach (var buff in buffs)
                {
                    if (buff?.Blueprint == null)
                        continue;

                    // 버프의 컴포넌트에서 AddCondition 찾기
                    var components = buff.Blueprint.ComponentsArray;
                    if (components == null)
                        continue;

                    foreach (var comp in components)
                    {
                        // AddCondition 컴포넌트 체크
                        var compType = comp.GetType();
                        if (compType.Name == "AddCondition")
                        {
                            // Reflection으로 Condition 필드 접근
                            var condField = compType.GetField("Condition");
                            if (condField != null)
                            {
                                var condValue = condField.GetValue(comp);
                                if (condValue is UnitCondition buffCondition && buffCondition == condition)
                                {
                                    return buff;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[CCAnalyzer] Error finding source buff: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 자가 해제 가능 여부 확인
        /// </summary>
        private static bool CanSelfRemoveCondition(UnitEntityData unit, UnitCondition condition)
        {
            // 일부 조건은 자가 해제 불가
            switch (condition)
            {
                case UnitCondition.Paralyzed:
                case UnitCondition.Petrified:
                case UnitCondition.Helpless:
                case UnitCondition.Unconscious:
                case UnitCondition.Stunned:
                    return false;  // 행동 자체가 불가능

                case UnitCondition.Prone:
                    return true;  // 일어나기 가능 (Move action)

                default:
                    // 해당 조건을 해제하는 능력이 있는지 확인
                    return HasSelfRemovalAbility(unit, condition);
            }
        }

        /// <summary>
        /// 자가 해제 능력 보유 확인
        /// ★ v0.2.62: AbilityClassifier.GetCCRemovalInfo() 사용 (문자열 검색 제거)
        /// </summary>
        private static bool HasSelfRemovalAbility(UnitEntityData unit, UnitCondition condition)
        {
            try
            {
                var abilities = unit?.Abilities?.Enumerable;
                if (abilities == null)
                    return false;

                foreach (var ability in abilities)
                {
                    var abilityData = ability?.Data;
                    var blueprint = abilityData?.Blueprint;
                    if (blueprint == null)
                        continue;

                    // ★ v0.2.62: 캐시된 CC 해제 정보 사용
                    var removalInfo = AbilityClassifier.GetCCRemovalInfo(blueprint);
                    if (removalInfo != null && removalInfo.CanRemove(condition))
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// CC 해제 능력 보유 확인 (타겟 지정용)
        /// ★ v0.2.62: AbilityClassifier.GetCCRemovalInfo() 사용 (문자열 검색 제거)
        /// </summary>
        private static bool HasRemovalAbility(UnitEntityData caster, UnitEntityData target)
        {
            try
            {
                var abilities = caster?.Abilities?.Enumerable;
                if (abilities == null)
                    return false;

                foreach (var ability in abilities)
                {
                    var abilityData = ability?.Data;
                    var blueprint = abilityData?.Blueprint;
                    if (blueprint == null)
                        continue;

                    // ★ v0.2.62: 캐시된 CC 해제 정보 사용
                    var removalInfo = AbilityClassifier.GetCCRemovalInfo(blueprint);
                    if (removalInfo == null)
                        continue;

                    // 어떤 종류든 CC 해제 가능하면 true
                    bool hasAnyRemoval = removalInfo.RemovesFear ||
                                         removalInfo.RemovesParalysis ||
                                         removalInfo.RemovesMovement ||
                                         removalInfo.RemovesDisease ||
                                         removalInfo.RemovesCurse ||
                                         removalInfo.RemovesPoison ||
                                         removalInfo.RemovesAny ||
                                         removalInfo.IsRestoration ||
                                         removalInfo.IsHeal;

                    if (hasAnyRemoval && abilityData.IsAvailable)
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 탈출 옵션 탐색
        /// </summary>
        private static void FindEscapeOptions(UnitEntityData unit, CCAnalysis analysis)
        {
            // 1. 자가 해제 능력 확인
            var selfRemovalAbility = FindSelfRemovalAbility(unit, analysis.ActiveCCs);
            if (selfRemovalAbility != null)
            {
                analysis.EscapeOptions.Add(new EscapeOption
                {
                    Type = EscapeType.SelfAbility,
                    Description = $"Use {selfRemovalAbility.Name}",
                    Ability = selfRemovalAbility,
                    Priority = 1.0f
                });
            }

            // 2. Prone 상태면 일어나기 (Stand Up)
            if (analysis.ActiveCCs.Any(c => c.Condition == UnitCondition.Prone))
            {
                // Stand up은 Move Action
                if (analysis.CanAct)
                {
                    analysis.EscapeOptions.Add(new EscapeOption
                    {
                        Type = EscapeType.SelfAbility,
                        Description = "Stand up from prone",
                        Priority = 0.9f
                    });
                }
            }

            // 3. 이동 가능하면 도주 옵션
            if (analysis.CanMove)
            {
                // AoE 위험 지역에서의 탈출
                var aoeDanger = AoeDangerAnalyzer.AnalyzeUnit(unit);
                if (aoeDanger.ShouldEscape && aoeDanger.SuggestedEscapePosition.HasValue)
                {
                    analysis.EscapeOptions.Add(new EscapeOption
                    {
                        Type = EscapeType.FleeIfPossible,
                        Description = "Move out of dangerous area",
                        Priority = 0.8f
                    });
                }
            }

            // 4. 아군 지원 필요 판단
            if (analysis.MaxSeverity >= CCSeverity.Severe && !analysis.CanAct)
            {
                analysis.NeedsAllyHelp = true;
                analysis.EscapeOptions.Add(new EscapeOption
                {
                    Type = EscapeType.WaitForAlly,
                    Description = "Wait for ally to dispel/remove CC",
                    Priority = 0.5f
                });
            }

            // 5. 최후의 수단: 턴 종료까지 버티기
            if (analysis.EscapeOptions.Count == 0)
            {
                analysis.EscapeOptions.Add(new EscapeOption
                {
                    Type = EscapeType.EndureTurn,
                    Description = "Endure until CC expires",
                    Priority = 0.1f
                });
            }

            // 우선순위 정렬
            analysis.EscapeOptions.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>
        /// 자가 해제 능력 찾기
        /// ★ v0.2.62: AbilityClassifier.GetCCRemovalInfo() 사용 (문자열 검색 제거)
        /// </summary>
        private static AbilityData FindSelfRemovalAbility(UnitEntityData unit, List<ActiveCC> activeCCs)
        {
            try
            {
                var abilities = unit?.Abilities?.Enumerable;
                if (abilities == null)
                    return null;

                foreach (var ability in abilities)
                {
                    var abilityData = ability?.Data;
                    if (abilityData == null || !abilityData.IsAvailable)
                        continue;

                    var blueprint = abilityData.Blueprint;
                    if (blueprint == null)
                        continue;

                    // ★ v0.2.62: 캐시된 CC 해제 정보 사용
                    var removalInfo = AbilityClassifier.GetCCRemovalInfo(blueprint);
                    if (removalInfo == null)
                        continue;

                    // 현재 CC에 맞는 해제 능력 찾기
                    foreach (var cc in activeCCs)
                    {
                        if (removalInfo.CanRemove(cc.Condition))
                            return abilityData;
                    }
                }
            }
            catch { }

            return null;
        }

        #endregion
    }
}
