// ★ v0.2.22: Unified Decision Engine - Main Entry Point
// ★ v0.2.33: 성능 측정 코드 추가
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
                var best = candidates.OrderByDescending(c => c.FinalScore).First();

                // ★ v0.2.33: 성능 로그 출력
                swTotal.Stop();
                if (swTotal.ElapsedMilliseconds > 0) // 1ms 이상일 때만 로그
                {
                    Main.Log($"[PERF] {unitName}: Total={swTotal.ElapsedMilliseconds}ms (Analyze={analyzeMs}, Phase={phaseMs}, Gen={genMs}, Score={scoreMs})");
                }

                Main.Log($"[DecisionEngine] {unitName} Phase={phase}: " +
                         $"Selected {best.ActionType} (Score={best.FinalScore:F1})");

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
                if (candidates.Count == 0)
                    return ActionCandidate.EndTurn("No valid actions");

                _scorer.ScoreAll(candidates, situation, situation.CombatPhase);
                _scorer.LogTopCandidates(candidates, unitName);

                return candidates.OrderByDescending(c => c.FinalScore).First();
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
        /// </summary>
        private List<ActionCandidate> GenerateCandidates(
            UnitEntityData unit, Situation situation, TurnState turnState)
        {
            var candidates = new List<ActionCandidate>();

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
            // Ability attacks
            if (situation.AvailableAttacks != null)
            {
                foreach (var attack in situation.AvailableAttacks)
                {
                    if (attack == null || !attack.IsAvailable)
                        continue;

                    // Get targets
                    var targets = GetValidTargets(attack, situation);

                    foreach (var enemy in targets)
                    {
                        var targetWrapper = new TargetWrapper(enemy);
                        if (!attack.CanTarget(targetWrapper))
                            continue;

                        var classification = AbilityClassifier.Classify(attack, unit);
                        var effectiveness = AbilityClassifier.EvaluateEffectiveness(classification, enemy);

                        candidates.Add(ActionCandidate.AbilityAttack(
                            attack, classification, enemy, effectiveness));
                    }
                }
            }

            // ★ v0.2.25: Basic attack - 무기 범위 기반 (근접/원거리 모두 지원)
            // HittableEnemies가 비어있어도 무기 범위 내 적에게 평타 가능
            var potentialTargets = situation.Enemies ?? Enumerable.Empty<UnitEntityData>();
            float weaponRange = situation.WeaponRange;

            foreach (var enemy in potentialTargets)
            {
                if (enemy == null || enemy.Descriptor?.State?.IsDead == true)
                    continue;

                float dist = Vector3.Distance(unit.Position, enemy.Position);

                // 무기 범위 내에 있으면 BasicAttack 후보 생성
                if (dist <= weaponRange + 1f)  // 약간의 여유
                {
                    candidates.Add(ActionCandidate.BasicAttack(enemy));
                    Main.Verbose($"[DecisionEngine] BasicAttack candidate: {enemy.CharacterName} (dist={dist:F1}m, range={weaponRange:F1}m)");
                }
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
