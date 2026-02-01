using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.GameInterface;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.Scoring;

namespace CompanionAI_Pathfinder.Core.TurnBased
{
    /// <summary>
    /// ★ v0.2.117: 턴 계획 빌더
    /// 역할에 따라 전체 턴 계획을 수립
    /// </summary>
    public class TurnPlanBuilder
    {
        #region Singleton

        private static TurnPlanBuilder _instance;
        public static TurnPlanBuilder Instance => _instance ?? (_instance = new TurnPlanBuilder());

        #endregion

        #region Dependencies

        private readonly SituationAnalyzer _analyzer;

        #endregion

        #region Constructor

        private TurnPlanBuilder()
        {
            _analyzer = new SituationAnalyzer();
        }

        #endregion

        #region Main Build Method

        /// <summary>
        /// 턴 계획 수립
        /// </summary>
        public TurnPlan BuildPlan(UnitEntityData unit, TurnState turnState)
        {
            string unitName = unit?.CharacterName ?? "Unknown";
            var plan = new TurnPlan(unit.UniqueId, GameAPI.GetCurrentRound());

            try
            {
                // 1. 상황 분석
                var situation = _analyzer.Analyze(unit, turnState);
                if (situation == null)
                {
                    Main.Log($"[PlanBuilder] {unitName}: Analysis failed");
                    plan.AddStep(CreateEndTurnStep("Analysis failed"));
                    return plan;
                }

                // 2. 역할 확인
                var settings = ModSettings.Instance?.GetOrCreateSettings(unit.UniqueId, unitName);
                var role = settings?.Role ?? AIRole.DPS;

                // 원거리/근거리 판별
                bool isRanged = !IsMeleeUnit(unit);

                Main.Log($"[PlanBuilder] {unitName}: Building plan for {role} (Ranged={isRanged})");

                // 3. 역할별 계획 수립
                switch (role)
                {
                    case AIRole.Tank:
                        BuildTankPlan(plan, unit, situation, turnState);
                        break;
                    case AIRole.Support:
                        BuildSupportPlan(plan, unit, situation, turnState);
                        break;
                    case AIRole.DPS:
                    default:
                        // DPS는 원거리/근거리 구분
                        if (isRanged)
                            BuildRangedPlan(plan, unit, situation, turnState);
                        else
                            BuildDPSPlan(plan, unit, situation, turnState);
                        break;
                }

                // 4. 스냅샷 저장
                var primaryTarget = GetPrimaryTarget(situation);
                plan.Snapshot = PlanSnapshot.Create(
                    unit,
                    primaryTarget,
                    situation.Enemies?.Count ?? 0,
                    situation.HittableEnemies?.Count ?? 0);

                plan.LogPlan(unitName);
                return plan;
            }
            catch (Exception ex)
            {
                Main.Error($"[PlanBuilder] {unitName}: Exception - {ex.Message}");
                plan.AddStep(CreateEndTurnStep($"Exception: {ex.Message}"));
                return plan;
            }
        }

        #endregion

        #region Role-Specific Plans

