// ★ v0.2.52: TargetNormalizer - TargetAnalysis → Consideration 변환 레이어
// TargetAnalyzer의 분석 결과를 0.0~1.0 Consideration 점수로 정규화
using System;
using Kingmaker.EntitySystem.Stats;
using CompanionAI_Pathfinder.Analysis;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// ★ v0.2.52: TargetAnalysis를 Consideration 점수로 변환
    ///
    /// 모든 Scorer에서 일관된 점수 계산을 위한 정규화 레이어
    /// </summary>
    public static class TargetNormalizer
    {
        #region HP 관련

        /// <summary>
        /// HP 긴급도 (힐링 우선순위용)
        /// 낮을수록 긴급 = 높은 점수
        /// </summary>
        /// <param name="hpPercent">HP 퍼센트 (0-100)</param>
        /// <returns>0.0 (풀피) ~ 1.0 (위험)</returns>
        public static float HPUrgency(float hpPercent)
        {
            if (hpPercent < 25f)
                return 1.0f;      // Critical - 최우선
            if (hpPercent < 50f)
                return 0.7f;      // Low - 높은 우선순위
            if (hpPercent < 75f)
                return 0.4f;      // Moderate - 보통
            if (hpPercent < 90f)
                return 0.2f;      // Healthy - 낮은 우선순위
            return 0.1f;          // Full - 힐 불필요
        }

        /// <summary>
        /// 타겟 HP 가치 (공격 타겟팅용)
        /// 낮을수록 마무리 가치 = 높은 점수
        /// </summary>
        public static float TargetHPValue(float hpPercent)
        {
            if (hpPercent < 20f)
                return 1.0f;      // 킬 확정
            if (hpPercent < 35f)
                return 0.85f;     // 마무리 가능
            if (hpPercent < 50f)
                return 0.6f;      // 손상됨
            if (hpPercent < 75f)
                return 0.4f;      // 건강함
            return 0.25f;         // 풀피 적
        }

        #endregion

        #region 위협도 관련

        /// <summary>
        /// 위협도 점수 (TargetAnalysis에서)
        /// </summary>
        /// <param name="analysis">타겟 분석 결과</param>
        /// <returns>0.0 ~ 1.0</returns>
        public static float ThreatLevel(TargetAnalysis analysis)
        {
            if (analysis == null)
                return 0.5f;

            return analysis.ThreatLevel;  // 이미 0-1 범위
        }

        /// <summary>
        /// 복합 타겟 가치 (HP + 위협도)
        /// </summary>
        public static float CombinedTargetValue(TargetAnalysis analysis)
        {
            if (analysis == null)
                return 0.5f;

            float hpValue = TargetHPValue(analysis.HPPercent);
            float threatValue = analysis.ThreatLevel;

            // 가중 평균 (HP 40%, 위협도 60%)
            return hpValue * 0.4f + threatValue * 0.6f;
        }

        #endregion

        #region 역할 관련

        /// <summary>
        /// 타겟 역할 가치 (공격 우선순위용)
        /// 힐러/캐스터 > 원거리 > 근접 > 탱크 > 잡몹
        /// </summary>
        public static float TargetRoleValue(TargetRole role)
        {
            return role switch
            {
                TargetRole.Healer => 1.0f,      // 힐러 최우선
                TargetRole.Caster => 0.95f,     // 캐스터 우선
                TargetRole.Ranged => 0.7f,      // 원거리
                TargetRole.Melee => 0.6f,       // 근접
                TargetRole.Tank => 0.5f,        // 탱크 (보통)
                TargetRole.Minion => 0.3f,      // 잡몹
                _ => 0.5f                       // 알 수 없음
            };
        }

        /// <summary>
        /// 아군 역할 가치 (힐링 우선순위용)
        /// 탱크 > 힐러 > DPS > 기타
        /// </summary>
        public static float AllyRoleValue(TargetRole role)
        {
            return role switch
            {
                TargetRole.Tank => 1.0f,        // 전선 유지 중요
                TargetRole.Healer => 0.9f,      // 힐러 생존 중요
                TargetRole.Caster => 0.7f,      // 캐스터 보호
                TargetRole.Melee => 0.6f,       // 근접 딜러
                TargetRole.Ranged => 0.5f,      // 원거리 딜러
                _ => 0.5f
            };
        }

        #endregion

        #region 세이브 관련

        /// <summary>
        /// 약한 세이브 타겟팅 보너스
        /// </summary>
        /// <param name="abilityRequiredSave">능력이 요구하는 세이브</param>
        /// <param name="targetWeakestSave">타겟의 가장 약한 세이브</param>
        /// <returns>1.0 (약점 공략) ~ 0.3 (강점 공격)</returns>
        public static float WeakSaveBonus(SavingThrowType abilityRequiredSave, SavingThrowType targetWeakestSave)
        {
            if (abilityRequiredSave == SavingThrowType.Unknown)
                return 0.5f;  // 세이브 무관

            if (abilityRequiredSave == targetWeakestSave)
                return 1.0f;  // 약점 공략

            // 세이브 값 차이에 따라 조정 (여기서는 단순화)
            return 0.5f;  // 기본값
        }

        /// <summary>
        /// 세이브 취약점 점수 (상세 버전)
        /// </summary>
        public static float SaveVulnerability(TargetAnalysis analysis, SavingThrowType requiredSave)
        {
            if (analysis == null)
                return 0.5f;

            // 요구 세이브가 타겟의 가장 약한 세이브인지
            if (requiredSave == analysis.WeakestSaveType)
                return 1.0f;

            // 요구 세이브 값 가져오기
            int saveValue = requiredSave switch
            {
                SavingThrowType.Fortitude => analysis.FortitudeSave,
                SavingThrowType.Reflex => analysis.ReflexSave,
                SavingThrowType.Will => analysis.WillSave,
                _ => 15  // 기본값
            };

            // 세이브 값이 낮을수록 높은 점수
            // 세이브 5 → 1.0, 세이브 15 → 0.5, 세이브 25 → 0.0
            float normalized = 1.0f - (saveValue - 5) / 20f;
            return Math.Max(0.0f, Math.Min(1.0f, normalized));
        }

        #endregion

        #region CC/면역 관련

        /// <summary>
        /// CC 적합성 점수
        /// 이미 CC 상태면 낮은 점수
        /// </summary>
        public static float CCEligibility(TargetAnalysis analysis)
        {
            if (analysis == null)
                return 0.5f;

            // 이미 CC 상태
            if (analysis.HasAnyCC)
                return 0.1f;  // 추가 CC 불필요

            // 죽거나 의식불명
            if (analysis.IsDead || analysis.IsUnconscious)
                return 0.0f;

            // HP가 너무 낮으면 죽이는게 나음
            if (analysis.HPPercent < 15f)
                return 0.2f;

            return 1.0f;  // CC 적합
        }

        /// <summary>
        /// 면역 여부 (Veto용)
        /// </summary>
        /// <param name="analysis">타겟 분석</param>
        /// <param name="isMindAffecting">마인드어페팅 여부</param>
        /// <returns>true = 면역 아님, false = 면역 (Veto)</returns>
        public static bool NotImmune(TargetAnalysis analysis, bool isMindAffecting)
        {
            if (analysis == null)
                return true;  // 모르면 통과

            if (isMindAffecting && analysis.IsMindAffectingImmune)
                return false;  // 면역 → Veto

            return true;
        }

        #endregion

        #region 전투 상태 관련

        /// <summary>
        /// 플랭킹 보너스
        /// </summary>
        public static float FlankedBonus(TargetAnalysis analysis)
        {
            if (analysis == null)
                return 0.5f;

            if (!analysis.CanBeFlanked)
                return 0.5f;  // 플랭킹 면역

            return analysis.IsFlanked ? 1.0f : 0.7f;
        }

        /// <summary>
        /// 교전 중인 아군 수 기반 우선순위
        /// 아군을 공격 중인 적 우선 처리
        /// </summary>
        public static float EngagementPriority(TargetAnalysis analysis)
        {
            if (analysis == null)
                return 0.5f;

            int engaged = analysis.EngagedEnemyCount;
            if (engaged == 0)
                return 0.5f;

            // 교전 수에 따라 0.6 ~ 1.0
            return Math.Min(1.0f, 0.5f + engaged * 0.15f);
        }

        #endregion
    }
}
