// ★ v0.2.95: 행동 시퀀스 클래스 - 패스파인더 API 기반 구현
// 여러 행동의 조합을 나타내며, 전체 시퀀스의 유용성 점수를 계산
// "이동 → 공격" vs "현재 위치에서 공격" 같은 대안들을 비교 가능

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 행동 시퀀스 - 여러 행동의 조합
    /// 각 시퀀스의 예상 결과(안전도, 데미지 등)를 시뮬레이션하여 비교
    /// </summary>
    public class ActionSequence
    {
        #region Properties

        /// <summary>시퀀스 ID (디버깅용)</summary>
        public string Id { get; }

        /// <summary>포함된 행동들</summary>
        public List<PlannedAction> Actions { get; } = new List<PlannedAction>();

        /// <summary>시퀀스 설명</summary>
        public string Description { get; set; }

        /// <summary>시퀀스 완료 후 예상 위치</summary>
        public Vector3? ExpectedFinalPosition { get; set; }

        /// <summary>시퀀스 완료 후 예상 안전도 (0~100)</summary>
        public float ExpectedSafety { get; set; }

        /// <summary>예상 총 데미지</summary>
        public float ExpectedDamage { get; set; }

        /// <summary>시퀀스에 이동이 포함되어 있는가</summary>
        public bool ContainsMove => Actions.Any(a => a.Type == ActionType.Move);

        /// <summary>시퀀스에 공격이 포함되어 있는가</summary>
        public bool ContainsAttack => Actions.Any(a => a.Type == ActionType.Attack);

        #endregion

        #region Scoring

        /// <summary>최종 점수 (모든 요소 합산)</summary>
        public float TotalScore { get; private set; }

        /// <summary>공격 가치 점수</summary>
        public float OffenseScore { get; private set; }

        /// <summary>안전 가치 점수</summary>
        public float SafetyScore { get; private set; }

        /// <summary>역할 적합성 점수</summary>
        public float RoleFitScore { get; private set; }

        #endregion

        #region Constructor

        public ActionSequence(string description = null)
        {
            Id = Guid.NewGuid().ToString().Substring(0, 8);
            Description = description ?? "Unnamed";
        }

        #endregion

        #region Builder Methods

        /// <summary>이동 행동 추가</summary>
        public ActionSequence AddMove(Vector3 destination, string reason = "Move")
        {
            Actions.Add(PlannedAction.Move(destination, reason));
            ExpectedFinalPosition = destination;
            return this;
        }

        /// <summary>공격 행동 추가</summary>
        public ActionSequence AddAttack(AbilityData attack, UnitEntityData target, string reason = null)
        {
            Actions.Add(PlannedAction.Attack(attack, target, reason ?? $"Attack {target?.CharacterName}", 1f));
            return this;
        }

        #endregion

        #region Simulation

        /// <summary>
        /// 시퀀스 완료 후 예상 상태 시뮬레이션
        /// ★ 패스파인더 API 사용
        /// </summary>
        public void SimulateFinalState(Situation situation)
        {
            // 예상 최종 위치
            Vector3 finalPos = ExpectedFinalPosition ?? situation.Unit.Position;

            // 예상 안전도 계산
            ExpectedSafety = EstimateSafetyAt(finalPos, situation);

            // 예상 데미지 계산 (능력에서 추정)
            foreach (var action in Actions)
            {
                if (action.Type == ActionType.Attack && action.Ability != null)
                {
                    // ★ v0.2.95: 간단한 데미지 추정 (추후 정교화 가능)
                    ExpectedDamage += EstimateAbilityDamage(action.Ability, situation);
                }
            }
        }

        /// <summary>
        /// 특정 위치에서의 예상 안전도
        /// ★ 패스파인더 API 사용 - 적과의 거리 기반
        /// </summary>
        private float EstimateSafetyAt(Vector3 position, Situation situation)
        {
            float safety = 50f;

            // 적과의 최소 거리
            float nearestDist = float.MaxValue;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null) continue;
                try
                {
                    if (enemy.Descriptor?.State?.IsDead == true) continue;
                    float dist = Vector3.Distance(position, enemy.Position);
                    if (dist < nearestDist) nearestDist = dist;
                }
                catch { }
            }

            // 거리 기반 안전도
            float safeDistance = situation.MinSafeDistance;
            if (nearestDist >= safeDistance * 1.5f)
                safety += 30f;
            else if (nearestDist >= safeDistance)
                safety += 15f;
            else if (nearestDist < safeDistance * 0.5f)
                safety -= 30f;
            else
                safety -= 10f;

            return Math.Max(0f, Math.Min(100f, safety));
        }

        /// <summary>
        /// 능력 데미지 추정
        /// ★ v0.2.95: 간단한 추정 (레벨 기반)
        /// </summary>
        private float EstimateAbilityDamage(AbilityData ability, Situation situation)
        {
            if (ability == null) return 0f;

            try
            {
                // 시전자 레벨 기반 추정
                int level = situation.Unit?.Progression?.CharacterLevel ?? 1;

                // 무기 공격인 경우
                if (ability.Blueprint.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon)
                {
                    var weapon = situation.Unit?.GetFirstWeapon();
                    if (weapon != null)
                    {
                        // 무기 기본 데미지 + STR/DEX 보너스 추정
                        return 8f + level * 0.5f;
                    }
                }

                // 주문인 경우 (레벨당 데미지 추정)
                return 5f + level * 2f;
            }
            catch
            {
                return 10f;  // 기본값
            }
        }

        #endregion

        #region Scoring

        /// <summary>
        /// 시퀀스 점수 계산
        /// ★ 패스파인더용 - Role 기반 가중치 적용
        /// </summary>
        public void CalculateScore(Situation situation, AIRole role)
        {
            float roleSafetyWeight = GetRoleSafetyWeight(role, situation);

            // 1. 공격 가치 점수
            OffenseScore = CalculateOffenseScore();

            // 2. 안전 가치 점수 (역할 가중치 적용)
            SafetyScore = CalculateSafetyScore(roleSafetyWeight, situation);

            // 3. 역할 적합성 점수
            RoleFitScore = CalculateRoleFitScore(role, situation);

            // 총점
            TotalScore = OffenseScore + SafetyScore + RoleFitScore;
        }

        private float GetRoleSafetyWeight(AIRole role, Situation situation)
        {
            switch (role)
            {
                case AIRole.Support:
                    return 0.8f;
                case AIRole.DPS:
                    return situation.PrefersRanged ? 0.5f : 0.3f;
                case AIRole.Tank:
                    return 0.2f;
                default:  // Auto
                    return situation.PrefersRanged ? 0.6f : 0.4f;
            }
        }

        private float CalculateOffenseScore()
        {
            float score = 0f;

            // 예상 데미지 기반
            score += ExpectedDamage * 0.5f;

            // 공격 행동 포함 보너스
            if (ContainsAttack)
                score += 30f;

            return score;
        }

        private float CalculateSafetyScore(float roleSafetyWeight, Situation situation)
        {
            float score = 0f;

            // 시퀀스 완료 후 안전도 (역할 가중치 적용)
            score += ExpectedSafety * roleSafetyWeight * 0.5f;

            // 위험 상황에서 이동하지 않고 공격만 하면 감점
            if (!ContainsMove && situation.IsInDanger && ContainsAttack)
            {
                score -= 20f * roleSafetyWeight;
            }

            // 이동으로 안전도 개선 보너스
            if (ContainsMove && ExpectedSafety > 70f)
            {
                score += 15f * roleSafetyWeight;
            }

            return score;
        }

        private float CalculateRoleFitScore(AIRole role, Situation situation)
        {
            float score = 0f;
            bool isSkipSequence = Actions.Count == 0;

            switch (role)
            {
                case AIRole.Support:
                    // Support가 위험한 공격 스킵 시 보너스
                    if (isSkipSequence && situation.IsInDanger)
                        score += 25f;
                    // 안전한 원거리 위치로 이동 보너스
                    if (ContainsMove && ExpectedSafety >= 70f)
                        score += 15f;
                    break;

                case AIRole.DPS:
                    // DPS: 공격 보너스
                    if (ContainsAttack)
                        score += 10f;
                    // Range DPS가 위험하면서 스킵: 적당한 보너스
                    if (isSkipSequence && situation.IsInDanger && situation.PrefersRanged)
                        score += 15f;
                    break;

                case AIRole.Tank:
                    // Tank: 전선 유지, 후퇴 감점
                    if (ContainsMove && ExpectedFinalPosition.HasValue)
                    {
                        float currentDist = situation.NearestEnemyDistance;
                        float newDist = Vector3.Distance(ExpectedFinalPosition.Value,
                            situation.NearestEnemy?.Position ?? Vector3.zero);
                        if (newDist > currentDist)
                            score -= 15f;  // 후퇴 감점
                    }
                    break;

                default:  // Auto
                    if (situation.PrefersRanged)
                    {
                        if (isSkipSequence && situation.IsInDanger)
                            score += 20f;
                        if (ContainsMove && ExpectedSafety >= 70f)
                            score += 10f;
                    }
                    break;
            }

            return score;
        }

        #endregion

        #region ToString

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[{Description}] Score={TotalScore:F0}");
            sb.Append($" (Off={OffenseScore:F0}, Safe={SafetyScore:F0}, Role={RoleFitScore:F0})");
            sb.Append($" | Safety={ExpectedSafety:F0}");

            if (Actions.Count > 0)
            {
                sb.Append(" | ");
                foreach (var action in Actions)
                {
                    sb.Append($"{action.Type}→");
                }
            }

            return sb.ToString();
        }

        #endregion
    }
}
