using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Commands.Base;
using TurnBased.Controllers;
using UnityEngine;

namespace CompanionAI_Pathfinder.GameInterface
{
    /// <summary>
    /// ★ v0.2.113: 게임 API 래퍼 - 모든 게임 상호작용을 중앙화
    /// RT v3.5.7 CombatAPI.cs 패턴을 패스파인더에 맞게 적용
    /// </summary>
    public static class GameAPI
    {
        #region Command State (핵심!)

        /// <summary>
        /// ★ 핵심: 명령 큐가 비어있는지 확인
        /// RT v3.5.7의 IsCommandQueueEmpty()와 동일
        /// TurnController가 ForceTick() 호출하는 조건과 일치
        /// </summary>
        public static bool IsCommandQueueEmpty(UnitEntityData unit)
        {
            if (unit?.Commands == null) return true;
            try
            {
                return unit.Commands.Empty;
            }
            catch { return true; }
        }

        /// <summary>
        /// ★ 핵심: 유닛이 다음 행동을 할 준비가 되었는지 확인
        /// Commands.Empty && CanAct
        /// </summary>
        public static bool IsReadyForNextAction(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                bool commandsEmpty = unit.Commands.Empty;
                bool canAct = unit.Descriptor.State.CanAct;

                Main.Verbose($"[GameAPI] IsReadyForNextAction: {unit.CharacterName} - Empty={commandsEmpty}, CanAct={canAct}");

                return commandsEmpty && canAct;
            }
            catch { return false; }
        }

        /// <summary>
        /// 실행 중인 명령이 있는지 확인
        /// </summary>
        public static bool HasRunningCommand(UnitEntityData unit)
        {
            if (unit?.Commands == null) return false;
            try
            {
                return unit.Commands.IsRunning();
            }
            catch { return false; }
        }

        /// <summary>
        /// 완료되지 않은 명령이 있는지 확인
        /// </summary>
        public static bool HasUnfinishedCommand(UnitEntityData unit)
        {
            if (unit?.Commands == null) return false;
            try
            {
                return unit.Commands.HasUnfinished();
            }
            catch { return false; }
        }

        #endregion

        #region Turn-Based State

        /// <summary>
        /// 턴제 전투 중인지 확인
        /// </summary>
        public static bool IsInTurnBasedCombat()
        {
            try
            {
                return CombatController.IsInTurnBasedCombat();
            }
            catch { return false; }
        }

