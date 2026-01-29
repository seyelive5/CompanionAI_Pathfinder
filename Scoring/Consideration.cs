// ★ v0.2.37: Geometric Mean Scoring System - Consideration
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CompanionAI_Pathfinder.Scoring
{
    /// <summary>
    /// 개별 고려 요소 (0.0 ~ 1.0 범위)
    /// Geometric Mean 계산에 사용되며, 0에 가까우면 Veto 효과
    /// </summary>
    public class Consideration
    {
        /// <summary>고려 요소 이름 (디버깅용)</summary>
        public string Name { get; private set; }

        /// <summary>정규화된 점수 (0.0 ~ 1.0)</summary>
        public float Score { get; private set; }

        /// <summary>Veto 임계값 - 이 값 이하면 Veto로 처리</summary>
        public const float VETO_THRESHOLD = 0.001f;

        /// <summary>이 고려 요소가 Veto인지 (점수가 0에 가까움)</summary>
        public bool IsVeto => Score <= VETO_THRESHOLD;

        public Consideration(string name, float score)
        {
            Name = name;
            // 0.0 ~ 1.0 범위로 클램핑
            Score = Mathf.Clamp01(score);
        }

        public override string ToString()
        {
            return $"{Name}={Score:F3}{(IsVeto ? "(VETO)" : "")}";
        }
    }

    /// <summary>
    /// Consideration 수집 및 Geometric Mean 계산을 담당하는 클래스
    /// </summary>
    public class ConsiderationSet
    {
        private readonly List<Consideration> _considerations = new List<Consideration>();

        /// <summary>등록된 고려 요소 수</summary>
        public int Count => _considerations.Count;

        /// <summary>읽기 전용 고려 요소 목록</summary>
        public IReadOnlyList<Consideration> Items => _considerations;

        #region Add Methods

        /// <summary>
        /// 새로운 고려 요소 추가
        /// </summary>
        /// <param name="name">고려 요소 이름 (디버깅용)</param>
        /// <param name="score">정규화된 점수 (0.0 ~ 1.0)</param>
        public void Add(string name, float score)
        {
            _considerations.Add(new Consideration(name, score));
        }

        /// <summary>
        /// 조건부 Veto 추가 - 조건이 false이면 Veto (점수 0)
        /// </summary>
        /// <param name="name">고려 요소 이름</param>
        /// <param name="condition">true면 점수 1.0, false면 점수 0 (Veto)</param>
        public void AddVeto(string name, bool condition)
        {
            _considerations.Add(new Consideration(name, condition ? 1f : 0f));
        }

        /// <summary>
        /// 기존 점수를 정규화하여 추가
        /// </summary>
        /// <param name="name">고려 요소 이름</param>
        /// <param name="rawScore">원본 점수</param>
        /// <param name="minValue">원본 점수 최소값 (이 값에서 0.0)</param>
        /// <param name="maxValue">원본 점수 최대값 (이 값에서 1.0)</param>
        public void AddNormalized(string name, float rawScore, float minValue, float maxValue)
        {
            if (maxValue <= minValue)
            {
                _considerations.Add(new Consideration(name, 0.5f));
                return;
            }

            float normalized = (rawScore - minValue) / (maxValue - minValue);
            _considerations.Add(new Consideration(name, normalized));
        }

        /// <summary>
        /// 모든 고려 요소 제거
        /// </summary>
        public void Clear()
        {
            _considerations.Clear();
        }

        #endregion

        #region Computation

        /// <summary>
        /// Veto가 있는지 확인
        /// 하나라도 Veto면 전체 행동이 차단됨
        /// </summary>
        public bool HasVeto => _considerations.Any(c => c.IsVeto);

        /// <summary>
        /// Geometric Mean 계산
        /// Veto가 있으면 0 반환
        /// </summary>
        /// <returns>0.0 ~ 1.0 범위의 기하평균</returns>
        public float ComputeGeometricMean()
        {
            if (_considerations.Count == 0)
                return 0f;

            // Veto 체크 - 하나라도 0이면 전체가 0
            if (HasVeto)
                return 0f;

            // 기하평균 계산: (c1 × c2 × ... × cn)^(1/n)
            // 로그 방식으로 오버플로우 방지
            float logSum = 0f;
            foreach (var c in _considerations)
            {
                // Log(0)은 -무한대이므로 VETO_THRESHOLD로 이미 걸러짐
                logSum += Mathf.Log(c.Score);
            }

            float logMean = logSum / _considerations.Count;
            return Mathf.Exp(logMean);
        }

        /// <summary>
        /// 보정된 Geometric Mean 계산
        /// 작은 점수들이 과도하게 결과를 낮추는 것을 방지
        /// </summary>
        /// <param name="compensationFactor">보정 계수 (0.0~1.0, 높을수록 보정 강함)</param>
        public float ComputeCompensatedGeometricMean(float compensationFactor = 0.3f)
        {
            if (_considerations.Count == 0)
                return 0f;

            if (HasVeto)
                return 0f;

            // 보정 적용: score' = score + (1 - score) * compensation
            // 이는 낮은 점수를 약간 높여주어 한 요소가 과도하게 결과를 낮추지 않게 함
            float logSum = 0f;
            foreach (var c in _considerations)
            {
                float compensatedScore = c.Score + (1f - c.Score) * compensationFactor;
                logSum += Mathf.Log(compensatedScore);
            }

            float logMean = logSum / _considerations.Count;
            return Mathf.Exp(logMean);
        }

        /// <summary>
        /// 가중 Geometric Mean 계산
        /// 각 고려 요소에 가중치를 적용
        /// </summary>
        /// <param name="weights">요소 이름별 가중치 (없는 요소는 1.0)</param>
        public float ComputeWeightedGeometricMean(Dictionary<string, float> weights)
        {
            if (_considerations.Count == 0)
                return 0f;

            if (HasVeto)
                return 0f;

            float weightedLogSum = 0f;
            float totalWeight = 0f;

            foreach (var c in _considerations)
            {
                float weight = weights != null && weights.TryGetValue(c.Name, out var w) ? w : 1f;
                weightedLogSum += Mathf.Log(c.Score) * weight;
                totalWeight += weight;
            }

            if (totalWeight <= 0)
                return 0f;

            float logMean = weightedLogSum / totalWeight;
            return Mathf.Exp(logMean);
        }

        #endregion

        #region Debugging

        /// <summary>
        /// 디버깅용 문자열 생성
        /// </summary>
        public string ToDebugString()
        {
            if (_considerations.Count == 0)
                return "[Empty]";

            var sb = new StringBuilder();
            sb.Append("[");

            for (int i = 0; i < _considerations.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(_considerations[i].ToString());
            }

            sb.Append($"] GM={ComputeGeometricMean():F3}");

            if (HasVeto)
                sb.Append(" (VETOED)");

            return sb.ToString();
        }

        /// <summary>
        /// Veto가 발생한 요소들의 이름 반환
        /// </summary>
        public IEnumerable<string> GetVetoReasons()
        {
            return _considerations
                .Where(c => c.IsVeto)
                .Select(c => c.Name);
        }

        /// <summary>
        /// 가장 낮은 점수의 고려 요소 반환 (병목 식별용)
        /// </summary>
        public Consideration GetBottleneck()
        {
            if (_considerations.Count == 0)
                return null;

            return _considerations.OrderBy(c => c.Score).FirstOrDefault();
        }

        #endregion
    }
}
