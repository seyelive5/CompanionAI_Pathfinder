using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 상황 분석기 - 전투 상황을 분석하여 Situation 객체 생성
    /// </summary>
    public class SituationAnalyzer
    {
        #region Ability Blacklist

        /// <summary>
        /// v0.2.9: 타입 기반 블랙리스트 체크 (스트링 매칭 금지)
        /// 능력의 컴포넌트에서 특정 ContextAction 타입을 검사
        /// </summary>
        private bool IsBlacklisted(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;

            try
            {
                // v0.2.9: Demoralize 액션을 포함하는 능력 블랙리스트
                // AbilityEffectRunAction 컴포넌트에서 Demoralize 타입 확인
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        // Demoralize ContextAction 타입 체크
                        if (action is Demoralize)
                        {
                            Main.Verbose($"[Analyzer] BLACKLISTED: {ability.Name} (contains Demoralize action)");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] IsBlacklisted error: {ex.Message}");
            }

            return false;
        }

        #endregion
        /// <summary>
        /// 현재 상황 분석
        /// ★ v0.2.33: 성능 측정 코드 추가
        /// </summary>
        public Situation Analyze(UnitEntityData unit, TurnState turnState)
        {
            var situation = new Situation
            {
                Unit = unit
            };

            // ★ v0.2.33: 성능 측정
            var sw = new Stopwatch();
            long gridMs = 0, stateMs = 0, weaponMs = 0, bfMs = 0, abilMs = 0, targetMs = 0, posMs = 0;

            try
            {
                // ★ v0.2.30: BattlefieldGrid 초기화 (전장 그리드 캐싱)
                sw.Start();
                BattlefieldGrid.Instance.EnsureInitialized();
                sw.Stop(); gridMs = sw.ElapsedMilliseconds;

                // 기본 상태
                sw.Restart();
                AnalyzeUnitState(situation, unit, turnState);
                sw.Stop(); stateMs = sw.ElapsedMilliseconds;

                // 설정
                LoadSettings(situation, unit);

                // 무기
                sw.Restart();
                AnalyzeWeapons(situation, unit);
                sw.Stop(); weaponMs = sw.ElapsedMilliseconds;

                // 전장 상황
                sw.Restart();
                AnalyzeBattlefield(situation, unit);
                sw.Stop(); bfMs = sw.ElapsedMilliseconds;

                // 능력 분류
                sw.Restart();
                AnalyzeAbilities(situation, unit);
                sw.Stop(); abilMs = sw.ElapsedMilliseconds;

                // 타겟 분석
                sw.Restart();
                AnalyzeTargets(situation, unit);
                sw.Stop(); targetMs = sw.ElapsedMilliseconds;

                // 위치 분석
                sw.Restart();
                AnalyzePosition(situation, unit);
                sw.Stop(); posMs = sw.ElapsedMilliseconds;

                // 턴 상태 복사
                CopyTurnState(situation, turnState);

                // ★ v0.2.30: TeamBlackboard에 상황 등록
                try
                {
                    string unitId = unit.UniqueId ?? unit.CharacterName;
                    TeamBlackboard.Instance.RegisterUnitSituation(unitId, situation);
                }
                catch (Exception bbEx)
                {
                    Main.Verbose($"[Analyzer] TeamBlackboard registration error: {bbEx.Message}");
                }

                // ★ v0.2.33: 성능 로그 출력 (총 1ms 이상일 때)
                long totalMs = gridMs + stateMs + weaponMs + bfMs + abilMs + targetMs + posMs;
                if (totalMs > 0)
                {
                    string unitName = unit?.CharacterName ?? "Unknown";
                    Main.Log($"[PERF-Analyze] {unitName}: Grid={gridMs}, State={stateMs}, Weapon={weaponMs}, BF={bfMs}, Abil={abilMs}, Target={targetMs}, Pos={posMs} (Total={totalMs}ms)");
                }

                Main.Verbose($"[Analyzer] {situation}");
            }
            catch (Exception ex)
            {
                Main.Error($"[Analyzer] Error analyzing situation: {ex.Message}");
                Main.Error($"[Analyzer] Stack: {ex.StackTrace}");
                return null;
            }

            return situation;
        }

        #region Analysis Methods

        private void AnalyzeUnitState(Situation situation, UnitEntityData unit, TurnState turnState)
        {
            situation.HPPercent = GetHPPercent(unit);
            situation.CanMove = CanMove(unit);
            situation.CanAct = CanAct(unit);

            // Pathfinder 액션 시스템
            situation.HasStandardAction = turnState?.HasStandardAction ?? true;
            situation.HasMoveAction = turnState?.HasMoveAction ?? true;
            situation.HasSwiftAction = turnState?.HasSwiftAction ?? true;

            // v0.2.18: 스닉 어택 보유 감지
            // ★ v0.2.62: AbilityClassifier.HasSneakAttack() 사용 (문자열 검색 제거)
            try
            {
                situation.HasSneakAttack = AbilityClassifier.HasSneakAttack(unit);
                if (situation.HasSneakAttack)
                    Main.Verbose($"[Analyzer] {unit.CharacterName} has Sneak Attack (dice={unit.Stats.SneakAttack.BaseValue})");
            }
            catch { }

            // v0.2.18: 교전 상태
            try
            {
                situation.IsEngaged = unit.CombatState?.IsEngaged ?? false;
                situation.IsFlanked = unit.CombatState?.IsFlanked ?? false;
                situation.EngagedByCount = unit.CombatState?.EngagedBy?.Count ?? 0;
            }
            catch { }
        }

        private void LoadSettings(Situation situation, UnitEntityData unit)
        {
            var settings = ModSettings.Instance;
            var charSettings = settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);

            situation.CharacterSettings = charSettings;
            situation.RangePreference = charSettings?.RangePreference ?? RangePreference.Mixed;
            situation.MinSafeDistance = 10f;  // 기본 안전 거리
        }

        private void AnalyzeWeapons(Situation situation, UnitEntityData unit)
        {
            situation.HasRangedWeapon = HasRangedWeapon(unit);
            situation.HasMeleeWeapon = HasMeleeWeapon(unit);

            // ★ v0.2.25: 무기 공격 범위 계산
            situation.WeaponRange = GetWeaponAttackRange(unit);
        }

        /// <summary>
        /// ★ v0.2.25: 주 무기의 공격 범위 반환 (원거리/근접 모두 지원)
        /// </summary>
        private float GetWeaponAttackRange(UnitEntityData unit)
        {
            try
            {
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return 2f;

                // 무기의 AttackRange 사용
                var attackRange = weapon.AttackRange;
                if (attackRange.Meters > 0)
                {
                    return attackRange.Meters;
                }

                // 폴백: 근접 무기면 2m, 아니면 기본 원거리 15m
                return weapon.Blueprint.IsMelee ? 2f : 15f;
            }
            catch
            {
                return 2f;
            }
        }

        private void AnalyzeBattlefield(Situation situation, UnitEntityData unit)
        {
            // 적 목록
            situation.Enemies = GetEnemies(unit);

            // 아군 목록
            situation.Allies = GetAllies(unit);

            // 가장 가까운 적
            situation.NearestEnemy = situation.Enemies
                .Where(e => e != null && !e.Descriptor.State.IsDead)
                .OrderBy(e => Vector3.Distance(unit.Position, e.Position))
                .FirstOrDefault();

            situation.NearestEnemyDistance = situation.NearestEnemy != null
                ? Vector3.Distance(unit.Position, situation.NearestEnemy.Position)
                : float.MaxValue;

            // 가장 부상당한 아군
            situation.MostWoundedAlly = situation.Allies
                .Where(a => a != null && !a.Descriptor.State.IsDead && a != unit)
                .OrderBy(a => GetHPPercent(a))
                .FirstOrDefault();

            // v0.2.17: 위협도 분석 - 아군을 교전 중인 적 수 계산
            AnalyzeThreats(situation, unit);

            // v0.2.18: 플랭킹된 적 목록 구성
            try
            {
                foreach (var enemy in situation.Enemies)
                {
                    if (enemy == null || enemy.Descriptor.State.IsDead) continue;
                    if (enemy.CombatState?.IsFlanked == true &&
                        !(enemy.Descriptor?.State?.Features?.CannotBeFlanked ?? false))
                    {
                        situation.FlankedEnemies.Add(enemy);
                    }
                }
                if (situation.FlankedEnemies.Count > 0)
                    Main.Verbose($"[Analyzer] {situation.FlankedEnemies.Count} flanked enemies detected");
            }
            catch { }

            // ★ v0.2.34: InfluenceMap 계산 - 전역 캐시 목록 사용
            // 이전: situation.Enemies/Allies (유닛별 필터링된 목록) → 캐시 미스 빈발
            // 개선: CombatDataCache 전역 목록 → 캐시 적중률 대폭 향상
            try
            {
                CombatDataCache.Instance.RefreshIfNeeded();
                situation.InfluenceMap = BattlefieldInfluenceMap.Compute(
                    CombatDataCache.Instance.AllEnemies,
                    CombatDataCache.Instance.AllAllies);
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] InfluenceMap compute error: {ex.Message}");
            }
        }

        /// <summary>
        /// v0.2.17: 위협도 분석 - 교전 상태 기반
        /// </summary>
        private void AnalyzeThreats(Situation situation, UnitEntityData unit)
        {
            try
            {
                int alliesUnderThreat = 0;
                var threateningEnemies = new HashSet<UnitEntityData>();

                foreach (var ally in situation.Allies)
                {
                    if (ally == null || ally.Descriptor.State.IsDead) continue;
                    if (ally == unit) continue;

                    // 아군을 교전 중인 적이 있으면 위협받고 있음
                    var engagedBy = ally.CombatState?.EngagedBy;
                    if (engagedBy != null && engagedBy.Count > 0)
                    {
                        alliesUnderThreat++;
                        foreach (var enemy in engagedBy)
                        {
                            if (enemy != null && !enemy.Descriptor.State.IsDead)
                                threateningEnemies.Add(enemy);
                        }
                    }
                }

                situation.AlliesUnderThreat = alliesUnderThreat;
                situation.EnemiesTargetingAllies = threateningEnemies.Count;

                if (alliesUnderThreat > 0)
                {
                    Main.Verbose($"[Analyzer] Threats: {alliesUnderThreat} allies under threat by {threateningEnemies.Count} enemies");
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] AnalyzeThreats error: {ex.Message}");
            }
        }

        private void AnalyzeTargets(Situation situation, UnitEntityData unit)
        {
            // 공격 가능한 적 찾기
            situation.HittableEnemies = new List<UnitEntityData>();

            var attacks = situation.AvailableAttacks;
            if (attacks == null || attacks.Count == 0)
            {
                Main.Verbose($"[Analyzer] No available attacks for hittable check");
            }
            else
            {
                Main.Verbose($"[Analyzer] Checking hittable with {attacks.Count} available attacks");
            }

            // v0.2.7: 각 적에 대해 공격 가능 여부 확인
            // ability.CanTarget()은 사거리 + LOS + 타겟 제한을 모두 확인
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || enemy.Descriptor.State.IsDead) continue;

                // ★ v0.2.61: 게임의 2D 거리 계산 사용 (Y축 무시) - 탑승 캐릭터 수정
                float distanceToEnemy = unit.DistanceTo(enemy);
                bool canHit = false;
                string hitReason = "";

                foreach (var attack in attacks ?? Enumerable.Empty<AbilityData>())
                {
                    if (attack == null) continue;

                    // v0.2.7: 게임 API의 CanTarget() 직접 사용 (사거리+LOS+제한 통합 체크)
                    var targetWrapper = new TargetWrapper(enemy);
                    if (attack.CanTarget(targetWrapper))
                    {
                        canHit = true;
                        hitReason = attack.Name;
                        break;
                    }
                    else
                    {
                        // ★ v0.2.61: 상세 원인 로깅 추가
                        string reason = "";
                        try { reason = attack.CantTargetReason(targetWrapper); } catch { }
                        Main.Verbose($"[Analyzer] {enemy.CharacterName}: {attack.Name} CanTarget=false (dist={distanceToEnemy:F1}m, reason={reason})");
                    }
                }

                // v0.2.7: 공격 능력 없으면 기본 공격으로 체크 (UnitAttack)
                // ★ v0.2.61: 탑승 캐릭터의 경우 Charge 등이 실패해도 기본 공격 가능
                float meleeReach = GetMeleeReach(unit);
                if (!canHit && distanceToEnemy <= meleeReach)
                {
                    // 근접 범위 내면 기본 공격 가능으로 간주
                    canHit = true;
                    hitReason = "Melee reach";
                }
                else if (!canHit)
                {
                    Main.Verbose($"[Analyzer] {enemy.CharacterName}: melee fallback failed (dist={distanceToEnemy:F1}m > reach={meleeReach:F1}m)");
                }

                // ★ v0.2.50: LOS (Line of Sight) 체크 추가
                // CanTarget()은 LOS를 체크하지 않으므로 별도 확인 필요
                if (canHit)
                {
                    bool hasLOS = LineOfSightChecker.HasLineOfSight(unit, enemy);
                    if (!hasLOS)
                    {
                        canHit = false;
                        Main.Verbose($"[Analyzer] {enemy.CharacterName}: LOS blocked (no line of sight)");
                    }
                }

                if (canHit)
                {
                    situation.HittableEnemies.Add(enemy);
                    Main.Verbose($"[Analyzer] ✓ {enemy.CharacterName} hittable by {hitReason}");
                }
            }

            int hittableCount = situation.HittableEnemies.Count;
            int totalEnemies = situation.Enemies?.Count ?? 0;
            Main.Log($"[Analyzer] Hittable: {hittableCount}/{totalEnemies}" +
                     (hittableCount == 0 && totalEnemies > 0 ? " - MOVEMENT NEEDED" : ""));

            // v0.2.17: 최적 타겟 선택 - TargetScorer 사용 (폴백도 스코어링)
            if (situation.HittableEnemies.Count > 0)
            {
                situation.BestTarget = TargetScorer.SelectBestEnemy(situation.HittableEnemies, situation);
            }
            else if (situation.Enemies.Count > 0)
            {
                // hittable 없으면 전체 적 중 최적 접근 타겟 선택 (단순 가까운 적 X)
                situation.BestTarget = TargetScorer.SelectBestEnemy(
                    situation.Enemies.Where(e => e != null && !e.Descriptor.State.IsDead).ToList(),
                    situation);
            }

            // v0.2.17: CanKillBestTarget 판정
            if (situation.BestTarget != null)
            {
                situation.CanKillBestTarget = EstimateCanKill(situation.BestTarget);
            }

            // Primary Attack 선택
            situation.PrimaryAttack = SelectPrimaryAttack(situation, unit);
        }

        /// <summary>
        /// v0.2.7: 유닛의 근접 도달 범위 계산
        /// Pathfinder에서 기본 reach는 5피트 (약 1.5m), Reach 무기는 10피트
        /// ★ v0.2.61: 게임 API 사용 (탑승, 크기, Reach 보너스 포함)
        /// </summary>
        private float GetMeleeReach(UnitEntityData unit)
        {
            try
            {
                // ★ v0.2.61: 게임 API의 GetWeaponRange 사용 - 탑승, 크기, Reach 스탯 모두 포함
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null)
                {
                    // AttackRange는 내부적으로 wielder.GetWeaponRange()를 호출
                    // 이는 무기 기본 사거리 + Stats.ReachRange (크기/Reach 보너스)를 포함
                    var attackRange = weapon.AttackRange;
                    if (attackRange.Meters > 0)
                    {
                        // Corpulence (몸 크기) 보너스 추가
                        float corpulenceBonus = unit.Corpulence;
                        float reach = attackRange.Meters + corpulenceBonus + 0.5f; // 약간의 여유
                        Main.Verbose($"[Analyzer] GetMeleeReach: {unit.CharacterName} = {reach:F1}m (wpn={attackRange.Meters:F1}m, corp={corpulenceBonus:F1}m)");
                        return reach;
                    }
                }

                // 폴백: Descriptor에서 직접 Reach 계산
                if (unit?.Descriptor != null)
                {
                    var reachRange = unit.Descriptor.GetWeaponRange(null);
                    if (reachRange.Meters > 0)
                    {
                        float reach = reachRange.Meters + unit.Corpulence + 0.5f;
                        Main.Verbose($"[Analyzer] GetMeleeReach (fallback): {unit.CharacterName} = {reach:F1}m");
                        return reach;
                    }
                }

                return 2.5f;  // 기본값 (5ft + 여유)
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] GetMeleeReach error: {ex.Message}");
                return 2.5f;
            }
        }

        private void AnalyzePosition(Situation situation, UnitEntityData unit)
        {
            // v0.2.18: 위험 상태 - 교전 기반으로 개선
            if (situation.PrefersRanged)
            {
                // 원거리: 교전 중이면 위험 (거리보다 정확한 판단)
                situation.IsInDanger = situation.IsEngaged ||
                                       situation.NearestEnemyDistance < situation.MinSafeDistance;
            }
            else
            {
                // 근접: 2명 이상에게 교전당하면 위험
                situation.IsInDanger = situation.EngagedByCount >= 3 && situation.IsHPLow;
            }

            // 이동 필요: 공격 가능한 적 없음
            situation.NeedsReposition = !situation.HasHittableEnemies &&
                                        situation.HasLivingEnemies &&
                                        situation.HasMoveAction;
        }

        private void AnalyzeAbilities(Situation situation, UnitEntityData unit)
        {
            var allAbilities = GetAvailableAbilities(unit);

            Main.Verbose($"[Analyzer] {unit.CharacterName}: Analyzing {allAbilities.Count} abilities");

            foreach (var ability in allAbilities)
            {
                if (ability?.Blueprint == null) continue;

                // v0.2.6: 블랙리스트 체크 (Demoralize 등)
                if (IsBlacklisted(ability))
                    continue;

                // AbilityClassifier 사용
                var classification = AbilityClassifier.Classify(ability, unit);

                // 이미 적용된 버프 스킵
                if (classification.IsAlreadyApplied)
                {
                    Main.Verbose($"[Analyzer] Skipping already applied: {ability.Name}");
                    continue;
                }

                Main.Verbose($"[Analyzer] {ability.Name}: Timing={classification.Timing}, Resource={classification.ResourceType}");

                switch (classification.Timing)
                {
                    case AbilityTiming.Healing:
                        situation.AvailableHeals.Add(ability);
                        break;

                    case AbilityTiming.PreCombatBuff:
                    case AbilityTiming.PermanentBuff:
                        situation.AvailableBuffs.Add(ability);
                        break;

                    case AbilityTiming.Debuff:
                        situation.AvailableDebuffs.Add(ability);
                        // v0.2.18: 세이브 타입 저장
                        if (classification.RequiredSave.HasValue)
                            situation.DebuffSaveTypes[ability] = classification.RequiredSave.Value;
                        break;

                    // v0.2.2: CrowdControl은 Debuff와 동일하게 처리
                    case AbilityTiming.CrowdControl:
                        situation.AvailableDebuffs.Add(ability);
                        // v0.2.18: CC도 세이브 타입 저장
                        if (classification.RequiredSave.HasValue)
                            situation.DebuffSaveTypes[ability] = classification.RequiredSave.Value;
                        Main.Verbose($"[Analyzer] {ability.Name}: CC ability added to debuffs");
                        break;

                    // v0.2.2: Channel 에너지는 힐 또는 공격에 추가
                    case AbilityTiming.Channel:
                        // 아군 대상이면 힐, 적 대상이면 공격
                        if (ability.Blueprint?.CanTargetFriends == true || ability.Blueprint?.CanTargetSelf == true)
                        {
                            situation.AvailableHeals.Add(ability);
                            Main.Verbose($"[Analyzer] {ability.Name}: Channel added to heals");
                        }
                        if (ability.Blueprint?.CanTargetEnemies == true)
                        {
                            situation.AvailableAttacks.Add(ability);
                            Main.Verbose($"[Analyzer] {ability.Name}: Channel added to attacks");
                        }
                        break;

                    // v0.2.2: Summon은 특수 능력으로 처리
                    case AbilityTiming.Summon:
                        situation.AvailableSpecialAbilities.Add(ability);
                        Main.Verbose($"[Analyzer] {ability.Name}: Summon added to special abilities");
                        break;

                    case AbilityTiming.DangerousAoE:
                    case AbilityTiming.Attack:
                        // 공격 능력
                        if (IsValidAttack(ability, situation.RangePreference))
                        {
                            situation.AvailableAttacks.Add(ability);
                        }
                        break;

                    case AbilityTiming.Utility:
                        // v0.2.2: Utility는 특수 능력으로 처리
                        situation.AvailableSpecialAbilities.Add(ability);
                        break;

                    default:
                        // 기타 공격성 능력
                        if (ability.Blueprint.CanTargetEnemies)
                        {
                            if (IsValidAttack(ability, situation.RangePreference))
                            {
                                situation.AvailableAttacks.Add(ability);
                            }
                        }
                        break;
                }
            }

            // 무기 공격 추가
            AddWeaponAttacks(situation, unit);

            // RangePreference 필터 적용
            FilterAbilitiesByRange(situation);

            // Best buff 선택
            situation.BestBuff = situation.AvailableBuffs.FirstOrDefault();

            // 분류 결과 로깅
            Main.Log($"[Analyzer] {unit.CharacterName} abilities: " +
                $"Buffs={situation.AvailableBuffs.Count}, " +
                $"Heals={situation.AvailableHeals.Count}, " +
                $"Debuffs={situation.AvailableDebuffs.Count}, " +
                $"Attacks={situation.AvailableAttacks.Count}");
        }

        private void CopyTurnState(Situation situation, TurnState turnState)
        {
            if (turnState == null) return;

            situation.HasPerformedFirstAction = turnState.HasPerformedFirstAction;
            situation.HasBuffedThisTurn = turnState.HasBuffedThisTurn;
            situation.HasAttackedThisTurn = turnState.HasAttackedThisTurn;
            situation.HasHealedThisTurn = turnState.HasHealedThisTurn;
            situation.HasMovedThisTurn = turnState.HasMovedThisTurn;
            situation.MoveCount = turnState.MoveCount;

            situation.AllowPostAttackMove = turnState.AllowPostAttackMove && !situation.HasHittableEnemies;
            situation.AllowChaseMove = turnState.AllowChaseMove && !situation.HasHittableEnemies && situation.HasMoveAction;
        }

        #endregion

        #region Helper Methods

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

        private bool CanMove(UnitEntityData unit)
        {
            try
            {
                return unit?.Descriptor?.State?.CanMove ?? false;
            }
            catch { return false; }
        }

        private bool CanAct(UnitEntityData unit)
        {
            try
            {
                return unit?.Descriptor?.State?.CanAct ?? false;
            }
            catch { return false; }
        }

        private bool HasRangedWeapon(UnitEntityData unit)
        {
            try
            {
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return false;
                return !weapon.Blueprint.IsMelee;
            }
            catch { return false; }
        }

        private bool HasMeleeWeapon(UnitEntityData unit)
        {
            try
            {
                var weapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return false;
                return weapon.Blueprint.IsMelee;
            }
            catch { return false; }
        }

        // v0.2.8: 적 감지 최대 거리 (미터) - 폴백용
        private const float MAX_ENEMY_DETECTION_DISTANCE = 30f;

        /// <summary>
        /// ★ v0.2.32: 전투 중인 적 목록 가져오기 (CombatDataCache 사용)
        /// 최적화: 전체 유닛 순회 → 캐시된 적 목록 필터링
        /// 6× 연산 감소 효과
        /// </summary>
        private List<UnitEntityData> GetEnemies(UnitEntityData unit)
        {
            var result = new List<UnitEntityData>();
            var addedUnits = new HashSet<UnitEntityData>();

            try
            {
                // ★ v0.2.32: CombatDataCache에서 적 목록 가져오기 (1회 계산)
                CombatDataCache.Instance.RefreshIfNeeded();

                // 1. 교전 중인 적 (EngagedBy) - 우선 추가
                if (unit.CombatState?.EngagedBy != null)
                {
                    foreach (var engaged in unit.CombatState.EngagedBy)
                    {
                        if (engaged != null && !engaged.Descriptor.State.IsDead && !addedUnits.Contains(engaged))
                        {
                            result.Add(engaged);
                            addedUnits.Add(engaged);
                        }
                    }
                }

                // 2. 이 유닛이 교전 중인 적 (EngagedUnits)
                if (unit.CombatState?.EngagedUnits != null)
                {
                    foreach (var engaged in unit.CombatState.EngagedUnits)
                    {
                        if (engaged != null && !engaged.Descriptor.State.IsDead && !addedUnits.Contains(engaged))
                        {
                            result.Add(engaged);
                            addedUnits.Add(engaged);
                        }
                    }
                }

                // ★ v0.2.32: 캐시된 적 목록에서 거리 필터링 (전체 순회 X)
                Vector3 unitPos = unit.Position;
                float maxDistSq = MAX_ENEMY_DETECTION_DISTANCE * MAX_ENEMY_DETECTION_DISTANCE;

                foreach (var enemy in CombatDataCache.Instance.AllEnemies)
                {
                    if (enemy == null || addedUnits.Contains(enemy)) continue;
                    if (enemy.Descriptor?.State?.IsDead == true) continue;

                    // sqrMagnitude 사용 (루트 연산 회피)
                    float distSq = (enemy.Position - unitPos).sqrMagnitude;
                    if (distSq <= maxDistSq)
                    {
                        result.Add(enemy);
                        addedUnits.Add(enemy);
                    }
                }

                Main.Verbose($"[Analyzer] GetEnemies (cached): Total={result.Count}");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] GetEnemies error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.32: CombatDataCache 사용으로 최적화
        /// 이전: Game.Instance.State.Units 전체 순회 (O(N))
        /// 개선: 캐시된 AllAllies 사용 (이미 필터링됨)
        /// </summary>
        private List<UnitEntityData> GetAllies(UnitEntityData unit)
        {
            var result = new List<UnitEntityData>();
            var addedIds = new HashSet<string>();

            try
            {
                // ★ v0.2.32: CombatDataCache 사용 - 프레임당 1회만 계산
                CombatDataCache.Instance.RefreshIfNeeded();
                var cachedAllies = CombatDataCache.Instance.AllAllies;

                if (cachedAllies == null || cachedAllies.Count == 0)
                    return result;

                foreach (var other in cachedAllies)
                {
                    if (other == null || other == unit) continue;
                    if (other.HPLeft <= 0) continue;

                    // 버프 대상 로깅 (디버그용)
                    Main.Verbose($"[Analyzer] Ally detected: {other.CharacterName} (PlayerFaction={other.IsPlayerFaction})");

                    // ★ v0.2.25: 마운트/라이더 상태 확인
                    bool isMounted = IsMountedUnit(other);
                    bool isMount = IsMountUnit(other);

                    if (isMounted)
                    {
                        Main.Verbose($"[Analyzer] {other.CharacterName} is MOUNTED (riding)");
                    }
                    if (isMount)
                    {
                        Main.Verbose($"[Analyzer] {other.CharacterName} is a MOUNT (being ridden)");
                        // 마운트의 라이더도 아군 목록에 추가
                        var rider = GetRiderOfMount(other);
                        if (rider != null && !addedIds.Contains(rider.UniqueId))
                        {
                            Main.Verbose($"[Analyzer] Adding rider {rider.CharacterName} from mount {other.CharacterName}");
                            result.Add(rider);
                            addedIds.Add(rider.UniqueId);
                        }
                    }

                    if (!addedIds.Contains(other.UniqueId))
                    {
                        result.Add(other);
                        addedIds.Add(other.UniqueId);
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] GetAllies error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v0.2.26: 유닛이 마운트를 타고 있는지 확인 (라이더)
        /// </summary>
        private bool IsMountedUnit(UnitEntityData unit)
        {
            try
            {
                var riderPart = unit.Get<Kingmaker.UnitLogic.Parts.UnitPartRider>();
                if (riderPart == null) return false;

                // SaddledUnit이 있으면 그것을 타고 있는 것
                bool isMounted = riderPart.SaddledUnit != null;

                // 디버그 로깅
                if (Main.TickCount % 100 == 0) // 너무 많은 로그 방지
                {
                    Main.Verbose($"[Mount] {unit.CharacterName}: RiderPart exists, SaddledUnit={riderPart.SaddledUnit?.CharacterName ?? "null"}");
                }

                return isMounted;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Mount] IsMountedUnit error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.26: 유닛이 마운트(탈것)인지 확인 - 누군가 타고 있는 경우
        /// </summary>
        private bool IsMountUnit(UnitEntityData unit)
        {
            try
            {
                // 모든 유닛을 검색해서 이 유닛을 타고 있는 유닛이 있는지 확인
                foreach (var other in Game.Instance.State.Units)
                {
                    if (other == null || other == unit) continue;
                    var riderPart = other.Get<Kingmaker.UnitLogic.Parts.UnitPartRider>();
                    if (riderPart != null && riderPart.SaddledUnit == unit)
                    {
                        if (Main.TickCount % 100 == 0)
                        {
                            Main.Verbose($"[Mount] {unit.CharacterName} is being ridden by {other.CharacterName}");
                        }
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Mount] IsMountUnit error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v0.2.26: 마운트의 라이더 가져오기
        /// </summary>
        private UnitEntityData GetRiderOfMount(UnitEntityData mount)
        {
            try
            {
                // 모든 유닛 검색해서 이 마운트를 타고 있는 유닛 찾기
                foreach (var unit in Game.Instance.State.Units)
                {
                    if (unit == null || unit == mount) continue;
                    var riderPart = unit.Get<Kingmaker.UnitLogic.Parts.UnitPartRider>();
                    if (riderPart != null && riderPart.SaddledUnit == mount)
                    {
                        return unit;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Mount] GetRiderOfMount error: {ex.Message}");
                return null;
            }
        }

        private List<AbilityData> GetAvailableAbilities(UnitEntityData unit)
        {
            var result = new List<AbilityData>();
            var addedAbilities = new HashSet<string>(); // 중복 방지용 (Blueprint GUID)

            try
            {
                if (unit == null) return result;

                int totalCount = 0;
                int availableCount = 0;
                int filteredCount = 0;

                // v0.2.13: 1. 기본 능력 (unit.Abilities.Enumerable)
                if (unit.Abilities?.Enumerable != null)
                {
                    foreach (var ability in unit.Abilities.Enumerable)
                    {
                        totalCount++;
                        var abilityData = ability?.Data;
                        if (abilityData?.Blueprint == null) continue;

                        string guid = abilityData.Blueprint.AssetGuid.ToString();
                        if (addedAbilities.Contains(guid)) continue;

                        if (!abilityData.IsAvailable)
                        {
                            filteredCount++;
                            if (filteredCount <= 10)
                            {
                                string reason = GetUnavailableReason(abilityData);
                                Main.Verbose($"[Analyzer] FILTERED: {abilityData.Name} - {reason}");
                            }
                            continue;
                        }

                        availableCount++;
                        result.Add(abilityData);
                        addedAbilities.Add(guid);
                    }
                }

                // v0.2.14: 2. Spellbook 주문 열거 (진단 로깅 강화)
                int spellbookCount = 0;
                int spellbookAvailable = 0;
                int spellbookFiltered = 0;

                try
                {
                    // v0.2.14: Spellbook 존재 여부 확인
                    var spellbooks = unit.Spellbooks?.ToList();
                    int sbListCount = spellbooks?.Count ?? 0;
                    Main.Verbose($"[Analyzer] {unit.CharacterName}: Found {sbListCount} spellbook(s)");

                    if (spellbooks != null && sbListCount > 0)
                    {
                        foreach (var spellbook in spellbooks)
                        {
                            if (spellbook == null)
                            {
                                Main.Verbose($"[Analyzer] Null spellbook encountered");
                                continue;
                            }

                            string sbName = spellbook.Blueprint?.Name ?? "Unknown";
                            int casterLevel = spellbook.CasterLevel;
                            int maxSpellLv = spellbook.MaxSpellLevel;
                            Main.Verbose($"[Analyzer] Spellbook '{sbName}': CasterLv={casterLevel}, MaxSpellLv={maxSpellLv}");

                            int sbTotal = 0;
                            int sbAvail = 0;

                            // 2a. 알려진 주문 (GetAllKnownSpells)
                            var knownSpells = spellbook.GetAllKnownSpells()?.ToList();
                            Main.Verbose($"[Analyzer] Spellbook '{sbName}': KnownSpells count = {knownSpells?.Count ?? 0}");

                            foreach (var spell in knownSpells ?? Enumerable.Empty<AbilityData>())
                            {
                                spellbookCount++;
                                sbTotal++;

                                if (spell?.Blueprint == null) continue;

                                string guid = spell.Blueprint.AssetGuid.ToString();
                                if (addedAbilities.Contains(guid)) continue;

                                if (!spell.IsAvailable)
                                {
                                    spellbookFiltered++;
                                    continue;
                                }

                                spellbookAvailable++;
                                sbAvail++;
                                result.Add(spell);
                                addedAbilities.Add(guid);
                            }

                            // 2b. 커스텀 주문 (GetAllCustomSpells)
                            foreach (var spell in spellbook.GetAllCustomSpells())
                            {
                                spellbookCount++;
                                sbTotal++;

                                if (spell?.Blueprint == null) continue;

                                string guid = spell.Blueprint.AssetGuid.ToString();
                                if (addedAbilities.Contains(guid)) continue;

                                if (!spell.IsAvailable)
                                {
                                    spellbookFiltered++;
                                    continue;
                                }

                                spellbookAvailable++;
                                sbAvail++;
                                result.Add(spell);
                                addedAbilities.Add(guid);
                            }

                            // 2c. 특수 주문 (Special spells - mythic 등)
                            for (int level = 0; level <= 10; level++)
                            {
                                try
                                {
                                    foreach (var spell in spellbook.GetSpecialSpells(level))
                                    {
                                        spellbookCount++;
                                        sbTotal++;

                                        if (spell?.Blueprint == null) continue;

                                        string guid = spell.Blueprint.AssetGuid.ToString();
                                        if (addedAbilities.Contains(guid)) continue;

                                        if (!spell.IsAvailable)
                                        {
                                            spellbookFiltered++;
                                            continue;
                                        }

                                        spellbookAvailable++;
                                        sbAvail++;
                                        result.Add(spell);
                                        addedAbilities.Add(guid);
                                    }
                                }
                                catch { /* 레벨 범위 초과 무시 */ }
                            }

                            if (sbTotal > 0)
                            {
                                Main.Verbose($"[Analyzer] Spellbook '{sbName}': Total={sbTotal}, Available={sbAvail}");
                            }
                        }
                    }
                }
                catch (Exception sbEx)
                {
                    Main.Verbose($"[Analyzer] Spellbook enumeration error: {sbEx.Message}");
                }

                Main.Verbose($"[Analyzer] {unit.CharacterName}: Abilities={totalCount}(avail={availableCount}), " +
                            $"Spells={spellbookCount}(avail={spellbookAvailable}), TotalResult={result.Count}");
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] GetAvailableAbilities error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// v0.2.12: 능력이 사용 불가능한 이유 진단
        /// </summary>
        private string GetUnavailableReason(AbilityData ability)
        {
            try
            {
                if (ability == null) return "null";

                // IsAvailable = IsAvailableInSpellbook && IsAvailableForCast && !TemporarilyDisabled
                if (ability.TemporarilyDisabled)
                    return "TemporarilyDisabled";

                // GetAvailableForCastCount가 0이면 리소스/슬롯 부족
                var castCount = ability.GetAvailableForCastCount();
                if (castCount == 0)
                    return "NoCastsLeft (slots/resources)";

                // Spellbook 관련
                if (ability.Spellbook != null)
                {
                    var spellCount = ability.Spellbook.GetAvailableForCastSpellCount(ability);
                    if (spellCount == 0)
                        return $"SpellSlotEmpty (Lv{ability.SpellLevel})";
                }

                return "Unknown";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private float GetAbilityRange(AbilityData ability)
        {
            try
            {
                if (ability?.Blueprint == null) return 0f;

                // 원거리는 블루프린트에서 가져옴
                var range = ability.Blueprint.Range;

                // Range enum to meters (approximate)
                switch (range)
                {
                    case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch:
                        return 2f;
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
                        // 무기 사거리 - 캐스터의 무기에서 가져옴
                        return GetWeaponRange(ability);
                    default:
                        return 10f;
                }
            }
            catch { return 10f; }
        }

        private float GetWeaponRange(AbilityData ability)
        {
            try
            {
                var weapon = ability?.Caster?.Unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null)
                {
                    return weapon.Blueprint?.IsMelee == true ? 2f : 15f;
                }
                return 2f;
            }
            catch { return 2f; }
        }

        private bool IsAbilityMelee(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;

            var range = ability.Blueprint.Range;
            return range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch ||
                   range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal ||
                   (range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon && IsWeaponMelee(ability));
        }

        private bool IsWeaponMelee(AbilityData ability)
        {
            try
            {
                var weapon = ability?.Caster?.Unit?.Body?.PrimaryHand?.MaybeWeapon;
                return weapon?.Blueprint?.IsMelee == true;
            }
            catch { return true; }
        }

        private bool CanTargetEnemy(AbilityData ability, UnitEntityData caster, UnitEntityData target)
        {
            try
            {
                if (ability?.Blueprint == null) return false;

                bool isWeaponAbility = ability.Blueprint.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon;
                if (!ability.Blueprint.CanTargetEnemies && !isWeaponAbility) return false;

                // 기본 타겟 가능성 체크
                var targetWrapper = new TargetWrapper(target);
                return ability.CanTarget(targetWrapper);
            }
            catch { return false; }
        }

        private bool IsValidAttack(AbilityData ability, RangePreference preference)
        {
            if (ability == null) return false;

            // 무기 기반 공격
            bool isWeaponAbility = ability.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon;
            if (isWeaponAbility)
            {
                bool isMelee = IsAbilityMelee(ability);
                if (preference == RangePreference.Ranged && isMelee)
                    return false;
                if (preference == RangePreference.Melee && !isMelee)
                    return false;
                return true;
            }

            // 스펠/능력
            return ability.Blueprint?.CanTargetEnemies == true;
        }

        private void AddWeaponAttacks(Situation situation, UnitEntityData unit)
        {
            try
            {
                // v0.2.16: 기본 공격 추가 - 무기 범위 능력 찾기 (IsAvailable 체크 포함)
                var primaryWeapon = unit?.Body?.PrimaryHand?.MaybeWeapon;
                if (primaryWeapon != null)
                {
                    var attackAbility = unit.Abilities.Enumerable
                        .FirstOrDefault(a => a.Data?.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon
                                          && a.Data.IsAvailable);

                    if (attackAbility != null && !situation.AvailableAttacks.Contains(attackAbility.Data))
                    {
                        situation.AvailableAttacks.Add(attackAbility.Data);
                        bool isMelee = attackAbility.Data.Caster?.Unit?.Body?.PrimaryHand?.MaybeWeapon?.Blueprint?.IsMelee ?? true;
                        Main.Verbose($"[Analyzer] AddWeaponAttacks: Added {attackAbility.Data.Name} (Melee={isMelee})");
                    }
                    else if (attackAbility == null)
                    {
                        // v0.2.16: 진단 - 왜 무기 공격이 없는지
                        var anyWeapon = unit.Abilities.Enumerable
                            .FirstOrDefault(a => a.Data?.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon);
                        if (anyWeapon != null)
                        {
                            Main.Verbose($"[Analyzer] AddWeaponAttacks: {anyWeapon.Data.Name} exists but IsAvailable=false");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Verbose($"[Analyzer] AddWeaponAttacks error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v0.2.28: 범위 선호도에 따른 공격 필터링 개선
        /// 이전 문제: 돌격/악 처단 같은 비무기 능력이 isWeaponAbility=false라서 필터링됨
        /// 수정: 무기 기반 공격만 범위 필터링, 비무기 능력은 항상 허용
        /// </summary>
        private void FilterAbilitiesByRange(Situation situation)
        {
            if (situation.RangePreference == RangePreference.Mixed)
                return; // 필터링 없음

            int beforeCount = situation.AvailableAttacks.Count;
            var filtered = new List<AbilityData>();
            foreach (var attack in situation.AvailableAttacks)
            {
                bool isWeaponAbility = attack.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon;
                bool isMelee = IsAbilityMelee(attack);

                if (situation.RangePreference == RangePreference.Melee)
                {
                    // ★ v0.2.28: 근접 선호 - 원거리 무기 공격만 필터링
                    // 비무기 능력(돌격, 악 처단 등)은 모두 허용
                    if (!isWeaponAbility || isMelee)
                    {
                        filtered.Add(attack);
                    }
                    else
                    {
                        Main.Verbose($"[Analyzer] FilterByRange: Dropped {attack.Name} (Pref=Melee, ranged weapon attack)");
                    }
                }
                else if (situation.RangePreference == RangePreference.Ranged)
                {
                    // ★ v0.2.28: 원거리 선호 - 근접 무기 공격만 필터링
                    // 비무기 능력은 모두 허용
                    if (!isWeaponAbility || !isMelee)
                    {
                        filtered.Add(attack);
                    }
                    else
                    {
                        Main.Verbose($"[Analyzer] FilterByRange: Dropped {attack.Name} (Pref=Ranged, melee weapon attack)");
                    }
                }
            }

            // v0.2.16: 필터링 결과가 비어있으면 원본 유지 (근접 무기밖에 없는 Ranged 캐릭터 보호)
            if (filtered.Count > 0)
            {
                situation.AvailableAttacks = filtered;
            }
            else if (beforeCount > 0)
            {
                Main.Verbose($"[Analyzer] FilterByRange: All {beforeCount} attacks filtered out by {situation.RangePreference}, keeping original");
            }
        }

        /// <summary>
        /// v0.2.17: 타겟 처치 가능 여부 추정
        /// HP 20% 이하면 처치 가능으로 판정 (정확한 데미지 계산은 비용이 높으므로 간이 추정)
        /// </summary>
        private bool EstimateCanKill(UnitEntityData target)
        {
            try
            {
                float hp = GetHPPercent(target);
                return hp <= 20f;
            }
            catch { return false; }
        }

        private AbilityData SelectPrimaryAttack(Situation situation, UnitEntityData unit)
        {
            var attacks = situation.AvailableAttacks;
            if (attacks == null || attacks.Count == 0) return null;

            // 무기 공격 우선
            return attacks
                .Where(a => a.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Weapon)
                .OrderBy(a => IsAbilityMelee(a) ? 0 : 1)  // 근접 우선 (안전)
                .FirstOrDefault()
                ?? attacks.FirstOrDefault();
        }

        #endregion
    }
}