        /// <summary>
        /// 현재 라운드 번호 가져오기
        /// </summary>
        public static int GetCurrentRound()
        {
            try
            {
                return Game.Instance?.TurnBasedCombatController?.RoundNumber ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 현재 턴 유닛 가져오기
        /// </summary>
        public static UnitEntityData GetCurrentTurnUnit()
        {
            try
            {
                return Game.Instance?.TurnBasedCombatController?.CurrentTurn?.Rider;
            }
            catch { return null; }
        }

        /// <summary>
        /// 현재 TurnController 가져오기
        /// </summary>
        public static TurnController GetCurrentTurnController()
        {
            try
            {
                return Game.Instance?.TurnBasedCombatController?.CurrentTurn;
            }
            catch { return null; }
        }

        /// <summary>
        /// TurnController Status 가져오기
        /// </summary>
        public static TurnController.TurnStatus? GetTurnStatus()
        {
            try
            {
                return Game.Instance?.TurnBasedCombatController?.CurrentTurn?.Status;
            }
            catch { return null; }
        }

        /// <summary>
        /// 턴 강제 종료 가능한지 확인
        /// </summary>
        public static bool CanEndTurn()
        {
            try
            {
                var turnController = Game.Instance?.TurnBasedCombatController?.CurrentTurn;
                return turnController?.CanEndTurnAndNoActing() ?? false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 턴 강제 종료
        /// </summary>
        public static void ForceEndTurn()
        {
            try
            {
                var turnController = Game.Instance?.TurnBasedCombatController?.CurrentTurn;
                if (turnController != null && turnController.CanEndTurnAndNoActing())
                {
                    turnController.ForceToEnd(true);
                }
            }
            catch (Exception ex)
            {
                Main.Error($"[GameAPI] ForceEndTurn error: {ex.Message}");
            }
        }

        #endregion

        #region Action Economy (Pathfinder)

        /// <summary>
        /// Standard Action 사용 가능 여부
        /// </summary>
        public static bool HasStandardAction(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                if (IsInTurnBasedCombat())
                {
                    return unit.HasStandardAction();
                }
                else
                {
                    var cooldown = unit.CombatState?.Cooldown;
                    return cooldown?.StandardAction <= 0f;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Move Action 사용 가능 여부
        /// </summary>
        public static bool HasMoveAction(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                if (IsInTurnBasedCombat())
                {
                    return unit.HasMoveAction();
                }
                else
                {
                    var cooldown = unit.CombatState?.Cooldown;
                    return cooldown?.MoveAction <= 0f;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Swift Action 사용 가능 여부
        /// </summary>
        public static bool HasSwiftAction(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                if (IsInTurnBasedCombat())
                {
                    return unit.HasSwiftAction();
                }
                else
                {
                    var cooldown = unit.CombatState?.Cooldown;
                    return cooldown?.SwiftAction <= 0f;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Full-Round Action 사용 가능 여부 (Standard + Move)
        /// </summary>
        public static bool HasFullRoundAction(UnitEntityData unit)
        {
            return HasStandardAction(unit) && HasMoveAction(unit);
        }

        #endregion

        #region Unit State

        /// <summary>
        /// 이동 가능 여부
        /// </summary>
        public static bool CanMove(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                return unit.Descriptor.State.CanMove;
            }
            catch { return false; }
        }

        /// <summary>
        /// 행동 가능 여부
        /// </summary>
        public static bool CanAct(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                return unit.Descriptor.State.CanAct;
            }
            catch { return false; }
        }

        /// <summary>
        /// HP 퍼센트 반환
        /// </summary>
        public static float GetHPPercent(UnitEntityData unit)
        {
            if (unit == null) return 0f;
            try
            {
                float hp = unit.HPLeft;
                float maxHp = unit.MaxHP;
                if (maxHp <= 0) return 100f;
                return hp / maxHp * 100f;
            }
            catch { return 100f; }
        }

        /// <summary>
        /// 유닛 거리 계산 (미터)
        /// </summary>
        public static float GetDistance(UnitEntityData from, UnitEntityData to)
        {
            if (from == null || to == null) return float.MaxValue;
            try
            {
                return Vector3.Distance(from.Position, to.Position);
            }
            catch { return float.MaxValue; }
        }

        #endregion

        #region Unit Lists

        /// <summary>
        /// 적 목록 가져오기
        /// </summary>
        public static List<UnitEntityData> GetEnemies(UnitEntityData unit)
        {
            var enemies = new List<UnitEntityData>();
            if (unit == null) return enemies;

            try
            {
                var allUnits = Game.Instance?.State?.Units;
                if (allUnits == null) return enemies;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.HPLeft <= 0) continue;

                    // 적 판별
                    if (unit.IsPlayerFaction && other.IsPlayersEnemy)
                    {
                        enemies.Add(other);
                    }
                    else if (!unit.IsPlayerFaction && !other.IsPlayersEnemy)
                    {
                        enemies.Add(other);
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[GameAPI] GetEnemies error: {ex.Message}");
            }

            return enemies;
        }

        /// <summary>
        /// 아군 목록 가져오기
        /// </summary>
        public static List<UnitEntityData> GetAllies(UnitEntityData unit)
        {
            var allies = new List<UnitEntityData>();
            if (unit == null) return allies;

            try
            {
                var allUnits = Game.Instance?.State?.Units;
                if (allUnits == null) return allies;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.HPLeft <= 0) continue;

                    // 아군 판별
                    if (unit.IsPlayerFaction == other.IsPlayerFaction)
                    {
                        allies.Add(other);
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[GameAPI] GetAllies error: {ex.Message}");
            }

            return allies;
        }

        #endregion

        #region Ability Checks

        /// <summary>
        /// 능력을 타겟에게 사용 가능한지 확인
        /// </summary>
        public static bool CanUseAbilityOn(AbilityData ability, UnitEntityData target, out string reason)
        {
            reason = null;
            if (ability == null || target == null)
            {
                reason = "Null ability or target";
                return false;
            }

            try
            {
                // 게임 API 사용
                bool canTarget = ability.CanTarget(target);
                if (!canTarget)
                {
                    reason = "Cannot target";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 능력이 사용 가능한지 확인 (쿨다운, 리소스 등)
        /// </summary>
        public static bool IsAbilityAvailable(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                return ability.IsAvailable;
            }
            catch { return false; }
        }

        /// <summary>
        /// 능력 사거리 가져오기 (미터)
        /// </summary>
        public static float GetAbilityRange(AbilityData ability)
        {
            if (ability == null) return 0f;
            try
            {
                // Pathfinder API - 사거리 종류별 처리
                var bp = ability.Blueprint;
                if (bp == null) return 0f;

                var range = bp.Range;
                switch (range)
                {
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal:
                        return 0f;
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch:
                        return 2f;
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Close:
                        return 9f;  // 30 feet
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Medium:
                        return 30f; // 100 feet
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Long:
                        return 120f; // 400 feet
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Unlimited:
                        return 1000f;
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon:
                        // 무기 사거리 사용
                        var weapon = ability.Caster?.Unit?.Body?.PrimaryHand?.MaybeWeapon;
                        return weapon?.AttackRange.Meters ?? 2f;
                    default:
                        return bp.CustomRange.Meters;  // Feet.Meters for conversion
                }
            }
            catch { return 15f; }
        }

        #endregion

        #region Weapon

        /// <summary>
        /// 주 무기 공격 범위 (미터)
        /// </summary>
        public static float GetWeaponRange(UnitEntityData unit)
        {
            if (unit == null) return 2f;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null)
                {
                    return weapon.AttackRange.Meters;
                }
                return 2f;  // 기본 근접
            }
            catch { return 2f; }
        }

        /// <summary>
        /// 원거리 무기 보유 여부
        /// </summary>
        public static bool HasRangedWeapon(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                return weapon != null && !weapon.Blueprint.IsMelee;
            }
            catch { return false; }
        }

        /// <summary>
        /// 근접 무기 보유 여부
        /// </summary>
        public static bool HasMeleeWeapon(UnitEntityData unit)
        {
            if (unit == null) return false;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                return weapon != null && weapon.Blueprint.IsMelee;
            }
            catch { return false; }
        }

        #endregion

        #region Movement

        /// <summary>
        /// 이동 속도 (미터/초)
        /// </summary>
        public static float GetMovementSpeed(UnitEntityData unit)
        {
            if (unit == null) return 6f;
            try
            {
                return unit.ModifiedSpeedMps;
            }
            catch { return 6f; }
        }

        /// <summary>
        /// 이번 턴 최대 이동 거리 (미터)
        /// Pathfinder: 1 Move Action = 속도 (feet/6초), 대략 속도 * 1.5
        /// </summary>
        public static float GetMaxMoveDistance(UnitEntityData unit)
        {
            if (unit == null) return 9f;
            try
            {
                float speed = GetMovementSpeed(unit);
                // Pathfinder 턴제: 1 Move Action ≈ 30 feet (약 9m)
                // 속도 30ft = 약 6초에 30ft = 9m
                return speed * 1.5f;  // 대략적인 추정
            }
            catch { return 9f; }
        }

        #endregion

        #region Logging Helpers

        /// <summary>
        /// 명령 상태 상세 로깅
        /// </summary>
        public static void LogCommandState(UnitEntityData unit, string context)
        {
            if (unit?.Commands == null) return;

            try
            {
                var commands = unit.Commands;
                Main.Log($"[GameAPI] === Command State: {context} ===");
                Main.Log($"[GameAPI] Empty={commands.Empty}, IsRunning={commands.IsRunning()}, HasUnfinished={commands.HasUnfinished()}");

                foreach (var cmd in commands.Raw)
                {
                    if (cmd == null) continue;
                    Main.Log($"[GameAPI]   - {cmd.GetType().Name}: Started={cmd.IsStarted}, Running={cmd.IsRunning}, Acted={cmd.IsActed}, Finished={cmd.IsFinished}");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[GameAPI] LogCommandState error: {ex.Message}");
            }
        }

        #endregion
    }
}
