// ★ v0.2.37: Geometric Mean Scoring System - Score Normalizer
using System;
using UnityEngine;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// 다양한 게임 요소를 0.0 ~ 1.0 범위로 정규화하는 유틸리티 클래스
    /// Consideration 시스템에서 사용
    /// </summary>
    public static class ScoreNormalizer
    {
        #region HP 관련

        /// <summary>
        /// 힐링 필요도 계산 (HP가 낮을수록 높은 점수)
        /// </summary>
        /// <param name="hpPercent">현재 HP 퍼센트 (0~100+)</param>
        /// <returns>0.05 ~ 1.0 (낮은 HP = 높은 점수, Veto 방지를 위해 최소 0.05)</returns>
        public static float HealNeed(float hpPercent)
        {
            // ★ v0.2.37 fix: 오버힐된 대상도 Veto되지 않도록 최소값 보장
            float result;

            // 계단식 매핑으로 더 직관적인 긴급도 제공
            if (hpPercent < 25f)
            {
                // 위험 (1.0~0.85) - 긴급 힐 필요
                result = 1.0f - (hpPercent / 25f) * 0.15f;
            }
            else if (hpPercent < 50f)
            {
                // 낮음 (0.85~0.55) - 우선 힐
                result = 0.85f - ((hpPercent - 25f) / 25f) * 0.3f;
            }
            else if (hpPercent < 75f)
            {
                // 보통 (0.55~0.25) - 힐 고려
                result = 0.55f - ((hpPercent - 50f) / 25f) * 0.3f;
            }
            else if (hpPercent <= 100f)
            {
                // 높음 (0.25~0.1) - 힐 불필요
                result = 0.25f - ((hpPercent - 75f) / 25f) * 0.15f;
            }
            else
            {
                // 오버힐 (100% 초과) - 매우 낮지만 Veto 아님
                result = 0.05f;
            }

            // Veto 방지: 최소 0.05 보장
            return Mathf.Max(0.05f, result);
        }

        /// <summary>
        /// 처치 우선순위 계산 (HP가 낮을수록 높은 점수 - 마무리)
        /// ★ v0.2.37 fix: 최소 0.1 보장하여 GM에서 soft-veto 방지
        /// </summary>
        /// <param name="hpPercent">타겟 HP 퍼센트 (0~100)</param>
        /// <returns>0.1 ~ 1.0 (최소 0.1 보장)</returns>
        public static float KillPriority(float hpPercent)
        {
            float result;

            // 10% 이하: 매우 높은 우선순위 (마무리 가능)
            if (hpPercent <= 10f)
                result = 1.0f;
            // 25% 이하: 높은 우선순위
            else if (hpPercent <= 25f)
                result = 0.8f + (25f - hpPercent) / 75f;
            // 50% 이하: 중간 우선순위
            else if (hpPercent <= 50f)
                result = 0.5f + (50f - hpPercent) / 50f * 0.3f;
            // 50% 초과: 낮은 우선순위 (아직 체력 많음)
            else
                result = 0.5f - (hpPercent - 50f) / 100f;

            // ★ v0.2.37: 최소 0.1 보장 - GM에서 0에 가까운 값은 전체 점수를 심각하게 낮춤
            return Mathf.Max(0.1f, result);
        }

        /// <summary>
        /// HP 기반 타겟 가치 계산 (공격 대상 선정용)
        /// 낮은 HP + 높은 위협 = 우선 타겟
        /// ★ v0.2.37 fix: 최소 0.15 보장하여 GM에서 soft-veto 방지
        /// </summary>
        public static float TargetValue(float hpPercent, float threatLevel)
        {
            float hpFactor = KillPriority(hpPercent);
            float threatFactor = Mathf.Clamp01(threatLevel);

            // 가중 결합: HP 60%, 위협 40%
            float result = hpFactor * 0.6f + threatFactor * 0.4f;

            // ★ v0.2.37: 최소 0.15 보장
            // GM에서 너무 낮은 TargetValue는 공격 점수를 과도하게 낮춤
            // 공격 가능한 유일한 적을 공격하는 것은 여전히 유효한 선택
            return Mathf.Max(0.15f, result);
        }

        #endregion

        #region 리소스 관련

        /// <summary>
        /// 능력 사용 가능 여부 (Veto용 - 이진값)
        /// </summary>
        public static float CanUseAbility(bool hasResource, bool notOnCooldown)
        {
            // 둘 다 만족해야 1.0 (사용 가능)
            // 하나라도 불만족이면 0 (Veto)
            return (hasResource && notOnCooldown) ? 1f : 0f;
        }

        /// <summary>
        /// 리소스 여유도 계산 (남은 리소스가 많을수록 높은 점수)
        /// </summary>
        /// <param name="remaining">남은 사용 횟수</param>
        /// <param name="max">최대 사용 횟수 (-1이면 무제한)</param>
        public static float ResourceAvailability(int remaining, int max)
        {
            // 무제한이면 최대 여유
            if (max <= 0 || remaining < 0)
                return 1f;

            float ratio = (float)remaining / max;

            // 마지막 1회는 매우 낮은 점수 (아껴야 함)
            if (remaining == 1)
                return 0.2f;

            // 25% 이하: 낮음
            if (ratio <= 0.25f)
                return 0.3f + ratio * 0.4f;

            // 50% 이하: 중간
            if (ratio <= 0.5f)
                return 0.5f + (ratio - 0.25f) * 0.8f;

            // 50% 초과: 높음
            return 0.7f + (ratio - 0.5f) * 0.6f;
        }

        /// <summary>
        /// 주문 레벨 기반 가치 (높은 레벨 = 낮은 점수 = 아끼는 경향)
        /// </summary>
        /// <param name="spellLevel">주문 레벨 (0~9)</param>
        /// <param name="conservationFactor">보존 성향 (0.0~1.0)</param>
        public static float SpellLevelValue(int spellLevel, float conservationFactor)
        {
            if (spellLevel <= 0)
                return 1f;  // 캔트립은 자유롭게 사용

            // 레벨이 높을수록, 보존 성향이 높을수록 낮은 점수
            float levelPenalty = spellLevel / 9f;  // 0~1
            float penalty = levelPenalty * conservationFactor;

            return Mathf.Max(0.1f, 1f - penalty * 0.7f);
        }

        #endregion

        #region 거리 관련

        /// <summary>
        /// 거리 적합성 계산 (최적 거리 범위에서 높은 점수)
        /// </summary>
        /// <param name="distance">현재 거리</param>
        /// <param name="optimalMin">최적 거리 최소값</param>
        /// <param name="optimalMax">최적 거리 최대값 (사거리)</param>
        public static float DistanceSuitability(float distance, float optimalMin, float optimalMax)
        {
            // 최적 범위 내: 1.0
            if (distance >= optimalMin && distance <= optimalMax)
                return 1f;

            // 너무 가까움: 점진적 감소
            if (distance < optimalMin)
            {
                if (optimalMin <= 0)
                    return 1f;
                return 0.5f + (distance / optimalMin) * 0.5f;
            }

            // 너무 멀음: 급격한 감소 (사거리 밖)
            float overshoot = distance - optimalMax;
            return Mathf.Max(0f, 1f - overshoot / 20f);  // 20m 초과 시 0
        }

        /// <summary>
        /// 사거리 내 여부 (Veto용)
        /// </summary>
        public static float InRange(float distance, float maxRange)
        {
            return distance <= maxRange ? 1f : 0f;
        }

        /// <summary>
        /// 근접 전투 거리 적합성
        /// </summary>
        /// <param name="distance">타겟까지 거리</param>
        public static float MeleeDistance(float distance)
        {
            // 최적: 1.5m 이내
            if (distance <= 1.5f)
                return 1f;

            // 근접 가능: 3m 이내
            if (distance <= 3f)
                return 0.8f - (distance - 1.5f) * 0.1f;

            // 이동 필요: 6m 이내
            if (distance <= 6f)
                return 0.6f - (distance - 3f) * 0.1f;

            // 먼 거리: 점진적 감소
            return Mathf.Max(0.1f, 0.3f - (distance - 6f) * 0.02f);
        }

        /// <summary>
        /// 원거리 전투 거리 적합성
        /// </summary>
        /// <param name="distance">타겟까지 거리</param>
        public static float RangedDistance(float distance)
        {
            // 너무 가까움: 위험
            if (distance < 3f)
                return 0.3f + distance * 0.1f;

            // 최적 거리: 6~15m
            if (distance >= 6f && distance <= 15f)
                return 1f;

            // 약간 가까움: 3~6m
            if (distance < 6f)
                return 0.6f + (distance - 3f) / 3f * 0.4f;

            // 약간 멀음: 15~25m
            if (distance <= 25f)
                return 1f - (distance - 15f) / 10f * 0.3f;

            // 너무 멀음
            return Mathf.Max(0.1f, 0.7f - (distance - 25f) * 0.03f);
        }

        #endregion

        #region 상태/조건 관련

        /// <summary>
        /// 타겟 취약성 (면역이 아니면 1, 면역이면 0 - Veto)
        /// </summary>
        public static float TargetVulnerable(bool isImmune)
        {
            return isImmune ? 0f : 1f;
        }

        /// <summary>
        /// 이미 버프가 있는지 (중복 시전 방지)
        /// </summary>
        public static float NotAlreadyBuffed(bool hasDebuff)
        {
            return hasDebuff ? 0.1f : 1f;  // 이미 있으면 크게 감소
        }

        /// <summary>
        /// 세이브 성공 예상 (낮은 세이브 = 높은 점수)
        /// </summary>
        /// <param name="targetSave">타겟의 세이브 보너스</param>
        /// <param name="casterDC">시전자의 DC</param>
        public static float SaveChance(int targetSave, int casterDC)
        {
            // 성공 확률 추정: (DC - Save - 1) / 20
            // 1은 항상 성공, 20은 항상 실패이므로 5~95% 범위
            int diff = casterDC - targetSave;
            float successRate = Mathf.Clamp((diff + 10f) / 20f, 0.05f, 0.95f);
            return successRate;
        }

        /// <summary>
        /// 위협 수준 (0~1)
        /// </summary>
        public static float Threat(float threatLevel)
        {
            return Mathf.Clamp01(threatLevel);
        }

        #endregion

        #region 전투 페이즈 관련

        /// <summary>
        /// 공격 행동의 페이즈 적합성
        /// </summary>
        public static float AttackPhaseFit(CombatPhase phase)
        {
            switch (phase)
            {
                case CombatPhase.Opening:
                    return 0.8f;   // 버프 우선
                case CombatPhase.Midgame:
                    return 1.0f;   // 공격 최적
                case CombatPhase.Cleanup:
                    return 1.0f;   // 마무리 최적
                case CombatPhase.Desperate:
                    return 0.6f;   // 생존 우선
                default:
                    return 0.8f;
            }
        }

        /// <summary>
        /// 버프 행동의 페이즈 적합성
        /// </summary>
        public static float BuffPhaseFit(CombatPhase phase)
        {
            switch (phase)
            {
                case CombatPhase.Opening:
                    return 1.0f;   // 버프 최적
                case CombatPhase.Midgame:
                    return 0.7f;   // 적당
                case CombatPhase.Cleanup:
                    return 0.3f;   // 낭비
                case CombatPhase.Desperate:
                    return 0.5f;   // 방어 버프는 유용
                default:
                    return 0.6f;
            }
        }

        /// <summary>
        /// ★ v0.2.39: 버프 전투 가치 정규화
        /// BuffEffectAnalyzer의 원시 전투 가치를 Consideration에 적합한 값으로 변환
        /// </summary>
        /// <param name="rawCombatValue">BuffEffectAnalyzer에서 반환한 값 (0.05 ~ 1.0)</param>
        /// <returns>정규화된 값 (0.1 ~ 1.0) - Veto 방지를 위해 최소 0.1 보장</returns>
        public static float BuffCombatValue(float rawCombatValue)
        {
            // 전투 가치 매핑:
            // - 1.0 (AC/Attack/Damage) → 1.0
            // - 0.8 (Stat bonuses) → 0.82
            // - 0.7 (Temp HP) → 0.73
            // - 0.6 (Resistances) → 0.64
            // - 0.4 (Healing over time) → 0.46
            // - 0.1 (Skill only) → 0.19
            // - 0.05 (Utility) → 0.145

            // 선형 매핑: 0.05~1.0 → 0.1~1.0
            // 최소 floor 0.1 보장하여 soft-veto 방지
            float normalized = 0.1f + rawCombatValue * 0.9f;
            return Mathf.Clamp(normalized, 0.1f, 1.0f);
        }

        /// <summary>
        /// 힐링 행동의 페이즈 적합성
        /// </summary>
        public static float HealPhaseFit(CombatPhase phase)
        {
            switch (phase)
            {
                case CombatPhase.Opening:
                    return 0.5f;   // 아직 피해 없음
                case CombatPhase.Midgame:
                    return 0.9f;   // 힐 유용
                case CombatPhase.Cleanup:
                    return 0.7f;   // 전투 거의 끝남
                case CombatPhase.Desperate:
                    return 1.0f;   // 생존 최우선
                default:
                    return 0.7f;
            }
        }

        /// <summary>
        /// CC/디버프 행동의 페이즈 적합성
        /// </summary>
        public static float CCPhaseFit(CombatPhase phase, bool isHardCC)
        {
            switch (phase)
            {
                case CombatPhase.Opening:
                    return isHardCC ? 1.0f : 0.9f;   // CC로 선제 제압
                case CombatPhase.Midgame:
                    return 0.8f;   // 계속 유용
                case CombatPhase.Cleanup:
                    return 0.3f;   // 그냥 처치하는 게 나음
                case CombatPhase.Desperate:
                    return isHardCC ? 1.0f : 0.7f;   // 적 제압으로 생존
                default:
                    return 0.7f;
            }
        }

        #endregion

        #region 역할 관련

        /// <summary>
        /// 역할-행동 적합성 계산
        /// </summary>
        public static float RoleActionFit(Settings.AIRole role, CandidateType actionType)
        {
            switch (role)
            {
                case Settings.AIRole.DPS:
                    switch (actionType)
                    {
                        case CandidateType.AbilityAttack:
                        case CandidateType.BasicAttack:
                            return 1.0f;   // DPS는 공격 최우선
                        case CandidateType.Debuff:
                            return 0.8f;   // 디버프도 유용
                        case CandidateType.Buff:
                        case CandidateType.Heal:
                            return 0.5f;   // 서포트는 부차적
                        default:
                            return 0.6f;
                    }

                case Settings.AIRole.Tank:
                    switch (actionType)
                    {
                        case CandidateType.Buff:
                            return 1.0f;   // 방어 버프 중요
                        case CandidateType.AbilityAttack:
                        case CandidateType.BasicAttack:
                            return 0.8f;   // 공격도 함
                        case CandidateType.Debuff:
                            return 0.9f;   // 도발/CC 유용
                        case CandidateType.Heal:
                            return 0.6f;   // 자힐 가능
                        default:
                            return 0.6f;
                    }

                case Settings.AIRole.Support:
                    switch (actionType)
                    {
                        case CandidateType.Heal:
                            return 1.0f;   // 힐러 본업
                        case CandidateType.Buff:
                            return 0.95f;  // 버프도 핵심
                        case CandidateType.Debuff:
                            return 0.7f;   // CC도 유용
                        case CandidateType.AbilityAttack:
                        case CandidateType.BasicAttack:
                            return 0.4f;   // 공격은 후순위
                        default:
                            return 0.5f;
                    }

                default:
                    return 0.7f;
            }
        }

        #endregion

        #region 유틸리티

        /// <summary>
        /// 시그모이드 정규화 (부드러운 S곡선)
        /// </summary>
        /// <param name="x">입력값</param>
        /// <param name="midpoint">0.5가 되는 지점</param>
        /// <param name="steepness">기울기 (높을수록 급격)</param>
        public static float Sigmoid(float x, float midpoint, float steepness = 0.1f)
        {
            return 1f / (1f + Mathf.Exp(-steepness * (x - midpoint)));
        }

        /// <summary>
        /// 역 시그모이드 (높은 입력 = 낮은 출력)
        /// </summary>
        public static float InverseSigmoid(float x, float midpoint, float steepness = 0.1f)
        {
            return 1f - Sigmoid(x, midpoint, steepness);
        }

        /// <summary>
        /// 선형 매핑 (min~max를 0~1로)
        /// </summary>
        public static float Linear(float value, float min, float max)
        {
            if (max <= min)
                return 0.5f;
            return Mathf.Clamp01((value - min) / (max - min));
        }

        /// <summary>
        /// 역 선형 매핑 (min~max를 1~0으로)
        /// </summary>
        public static float InverseLinear(float value, float min, float max)
        {
            return 1f - Linear(value, min, max);
        }

        #endregion
    }
}
