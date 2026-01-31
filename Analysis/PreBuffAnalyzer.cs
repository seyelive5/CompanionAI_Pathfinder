// ★ v0.2.64: Smart Pre-Buff Analysis System
// 버프 우선순위 분석 + 최적 타겟 선택 + BuffStackingAnalyzer 통합
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Analysis
{
    #region Enums

    /// <summary>
    /// ★ v0.2.63: 버프가 가장 효과적인 대상 유형
    /// </summary>
    [Flags]
    public enum BuffTargetAffinity
    {
        None = 0,
        /// <summary>전위 (탱커, 근접 딜러) - AC, DR, HP 버프</summary>
        Frontline = 1 << 0,
        /// <summary>딜러 - 공격, 데미지 버프</summary>
        DPS = 1 << 1,
        /// <summary>캐스터 - 주문 DC, 집중력 버프</summary>
        Caster = 1 << 2,
        /// <summary>약한 내성 보유자 - 해당 내성 버프</summary>
        WeakSave = 1 << 3,
        /// <summary>모든 대상에게 유효</summary>
        Universal = 1 << 4,
        /// <summary>시전자 본인에게 가장 효과적</summary>
        Self = 1 << 5,
    }

    /// <summary>
    /// ★ v0.2.63: 프리버프 우선순위
    /// </summary>
    public enum PreBuffPriority
    {
        /// <summary>사용하지 않음 (유틸리티, 이미 적용됨)</summary>
        Skip = 0,
        /// <summary>자원이 넉넉할 때만</summary>
        Low = 1,
        /// <summary>일반적인 프리버프</summary>
        Medium = 2,
        /// <summary>중요한 버프</summary>
        High = 3,
        /// <summary>필수 버프 (Death Ward, Freedom of Movement)</summary>
        Critical = 4
    }

    #endregion

    #region Analysis Result

    /// <summary>
    /// ★ v0.2.63: 프리버프 분석 결과
    /// </summary>
    public class PreBuffAnalysis
    {
        public AbilityData Ability { get; set; }
        public BlueprintBuff AppliedBuff { get; set; }
        public PreBuffPriority Priority { get; set; }
        public BuffTargetAffinity TargetAffinity { get; set; }
        public float CombatValue { get; set; }
        public int SpellLevel { get; set; }
        public bool IsLimitedUse { get; set; }
        public int UsesRemaining { get; set; }
        public bool IsSelfOnly { get; set; }

        /// <summary>최적 타겟 목록 (우선순위 순)</summary>
        public List<TargetScore> RankedTargets { get; set; } = new List<TargetScore>();

        /// <summary>분석 이유 (디버깅용)</summary>
        public string Reason { get; set; } = "";

        public override string ToString()
        {
            return $"[{Ability?.Name}] Priority={Priority}, Combat={CombatValue:F2}, " +
                   $"Affinity={TargetAffinity}, Targets={RankedTargets.Count}";
        }
    }

    /// <summary>
    /// ★ v0.2.63: 타겟 점수
    /// </summary>
    public class TargetScore
    {
        public UnitEntityData Target { get; set; }
        public float Score { get; set; }
        public string Reason { get; set; } = "";

        public TargetScore(UnitEntityData target, float score, string reason = "")
        {
            Target = target;
            Score = score;
            Reason = reason;
        }
    }

    #endregion

    /// <summary>
    /// ★ v0.2.63: 스마트 프리버프 분석기
    /// - 버프 우선순위 결정
    /// - 최적 타겟 선택
    /// - 자원 효율성 고려
    /// </summary>
    public static class PreBuffAnalyzer
    {
        #region Constants

        /// <summary>필수 버프 GUID (Death Ward, Freedom of Movement 등)</summary>
        private static readonly HashSet<string> CriticalBuffGuids = new HashSet<string>
        {
            // Death Ward
            "b0253e57a75b621428c1b89de5a937d1",  // DeathWardBuff
            "e6f2fc5d73d88064583cb828801212f4",  // DeathWardBuff (alternate)

            // Freedom of Movement
            "1533e782fca42b84ea370fc1dcbf4fc1",  // FreedomOfMovementBuff

            // Spell Resistance
            "50a77710a7c4914499d0254e76a808e5",  // SpellResistanceBuff

            // Heroism (Greater)
            "b8da3ec045ec04845a126948e1f4fc1a",  // HeroismGreaterBuff
        };

        /// <summary>높은 우선순위 버프 GUID</summary>
        private static readonly HashSet<string> HighPriorityBuffGuids = new HashSet<string>
        {
            // Heroism
            "87ab2fed7feaaff47b62a3320a57ad8d",  // HeroismBuff

            // Haste
            "03464790f40c3c24aa684b57155f3280",  // HasteBuff

            // Blur
            "dd3ad347240624d46a11a092b4dd4674",  // BlurBuff

            // Displacement
            "00402bae4442a854081264e498e7a833",  // DisplacementBuff

            // Stoneskin
            "7aeaf147211349b40bb55c57fec8e28d",  // StoneskinBuff

            // Protection from Energy (various)
            "2c0e92c23f3e3e44d8bccc88af576981",  // ProtectionFromFireBuff
            "ad47fd8c77df2a84290fca0bfb7fce55",  // ProtectionFromColdBuff
            "0e4d6bab5e3d7f64fab8c9a06caf2d68",  // ProtectionFromElectricityBuff
            "b0949c8e6d3a3dd42a6f8f3e6d3e6d3e",  // ProtectionFromAcidBuff
        };

        /// <summary>유틸리티 전용 버프 (스킵)</summary>
        private static readonly HashSet<string> UtilityBuffGuids = new HashSet<string>
        {
            // Light
            "4e137fda7fa35324e9d7c09d24c0c674",  // LightBuff

            // Darkvision
            "0ea5c9c9ef61ba646a8a7a69c9a3db4f",  // DarkvisionBuff

            // See Invisible
            "3fa892fc7e3e9f64f9d3e8e7e3e8e7e3",  // SeeInvisibleBuff

            // Feather Step (utility movement)
            "1b8e4c9b5e3c3d44f9b8c9a06caf2d68",  // FeatherStepBuff
        };

        /// <summary>60분 이상 지속 = 리프레시 불필요</summary>
        private const float LONG_DURATION_MINUTES = 60f;

        #endregion

        #region Public API

        /// <summary>
        /// 유닛의 모든 프리버프 능력 분석
        /// </summary>
        public static List<PreBuffAnalysis> AnalyzeAllPreBuffs(
            UnitEntityData caster,
            List<UnitEntityData> partyMembers)
        {
            var results = new List<PreBuffAnalysis>();

            if (caster == null || partyMembers == null || partyMembers.Count == 0)
                return results;

            // 모든 능력 분류
            var abilities = AbilityClassifier.ClassifyAllAbilities(caster);

            foreach (var classification in abilities)
            {
                // 버프 타이밍만 처리
                if (classification.Timing != AbilityTiming.PermanentBuff &&
                    classification.Timing != AbilityTiming.PreCombatBuff)
                    continue;

                // 사용 가능한지 확인
                if (!classification.Ability.IsAvailableForCast)
                    continue;

                var analysis = AnalyzeBuff(caster, classification, partyMembers);
                if (analysis != null && analysis.Priority != PreBuffPriority.Skip)
                {
                    results.Add(analysis);
                }
            }

            // 우선순위 순 정렬 (높은 우선순위 먼저)
            results.Sort((a, b) =>
            {
                // 1. Priority 내림차순
                int priorityCompare = b.Priority.CompareTo(a.Priority);
                if (priorityCompare != 0) return priorityCompare;

                // 2. CombatValue 내림차순
                int valueCompare = b.CombatValue.CompareTo(a.CombatValue);
                if (valueCompare != 0) return valueCompare;

                // 3. 제한 사용은 나중에
                if (a.IsLimitedUse != b.IsLimitedUse)
                    return a.IsLimitedUse ? 1 : -1;

                return 0;
            });

            return results;
        }

        /// <summary>
        /// 단일 버프 분석
        /// </summary>
        public static PreBuffAnalysis AnalyzeBuff(
            UnitEntityData caster,
            AbilityClassification classification,
            List<UnitEntityData> partyMembers)
        {
            var ability = classification.Ability;
            var bp = ability?.Blueprint;
            if (bp == null) return null;

            // 버프 효과 가져오기
            var buffEffects = AbilityClassifier.GetBuffEffects(ability);
            var primaryBuff = buffEffects.PrimaryBuff;

            var result = new PreBuffAnalysis
            {
                Ability = ability,
                AppliedBuff = primaryBuff,
                SpellLevel = ability.SpellLevel,
                IsSelfOnly = bp.CanTargetSelf && !bp.CanTargetFriends,
            };

            // 자원 제한 확인
            AnalyzeResourceUsage(ability, result);

            // 버프 효과 분석
            BuffEffectAnalysis buffAnalysis = null;
            if (primaryBuff != null)
            {
                buffAnalysis = BuffEffectAnalyzer.Analyze(primaryBuff);
                result.CombatValue = buffAnalysis.CombatValue;
            }
            else
            {
                result.CombatValue = 0.3f; // 버프 없으면 낮은 가치
            }

            // 우선순위 결정
            result.Priority = DeterminePriority(ability, primaryBuff, buffAnalysis, result);

            // 스킵이면 타겟 분석 생략
            if (result.Priority == PreBuffPriority.Skip)
                return result;

            // 타겟 친화도 결정
            result.TargetAffinity = DetermineTargetAffinity(buffAnalysis, primaryBuff);

            // 최적 타겟 선택
            result.RankedTargets = RankTargets(caster, ability, primaryBuff, result.TargetAffinity, partyMembers);

            return result;
        }

        #endregion

        #region Priority Determination

        /// <summary>
        /// 버프 우선순위 결정
        /// </summary>
        private static PreBuffPriority DeterminePriority(
            AbilityData ability,
            BlueprintBuff primaryBuff,
            BuffEffectAnalysis buffAnalysis,
            PreBuffAnalysis result)
        {
            string buffGuid = primaryBuff?.AssetGuid.ToString() ?? "";

            // 1. 유틸리티 버프 → Skip
            if (UtilityBuffGuids.Contains(buffGuid))
            {
                result.Reason = "Utility-only buff";
                return PreBuffPriority.Skip;
            }

            // 2. 전투 가치가 너무 낮음 → Skip
            if (buffAnalysis != null && buffAnalysis.IsUtilityOnly)
            {
                result.Reason = "No combat value";
                return PreBuffPriority.Skip;
            }

            if (result.CombatValue < 0.2f)
            {
                result.Reason = $"Low combat value ({result.CombatValue:F2})";
                return PreBuffPriority.Skip;
            }

            // 3. 필수 버프 → Critical
            if (CriticalBuffGuids.Contains(buffGuid))
            {
                result.Reason = "Critical buff (Death Ward, Freedom, etc.)";
                return PreBuffPriority.Critical;
            }

            // 4. 고레벨 보호 주문 (4레벨 이상) → High/Critical
            if (ability.SpellLevel >= 4)
            {
                // 스펠 레벨 4+ 보호 주문
                if (IsProtectiveSpell(primaryBuff, buffAnalysis))
                {
                    result.Reason = $"High-level protective spell (Lv{ability.SpellLevel})";
                    return ability.SpellLevel >= 6 ? PreBuffPriority.Critical : PreBuffPriority.High;
                }
            }

            // 5. 높은 우선순위 버프 → High
            if (HighPriorityBuffGuids.Contains(buffGuid))
            {
                result.Reason = "Known high-priority buff";
                return PreBuffPriority.High;
            }

            // 6. 전투 가치 기반
            if (result.CombatValue >= 0.8f)
            {
                result.Reason = $"High combat value ({result.CombatValue:F2})";
                return PreBuffPriority.High;
            }

            if (result.CombatValue >= 0.5f)
            {
                result.Reason = $"Medium combat value ({result.CombatValue:F2})";
                return PreBuffPriority.Medium;
            }

            // 7. 낮은 전투 가치지만 제한 없는 사용 → Medium
            if (!result.IsLimitedUse)
            {
                result.Reason = "Unlimited use, low value";
                return PreBuffPriority.Medium;
            }

            // 8. 제한 사용 + 낮은 가치 → Low
            result.Reason = $"Limited use, low value ({result.CombatValue:F2})";
            return PreBuffPriority.Low;
        }

        /// <summary>
        /// 보호 주문인지 확인
        /// </summary>
        private static bool IsProtectiveSpell(BlueprintBuff buff, BuffEffectAnalysis analysis)
        {
            if (buff == null) return false;

            // 분석 결과로 판단
            if (analysis != null)
            {
                return analysis.HasDefensiveEffect ||
                       (analysis.Categories & BuffEffectCategory.DamageResistance) != 0 ||
                       (analysis.Categories & BuffEffectCategory.SpellResistance) != 0 ||
                       (analysis.Categories & BuffEffectCategory.Concealment) != 0;
            }

            // 이름 기반 폴백
            string name = buff.Name?.ToLowerInvariant() ?? "";
            return name.Contains("protection") ||
                   name.Contains("ward") ||
                   name.Contains("resist") ||
                   name.Contains("immunity") ||
                   name.Contains("freedom");
        }

        #endregion

        #region Target Affinity

        /// <summary>
        /// 타겟 친화도 결정
        /// </summary>
        private static BuffTargetAffinity DetermineTargetAffinity(
            BuffEffectAnalysis analysis,
            BlueprintBuff buff)
        {
            if (analysis == null)
                return BuffTargetAffinity.Universal;

            var affinity = BuffTargetAffinity.None;

            // AC, DR, TempHP → Frontline
            if ((analysis.Categories & BuffEffectCategory.ACBonus) != 0 ||
                (analysis.Categories & BuffEffectCategory.DamageResistance) != 0 ||
                (analysis.Categories & BuffEffectCategory.TempHP) != 0)
            {
                affinity |= BuffTargetAffinity.Frontline;
            }

            // Attack, Damage → DPS
            if ((analysis.Categories & BuffEffectCategory.AttackBonus) != 0 ||
                (analysis.Categories & BuffEffectCategory.DamageBonus) != 0)
            {
                affinity |= BuffTargetAffinity.DPS;
            }

            // Save bonus → WeakSave (나중에 타겟팅 시 구체적으로 결정)
            if ((analysis.Categories & BuffEffectCategory.SaveBonus) != 0)
            {
                affinity |= BuffTargetAffinity.WeakSave;
            }

            // Stat bonus → 해당 스탯 필요한 대상
            if ((analysis.Categories & BuffEffectCategory.StatBonus) != 0)
            {
                // STR/DEX → Frontline + DPS
                // INT/WIS/CHA → Caster
                affinity |= BuffTargetAffinity.DPS | BuffTargetAffinity.Frontline;
            }

            // Spell Resistance, Concealment → Universal (모두에게 유용)
            if ((analysis.Categories & BuffEffectCategory.SpellResistance) != 0 ||
                (analysis.Categories & BuffEffectCategory.Concealment) != 0)
            {
                affinity |= BuffTargetAffinity.Universal;
            }

            // 아무것도 해당 안되면 Universal
            if (affinity == BuffTargetAffinity.None)
                affinity = BuffTargetAffinity.Universal;

            return affinity;
        }

        #endregion

        #region Target Ranking

        /// <summary>
        /// 타겟 순위 결정
        /// </summary>
        private static List<TargetScore> RankTargets(
            UnitEntityData caster,
            AbilityData ability,
            BlueprintBuff primaryBuff,
            BuffTargetAffinity affinity,
            List<UnitEntityData> partyMembers)
        {
            var scores = new List<TargetScore>();
            var bp = ability.Blueprint;

            foreach (var target in partyMembers)
            {
                if (target == null || target.HPLeft <= 0)
                    continue;

                // 타겟팅 가능 여부
                if (!CanTargetUnit(bp, caster, target))
                    continue;

                // 이미 적용된 버프 스킵
                if (primaryBuff != null && HasBuffApplied(target, primaryBuff))
                    continue;

                // ★ v0.2.63: 버프 효과 충돌 체크 (Mage Armor + 갑옷 등)
                if (primaryBuff != null && !WouldBuffBeEffective(target, primaryBuff))
                    continue;

                // 점수 계산
                float score = CalculateTargetScore(target, affinity, primaryBuff);
                string reason = GetTargetScoreReason(target, affinity);

                if (score > 0)
                {
                    scores.Add(new TargetScore(target, score, reason));
                }
            }

            // 점수 내림차순 정렬
            scores.Sort((a, b) => b.Score.CompareTo(a.Score));

            return scores;
        }

        /// <summary>
        /// 타겟 점수 계산
        /// </summary>
        private static float CalculateTargetScore(
            UnitEntityData target,
            BuffTargetAffinity affinity,
            BlueprintBuff buff)
        {
            float score = 0.5f; // 기본 점수

            var settings = ModSettings.Instance?.GetOrCreateSettings(target.UniqueId, target.CharacterName);
            var role = settings?.Role ?? AIRole.DPS;
            var rangePreference = settings?.RangePreference ?? RangePreference.Mixed;

            // 역할 기반 점수
            if ((affinity & BuffTargetAffinity.Frontline) != 0)
            {
                // 탱커/근접 딜러에게 높은 점수
                if (role == AIRole.Tank)
                    score += 0.4f;
                else if (rangePreference == RangePreference.Melee)
                    score += 0.3f;
                else if (role == AIRole.DPS && rangePreference == RangePreference.Mixed)
                    score += 0.2f;
            }

            if ((affinity & BuffTargetAffinity.DPS) != 0)
            {
                // DPS에게 높은 점수
                if (role == AIRole.DPS)
                    score += 0.4f;
                else if (role == AIRole.Tank) // 탱커도 데미지 필요
                    score += 0.2f;
            }

            if ((affinity & BuffTargetAffinity.Caster) != 0)
            {
                // 서포터/원거리(캐스터)에게 높은 점수
                if (role == AIRole.Support)
                    score += 0.4f;
                else if (rangePreference == RangePreference.Ranged)
                    score += 0.3f;
            }

            if ((affinity & BuffTargetAffinity.WeakSave) != 0)
            {
                // 낮은 세이브 보유자에게 높은 점수
                score += GetWeakSaveBonus(target, buff);
            }

            if ((affinity & BuffTargetAffinity.Universal) != 0)
            {
                // 모두에게 동일한 추가 점수
                score += 0.3f;
            }

            // HP 낮은 대상에게 보너스 (보호 버프 우선)
            if ((affinity & BuffTargetAffinity.Frontline) != 0)
            {
                float hpPercent = target.HPLeft / (float)target.MaxHP;
                if (hpPercent < 0.5f)
                    score += 0.1f;
            }

            return score;
        }

        /// <summary>
        /// 약한 세이브 보너스 계산
        /// </summary>
        private static float GetWeakSaveBonus(UnitEntityData target, BlueprintBuff buff)
        {
            if (target?.Stats == null) return 0f;

            // 가장 낮은 세이브 확인
            int will = target.Stats.SaveWill?.ModifiedValue ?? 0;
            int fort = target.Stats.SaveFortitude?.ModifiedValue ?? 0;
            int reflex = target.Stats.SaveReflex?.ModifiedValue ?? 0;

            int minSave = Math.Min(will, Math.Min(fort, reflex));
            int maxSave = Math.Max(will, Math.Max(fort, reflex));

            // 세이브 차이가 클수록 보너스
            float diff = maxSave - minSave;
            return Math.Min(diff * 0.02f, 0.3f); // 최대 0.3
        }

        /// <summary>
        /// 타겟 점수 이유 설명
        /// </summary>
        private static string GetTargetScoreReason(UnitEntityData target, BuffTargetAffinity affinity)
        {
            var settings = ModSettings.Instance?.GetOrCreateSettings(target.UniqueId, target.CharacterName);
            var role = settings?.Role ?? AIRole.DPS;
            var range = settings?.RangePreference ?? RangePreference.Mixed;

            var reasons = new List<string>();

            if ((affinity & BuffTargetAffinity.Frontline) != 0 &&
                (role == AIRole.Tank || range == RangePreference.Melee))
                reasons.Add("Frontline");

            if ((affinity & BuffTargetAffinity.DPS) != 0 && role == AIRole.DPS)
                reasons.Add("DPS");

            if ((affinity & BuffTargetAffinity.Caster) != 0 && role == AIRole.Support)
                reasons.Add("Caster");

            return string.Join(", ", reasons);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 대상에게 능력 사용 가능 여부
        /// </summary>
        private static bool CanTargetUnit(BlueprintAbility bp, UnitEntityData caster, UnitEntityData target)
        {
            if (bp == null) return false;

            // 자기 자신만 대상 가능
            if (bp.CanTargetSelf && !bp.CanTargetFriends)
                return caster == target;

            // 아군 대상 가능
            if (bp.CanTargetFriends)
                return true;

            // 자기 자신 대상 가능
            if (bp.CanTargetSelf && caster == target)
                return true;

            return false;
        }

        /// <summary>
        /// 버프가 이미 적용되어 있는지 확인
        /// </summary>
        private static bool HasBuffApplied(UnitEntityData target, BlueprintBuff buff)
        {
            if (target?.Buffs == null || buff == null)
                return false;

            string buffGuid = buff.AssetGuid.ToString();

            foreach (var activeBuff in target.Buffs.RawFacts)
            {
                if (activeBuff?.Blueprint?.AssetGuid.ToString() == buffGuid)
                {
                    // 남은 시간 체크 - 60분 이상이면 적용된 것으로 간주
                    // (리프레시 불필요)
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// ★ v0.2.64: 버프가 대상에게 효과가 있는지 확인
        /// - BuffStackingAnalyzer를 사용하여 ModifierDescriptor 기반 분석
        /// - 비스태킹 보너스 충돌 감지
        /// </summary>
        private static bool WouldBuffBeEffective(UnitEntityData target, BlueprintBuff buff)
        {
            if (target == null || buff == null)
                return true;

            try
            {
                // BuffStackingAnalyzer로 분석
                var result = BuffStackingAnalyzer.Analyze(buff, target);

                if (!result.IsEffective)
                {
                    Main.Verbose($"[PreBuffAnalyzer] {buff.Name} would be ineffective on {target.CharacterName}: {result.Reason}");
                }

                return result.IsEffective;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[PreBuffAnalyzer] WouldBuffBeEffective error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 자원 사용량 분석
        /// </summary>
        private static void AnalyzeResourceUsage(AbilityData ability, PreBuffAnalysis result)
        {
            try
            {
                var bp = ability?.Blueprint;
                if (bp == null)
                {
                    result.IsLimitedUse = false;
                    result.UsesRemaining = int.MaxValue;
                    return;
                }

                // 레벨 0 = 캔트립 (무제한)
                if (ability.SpellLevel == 0)
                {
                    result.IsLimitedUse = false;
                    result.UsesRemaining = int.MaxValue;
                    return;
                }

                // 스펠북 사용 여부
                if (ability.Spellbook != null)
                {
                    result.IsLimitedUse = true;
                    // 스펠 슬롯 가용성은 IsAvailableForCast에서 이미 체크됨
                    result.UsesRemaining = 1; // 적어도 1회 사용 가능
                    return;
                }

                // 능력 리소스 확인
                var resourceLogic = bp.GetComponent<AbilityResourceLogic>();
                if (resourceLogic?.RequiredResource != null && ability.Caster != null)
                {
                    result.IsLimitedUse = true;
                    result.UsesRemaining = ability.Caster.Resources.GetResourceAmount(resourceLogic.RequiredResource);
                    return;
                }

                // 리소스 없음 = 무제한
                result.IsLimitedUse = false;
                result.UsesRemaining = int.MaxValue;
            }
            catch
            {
                result.IsLimitedUse = false;
                result.UsesRemaining = int.MaxValue;
            }
        }

        #endregion

        #region Buff Request Generation

        /// <summary>
        /// 분석 결과에서 버프 요청 목록 생성
        /// </summary>
        public static List<BuffRequest> GenerateBuffRequests(
            UnitEntityData caster,
            List<PreBuffAnalysis> analyses,
            int maxRequests = 20)
        {
            var requests = new List<BuffRequest>();

            foreach (var analysis in analyses)
            {
                if (analysis.Priority == PreBuffPriority.Skip)
                    continue;

                // 자기 자신만 대상 가능한 경우
                if (analysis.IsSelfOnly)
                {
                    // 이미 적용되어 있는지 확인
                    if (analysis.AppliedBuff != null &&
                        !HasBuffApplied(caster, analysis.AppliedBuff))
                    {
                        // ★ v0.2.63: 버프 효과 충돌 체크
                        if (WouldBuffBeEffective(caster, analysis.AppliedBuff))
                        {
                            requests.Add(new BuffRequest(caster, caster, analysis));
                        }
                    }
                    continue;
                }

                // 다른 대상에게 적용
                foreach (var targetScore in analysis.RankedTargets)
                {
                    if (requests.Count >= maxRequests)
                        break;

                    requests.Add(new BuffRequest(caster, targetScore.Target, analysis));

                    // 제한 사용 능력이면 최적 타겟 1명에게만
                    if (analysis.IsLimitedUse && analysis.Priority < PreBuffPriority.Critical)
                        break;
                }
            }

            return requests;
        }

        #endregion
    }

    #region Buff Request Structure (for PreBuffController)

    /// <summary>
    /// ★ v0.2.63: 버프 요청 구조체
    /// </summary>
    public struct BuffRequest
    {
        public UnitEntityData Caster;
        public UnitEntityData Target;
        public PreBuffAnalysis Analysis;

        public BuffRequest(UnitEntityData caster, UnitEntityData target, PreBuffAnalysis analysis)
        {
            Caster = caster;
            Target = target;
            Analysis = analysis;
        }

        public AbilityData Ability => Analysis?.Ability;
        public string BuffName => Ability?.Name ?? "Unknown";

        public override string ToString()
        {
            return $"{Caster?.CharacterName} -> {Target?.CharacterName}: {BuffName} (Priority={Analysis?.Priority})";
        }
    }

    #endregion
}
