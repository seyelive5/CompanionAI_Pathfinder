using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using Kingmaker.EntitySystem.Stats;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.Scoring;  // ★ v0.2.22

namespace CompanionAI_Pathfinder.Analysis
{
    /// <summary>
    /// 현재 전투 상황 스냅샷
    /// SituationAnalyzer가 생성하고, TurnPlanner가 소비
    /// </summary>
    public class Situation
    {
        #region Unit State

        /// <summary>현재 유닛</summary>
        public UnitEntityData Unit { get; set; }

        /// <summary>HP 퍼센트</summary>
        public float HPPercent { get; set; }

        /// <summary>이동 가능 여부</summary>
        public bool CanMove { get; set; }

        /// <summary>행동 가능 여부</summary>
        public bool CanAct { get; set; }

        #endregion

        #region Action Resources (Pathfinder Action System)

        /// <summary>Standard Action 사용 가능 여부</summary>
        public bool HasStandardAction { get; set; } = true;

        /// <summary>Move Action 사용 가능 여부</summary>
        public bool HasMoveAction { get; set; } = true;

        /// <summary>Swift Action 사용 가능 여부</summary>
        public bool HasSwiftAction { get; set; } = true;

        /// <summary>Full-Round Action 사용 가능 여부</summary>
        public bool HasFullRoundAction => HasStandardAction && HasMoveAction;

        #endregion

        #region Settings

        /// <summary>유닛 설정</summary>
        public CharacterSettings CharacterSettings { get; set; }

        /// <summary>거리 선호도</summary>
        public RangePreference RangePreference { get; set; }

        /// <summary>안전 거리 (원거리 캐릭터용)</summary>
        public float MinSafeDistance { get; set; } = 10f;

        #endregion

        #region Weapon

        /// <summary>원거리 무기 보유</summary>
        public bool HasRangedWeapon { get; set; }

        /// <summary>근접 무기 보유</summary>
        public bool HasMeleeWeapon { get; set; }

        /// <summary>★ v0.2.25: 주 무기 공격 범위 (미터)</summary>
        public float WeaponRange { get; set; } = 2f;

        #endregion

        #region Battlefield

        /// <summary>모든 적</summary>
        public List<UnitEntityData> Enemies { get; set; } = new List<UnitEntityData>();

        /// <summary>모든 아군</summary>
        public List<UnitEntityData> Allies { get; set; } = new List<UnitEntityData>();

        /// <summary>가장 가까운 적과의 거리</summary>
        public float NearestEnemyDistance { get; set; } = float.MaxValue;

        /// <summary>가장 가까운 적</summary>
        public UnitEntityData NearestEnemy { get; set; }

        /// <summary>가장 부상당한 아군</summary>
        public UnitEntityData MostWoundedAlly { get; set; }

        #endregion

        #region Target Analysis

        /// <summary>현재 위치에서 공격 가능한 적</summary>
        public List<UnitEntityData> HittableEnemies { get; set; } = new List<UnitEntityData>();

        /// <summary>최적 타겟</summary>
        public UnitEntityData BestTarget { get; set; }

        /// <summary>최적 타겟 처치 가능 여부</summary>
        public bool CanKillBestTarget { get; set; }

        #endregion

        #region Threat Analysis

        /// <summary>타겟팅 당하는 아군 수 (자신 제외)</summary>
        public int AlliesUnderThreat { get; set; }

        /// <summary>아군(자신 제외)을 타겟팅 중인 적 수</summary>
        public int EnemiesTargetingAllies { get; set; }

        #endregion

        #region v0.2.18: Flanking & Engagement

        /// <summary>스닉 어택 보유 여부</summary>
        public bool HasSneakAttack { get; set; }

        /// <summary>플랭킹된 적 목록</summary>
        public List<UnitEntityData> FlankedEnemies { get; set; } = new List<UnitEntityData>();

        /// <summary>이 유닛이 플랭킹 당하고 있는지</summary>
        public bool IsFlanked { get; set; }

        /// <summary>교전 중인지</summary>
        public bool IsEngaged { get; set; }

        /// <summary>교전 중인 적 수</summary>
        public int EngagedByCount { get; set; }

        #endregion

        #region Position Analysis

        /// <summary>위험 상태 (원거리인데 적이 가까움)</summary>
        public bool IsInDanger { get; set; }

