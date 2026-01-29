using System;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_Pathfinder.Core;
using CompanionAI_Pathfinder.Analysis;
using CompanionAI_Pathfinder.Planning.Plans;
using CompanionAI_Pathfinder.Settings;

namespace CompanionAI_Pathfinder.Planning
{
    /// <summary>
    /// 턴 계획 관리자
    /// Role에 따라 적절한 Plan을 선택하여 턴 계획 생성
    /// </summary>
    public class TurnPlanner
    {
        #region Singleton

        private static TurnPlanner _instance;
        public static TurnPlanner Instance => _instance ?? (_instance = new TurnPlanner());

        private TurnPlanner()
        {
            _dpsPlan = new DPSPlan();
            _tankPlan = new TankPlan();
            _supportPlan = new SupportPlan();
        }

        #endregion

        #region Plans

        private readonly DPSPlan _dpsPlan;
        private readonly TankPlan _tankPlan;
        private readonly SupportPlan _supportPlan;

        #endregion

        /// <summary>
        /// 유닛의 턴 계획 생성
        /// </summary>
        public TurnPlan CreatePlan(UnitEntityData unit, Situation situation, TurnState turnState)
        {
            if (unit == null)
            {
                Main.Error("[TurnPlanner] CreatePlan called with null unit");
                return CreateEndTurnPlan("Null unit");
            }

            if (situation == null)
            {
                Main.Error("[TurnPlanner] CreatePlan called with null situation");
                return CreateEndTurnPlan("Null situation");
            }

            try
            {
                // Role 기반 Plan 선택
                var plan = SelectPlan(unit);
                if (plan == null)
                {
                    Main.Log($"[TurnPlanner] No plan found for {unit.CharacterName}, using DPS default");
                    plan = _dpsPlan;
                }

                // Plan 생성
                var turnPlan = plan.CreatePlan(situation, turnState);

                if (turnPlan == null)
                {
                    Main.Log($"[TurnPlanner] Plan returned null for {unit.CharacterName}");
                    return CreateEndTurnPlan("Plan returned null");
                }

                Main.Log($"[TurnPlanner] {unit.CharacterName}: {turnPlan.RemainingActionCount} actions planned - {turnPlan.Reasoning}");
                return turnPlan;
            }
            catch (Exception ex)
            {
                Main.Error($"[TurnPlanner] Error creating plan for {unit.CharacterName}: {ex.Message}");
                Main.Error($"[TurnPlanner] Stack: {ex.StackTrace}");
                return CreateEndTurnPlan($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Role에 따라 적절한 Plan 선택
        /// </summary>
        private BasePlan SelectPlan(UnitEntityData unit)
        {
            // 설정에서 Role 가져오기
            var settings = ModSettings.Instance;
            var charSettings = settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);
            var role = charSettings?.Role ?? AIRole.DPS;

            Main.Verbose($"[TurnPlanner] {unit.CharacterName} role: {role}");

            switch (role)
            {
                case AIRole.Tank:
                    return _tankPlan;

                case AIRole.Support:
                    return _supportPlan;

                case AIRole.DPS:
                default:
                    return _dpsPlan;
            }
        }

        /// <summary>
        /// 턴 종료 계획 생성 (폴백용)
        /// </summary>
        private TurnPlan CreateEndTurnPlan(string reason)
        {
            var actions = new System.Collections.Generic.List<PlannedAction>
            {
                PlannedAction.EndTurn(reason)
            };
            return new TurnPlan(actions, TurnPriority.EndTurn, reason);
        }

        /// <summary>
        /// 상황 분석 및 계획 생성 (통합 메서드)
        /// </summary>
        public TurnPlan AnalyzeAndPlan(UnitEntityData unit, TurnState turnState)
        {
            if (unit == null)
                return CreateEndTurnPlan("Null unit");

            try
            {
                // 1. 상황 분석
                var analyzer = new SituationAnalyzer();
                var situation = analyzer.Analyze(unit, turnState);

                if (situation == null)
                {
                    Main.Log($"[TurnPlanner] Situation analysis failed for {unit.CharacterName}");
                    return CreateEndTurnPlan("Situation analysis failed");
                }

                // 2. 계획 생성
                return CreatePlan(unit, situation, turnState);
            }
            catch (Exception ex)
            {
                Main.Error($"[TurnPlanner] AnalyzeAndPlan error: {ex.Message}");
                return CreateEndTurnPlan($"Error: {ex.Message}");
            }
        }
    }
}