        /// <summary>
        /// 탱커 계획: 적 밀집 지역으로 진입, 위협 유지
        /// </summary>
        private void BuildTankPlan(TurnPlan plan, UnitEntityData unit, Situation situation, TurnState turnState)
        {
            bool hasStandard = turnState?.HasStandardAction ?? true;
            bool hasMove = turnState?.HasMoveAction ?? true;
            bool hasSwift = turnState?.HasSwiftAction ?? true;

            // Phase 1: Swift 버프 (있으면)
            if (hasSwift)
            {
                var swiftBuff = FindBestBuff(unit, situation, isSwift: true, preferSelf: true);
                if (swiftBuff != null)
                {
                    plan.AddStep(CreateBuffStep(swiftBuff, unit, ActionCostType.Swift));
                }
            }

            // Phase 2: 공격 가능 여부 확인
            var bestTarget = FindBestAttackTarget(unit, situation);
            var bestAttack = FindBestAttack(unit, situation, bestTarget);

            if (bestTarget != null && bestAttack != null)
            {
                float distance = Vector3.Distance(unit.Position, bestTarget.Position);
                float attackRange = GetAbilityRange(bestAttack, unit);

                // 이동 필요?
                if (distance > attackRange && hasMove)
                {
                    var attackPos = SafePositionCalculator.GetAttackPosition(
                        unit, bestTarget, attackRange, GetMoveDistance(unit));
                    if (attackPos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(attackPos.Value, PlanStepType.MoveToAttack,
                            $"Move to attack {bestTarget.CharacterName}"));
                    }
                }

                // 공격
                if (hasStandard)
                {
                    plan.AddStep(CreateAttackStep(bestAttack, bestTarget));
                }
            }
            else
            {
                // 공격 불가 - 적 밀집 지역으로 접근
                if (hasMove && situation.Enemies?.Count > 0)
                {
                    // ★ v0.2.115: 이동할 타겟과 공격 미리 선정
                    var closestEnemy = situation.Enemies
                        .Where(e => e.HPLeft > 0)
                        .OrderBy(e => Vector3.Distance(unit.Position, e.Position))
                        .FirstOrDefault();

                    var engagePos = SafePositionCalculator.GetEngagePosition(
                        unit, situation.Enemies, GetMoveDistance(unit));

                    if (engagePos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(engagePos.Value, PlanStepType.MoveToEngage,
                            "Advance toward enemies"));

                        // ★ v0.2.115: 이동 후 공격 스텝 추가 (범위 체크 없이)
                        if (hasStandard && closestEnemy != null)
                        {
                            var attack = FindBestAttackIgnoreRange(unit, situation);
                            if (attack != null)
                            {
                                plan.AddStep(CreateAttackStep(attack, closestEnemy));
                            }
                        }
                    }
                }
            }