        /// <summary>더 나은 위치 존재</summary>
        public bool BetterPositionAvailable { get; set; }

        /// <summary>이동 필요 (공격 불가)</summary>
        public bool NeedsReposition { get; set; }

        #endregion

        #region Available Abilities (분류됨)

        /// <summary>사용 가능한 버프</summary>
        public List<AbilityData> AvailableBuffs { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 공격</summary>
        public List<AbilityData> AvailableAttacks { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 힐</summary>
        public List<AbilityData> AvailableHeals { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 디버프</summary>
        public List<AbilityData> AvailableDebuffs { get; set; } = new List<AbilityData>();

        /// <summary>v0.2.18: 디버프별 요구 세이브 타입</summary>
        public Dictionary<AbilityData, SavingThrowType> DebuffSaveTypes { get; set; } = new Dictionary<AbilityData, SavingThrowType>();

        /// <summary>사용 가능한 특수 능력</summary>
        public List<AbilityData> AvailableSpecialAbilities { get; set; } = new List<AbilityData>();

        /// <summary>위치 타겟 버프 (AOE 버프 등)</summary>
        public List<AbilityData> AvailablePositionalBuffs { get; set; } = new List<AbilityData>();

        /// <summary>주 공격 능력</summary>
        public AbilityData PrimaryAttack { get; set; }

        /// <summary>최적 버프</summary>
        public AbilityData BestBuff { get; set; }

        #endregion

        #region Combat Phase (★ v0.2.22)

        /// <summary>Current combat phase (set by CombatPhaseDetector)</summary>
        public CombatPhase CombatPhase { get; set; } = CombatPhase.Midgame;

        #endregion

        #region Turn State

        /// <summary>이번 턴 첫 행동 완료 여부</summary>
        public bool HasPerformedFirstAction { get; set; }

        /// <summary>이번 턴 버프 사용 여부</summary>
        public bool HasBuffedThisTurn { get; set; }

        /// <summary>이번 턴 공격 완료 여부</summary>
        public bool HasAttackedThisTurn { get; set; }

        /// <summary>이번 턴 힐 사용 여부</summary>
        public bool HasHealedThisTurn { get; set; }

        /// <summary>이번 턴 이동 완료 여부 (중복 이동 방지)</summary>
        public bool HasMovedThisTurn { get; set; }

        /// <summary>이번 턴 이동 횟수</summary>
        public int MoveCount { get; set; }

        /// <summary>공격 후 추가 이동 허용</summary>
        public bool AllowPostAttackMove { get; set; }

        /// <summary>추격 이동 허용 (이동했지만 공격 못함)</summary>
        public bool AllowChaseMove { get; set; }

        #endregion

        #region Computed Properties

        /// <summary>공격 가능한 적이 있는가?</summary>
        public bool HasHittableEnemies => HittableEnemies?.Count > 0;

        /// <summary>살아있는 적이 있는가?</summary>
        public bool HasLivingEnemies => Enemies?.Count > 0;

        /// <summary>HP가 위험한가? (30% 미만)</summary>
        public bool IsHPCritical => HPPercent < 30f;

        /// <summary>HP가 낮은가? (50% 미만)</summary>
        public bool IsHPLow => HPPercent < 50f;

        /// <summary>
        /// 원거리 선호인가?
        /// </summary>
        public bool PrefersRanged => RangePreference == RangePreference.Ranged;

        /// <summary>
        /// 근접 선호인가?
        /// </summary>
        public bool PrefersMelee => RangePreference == RangePreference.Melee;

        #endregion

        #region Tactical Info (★ v0.2.30)

        /// <summary>전장 영향력 맵</summary>
        public BattlefieldInfluenceMap InfluenceMap { get; set; }

        #endregion

        public override string ToString()
        {
            return $"[Situation] {Unit?.CharacterName}: HP={HPPercent:F0}%, Phase={CombatPhase}, " +
                   $"Std={HasStandardAction}, Move={HasMoveAction}, Swift={HasSwiftAction}, " +
                   $"Enemies={Enemies?.Count ?? 0}, Hittable={HittableEnemies?.Count ?? 0}, " +
                   $"InDanger={IsInDanger}, WpnRange={WeaponRange:F0}m";
        }
    }
}
