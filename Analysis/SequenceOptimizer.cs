// ★ v0.2.96: 시퀀스 최적화기 - 패스파인더 API 기반 구현
// 여러 가능한 행동 시퀀스를 생성하고 비교하여 최적의 시퀀스 선택
// 핵심: "후퇴 → 공격" vs "직접 공격" vs "공격 스킵" 비교
// ★ v0.2.96: 도달 가능성 체크 추가 - 이동 가능 거리 내 타겟만 공격 대상으로 선정

using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using TurnBased.Controllers;
using UnityEngine;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.GameInterface;
using CompanionAI_Pathfinder.Planning;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// ★ v0.2.96: 시퀀스 최적화기
    ///
    /// 핵심 역할:
    /// 1. 여러 가능한 행동 시퀀스 생성
    /// 2. 각 시퀀스의 점수 계산 (안전도, 데미지, 역할 적합성)
    /// 3. 최적 시퀀스 선택
    ///
    /// 주요 비교 시나리오:
    /// - "현재 위치에서 공격" vs "후퇴 → 공격"
    /// - "공격" vs "공격 스킵" (위험할 때)
    ///
    /// ★ v0.2.96 핵심 개선:
    /// - 도달 가능성(Reachability) 체크 추가
    /// - 이동 거리 내 도달 불가능한 타겟은 제외
    /// - "선택 후 헛무빙" 문제 방지
    /// </summary>
    public class SequenceOptimizer
    {
        private readonly Situation _situation;
        private readonly AIRole _role;
        private readonly string _logPrefix;

        public SequenceOptimizer(Situation situation, AIRole role, string logPrefix = "SeqOpt")
        {
            _situation = situation ?? throw new ArgumentNullException(nameof(situation));
            _role = role;
            _logPrefix = logPrefix;
        }

        #region Main Optimization Method

        /// <summary>
        /// 공격 시퀀스 최적화
        ///
        /// 반환값:
        /// - null: 최적화 실패 또는 "공격 스킵" 결정
        /// - ActionSequence: 최적 시퀀스
        /// </summary>
        public ActionSequence OptimizeAttackSequence(
            List<AbilityData> availableAttacks,
            UnitEntityData target)
        {
            if (availableAttacks == null || availableAttacks.Count == 0 || target == null)
                return null;

            var sequences = new List<ActionSequence>();

            // 1. 사용 가능한 공격 필터링
            var usableAttacks = availableAttacks
                .Where(a => a != null && a.IsAvailable)
                .Where(a => CanUseAttackOn(a, target))
                .ToList();

            if (usableAttacks.Count == 0)
            {
                Main.Verbose($"[{_logPrefix}] No usable attacks on {target.CharacterName}");
                return null;
            }

            // ★ v0.2.96: 도달 가능한 공격만 필터링
            var reachableAttacks = usableAttacks
                .Where(a => CheckReachability(a, target).CanReach)
                .ToList();

            Main.Verbose($"[{_logPrefix}] Usable attacks: {usableAttacks.Count}, Reachable: {reachableAttacks.Count}");

            if (reachableAttacks.Count == 0)
            {
                Main.Log($"[{_logPrefix}] ★ No reachable attacks on {target.CharacterName} - target too far");
                return null;
            }

            // 2. "공격 스킵" 옵션 (위험 상황 + 원거리 캐릭터)
            if (ShouldConsiderSkip())
            {
                var skipSequence = CreateSkipSequence();
                sequences.Add(skipSequence);
            }

            // 3. 각 공격에 대해 시퀀스 생성 (★ v0.2.96: 도달 가능한 공격만)
            foreach (var attack in reachableAttacks)
            {
                // Option A: 직접 공격
                var directAttack = CreateDirectAttackSequence(attack, target);
                if (directAttack != null)
                {
                    sequences.Add(directAttack);
                }

                // Option B: 후퇴 → 공격 (원거리 + 위험 상황)
                if (ShouldConsiderRetreatFirst())
                {
                    var retreatThenAttack = CreateRetreatThenAttackSequence(attack, target);
                    if (retreatThenAttack != null)
                    {
                        sequences.Add(retreatThenAttack);
                    }
                }
            }

            if (sequences.Count == 0)
            {
                Main.Verbose($"[{_logPrefix}] No valid sequences generated");
                return null;
            }

            // 4. 점수 계산
            foreach (var seq in sequences)
            {
                seq.SimulateFinalState(_situation);
                seq.CalculateScore(_situation, _role);
            }

            // 5. 로깅 및 최적 시퀀스 선택
            LogSequenceComparison(sequences);

            var best = sequences.OrderByDescending(s => s.TotalScore).First();

            // "Skip" 시퀀스가 선택되면 null 반환 (공격 스킵)
            if (best.Description == "Skip")
            {
                Main.Log($"[{_logPrefix}] ★ Decision: Skip attack (safety priority, score={best.TotalScore:F0})");
                return null;
            }

            Main.Log($"[{_logPrefix}] ★ Selected: {best.Description} (score={best.TotalScore:F0})");
            return best;
        }

        #endregion

        #region Sequence Creation

        /// <summary>
        /// "공격 스킵" 시퀀스 생성
        /// 위험한 상황에서 안전을 유지하는 옵션
        /// </summary>
        private ActionSequence CreateSkipSequence()
        {
            var seq = new ActionSequence("Skip");
            seq.ExpectedFinalPosition = _situation.Unit.Position;
            // 행동 없음 - Actions는 빈 리스트
            return seq;
        }

        /// <summary>
        /// 직접 공격 시퀀스
        /// ★ v0.2.96: 도달 가능성 체크 추가
        /// </summary>
        private ActionSequence CreateDirectAttackSequence(AbilityData attack, UnitEntityData target)
        {
            // ★ v0.2.96: 도달 가능성 체크 - 타겟에 도달 불가능하면 null 반환
            var reachability = CheckReachability(attack, target);
            if (!reachability.CanReach)
            {
                Main.Verbose($"[{_logPrefix}] Direct attack rejected: {attack.Name} - target not reachable (dist={reachability.CurrentDistance:F1}m, range={reachability.AbilityRange:F1}m, moveRange={reachability.AvailableMovement:F1}m)");
                return null;
            }

            // 이동이 필요한 경우 → Move + Attack 시퀀스로 생성
            if (reachability.NeedsMovement)
            {
                var seq = new ActionSequence("Move then attack");

                // 타겟 방향으로 필요한 만큼 이동
                Vector3 direction = (target.Position - _situation.Unit.Position).normalized;
                float moveDistance = reachability.RequiredMovement;
                Vector3 moveDestination = _situation.Unit.Position + direction * moveDistance;

                seq.AddMove(moveDestination, $"Move to attack range");
                seq.AddAttack(attack, target, $"Attack: {attack.Name}");
                seq.ExpectedFinalPosition = moveDestination;

                Main.Verbose($"[{_logPrefix}] Move+Attack: move {moveDistance:F1}m then {attack.Name}");
                return seq;
            }

            // 이동 불필요 - 현재 위치에서 공격 가능
            var directSeq = new ActionSequence("Direct attack");
            directSeq.AddAttack(attack, target, $"Direct: {attack.Name}");
            directSeq.ExpectedFinalPosition = _situation.Unit.Position;
            return directSeq;
        }

        /// <summary>
        /// ★ v0.2.96: 도달 가능성 정보
        /// </summary>
        private struct ReachabilityInfo
        {
            public bool CanReach;           // 이번 턴에 도달 가능?
            public bool NeedsMovement;      // 이동 필요?
            public float CurrentDistance;   // 현재 거리
            public float AbilityRange;      // 능력 사거리
            public float AvailableMovement; // 사용 가능한 이동 거리
            public float RequiredMovement;  // 필요한 이동 거리
        }

        /// <summary>
        /// ★ v0.2.96: 핵심 메서드 - 타겟 도달 가능성 체크
        /// 패스파인더 API 사용: ability.GetApproachDistance, unit.CurrentSpeedMps
        /// </summary>
        private ReachabilityInfo CheckReachability(AbilityData ability, UnitEntityData target)
        {
            var info = new ReachabilityInfo
            {
                CanReach = false,
                NeedsMovement = false,
                CurrentDistance = float.MaxValue,
                AbilityRange = 0f,
                AvailableMovement = 0f,
                RequiredMovement = 0f
            };

            if (ability == null || target == null)
                return info;

            try
            {
                // 1. 현재 거리 계산
                info.CurrentDistance = Vector3.Distance(_situation.Unit.Position, target.Position);

                // 2. 능력 사거리 (패스파인더 API)
                info.AbilityRange = ability.GetApproachDistance(target);

                // 3. 사용 가능한 이동 거리 계산 (Move Action 보유 시)
                info.AvailableMovement = 0f;
                if (_situation.HasMoveAction && _situation.CanMove)
                {
                    // 턴제 전투: 1 Move Action = CurrentSpeedMps * 3초
                    // 실시간 전투: CurrentSpeedMps * 6초 (전체 라운드)
                    float movementTime = CombatController.IsInTurnBasedCombat() ? 3f : 6f;
                    info.AvailableMovement = _situation.Unit.CurrentSpeedMps * movementTime;
                }

                // 4. 현재 위치에서 공격 가능?
                if (info.CurrentDistance <= info.AbilityRange)
                {
                    info.CanReach = true;
                    info.NeedsMovement = false;
                    info.RequiredMovement = 0f;
                    return info;
                }

                // 5. 이동 후 공격 가능?
                float distanceGap = info.CurrentDistance - info.AbilityRange;
                info.RequiredMovement = distanceGap + 0.5f;  // 약간의 여유

                if (info.RequiredMovement <= info.AvailableMovement)
                {
                    info.CanReach = true;
                    info.NeedsMovement = true;
                    return info;
                }

                // 6. 도달 불가
                info.CanReach = false;
                return info;
            }
            catch (Exception ex)
            {
                Main.Error($"[{_logPrefix}] CheckReachability error: {ex.Message}");
                return info;
            }
        }

        /// <summary>
        /// 후퇴 → 공격 시퀀스
        /// ★ 패스파인더 API 사용: GetApproachDistance()로 사거리 체크
        /// </summary>
        private ActionSequence CreateRetreatThenAttackSequence(AbilityData attack, UnitEntityData target)
        {
            if (!_situation.CanMove)
                return null;

            // 1. 후퇴 위치 찾기
            var retreatScore = MovementAPI.FindRetreatPosition(
                _situation.Unit,
                _situation.Enemies,
                _situation.MinSafeDistance,
                _situation.InfluenceMap
            );

            if (retreatScore == null)
            {
                Main.Verbose($"[{_logPrefix}] Retreat position not found");
                return null;
            }

            Vector3 retreatPos = retreatScore.Position;

            // 2. ★ 핵심: 후퇴 후에도 타겟 공격 가능한지 확인
            // 패스파인더 API 사용: ability.GetApproachDistance(target)
            if (!CanAttackFromPosition(attack, target, retreatPos))
            {
                Main.Verbose($"[{_logPrefix}] Retreat rejected: cannot attack {target.CharacterName} from retreat position");
                return null;
            }

            // 3. 시퀀스 생성
            var seq = new ActionSequence("Retreat then attack");
            seq.AddMove(retreatPos, "Retreat for safety");
            seq.AddAttack(attack, target, $"Attack after retreat: {attack.Name}");
            seq.ExpectedFinalPosition = retreatPos;

            return seq;
        }

        /// <summary>
        /// ★ 핵심 메서드: 특정 위치에서 타겟 공격 가능 여부
        /// 패스파인더 API 사용: ability.GetApproachDistance(target)
        /// </summary>
        private bool CanAttackFromPosition(AbilityData ability, UnitEntityData target, Vector3 fromPosition)
        {
            if (ability == null || target == null)
                return false;

            try
            {
                // 1. 능력 사거리 가져오기 (패스파인더 API)
                float abilityRange = ability.GetApproachDistance(target);

                // 2. 후퇴 위치에서 타겟까지 거리
                float distanceToTarget = Vector3.Distance(fromPosition, target.Position);

                // 3. 거리 <= 사거리이면 공격 가능
                bool canAttack = distanceToTarget <= abilityRange;

                Main.Verbose($"[{_logPrefix}] CanAttackFrom: {ability.Name} range={abilityRange:F1}m, dist={distanceToTarget:F1}m => {canAttack}");

                return canAttack;
            }
            catch (Exception ex)
            {
                Main.Error($"[{_logPrefix}] CanAttackFromPosition error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private bool ShouldConsiderSkip()
        {
            // 위험 상황 + 원거리 캐릭터 + Support/DPS 역할
            if (!_situation.IsInDanger)
                return false;

            if (!_situation.PrefersRanged)
                return false;

            return _role == AIRole.Support || _role == AIRole.DPS || _role == AIRole.DPS;
        }

        private bool ShouldConsiderRetreatFirst()
        {
            // 원거리 캐릭터가 위험 상황일 때
            if (!_situation.PrefersRanged)
                return false;

            if (!_situation.CanMove)
                return false;

            // 적이 안전 거리보다 가까우면 고려
            return _situation.NearestEnemyDistance < _situation.MinSafeDistance;
        }

        private bool CanUseAttackOn(AbilityData attack, UnitEntityData target)
        {
            if (attack == null || target == null)
                return false;

            try
            {
                var targetWrapper = new TargetWrapper(target);
                return attack.CanTarget(targetWrapper);
            }
            catch
            {
                return false;
            }
        }

        private void LogSequenceComparison(List<ActionSequence> sequences)
        {
            if (sequences == null || sequences.Count == 0) return;

            Main.Log($"[{_logPrefix}] Comparing {sequences.Count} sequences:");

            var sorted = sequences.OrderByDescending(s => s.TotalScore).ToList();
            for (int i = 0; i < sorted.Count && i < 5; i++)
            {
                var seq = sorted[i];
                string marker = i == 0 ? "★ BEST" : $"  #{i + 1}";
                Main.Log($"[{_logPrefix}] {marker}: {seq}");
            }
        }

        #endregion

        #region Static Factory Method

        /// <summary>
        /// 정적 팩토리 메서드 - Plan에서 쉽게 호출
        ///
        /// 반환값:
        /// - null: 최적화 불가 또는 "공격 스킵" 결정
        /// - ActionSequence: 최적 시퀀스
        /// </summary>
        public static ActionSequence GetOptimalAttackSequence(
            Situation situation,
            List<AbilityData> attacks,
            UnitEntityData target,
            AIRole role,
            string logPrefix = "SeqOpt")
        {
            if (situation == null || attacks == null || attacks.Count == 0 || target == null)
                return null;

            try
            {
                var optimizer = new SequenceOptimizer(situation, role, logPrefix);
                return optimizer.OptimizeAttackSequence(attacks, target);
            }
            catch (Exception ex)
            {
                Main.Error($"[{logPrefix}] Error in sequence optimization: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 후퇴 먼저 해야 하는지 판단
        /// Plan에서 쉽게 호출 가능한 정적 메서드
        /// </summary>
        public static bool ShouldRetreatBeforeAttack(Situation situation, AbilityData attack, UnitEntityData target)
        {
            if (situation == null || attack == null || target == null)
                return false;

            if (!situation.PrefersRanged || !situation.CanMove)
                return false;

            if (!situation.IsInDanger)
                return false;

            // 후퇴 위치 찾기
            var retreatScore = MovementAPI.FindRetreatPosition(
                situation.Unit,
                situation.Enemies,
                situation.MinSafeDistance,
                situation.InfluenceMap
            );

            if (retreatScore == null)
                return false;  // 후퇴 위치 없음 → 그냥 공격

            // 후퇴 후 공격 가능 여부
            try
            {
                float abilityRange = attack.GetApproachDistance(target);
                float distAfterRetreat = Vector3.Distance(retreatScore.Position, target.Position);

                if (distAfterRetreat > abilityRange)
                    return false;  // 후퇴하면 공격 불가 → 그냥 공격

                // 후퇴해도 공격 가능 → 직접 vs 후퇴 비교
                var optimizer = new SequenceOptimizer(situation, AIRole.DPS, "RetreatCheck");
                var directSeq = optimizer.CreateDirectAttackSequence(attack, target);
                var retreatSeq = optimizer.CreateRetreatThenAttackSequence(attack, target);

                if (directSeq == null || retreatSeq == null)
                    return false;

                directSeq.SimulateFinalState(situation);
                directSeq.CalculateScore(situation, AIRole.DPS);

                retreatSeq.SimulateFinalState(situation);
                retreatSeq.CalculateScore(situation, AIRole.DPS);

                bool shouldRetreat = retreatSeq.TotalScore > directSeq.TotalScore;

                Main.Log($"[RetreatCheck] Direct={directSeq.TotalScore:F0}, Retreat={retreatSeq.TotalScore:F0} => {(shouldRetreat ? "RETREAT FIRST" : "DIRECT")}");

                return shouldRetreat;
            }
            catch (Exception ex)
            {
                Main.Error($"[RetreatCheck] Error: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
