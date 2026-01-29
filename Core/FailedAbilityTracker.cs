// ★ v0.2.46: 실패한 능력+타겟 조합 추적
// ★ v0.2.48: 마지막 발행 명령 추적 - 연속 반복 시 자동 블랙리스트
// Hex 등 1회성 능력이 같은 대상에게 반복 시도되는 것 방지
using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;

namespace CompanionAI_Pathfinder.Core
{
    /// <summary>
    /// 실패한 능력 시도 추적
    /// 명령이 큐에 들어가지 않은 경우 (Hex 1회 제한 등) 기록
    /// ★ v0.2.48: 마지막 발행 명령 추적으로 연속 반복 감지
    /// </summary>
    public class FailedAbilityTracker
    {
        #region Singleton

        private static FailedAbilityTracker _instance;
        public static FailedAbilityTracker Instance => _instance ?? (_instance = new FailedAbilityTracker());

        private FailedAbilityTracker() { }

        #endregion

        // Key: "UnitGuid_AbilityGuid_TargetGuid", Value: 실패 시간
        private readonly Dictionary<string, float> _failedCombinations = new Dictionary<string, float>();

        // ★ v0.2.48: 마지막 발행 명령 추적
        // Key: UnitGuid, Value: (AbilityGuid_TargetGuid, 발행시간, 반복횟수)
        private readonly Dictionary<string, LastCommandInfo> _lastCommands = new Dictionary<string, LastCommandInfo>();

        // 블랙리스트 유지 시간 (초) - 전투 중에는 다시 시도하지 않음
        private const float BLACKLIST_DURATION = 300f;  // 5분

        // ★ v0.2.48: 연속 반복 감지 임계값
        private const float REPEAT_WINDOW = 3.0f;  // 3초 내 반복
        private const int REPEAT_THRESHOLD = 2;     // 2회 반복 시 블랙리스트

        private class LastCommandInfo
        {
            public string AbilityTargetKey;
            public float IssueTime;
            public int RepeatCount;
        }

        /// <summary>
        /// 능력 실행 실패 기록
        /// ★ v0.2.48: 변형 능력(Evil Eye 등)은 기본 이름으로 블랙리스트
        /// </summary>
        public void RecordFailure(UnitEntityData caster, AbilityData ability, UnitEntityData target)
        {
            if (caster == null || ability == null || target == null)
                return;

            string key = GetKey(caster, ability, target);
            _failedCombinations[key] = UnityEngine.Time.time;

            // ★ v0.2.48: 변형 능력 처리 - 기본 이름으로도 블랙리스트
            string baseKey = GetBaseNameKey(caster, ability, target);
            if (baseKey != key)
            {
                _failedCombinations[baseKey] = UnityEngine.Time.time;
                Main.Verbose($"[FailedAbilityTracker] Also blacklisted base name: {GetAbilityBaseName(ability.Name)}");
            }

            Main.Verbose($"[FailedAbilityTracker] Recorded failure: {caster.CharacterName} -> {ability.Name} -> {target.CharacterName}");
        }

        /// <summary>
        /// ★ v0.2.48: 명령 발행 기록 및 반복 감지
        /// 같은 명령이 짧은 시간 내 반복되면 자동 블랙리스트
        /// ★ 변형 능력(Evil Eye 등)은 기본 이름으로 감지
        /// </summary>
        /// <returns>true if this is a repeated command and should be blacklisted</returns>
        public bool RecordCommandAndCheckRepeat(UnitEntityData caster, AbilityData ability, UnitEntityData target)
        {
            if (caster == null || ability == null || target == null)
                return false;

            string unitKey = caster.UniqueId ?? caster.CharacterName;
            string targetGuid = target.UniqueId ?? target.CharacterName;

            // ★ v0.2.48: 기본 이름으로 추적 (변형 능력 처리)
            // "악의 눈 - AC"와 "악의 눈 - 공격"이 같은 것으로 감지됨
            string baseName = GetAbilityBaseName(ability.Name);
            string abilityTargetKey = $"NAME:{baseName}_{targetGuid}";

            float currentTime = UnityEngine.Time.time;

            if (_lastCommands.TryGetValue(unitKey, out var lastCmd))
            {
                // 같은 능력+타겟 조합인지, 그리고 짧은 시간 내인지 확인
                if (lastCmd.AbilityTargetKey == abilityTargetKey &&
                    currentTime - lastCmd.IssueTime < REPEAT_WINDOW)
                {
                    lastCmd.RepeatCount++;
                    lastCmd.IssueTime = currentTime;

                    Main.Verbose($"[FailedAbilityTracker] Repeat detected: {caster.CharacterName} -> {baseName} (count={lastCmd.RepeatCount})");

                    // 반복 횟수 임계값 초과 시 블랙리스트
                    if (lastCmd.RepeatCount >= REPEAT_THRESHOLD)
                    {
                        RecordFailure(caster, ability, target);
                        Main.Log($"[FailedAbilityTracker] ★ Auto-blacklisted due to repeat: {caster.CharacterName} -> {baseName} -> {target.CharacterName}");
                        return true;
                    }
                }
                else
                {
                    // 다른 명령이거나 시간 경과 → 리셋
                    lastCmd.AbilityTargetKey = abilityTargetKey;
                    lastCmd.IssueTime = currentTime;
                    lastCmd.RepeatCount = 1;
                }
            }
            else
            {
                // 첫 기록
                _lastCommands[unitKey] = new LastCommandInfo
                {
                    AbilityTargetKey = abilityTargetKey,
                    IssueTime = currentTime,
                    RepeatCount = 1
                };
            }

            return false;
        }

