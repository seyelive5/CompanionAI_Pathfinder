// ★ v0.2.22: Unified Decision Engine - Main Entry Point
// ★ v0.2.33: 성능 측정 코드 추가
// ★ v0.2.41: FinalScore → HybridFinalScore 수정 (Geometric Mean 기반 선택)
// ★ v0.2.48: SpellDescriptor 면역 체크 추가 (악의 눈→드레치 무한 루프 해결)
// ★ v0.2.49: AoE 위험 지역 탈출 및 CC 탈출 로직 추가
// ★ v0.2.50: LOS 체크 추가 + 근접 캐릭터 BasicAttack 이동 거리 고려
// ★ v0.2.109: 도달 가능성 검증 추가 - 이동+공격으로 도달 불가능한 타겟 제외
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Planning;
using CompanionAI_Pathfinder.Scoring;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Core.DecisionEngine
{
    /// <summary>
    /// Unified AI decision engine - single entry point for both RT and TB modes.
    /// Replaces: RealTimeController.ExecuteAIDecision() role-based logic
    ///           and TurnPlanner.CreatePlan() plan generation
    /// </summary>
    public class UnifiedDecisionEngine
    {
        #region Singleton

        private static UnifiedDecisionEngine _instance;
        public static UnifiedDecisionEngine Instance => _instance ?? (_instance = new UnifiedDecisionEngine());

        #endregion

        #region Components

        private readonly SituationAnalyzer _analyzer;
        private readonly UtilityScorer _scorer;
        private readonly CombatPhaseDetector _phaseDetector;

        #endregion

        #region Constants

        private const float MELEE_REACH = 2f;
        private const float MAX_ENGAGEMENT_DISTANCE = 30f;

        #endregion

        #region Constructor

        private UnifiedDecisionEngine()
        {
            _analyzer = new SituationAnalyzer();
            _scorer = new UtilityScorer();
            _phaseDetector = new CombatPhaseDetector();

            Main.Log("[UnifiedDecisionEngine] Initialized");
        }

        #endregion

        #region Main Decision Method

        /// <summary>
        /// Main decision method - used by both RealTimeController and TurnOrchestrator.
        /// Returns the single best action to execute now.
        /// ★ v0.2.33: 성능 측정 코드 추가
        /// </summary>
        /// <param name="unit">The unit making the decision</param>
        /// <param name="turnState">Optional turn state (for turn-based mode)</param>
        /// <returns>The best action candidate to execute</returns>
        public ActionCandidate DecideAction(UnitEntityData unit, TurnState turnState = null)
        {
            string unitName = unit?.CharacterName ?? "Unknown";

            // ★ v0.2.33: 성능 측정
            var swTotal = Stopwatch.StartNew();
            long analyzeMs = 0, phaseMs = 0, genMs = 0, scoreMs = 0;

            try
            {
                // 1. Analyze situation (reuse existing SituationAnalyzer)
                var swStep = Stopwatch.StartNew();
                var situation = _analyzer.Analyze(unit, turnState);
                swStep.Stop();
                analyzeMs = swStep.ElapsedMilliseconds;

                if (situation == null)
                {
                    Main.Log($"[DecisionEngine] {unitName}: Analysis failed");
                    return ActionCandidate.EndTurn("Analysis failed");
                }

                // 2. Detect combat phase
                swStep.Restart();
                var phase = _phaseDetector.DetectPhase(situation);
                situation.CombatPhase = phase;
                swStep.Stop();
                phaseMs = swStep.ElapsedMilliseconds;

                // 3. Generate all candidate actions
                swStep.Restart();
                var candidates = GenerateCandidates(unit, situation, turnState);

                // ★ v0.2.78: 액션 가용성 기반 필터링 (턴제 모드에서 특히 중요)
                int beforeFilter = candidates.Count;
                candidates = FilterByActionAvailability(candidates, situation);
                if (candidates.Count < beforeFilter)
                {
                    Main.Verbose($"[DecisionEngine] Filtered {beforeFilter} → {candidates.Count} by action availability");
                }

                swStep.Stop();
                genMs = swStep.ElapsedMilliseconds;

                if (candidates.Count == 0)
                {
                    Main.Log($"[DecisionEngine] {unitName}: No valid actions");
                    return ActionCandidate.EndTurn("No valid actions");
                }

                // 4. Score all candidates with phase weights
                swStep.Restart();
                _scorer.ScoreAll(candidates, situation, phase);
                swStep.Stop();
                scoreMs = swStep.ElapsedMilliseconds;

                // 5. Log top candidates for debugging
                _scorer.LogTopCandidates(candidates, unitName);

                // 6. Select best (highest score)
                // ★ v0.2.41: HybridFinalScore 사용 (Geometric Mean 기반)
                var best = candidates.OrderByDescending(c => c.HybridFinalScore).First();

                // ★ v0.2.33: 성능 로그 출력
                swTotal.Stop();
                if (swTotal.ElapsedMilliseconds > 0) // 1ms 이상일 때만 로그
                {
                    Main.Log($"[PERF] {unitName}: Total={swTotal.ElapsedMilliseconds}ms (Analyze={analyzeMs}, Phase={phaseMs}, Gen={genMs}, Score={scoreMs})");
                }

                Main.Log($"[DecisionEngine] {unitName} Phase={phase}: " +
                         $"Selected {best.ActionType} (Score={best.HybridFinalScore:F1})");

                return best;
            }
            catch (Exception ex)
            {
                Main.Error($"[DecisionEngine] {unitName} Error: {ex.Message}");
                Main.Error($"[DecisionEngine] Stack: {ex.StackTrace}");
                return ActionCandidate.EndTurn($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Decide action with pre-analyzed situation (for optimization)
        /// </summary>
        public ActionCandidate DecideAction(UnitEntityData unit, Situation situation)
        {
            string unitName = unit?.CharacterName ?? "Unknown";

            try
            {
                if (situation == null)
                    return ActionCandidate.EndTurn("No situation");

                // Detect phase if not set
                if (situation.CombatPhase == CombatPhase.Midgame)
                {
                    situation.CombatPhase = _phaseDetector.DetectPhase(situation);
                }

                var candidates = GenerateCandidates(unit, situation, null);

                // ★ v0.2.78: 액션 가용성 기반 필터링
                candidates = FilterByActionAvailability(candidates, situation);

                if (candidates.Count == 0)
                    return ActionCandidate.EndTurn("No valid actions");

                _scorer.ScoreAll(candidates, situation, situation.CombatPhase);
                _scorer.LogTopCandidates(candidates, unitName);

                // ★ v0.2.41: HybridFinalScore 사용
                return candidates.OrderByDescending(c => c.HybridFinalScore).First();
            }
            catch (Exception ex)
            {
                Main.Error($"[DecisionEngine] {unitName} Error: {ex.Message}");
                return ActionCandidate.EndTurn($"Error: {ex.Message}");
            }
        }

        #endregion

        #region Candidate Generation

        /// <summary>
        /// Generate all possible action candidates
        /// ★ v0.2.49: AoE 위험/CC 탈출 후보 우선 생성
        /// </summary>
        private List<ActionCandidate> GenerateCandidates(
            UnitEntityData unit, Situation situation, TurnState turnState)
        {
            var candidates = new List<ActionCandidate>();

            // ★ v0.2.49: 위험 상황 분석 (AoE 지역 + CC 상태)
            var aoeDanger = AoeDangerAnalyzer.AnalyzeUnit(unit);
            var ccAnalysis = CrowdControlAnalyzer.AnalyzeUnit(unit);

            // === 0. PRIORITY: Escape dangerous situations ===
            // CC 탈출 및 AoE 탈출은 최우선
            GenerateEscapeCandidates(candidates, unit, situation, aoeDanger, ccAnalysis);

            // === 1. Attack candidates (abilities + basic attack) ===
            GenerateAttackCandidates(candidates, unit, situation);

            // === 2. Buff candidates (self + ally) ===
            GenerateBuffCandidates(candidates, unit, situation);

            // === 3. Heal candidates ===
            GenerateHealCandidates(candidates, unit, situation);

            // === 4. Debuff/CC candidates ===
            GenerateDebuffCandidates(candidates, unit, situation);

            // === 5. Movement candidates ===
            GenerateMovementCandidates(candidates, unit, situation);

            // === 6. Always add EndTurn as fallback ===
            candidates.Add(ActionCandidate.EndTurn("No better option"));

            Main.Verbose($"[DecisionEngine] Generated {candidates.Count} candidates for {unit.CharacterName}");
            return candidates;
        }

        #endregion

        #region Attack Candidates

        private void GenerateAttackCandidates(List<ActionCandidate> candidates,
            UnitEntityData unit, Situation situation)
        {
            string unitId = unit?.UniqueId;
            int unreachableCount = 0;  // ★ v0.2.109: 도달 불가 카운터

            // Ability attacks
            if (situation.AvailableAttacks != null)
            {
                foreach (var attack in situation.AvailableAttacks)
                {
                    if (attack == null || !attack.IsAvailable)
                        continue;

                    // ★ v0.2.100: 전투 블랙리스트 체크 (2차 방어선)
                    // SituationAnalyzer에서 필터링되어야 하지만, 누락 방지를 위해 여기서도 체크
                    if (AbilityClassifier.IsBlacklistedForCombat(attack))
                    {
                        Main.Verbose($"[DecisionEngine] Skipping blacklisted ability: {attack.Name}");
                        continue;
                    }

                    // ★ v0.2.99: 이번 턴에 실패한 능력 제외 (무한 재시도 방지)
                    string abilityGuid = attack.Blueprint?.AssetGuid.ToString();
                    if (!string.IsNullOrEmpty(abilityGuid) && TurnOrchestrator.Instance.IsAbilityFailedThisTurn(unitId, abilityGuid))
                    {
                        Main.Verbose($"[DecisionEngine] Skipping failed ability: {attack.Name}");
                        continue;
                    }

                    // Get targets
                    var targets = GetValidTargets(attack, situation);

                    foreach (var enemy in targets)
                    {
                        var targetWrapper = new TargetWrapper(enemy);
                        if (!attack.CanTarget(targetWrapper))
                            continue;

                        // ★ v0.2.109: 도달 가능성 검증 (턴제 전투에서 핵심!)
                        // 이동 + 공격으로 이번 턴에 도달 불가능한 타겟은 제외
                        if (situation.IsTurnBasedMode && !IsTargetReachable(unit, enemy, attack, situation))
                        {
                            unreachableCount++;
                            Main.Verbose($"[DecisionEngine] ★ EXCLUDED unreachable: {attack.Name} -> {enemy.CharacterName}");
                            continue;
                        }

                        // ★ v0.2.46: 블랙리스트된 능력+타겟 조합 제외 (Hex 1회 제한 등)
                        if (FailedAbilityTracker.Instance.IsBlacklisted(unit, attack, enemy))
                        {
                            Main.Verbose($"[DecisionEngine] Skipping blacklisted: {attack.Name} -> {enemy.CharacterName}");
                            continue;
                        }

                        // ★ v0.2.48: SpellDescriptor 면역 체크 (Mind-Affecting 면역 등)
                        if (TargetImmunityChecker.IsImmuneTo(enemy, attack))
                        {
                            Main.Verbose($"[DecisionEngine] Skipping immune target: {attack.Name} -> {enemy.CharacterName}");
                            continue;
                        }

                        var classification = AbilityClassifier.Classify(attack, unit);
                        var effectiveness = AbilityClassifier.EvaluateEffectiveness(classification, enemy);

                        candidates.Add(ActionCandidate.AbilityAttack(
                            attack, classification, enemy, effectiveness));
                    }
                }
            }

            // ★ v0.2.109: 도달 불가 로그 요약
            if (unreachableCount > 0)
            {
                Main.Log($"[DecisionEngine] {unit.CharacterName}: ★ Excluded {unreachableCount} unreachable ability-target combinations");
            }

            // ★ v0.2.25: Basic attack - 무기 범위 기반 (근접/원거리 모두 지원)
            // ★ v0.2.50: 근접 캐릭터는 이동 후 공격 가능 거리까지 고려
            // ★ v0.2.109: 도달 가능성 검증 강화 - 실제 이동 가능 거리 사용
            var potentialTargets = situation.Enemies ?? Enumerable.Empty<UnitEntityData>();
            float weaponRange = situation.WeaponRange;
            bool isMelee = weaponRange <= 3f;  // 3m 이하면 근접
            int basicUnreachableCount = 0;

            foreach (var enemy in potentialTargets)
            {
                if (enemy == null || enemy.Descriptor?.State?.IsDead == true)
                    continue;

                // ★ v0.2.109: 턴제 전투에서 도달 가능성 검증
                if (situation.IsTurnBasedMode && !IsBasicAttackReachable(unit, enemy, situation))
                {
                    basicUnreachableCount++;
                    continue;
                }

                float dist = Vector3.Distance(unit.Position, enemy.Position);

                // ★ v0.2.109: 이동 가능 여부에 따른 유효 사거리 계산
                float effectiveRange;
                if (dist <= weaponRange + 1f)
                {
                    // 이미 무기 사거리 내
                    effectiveRange = weaponRange + 1f;
                }
                else if (situation.HasMoveAction && situation.CanMove)
                {
                    // 이동 후 공격 가능 거리 (실제 이동 가능 거리 사용)
                    effectiveRange = weaponRange + situation.MaxMoveDistance;
                }
                else
                {
                    // 이동 불가 - 무기 사거리만
                    effectiveRange = weaponRange + 1f;
                }

                // 유효 사거리 내인지 확인
                if (dist <= effectiveRange)
                {
                    // ★ v0.2.50: LOS 체크 - 원거리 무기의 경우 시야 차단 확인
                    bool hasLOS = true;
                    if (!isMelee && dist > 3f)  // 원거리 공격 시에만 LOS 체크
                    {
                        hasLOS = LineOfSightChecker.HasLineOfSight(unit, enemy);
                    }

                    if (hasLOS)
                    {
                        candidates.Add(ActionCandidate.BasicAttack(enemy));
                        Main.Verbose($"[DecisionEngine] BasicAttack candidate: {enemy.CharacterName} (dist={dist:F1}m, range={weaponRange:F1}m, effective={effectiveRange:F1}m)");
                    }
                    else
                    {
                        Main.Verbose($"[DecisionEngine] BasicAttack BLOCKED by LOS: {enemy.CharacterName} (dist={dist:F1}m)");
                    }
                }
            }

            // ★ v0.2.109: BasicAttack 도달 불가 로그
            if (basicUnreachableCount > 0)
            {
                Main.Log($"[DecisionEngine] {unit.CharacterName}: ★ Excluded {basicUnreachableCount} unreachable BasicAttack targets");
            }
        }

        private List<UnitEntityData> GetValidTargets(AbilityData ability, Situation situation)
        {
            var targets = new List<UnitEntityData>();

            // Prioritize hittable enemies
            if (situation.HittableEnemies != null)
                targets.AddRange(situation.HittableEnemies);

            // Add other enemies if ability has range
            if (situation.Enemies != null)
            {
                foreach (var enemy in situation.Enemies)
                {
                    if (!targets.Contains(enemy))
                        targets.Add(enemy);
                }
            }

            return targets.Where(e => e != null && !e.Descriptor?.State?.IsDead == true).ToList();
        }

        #endregion

        #region Buff Candidates

        private void GenerateBuffCandidates(List<ActionCandidate> candidates,
            UnitEntityData unit, Situation situation)
        {
            if (situation.AvailableBuffs == null)
                return;

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff == null || !buff.IsAvailable)
                    continue;

                var classification = AbilityClassifier.Classify(buff, unit);

                // Self buff
                if (buff.Blueprint?.CanTargetSelf == true)
                {
                    var selfWrapper = new TargetWrapper(unit);
                    if (buff.CanTarget(selfWrapper))
                    {
                        // Skip if already applied
                        if (!AbilityClassifier.IsBuffAlreadyApplied(buff, unit))
                        {
                            // ★ v0.2.24: Skip if already pending (by ANY caster including self)
                            if (!PendingActionTracker.Instance.IsBuffPending(buff, unit))
                            {
                                candidates.Add(ActionCandidate.Buff(buff, classification, unit,
                                    $"Self buff: {buff.Name}"));
                            }
                        }
                    }
                }

                // Ally buffs
                if (buff.Blueprint?.CanTargetFriends == true && situation.Allies != null)
                {
                    foreach (var ally in situation.Allies)
                    {
                        if (ally == null || ally == unit)
                            continue;

                        var allyWrapper = new TargetWrapper(ally);
                        if (!buff.CanTarget(allyWrapper))
                            continue;

                        // Skip if already applied
                        if (AbilityClassifier.IsBuffAlreadyApplied(buff, ally))
                            continue;

                        // ★ v0.2.24: Skip if already pending (by ANY caster including self)
                        if (PendingActionTracker.Instance.IsBuffPending(buff, ally))
                        {
                            Main.Verbose($"[DecisionEngine] Skipping buff {buff.Name} on {ally.CharacterName}: already pending");
                            continue;
                        }

                        candidates.Add(ActionCandidate.Buff(buff, classification, ally,
                            $"Buff {ally.CharacterName}: {buff.Name}"));
                    }
                }
            }
        }

        #endregion

        #region Heal Candidates

        private void GenerateHealCandidates(List<ActionCandidate> candidates,
            UnitEntityData unit, Situation situation)
        {
            if (situation.AvailableHeals == null)
                return;

            foreach (var heal in situation.AvailableHeals)
            {
                if (heal == null || !heal.IsAvailable)
                    continue;

                var classification = AbilityClassifier.Classify(heal, unit);

                // Self heal
                if (heal.Blueprint?.CanTargetSelf == true)
                {
                    var selfWrapper = new TargetWrapper(unit);
                    if (heal.CanTarget(selfWrapper))
                    {
                        float urgency = ResponseCurves.HealUrgency(situation.HPPercent);
                        candidates.Add(ActionCandidate.Heal(heal, classification, unit, urgency,
                            $"Self heal: {heal.Name}"));
                    }
                }

                // Ally heals
                if (heal.Blueprint?.CanTargetFriends == true && situation.Allies != null)
                {
                    foreach (var ally in situation.Allies)
                    {
                        if (ally == null || ally == unit)
                            continue;

                        var allyWrapper = new TargetWrapper(ally);
                        if (!heal.CanTarget(allyWrapper))
                            continue;

                        float allyHP = GetHPPercent(ally);
                        float urgency = ResponseCurves.HealUrgency(allyHP);

                        // Only generate heal candidate if ally needs healing
                        if (allyHP < 80f)
                        {
                            candidates.Add(ActionCandidate.Heal(heal, classification, ally, urgency,
                                $"Heal {ally.CharacterName}: {heal.Name}"));
                        }
                    }
                }
            }
        }

        #endregion

        #region Debuff Candidates

        private void GenerateDebuffCandidates(List<ActionCandidate> candidates,
            UnitEntityData unit, Situation situation)
        {
            if (situation.AvailableDebuffs == null)
                return;

            foreach (var debuff in situation.AvailableDebuffs)
            {
                if (debuff == null || !debuff.IsAvailable)
                    continue;

                var classification = AbilityClassifier.Classify(debuff, unit);
                var effectiveness = 1f;

                // Find best target for this debuff
                foreach (var enemy in situation.Enemies ?? Enumerable.Empty<UnitEntityData>())
                {
                    if (enemy == null)
                        continue;

                    var targetWrapper = new TargetWrapper(enemy);
                    if (!debuff.CanTarget(targetWrapper))
                        continue;

                    // ★ v0.2.48: SpellDescriptor 면역 체크
                    if (TargetImmunityChecker.IsImmuneTo(enemy, debuff))
                    {
                        Main.Verbose($"[DecisionEngine] Skipping immune debuff target: {debuff.Name} -> {enemy.CharacterName}");
                        continue;
                    }

                    effectiveness = AbilityClassifier.EvaluateEffectiveness(classification, enemy);

                    // Only if reasonably effective
                    if (effectiveness > 0.3f)
                    {
                        candidates.Add(ActionCandidate.Debuff(debuff, classification, enemy, effectiveness,
                            $"Debuff {enemy.CharacterName}: {debuff.Name}"));
                    }
                }
            }
        }

        #endregion

        #region Movement Candidates

        /// <summary>
        /// ★ v0.2.30: MovementPlanner 기반 이동 후보 생성
        /// 기존 단순 벡터 계산 → MovementAPI 기반 다중 요소 스코어링
        /// </summary>
        private void GenerateMovementCandidates(List<ActionCandidate> candidates,
            UnitEntityData unit, Situation situation)
        {
            if (!situation.CanMove)
                return;

            string roleName = situation.CharacterSettings?.Role.ToString() ?? "Auto";

            // ★ v0.2.30: MovementPlanner 기반 이동 계획
            // 1. 후퇴 체크 (원거리가 위험할 때)
            if (MovementPlanner.ShouldRetreat(situation))
            {
                var retreatDecision = MovementPlanner.PlanRetreat(situation, roleName);
                if (retreatDecision != null)
                {
                    candidates.Add(ActionCandidate.Move(
                        retreatDecision.Destination,
                        retreatDecision.Reason
                    ));
                    Main.Verbose($"[DecisionEngine] Added retreat move candidate: {retreatDecision}");
                }
            }

            // 2. 공격 가능한 적 없으면 이동 (또는 위치 개선 필요 시)
            bool needsMove = !situation.HasHittableEnemies && situation.HasLivingEnemies;
            bool needsReposition = situation.IsInDanger && situation.PrefersRanged;

            if (needsMove || needsReposition)
            {
                var moveDecision = MovementPlanner.PlanMove(situation, roleName, forceMove: needsReposition);
                if (moveDecision != null)
                {
                    candidates.Add(ActionCandidate.Move(
                        moveDecision.Destination,
                        moveDecision.Reason
                    ));
                    Main.Verbose($"[DecisionEngine] Added move candidate: {moveDecision}");
                }
            }

            // 3. 원거리 캐릭터 최적 거리 유지 (현재 위치가 좋지 않을 때)
            if (situation.PrefersRanged && situation.NearestEnemy != null)
            {
                float currentDist = situation.NearestEnemyDistance;

                // 너무 가깝거나 너무 멀면 재배치
                if (currentDist < situation.MinSafeDistance || currentDist > situation.WeaponRange)
                {
                    var repositionDecision = MovementPlanner.PlanMove(situation, roleName, forceMove: true);
                    if (repositionDecision != null)
                    {
                        // 중복 체크 (이미 같은 목적지가 후보에 있으면 스킵)
                        bool alreadyAdded = candidates.Any(c =>
                            c.ActionType == CandidateType.Move &&
                            c.MoveDestination.HasValue &&
                            Vector3.Distance(c.MoveDestination.Value, repositionDecision.Destination) < 2f);

                        if (!alreadyAdded)
                        {
                            candidates.Add(ActionCandidate.Move(
                                repositionDecision.Destination,
                                "Reposition to optimal range"
                            ));
                            Main.Verbose($"[DecisionEngine] Added reposition candidate: {repositionDecision}");
                        }
                    }
                }
            }
        }

        #endregion

        #region Escape Candidates (v0.2.49)

        /// <summary>
        /// ★ v0.2.49: 위험 상황 탈출 후보 생성
        /// AoE 위험 지역 탈출 및 CC 해제/탈출
        /// </summary>
        private void GenerateEscapeCandidates(
            List<ActionCandidate> candidates,
            UnitEntityData unit,
            Situation situation,
            AoeDangerAnalyzer.DangerAnalysis aoeDanger,
            CrowdControlAnalyzer.CCAnalysis ccAnalysis)
        {
            // === CC 탈출 처리 ===
            if (ccAnalysis.IsUnderCC)
            {
                Main.Log($"[DecisionEngine] {unit.CharacterName} is under CC: {ccAnalysis.MaxSeverity}");

                // CC 해제 능력 사용 가능?
                foreach (var option in ccAnalysis.EscapeOptions)
                {
                    if (option.Type == CrowdControlAnalyzer.EscapeType.SelfAbility && option.Ability != null)
                    {
                        var classification = AbilityClassifier.Classify(option.Ability, unit);

                        // CC 해제는 매우 높은 우선순위
                        var candidate = ActionCandidate.Buff(
                            option.Ability,
                            classification,
                            unit,
                            $"CC Escape: {option.Description}"
                        );

                        // 점수 부스트 (CC 탈출은 최우선)
                        candidate.PriorityBoost = 100f * (float)ccAnalysis.MaxSeverity;
                        candidates.Add(candidate);

                        Main.Verbose($"[DecisionEngine] Added CC escape: {option.Ability.Name} (boost={candidate.PriorityBoost:F0})");
                    }
                }

                // Prone 상태면 일어나기 (특별 처리)
                if (ccAnalysis.ActiveCCs.Any(c => c.Condition == Kingmaker.UnitLogic.UnitCondition.Prone))
                {
                    // Stand Up은 별도 처리 필요 (게임 API에서 지원하는지 확인 필요)
                    Main.Verbose($"[DecisionEngine] {unit.CharacterName} is Prone - stand up needed");
                }
            }

            // === AoE 위험 지역 탈출 ===
            if (aoeDanger.ShouldEscape && aoeDanger.SuggestedEscapePosition.HasValue)
            {
                // 이동 가능한지 확인
                if (situation.CanMove && (ccAnalysis.CanMove || !ccAnalysis.IsUnderCC))
                {
                    var escapeCandidate = ActionCandidate.Move(
                        aoeDanger.SuggestedEscapePosition.Value,
                        $"Escape AoE danger (level={aoeDanger.TotalDangerLevel:F2})"
                    );

                    // 위험도에 따른 우선순위 부스트
                    escapeCandidate.PriorityBoost = 50f * aoeDanger.TotalDangerLevel;
                    candidates.Add(escapeCandidate);

                    string effectNames = string.Join(", ", aoeDanger.DangerousEffects.Select(e => e.Name));
                    Main.Log($"[DecisionEngine] {unit.CharacterName} should escape AoE: {effectNames}");
                }
                else
                {
                    Main.Verbose($"[DecisionEngine] {unit.CharacterName} can't escape AoE (CanMove={situation.CanMove}, CC.CanMove={ccAnalysis.CanMove})");
                }
            }
        }

        #endregion

        #region Helper Methods

        private float GetMeleeReach(UnitEntityData unit)
        {
            // Could check weapon reach here
            return MELEE_REACH;
        }

        private float GetHPPercent(UnitEntityData unit)
        {
            try
            {
                if (unit?.Stats?.HitPoints == null) return 100f;
                float current = unit.Stats.HitPoints.ModifiedValue;
                float max = unit.Stats.HitPoints.BaseValue;
                if (max <= 0) return 100f;
                return (current / max) * 100f;
            }
            catch { return 100f; }
        }

        /// <summary>
        /// ★ v0.2.78: 액션 가용성 기반 후보 필터링
        /// 턴제 전투에서 이미 사용한 액션 타입의 후보를 제거
        /// </summary>
        private List<ActionCandidate> FilterByActionAvailability(
            List<ActionCandidate> candidates, Situation situation)
        {
            // 실시간 모드에서는 필터링 완화 (쿨다운은 짧음)
            if (!situation.IsTurnBasedMode)
                return candidates;

            return candidates.Where(c =>
            {
                switch (c.ActionType)
                {
                    case CandidateType.AbilityAttack:
                    case CandidateType.BasicAttack:
                        // 공격은 Standard Action 필요
                        if (!situation.HasStandardAction)
                        {
                            Main.Verbose($"[Filter] Removed {c.ActionType}: No Standard Action");
                            return false;
                        }
                        return true;

                    case CandidateType.Move:
                        // 이동은 Move Action 필요
                        if (!situation.HasMoveAction)
                        {
                            Main.Verbose($"[Filter] Removed Move: No Move Action");
                            return false;
                        }
                        // 물리적으로 이동 가능한지도 체크
                        if (!situation.CanMove)
                        {
                            Main.Verbose($"[Filter] Removed Move: CanMove=false");
                            return false;
                        }
                        return true;

                    case CandidateType.Buff:
                        // 버프는 보통 Standard, 일부는 Swift
                        // Classification에서 ActionType 확인 (있으면)
                        if (c.Classification != null && IsSwiftAbility(c.Classification))
                        {
                            if (!situation.HasSwiftAction)
                            {
                                Main.Verbose($"[Filter] Removed Swift Buff: No Swift Action");
                                return false;
                            }
                        }
                        else
                        {
                            if (!situation.HasStandardAction)
                            {
                                Main.Verbose($"[Filter] Removed Standard Buff: No Standard Action");
                                return false;
                            }
                        }
                        return true;

                    case CandidateType.Heal:
                    case CandidateType.Debuff:
                        // 힐과 디버프는 대부분 Standard Action
                        if (!situation.HasStandardAction)
                        {
                            Main.Verbose($"[Filter] Removed {c.ActionType}: No Standard Action");
                            return false;
                        }
                        return true;

                    case CandidateType.EndTurn:
                        // EndTurn은 항상 가능
                        return true;

                    default:
                        // 알 수 없는 타입은 Standard 체크
                        return situation.HasStandardAction;
                }
            }).ToList();
        }

        /// <summary>
        /// ★ v0.2.78: 능력이 Swift Action인지 확인
        /// </summary>
        private bool IsSwiftAbility(AbilityClassification classification)
        {
            if (classification == null)
                return false;

            // AbilityClassification의 CommandActionType 사용
            return classification.IsSwiftAction;
        }

        #endregion

        #region ★ v0.2.109: Reachability Check

        /// <summary>
        /// ★ v0.2.109: 타겟이 이번 턴에 도달 가능한지 확인
        /// 핵심 로직: 거리 <= 이동거리 + 능력사거리
        ///
        /// 턴제 전투에서 중요:
        /// - Move + Standard 조합으로 도달 가능해야 공격 가능
        /// - 도달 불가능한 타겟에 대한 공격은 턴 낭비
        /// </summary>
        private bool IsTargetReachable(
            UnitEntityData unit,
            UnitEntityData target,
            AbilityData ability,
            Situation situation)
        {
            if (unit == null || target == null)
                return false;

            float distance = Vector3.Distance(unit.Position, target.Position);

            // 1. 능력 사거리 계산
            float abilityRange = GetAbilityEffectiveRange(ability, unit);

            // 2. 이미 사거리 내에 있으면 도달 가능
            if (distance <= abilityRange + 1f)  // 약간의 여유
            {
                Main.Verbose($"[Reachability] {target.CharacterName}: Already in range (dist={distance:F1}m, range={abilityRange:F1}m)");
                return true;
            }

            // 3. 이동 액션이 없으면 사거리 밖 타겟은 도달 불가
            if (!situation.HasMoveAction || !situation.CanMove)
            {
                Main.Verbose($"[Reachability] {target.CharacterName}: Out of range and no Move (dist={distance:F1}m, range={abilityRange:F1}m)");
                return false;
            }

            // 4. 이동 + 공격으로 도달 가능한지 확인
            float maxMoveDistance = situation.MaxMoveDistance;
            float effectiveReach = maxMoveDistance + abilityRange;

            bool reachable = distance <= effectiveReach + 1f;  // 약간의 여유

            if (reachable)
            {
                Main.Verbose($"[Reachability] {target.CharacterName}: Reachable with Move+Attack (dist={distance:F1}m, move={maxMoveDistance:F1}m, range={abilityRange:F1}m, total={effectiveReach:F1}m)");
            }
            else
            {
                Main.Verbose($"[Reachability] {target.CharacterName}: ★ UNREACHABLE (dist={distance:F1}m > move+range={effectiveReach:F1}m)");
            }

            return reachable;
        }

        /// <summary>
        /// ★ v0.2.109: BasicAttack 타겟이 도달 가능한지 확인 (무기 사거리 기반)
        /// </summary>
        private bool IsBasicAttackReachable(
            UnitEntityData unit,
            UnitEntityData target,
            Situation situation)
        {
            if (unit == null || target == null)
                return false;

            float distance = Vector3.Distance(unit.Position, target.Position);
            float weaponRange = situation.WeaponRange;
            bool isMelee = weaponRange <= 3f;

            // 1. 이미 무기 사거리 내에 있으면 도달 가능
            if (distance <= weaponRange + 1f)
            {
                return true;
            }

            // 2. 이동 액션이 없으면 사거리 밖 타겟은 도달 불가
            if (!situation.HasMoveAction || !situation.CanMove)
            {
                Main.Verbose($"[Reachability-Basic] {target.CharacterName}: Out of weapon range and no Move (dist={distance:F1}m, wpnRange={weaponRange:F1}m)");
                return false;
            }

            // 3. 근접 무기: 이동 + 공격으로 도달 가능한지 확인
            if (isMelee)
            {
                float maxMoveDistance = situation.MaxMoveDistance;
                float effectiveReach = maxMoveDistance + weaponRange;
                bool reachable = distance <= effectiveReach + 1f;

                if (!reachable)
                {
                    Main.Verbose($"[Reachability-Basic] {target.CharacterName}: ★ MELEE UNREACHABLE (dist={distance:F1}m > move+range={effectiveReach:F1}m)");
                }
                return reachable;
            }

            // 4. 원거리 무기: 보통 충분히 긴 사거리를 가짐
            // 하지만 혹시 모르니 이동 후 도달 가능한지 체크
            float rangedEffectiveReach = situation.MaxMoveDistance + weaponRange;
            bool rangedReachable = distance <= rangedEffectiveReach + 1f;

            if (!rangedReachable)
            {
                Main.Verbose($"[Reachability-Basic] {target.CharacterName}: ★ RANGED UNREACHABLE (dist={distance:F1}m > move+range={rangedEffectiveReach:F1}m)");
            }

            return rangedReachable;
        }

        /// <summary>
        /// ★ v0.2.109: 능력의 실제 사거리 계산
        /// </summary>
        private float GetAbilityEffectiveRange(AbilityData ability, UnitEntityData unit)
        {
            if (ability?.Blueprint == null)
                return 2f;

            var range = ability.Blueprint.Range;

            switch (range)
            {
                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch:
                    return 2f + (unit?.Corpulence ?? 0.5f);

                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Close:
                    return 7.5f;  // 25 feet

                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Medium:
                    return 30f;   // 100 feet

                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Long:
                    return 120f;  // 400 feet

                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Unlimited:
                    return 1000f;

                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal:
                    return 0f;

                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon:
                    // 무기 사거리 사용
                    var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                    if (weapon != null)
                    {
                        return weapon.AttackRange.Meters > 0
                            ? weapon.AttackRange.Meters + (unit?.Corpulence ?? 0.5f)
                            : (weapon.Blueprint.IsMelee ? 2f : 15f);
                    }
                    return 2f;

                default:
                    return 10f;
            }
        }

        #endregion

        #region Static Access

        /// <summary>
        /// Reset the singleton instance (for testing/reload)
        /// </summary>
        public static void Reset()
        {
            _instance = null;
            Main.Log("[UnifiedDecisionEngine] Reset");
        }

        #endregion
    }
}
