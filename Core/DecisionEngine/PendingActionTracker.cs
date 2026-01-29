// ★ v0.2.23: Pending Action Tracker - 실시간 전투에서 중복 행동 방지
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;

namespace CompanionAI_Pathfinder.Core.DecisionEngine
{
    /// <summary>
    /// 실시간 전투에서 여러 캐릭터가 동시에 같은 행동을 수행하는 것을 방지
    /// 예: 여러 캐릭터가 동시에 같은 대상에게 같은 버프 시전
    /// </summary>
    public class PendingActionTracker
    {
        #region Singleton

        private static PendingActionTracker _instance;
        public static PendingActionTracker Instance => _instance ?? (_instance = new PendingActionTracker());

        private PendingActionTracker()
        {
            Main.Log("[PendingActionTracker] Initialized");
        }

        #endregion

        #region Data Structures

        /// <summary>
        /// Pending action entry
        /// </summary>
        private class PendingEntry
        {
            public string AbilityGuid { get; set; }
            public string TargetId { get; set; }
            public string CasterId { get; set; }
            public float StartTime { get; set; }
            public float ExpireTime { get; set; }
        }

        // Key: "abilityGuid:targetId", Value: PendingEntry
        private readonly Dictionary<string, PendingEntry> _pendingBuffs = new Dictionary<string, PendingEntry>();

        // ★ v0.2.27: 버프 중복 방지 - 5초로 감소 (30초는 너무 길었음)
        // 실제 버프 적용까지 약 2-3초면 충분
        private const float PENDING_DURATION = 5.0f;

        // Cleanup interval
        private float _lastCleanupTime = 0f;
        private const float CLEANUP_INTERVAL = 1.0f;

        #endregion

        #region Public API

        /// <summary>
        /// Check if a buff is already pending for the target
        /// </summary>
        public bool IsBuffPending(AbilityData ability, UnitEntityData target)
        {
            if (ability?.Blueprint == null || target == null)
                return false;

            CleanupExpiredEntries();

            string key = GetKey(ability, target);
            return _pendingBuffs.ContainsKey(key);
        }

        /// <summary>
        /// Check if a buff is pending for the target (by a different caster)
        /// </summary>
        public bool IsBuffPendingByOther(AbilityData ability, UnitEntityData target, UnitEntityData caster)
        {
            if (ability?.Blueprint == null || target == null || caster == null)
                return false;

            CleanupExpiredEntries();

            string key = GetKey(ability, target);
            if (_pendingBuffs.TryGetValue(key, out var entry))
            {
                // Return true only if it's a different caster
                return entry.CasterId != caster.UniqueId;
            }
            return false;
        }

        /// <summary>
        /// Register a pending buff action
        /// </summary>
        public void RegisterPendingBuff(AbilityData ability, UnitEntityData target, UnitEntityData caster)
        {
            if (ability?.Blueprint == null || target == null || caster == null)
                return;

            string key = GetKey(ability, target);
            float currentTime = Time.time;

            _pendingBuffs[key] = new PendingEntry
            {
                AbilityGuid = ability.Blueprint.AssetGuid.ToString(),
                TargetId = target.UniqueId,
                CasterId = caster.UniqueId,
                StartTime = currentTime,
                ExpireTime = currentTime + PENDING_DURATION
            };

            string casterName = caster.CharacterName;
            string targetName = target.CharacterName;
            Main.Verbose($"[PendingTracker] Registered: {casterName} -> {targetName} ({ability.Name})");
        }

        /// <summary>
        /// Remove a pending entry (when action completes or fails)
        /// </summary>
        public void RemovePending(AbilityData ability, UnitEntityData target)
        {
            if (ability?.Blueprint == null || target == null)
                return;

            string key = GetKey(ability, target);
            if (_pendingBuffs.Remove(key))
            {
                Main.Verbose($"[PendingTracker] Removed: {ability.Name} -> {target.CharacterName}");
            }
        }

        /// <summary>
        /// Clear all pending entries (e.g., combat end)
        /// </summary>
        public void Clear()
        {
            _pendingBuffs.Clear();
            Main.Verbose("[PendingTracker] Cleared all pending entries");
        }

        /// <summary>
        /// Reset the singleton instance
        /// </summary>
        public static void Reset()
        {
            _instance?.Clear();
            _instance = null;
            Main.Log("[PendingActionTracker] Reset");
        }

        #endregion

        #region Helper Methods

        private string GetKey(AbilityData ability, UnitEntityData target)
        {
            return $"{ability.Blueprint.AssetGuid}:{target.UniqueId}";
        }

        private void CleanupExpiredEntries()
        {
            float currentTime = Time.time;

            // Throttle cleanup
            if (currentTime - _lastCleanupTime < CLEANUP_INTERVAL)
                return;

            _lastCleanupTime = currentTime;

            var expiredKeys = _pendingBuffs
                .Where(kvp => kvp.Value.ExpireTime < currentTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _pendingBuffs.Remove(key);
            }

            if (expiredKeys.Count > 0)
            {
                Main.Verbose($"[PendingTracker] Cleaned up {expiredKeys.Count} expired entries");
            }
        }

        #endregion
    }
}