        /// <summary>
        /// 해당 조합이 블랙리스트에 있는지 확인
        /// ★ v0.2.48: 변형 능력도 기본 이름으로 체크
        /// </summary>
        public bool IsBlacklisted(UnitEntityData caster, AbilityData ability, UnitEntityData target)
        {
            if (caster == null || ability == null || target == null)
                return false;

            // 정확한 GUID로 체크
            string key = GetKey(caster, ability, target);
            if (CheckBlacklist(key))
                return true;

            // ★ v0.2.48: 기본 이름으로도 체크 (변형 능력 처리)
            string baseKey = GetBaseNameKey(caster, ability, target);
            if (baseKey != key && CheckBlacklist(baseKey))
                return true;

            return false;
        }

        private bool CheckBlacklist(string key)
        {
            if (_failedCombinations.TryGetValue(key, out float failTime))
            {
                float elapsed = UnityEngine.Time.time - failTime;
                if (elapsed < BLACKLIST_DURATION)
                {
                    return true;
                }
                else
                {
                    // 시간 지났으면 제거
                    _failedCombinations.Remove(key);
                }
            }
            return false;
        }

        /// <summary>
        /// 전투 종료 시 블랙리스트 초기화
        /// </summary>
        public void ClearAll()
        {
            _failedCombinations.Clear();
            Main.Verbose($"[FailedAbilityTracker] Cleared all blacklisted combinations");
        }

        /// <summary>
        /// 오래된 항목 정리
        /// </summary>
        public void Cleanup()
        {
            float currentTime = UnityEngine.Time.time;
            var keysToRemove = new List<string>();

            foreach (var kvp in _failedCombinations)
            {
                if (currentTime - kvp.Value > BLACKLIST_DURATION)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _failedCombinations.Remove(key);
            }
        }

        private string GetKey(UnitEntityData caster, AbilityData ability, UnitEntityData target)
        {
            string casterGuid = caster.UniqueId ?? caster.CharacterName;
            string abilityGuid = ability.Blueprint?.AssetGuid.ToString() ?? ability.Name;
            string targetGuid = target.UniqueId ?? target.CharacterName;

            return $"{casterGuid}_{abilityGuid}_{targetGuid}";
        }

        /// <summary>
        /// ★ v0.2.48: 능력의 기본 이름으로 키 생성 (변형 능력 처리)
        /// 예: "악의 눈 - AC" → "악의 눈"으로 블랙리스트
        /// </summary>
        private string GetBaseNameKey(UnitEntityData caster, AbilityData ability, UnitEntityData target)
        {
            string casterGuid = caster.UniqueId ?? caster.CharacterName;
            string baseName = GetAbilityBaseName(ability.Name);
            string targetGuid = target.UniqueId ?? target.CharacterName;

            return $"{casterGuid}_NAME:{baseName}_{targetGuid}";
        }

        /// <summary>
        /// ★ v0.2.48: 능력 이름에서 기본 이름 추출
        /// "악의 눈 - AC" → "악의 눈"
        /// "Evil Eye - Attack" → "Evil Eye"
        /// </summary>
        private string GetAbilityBaseName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return fullName;

            // 일반적인 구분자들: " - ", " (", ":", " –"
            string[] separators = { " - ", " — ", " – ", " (", ":", "（" };

            foreach (var sep in separators)
            {
                int idx = fullName.IndexOf(sep);
                if (idx > 0)
                {
                    return fullName.Substring(0, idx).Trim();
                }
            }

            return fullName;
        }
    }
}