            // 계획이 비어있으면 턴 종료
            if (plan.Steps.Count == 0)
            {
                plan.AddStep(CreateEndTurnStep("No actions available"));
            }
        }

        /// <summary>
        /// 원거리 계획: 안전거리 유지, 사격
        /// </summary>
        private void BuildRangedPlan(TurnPlan plan, UnitEntityData unit, Situation situation, TurnState turnState)
        {
            bool hasStandard = turnState?.HasStandardAction ?? true;
            bool hasMove = turnState?.HasMoveAction ?? true;
            bool hasSwift = turnState?.HasSwiftAction ?? true;

            // Phase 1: Swift 버프 (자가 버프 우선)
            if (hasSwift)
            {
                var swiftBuff = FindBestBuff(unit, situation, isSwift: true, preferSelf: true);
                if (swiftBuff != null)
                {
                    plan.AddStep(CreateBuffStep(swiftBuff, unit, ActionCostType.Swift));
                }
            }

            // Phase 2: 공격 가능 타겟 확인
            var bestTarget = FindBestAttackTarget(unit, situation);
            var bestAttack = FindBestAttack(unit, situation, bestTarget);

            if (bestTarget != null && bestAttack != null)
            {
                float distance = Vector3.Distance(unit.Position, bestTarget.Position);
                float attackRange = GetAbilityRange(bestAttack, unit);

                // 이동 필요?
                if (distance > attackRange && hasMove)
                {
                    var attackPos = SafePositionCalculator.GetAttackPosition(
                        unit, bestTarget, attackRange, GetMoveDistance(unit));
                    if (attackPos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(attackPos.Value, PlanStepType.MoveToAttack,
                            $"Move to shoot {bestTarget.CharacterName}"));
                    }
                }

                // 공격
                if (hasStandard)
                {
                    plan.AddStep(CreateAttackStep(bestAttack, bestTarget));
                }
            }
            else
            {
                // 공격 불가 - 안전 위치로 후퇴
                if (hasMove && situation.Enemies?.Count > 0)
                {
                    var safePos = SafePositionCalculator.GetSafeRetreatPosition(
                        unit, situation.Enemies, situation.Allies, GetMoveDistance(unit));
                    if (safePos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(safePos.Value, PlanStepType.MoveToSafety,
                            "Retreat to safe position"));
                    }
                }
            }

            if (plan.Steps.Count == 0)
            {
                plan.AddStep(CreateEndTurnStep("No actions available"));
            }
        }

        /// <summary>
        /// 서포트 계획: 팀 버프 우선, 안전 유지
        /// </summary>
        private void BuildSupportPlan(TurnPlan plan, UnitEntityData unit, Situation situation, TurnState turnState)
        {
            bool hasStandard = turnState?.HasStandardAction ?? true;
            bool hasMove = turnState?.HasMoveAction ?? true;
            bool hasSwift = turnState?.HasSwiftAction ?? true;

            // Phase 1: Swift 버프 (팀 버프 우선)
            if (hasSwift)
            {
                var swiftBuff = FindBestBuff(unit, situation, isSwift: true, preferSelf: false);
                if (swiftBuff != null)
                {
                    var target = FindBestBuffTarget(unit, situation, swiftBuff);
                    plan.AddStep(CreateBuffStep(swiftBuff, target ?? unit, ActionCostType.Swift));
                }
            }

            // Phase 2: Standard 버프 (팀 버프 우선)
            if (hasStandard)
            {
                var standardBuff = FindBestBuff(unit, situation, isSwift: false, preferSelf: false);
                if (standardBuff != null)
                {
                    var target = FindBestBuffTarget(unit, situation, standardBuff);
                    plan.AddStep(CreateBuffStep(standardBuff, target ?? unit, ActionCostType.Standard));
                }
                else
                {
                    // 버프 없으면 공격 시도
                    var bestTarget = FindBestAttackTarget(unit, situation);
                    var bestAttack = FindBestAttack(unit, situation, bestTarget);
                    if (bestTarget != null && bestAttack != null)
                    {
                        float distance = Vector3.Distance(unit.Position, bestTarget.Position);
                        float attackRange = GetAbilityRange(bestAttack, unit);

                        if (distance <= attackRange)
                        {
                            plan.AddStep(CreateAttackStep(bestAttack, bestTarget));
                        }
                    }
                }
            }

            // Phase 3: 안전 위치 이동 (남은 Move)
            if (hasMove && situation.Enemies?.Count > 0)
            {
                // 위험한 위치인지 확인
                bool inDanger = IsInEnemyRange(unit, situation.Enemies);
                if (inDanger)
                {
                    var safePos = SafePositionCalculator.GetSafeRetreatPosition(
                        unit, situation.Enemies, situation.Allies, GetMoveDistance(unit));
                    if (safePos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(safePos.Value, PlanStepType.MoveToSafety,
                            "Retreat to safety"));
                    }
                }
            }

            if (plan.Steps.Count == 0)
            {
                plan.AddStep(CreateEndTurnStep("No actions available"));
            }
        }

        /// <summary>
        /// DPS (근접) 계획: 자가 버프, 탱커 뒤따라 공격
        /// </summary>
        private void BuildDPSPlan(TurnPlan plan, UnitEntityData unit, Situation situation, TurnState turnState)
        {
            bool hasStandard = turnState?.HasStandardAction ?? true;
            bool hasMove = turnState?.HasMoveAction ?? true;
            bool hasSwift = turnState?.HasSwiftAction ?? true;
            bool isMelee = IsMeleeUnit(unit);

            // Phase 1: Swift 버프 (자가 버프 우선)
            if (hasSwift)
            {
                var swiftBuff = FindBestBuff(unit, situation, isSwift: true, preferSelf: true);
                if (swiftBuff != null)
                {
                    plan.AddStep(CreateBuffStep(swiftBuff, unit, ActionCostType.Swift));
                }
            }

            // Phase 2: 공격 타겟 확인
            var bestTarget = FindBestAttackTarget(unit, situation);
            var bestAttack = FindBestAttack(unit, situation, bestTarget);

            if (bestTarget != null && bestAttack != null)
            {
                float distance = Vector3.Distance(unit.Position, bestTarget.Position);
                float attackRange = GetAbilityRange(bestAttack, unit);

                // 근접 딜러의 경우 탱커 체크
                if (isMelee && distance > attackRange && hasMove)
                {
                    var tank = SafePositionCalculator.FindTankInParty(unit, situation.Allies);
                    if (tank != null)
                    {
                        // 탱커가 적에게 더 가까운지 확인
                        float tankDistToEnemy = Vector3.Distance(tank.Position, bestTarget.Position);
                        if (tankDistToEnemy > distance + 3f)
                        {
                            // 탱커보다 먼저 뛰어들지 않음 - 탱커 뒤로
                            var behindTank = SafePositionCalculator.GetPositionBehindTank(
                                unit, tank, situation.Enemies, GetMoveDistance(unit));
                            if (behindTank.HasValue)
                            {
                                plan.AddStep(CreateMoveStep(behindTank.Value, PlanStepType.MoveToAttack,
                                    "Follow tank"));
                                // 공격은 다음 턴에
                                if (plan.Steps.Count == 0)
                                {
                                    plan.AddStep(CreateEndTurnStep("Waiting for tank"));
                                }
                                return;
                            }
                        }
                    }

                    // 탱커 없거나 탱커가 더 가까우면 직접 접근
                    var attackPos = SafePositionCalculator.GetAttackPosition(
                        unit, bestTarget, attackRange, GetMoveDistance(unit));
                    if (attackPos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(attackPos.Value, PlanStepType.MoveToAttack,
                            $"Move to attack {bestTarget.CharacterName}"));
                    }
                }
                else if (distance > attackRange && hasMove)
                {
                    // 원거리 DPS
                    var attackPos = SafePositionCalculator.GetAttackPosition(
                        unit, bestTarget, attackRange, GetMoveDistance(unit));
                    if (attackPos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(attackPos.Value, PlanStepType.MoveToAttack,
                            $"Move to attack {bestTarget.CharacterName}"));
                    }
                }

                // 공격
                if (hasStandard)
                {
                    plan.AddStep(CreateAttackStep(bestAttack, bestTarget));
                }
            }
            else
            {
                // 공격 불가
                if (hasMove && situation.Enemies?.Count > 0)
                {
                    // DPS는 공격 못하면 안전 위치로
                    var safePos = SafePositionCalculator.GetSafeRetreatPosition(
                        unit, situation.Enemies, situation.Allies, GetMoveDistance(unit));
                    if (safePos.HasValue)
                    {
                        plan.AddStep(CreateMoveStep(safePos.Value, PlanStepType.MoveToSafety,
                            "Retreat - no targets"));
                    }
                }
            }

            if (plan.Steps.Count == 0)
            {
                plan.AddStep(CreateEndTurnStep("No actions available"));
            }
        }

        #endregion

        #region Step Creators

        private TurnPlanStep CreateBuffStep(AbilityData ability, UnitEntityData target, ActionCostType cost)
        {
            return new TurnPlanStep
            {
                StepType = target?.UniqueId == ability.Caster.Unit.UniqueId
                    ? PlanStepType.SelfBuff
                    : PlanStepType.AllyBuff,
                Ability = ability,
                TargetUnit = target,
                ActionCost = cost,
                Description = $"{ability.Name} → {target?.CharacterName ?? "Self"}",
                IsOptional = true
            };
        }

        private TurnPlanStep CreateMoveStep(Vector3 position, PlanStepType type, string description)
        {
            return new TurnPlanStep
            {
                StepType = type,
                TargetPosition = position,
                ActionCost = ActionCostType.Move,
                Description = description
            };
        }

        private TurnPlanStep CreateAttackStep(AbilityData ability, UnitEntityData target)
        {
            return new TurnPlanStep
            {
                StepType = PlanStepType.Attack,
                Ability = ability,
                TargetUnit = target,
                ActionCost = ActionCostType.Standard,
                Description = $"{ability.Name} → {target.CharacterName}"
            };
        }

        private TurnPlanStep CreateEndTurnStep(string reason)
        {
            return new TurnPlanStep
            {
                StepType = PlanStepType.EndTurn,
                ActionCost = ActionCostType.Free,
                Description = reason
            };
        }

        #endregion

        #region Helpers

        private AbilityData FindBestBuff(UnitEntityData unit, Situation situation, bool isSwift, bool preferSelf)
        {
            var buffs = situation.AvailableBuffs;
            if (buffs == null || buffs.Count == 0) return null;

            return buffs
                .Where(b => IsSwiftAbility(b) == isSwift)
                .Where(b => b.IsAvailable)
                .OrderByDescending(b => preferSelf && b.CanTarget(unit) ? 10 : 0)
                .FirstOrDefault();
        }

        private UnitEntityData FindBestBuffTarget(UnitEntityData unit, Situation situation, AbilityData buff)
        {
            if (buff == null) return unit;

            var allies = situation.Allies ?? new List<UnitEntityData>();
            foreach (var ally in allies)
            {
                if (buff.CanTarget(ally))
                    return ally;
            }
            return unit;
        }

        /// <summary>
        /// ★ v0.2.117: TargetScorer가 선택한 BestTarget 사용
        /// </summary>
        private UnitEntityData FindBestAttackTarget(UnitEntityData unit, Situation situation)
        {
            // ★ v0.2.117: TargetScorer가 선택한 최적 타겟 사용
            if (situation.BestTarget != null && situation.BestTarget.HPLeft > 0)
            {
                return situation.BestTarget;
            }

            // 폴백: 가장 가까운 적
            var enemies = situation.Enemies;
            if (enemies == null || enemies.Count == 0) return null;

            return enemies
                .Where(e => e.HPLeft > 0)
                .OrderBy(e => Vector3.Distance(unit.Position, e.Position))
                .FirstOrDefault();
        }

        /// <summary>
        /// ★ v0.2.117: 기존 스코어링 시스템 활용
        /// 적중률, 킬 포텐셜, 면역, 내성 등 모두 고려
        /// </summary>
        private AbilityData FindBestAttack(UnitEntityData unit, Situation situation, UnitEntityData target)
        {
            if (target == null) return null;

            var attacks = situation.AvailableAttacks ?? new List<AbilityData>();

            var validAttacks = attacks
                .Where(a => a.IsAvailable)
                .Where(a => !AbilityClassifier.IsBlacklistedForCombat(a))
                .Where(a => a.CanTarget(target))
                .ToList();

            if (validAttacks.Count == 0) return null;

            // ★ v0.2.117: 각 공격의 점수 계산
            var scoredAttacks = validAttacks
                .Select(a => new { Ability = a, Score = ScoreAttack(a, target, situation) })
                .OrderByDescending(x => x.Score)
                .ToList();

            if (scoredAttacks.Count > 0)
            {
                var best = scoredAttacks[0];
                Main.Verbose($"[PlanBuilder] Best attack: {best.Ability.Name} (Score={best.Score:F1})");
            }

            return scoredAttacks.FirstOrDefault()?.Ability;
        }

        /// <summary>
        /// ★ v0.2.117: 공격 점수 계산 (AttackScorer 로직 사용)
        /// 적중률, 킬 포텐셜, 면역, 내성, 주문 레벨 등 고려
        /// </summary>
        private float ScoreAttack(AbilityData ability, UnitEntityData target, Situation situation)
        {
            float score = 0f;

            // 1. 주문 레벨 (높을수록 강력)
            int spellLevel = ability.SpellLevel;
            score += spellLevel * 25f;  // 레벨 1 주문 = +25점

            // 2. 데미지 타입 확인
            var classification = AbilityClassifier.Classify(ability);
            bool isDamaging = classification?.Timing == AbilityTiming.Attack;
            bool isDebuff = classification?.DebuffType != DebuffType.None;
            bool isCC = classification?.CCType != CCType.None;
            bool isAoE = (classification?.AoERadius ?? 0) > 0;

            if (isDamaging) score += 30f;
            if (isAoE) score += 20f;

            // 3. ★ 적중 확률 계산 (HitChanceCalculator 사용)
            var hitResult = HitChanceCalculator.CalculateAbilityHitChance(situation.Unit, target, ability);
            float hitChance = hitResult.HitChance;
            score += hitChance * 60f; // 적중률 100% = +60점, 50% = +30점

            // 4. ★ 킬 포텐셜 (HP가 낮은 적에게 공격 우선)
            float targetHPPercent = target.HPLeft / (float)Math.Max(1, target.MaxHP);
            if (targetHPPercent < 0.3f)
            {
                score += 40f; // 30% 미만 HP 타겟에 보너스
            }
            else if (targetHPPercent < 0.5f)
            {
                score += 20f; // 50% 미만 HP 타겟에 보너스
            }

            // 5. ★ 내성 체크 고려 (디버프/CC의 경우)
            if (isDebuff || isCC)
            {
                // 취약한 내성 확인
                var targetAnalysis = TargetAnalyzer.Analyze(target, situation.Unit);
                if (targetAnalysis != null)
                {
                    var weakSaveType = targetAnalysis.WeakestSaveType;

                    // 주문이 취약한 내성을 공격하면 보너스
                    var spellSaveType = GetSpellSaveType(ability, classification);
                    if (spellSaveType == weakSaveType)
                    {
                        score += 30f;
                        Main.Verbose($"[PlanBuilder] {ability.Name}: Targets weak save ({weakSaveType}) +30");
                    }
                }
            }

            // 6. ★ 면역 체크 (면역이면 큰 페널티)
            if (isDebuff || isCC || isDamaging)
            {
                var resistAnalysis = CombatRulesAnalyzer.AnalyzeResistance(situation.Unit, target, ability);
                if (resistAnalysis.IsCompletelyIneffective)
                {
                    score -= 500f;
                    Main.Verbose($"[PlanBuilder] {ability.Name}: Target is immune -500");
                }
            }

            // 7. 캔트립 페널티 (주문 슬롯 사용 공격 우선)
            if (spellLevel == 0 && isDamaging)
            {
                score -= 15f;  // 캔트립은 약간 후순위
            }

            return score;
        }

        /// <summary>
        /// 주문의 내성 굴림 타입 확인
        /// </summary>
        private SavingThrowType GetSpellSaveType(
            AbilityData ability, AbilityClassification classification)
        {
            // 먼저 HitChanceCalculator의 API 사용
            var saveType = HitChanceCalculator.GetAbilitySaveType(ability);
            if (saveType != SavingThrowType.Unknown)
                return saveType;

            // CC 주문은 대부분 Will save
            if (classification?.CCType != CCType.None)
                return SavingThrowType.Will;

            // 데미지 주문은 대부분 Reflex save
            if (classification?.Timing == AbilityTiming.Attack)
                return SavingThrowType.Reflex;

            // 그 외는 Fortitude
            return SavingThrowType.Fortitude;
        }

        /// <summary>
        /// ★ v0.2.115: 범위 체크 없이 공격 찾기 (이동 후 공격용)
        /// </summary>
        private AbilityData FindBestAttackIgnoreRange(UnitEntityData unit, Situation situation)
        {
            var attacks = situation.AvailableAttacks ?? new List<AbilityData>();

            // 범위 체크 없이 사용 가능한 공격 찾기
            return attacks
                .Where(a => a.IsAvailable)
                .Where(a => !AbilityClassifier.IsBlacklistedForCombat(a))
                .FirstOrDefault();
        }

        private float GetAbilityRange(AbilityData ability, UnitEntityData unit)
        {
            if (ability == null) return 2f;
            return GameAPI.GetAbilityRange(ability);
        }

        private float GetMoveDistance(UnitEntityData unit)
        {
            return GameAPI.GetMaxMoveDistance(unit);
        }

        private bool IsSwiftAbility(AbilityData ability)
        {
            return ability?.Blueprint?.ActionType == Kingmaker.UnitLogic.Commands.Base.UnitCommand.CommandType.Swift;
        }

        private bool IsMeleeUnit(UnitEntityData unit)
        {
            var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
            return weapon?.Blueprint?.IsMelee ?? true;
        }

        private bool IsInEnemyRange(UnitEntityData unit, List<UnitEntityData> enemies)
        {
            foreach (var enemy in enemies)
            {
                float dist = Vector3.Distance(unit.Position, enemy.Position);
                var weapon = enemy.Body?.PrimaryHand?.MaybeWeapon;
                float range = (weapon?.AttackRange.Meters ?? 2f) + enemy.Corpulence + 2f;
                if (dist <= range)
                    return true;
            }
            return false;
        }

        private UnitEntityData GetPrimaryTarget(Situation situation)
        {
            return situation.Enemies?.FirstOrDefault();
        }

        #endregion
    }
}
