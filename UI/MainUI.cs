using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_Pathfinder.Settings;
using CompanionAI_Pathfinder.GameInterface;

namespace CompanionAI_Pathfinder.UI
{
    /// <summary>
    /// 메인 UI (Unity Mod Manager)
    /// </summary>
    public static class MainUI
    {
        private static string _selectedCharacterId = "";
        private static CharacterSettings _editingSettings = null;
        private static Vector2 _scrollPosition = Vector2.zero;

        private static GUIStyle _headerStyle;
        private static GUIStyle _boldLabelStyle;
        private static GUIStyle _boxStyle;
        private static GUIStyle _descriptionStyle;

        private const float CHECKBOX_SIZE = 40f;
        private const float BUTTON_HEIGHT = 40f;
        private const float RANGE_BUTTON_WIDTH = 100f;
        private const float ROLE_BUTTON_WIDTH = 100f;
        private const float CHAR_NAME_WIDTH = 260f;  // v0.2.20: 펫 표시를 위해 확장
        private const float RANGE_LABEL_WIDTH = 80f;
        private const float ROLE_LABEL_WIDTH = 80f;
        private const float LANG_BUTTON_WIDTH = 120f;

        private static string L(string key) => Localization.Get(key);

        public static void OnGUI()
        {
            if (ModSettings.Instance == null) return;

            Localization.CurrentLanguage = ModSettings.Instance.UILanguage;
            InitStyles();

            try
            {
                _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(600));
                GUILayout.BeginVertical("box");

                DrawHeader();
                DrawDivider();
                DrawStatusSection();
                DrawDivider();
                DrawGlobalSettings();
                DrawDivider();
                DrawUnitControlSettings();
                DrawDivider();
                DrawCombatModeSettings();
                DrawDivider();
                DrawCharacterSelection();

                GUILayout.EndVertical();
                GUILayout.EndScrollView();
            }
            catch (Exception ex)
            {
                GUILayout.Label($"<color=#FF0000>UI Error: {ex.Message}</color>");
            }
        }

        private static void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, richText = true };
            }
            if (_boldLabelStyle == null)
            {
                _boldLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, richText = true };
            }
            if (_descriptionStyle == null)
            {
                _descriptionStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, wordWrap = true };
            }
            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 10, 10) };
            }
        }

        private static void DrawDivider() => GUILayout.Space(10);

        private static void DrawHeader()
        {
            GUILayout.Label($"<color=#00FFFF><b>{L("Title")}</b></color>", _headerStyle);
            GUILayout.Label($"<color=#D8D8D8>{L("Subtitle")}</color>", _descriptionStyle);
        }

        /// <summary>
        /// 상태 정보 표시
        /// </summary>
        private static void DrawStatusSection()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("Status")}</b>", _boldLabelStyle);
            GUILayout.Space(5);

            // 모드 활성화 상태
            string enabledColor = Main.Enabled ? "#00FF00" : "#FF0000";
            string enabledText = Main.Enabled ? L("Enabled") : L("Disabled");
            GUILayout.Label($"{L("Status")}: <color={enabledColor}><b>{enabledText}</b></color>");

            // 패치 상태
            string patchColor = Main.TickBrainPatched ? "#00FF00" : "#FF0000";
            GUILayout.Label($"{L("TickBrainPatch")}: <color={patchColor}><b>{Main.PatchStatus}</b></color>");

            // 전투 모드
            string combatModeText = GetCurrentCombatModeString();
            GUILayout.Label($"{L("CombatMode")}: <color=#FFFF00><b>{combatModeText}</b></color>");

            // 통계
            GUILayout.Label($"{L("AITickCount")}: <color=#00BFFF>{Main.TickCount}</color>");
            GUILayout.Label($"{L("ProcessedUnits")}: <color=#00BFFF>{Main.ProcessedUnits}</color>");

            // ★ v0.2.38: Pre-Buff 버튼
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"<color=#FFD700><b>⚔ {L("PreBuffParty")}</b></color>", GUILayout.Width(200), GUILayout.Height(35)))
            {
                PreBuffController.TriggerPartyBuff();
            }
            GUILayout.Label($"<color=#888888><size=11>{L("PreBuffPartyDesc")}</size></color>", _descriptionStyle);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private static string GetCurrentCombatModeString()
        {
            try
            {
                var state = Abstraction.PathfinderCombatStateAdapter.Instance;
                return state.CurrentMode switch
                {
                    Abstraction.CombatMode.TurnBased => L("CombatMode_TurnBased"),
                    Abstraction.CombatMode.RealTime => L("CombatMode_RealTime"),
                    _ => L("CombatMode_None")
                };
            }
            catch
            {
                return "Unknown";
            }
        }

        private static void DrawGlobalSettings()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("GlobalSettings")}</b>", _boldLabelStyle);
            GUILayout.Space(5);

            // Language
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{L("Language")}:</b>", _boldLabelStyle, GUILayout.Width(80));

            foreach (Language lang in Enum.GetValues(typeof(Language)))
            {
                bool isSelected = ModSettings.Instance.UILanguage == lang;
                string langName = lang == Language.English ? "English" : "한국어";
                string buttonText = isSelected ? $"<color=#00FF00><b>{langName}</b></color>" : $"<color=#D8D8D8>{langName}</color>";

                if (GUILayout.Button(buttonText, GUI.skin.button, GUILayout.Width(LANG_BUTTON_WIDTH), GUILayout.Height(30)))
                {
                    ModSettings.Instance.UILanguage = lang;
                    Localization.CurrentLanguage = lang;
                    ModSettings.Save();
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5);

            // Debug Logging
            ModSettings.Instance.EnableDebugLogging = DrawCheckbox(ModSettings.Instance.EnableDebugLogging, L("EnableDebugLogging"));

            GUILayout.EndVertical();
        }

        /// <summary>
        /// 유닛 제어 설정 (주인공/동료 제어 여부)
        /// </summary>
        private static void DrawUnitControlSettings()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("UnitControlSettings")}</b>", _boldLabelStyle);
            GUILayout.Space(5);

            // 주인공 제어
            bool prevControlMain = ModSettings.Instance.ControlMainCharacter;
            ModSettings.Instance.ControlMainCharacter = DrawCheckbox(ModSettings.Instance.ControlMainCharacter, L("ControlMainCharacter"));
            GUILayout.Label($"<color=#888888><size=11>{L("ControlMainCharacterDesc")}</size></color>", _descriptionStyle);
            GUILayout.Space(5);

            // 동료 제어
            bool prevControlCompanions = ModSettings.Instance.ControlCompanions;
            ModSettings.Instance.ControlCompanions = DrawCheckbox(ModSettings.Instance.ControlCompanions, L("ControlCompanions"));
            GUILayout.Label($"<color=#888888><size=11>{L("ControlCompanionsDesc")}</size></color>", _descriptionStyle);

            // 변경시 저장
            if (prevControlMain != ModSettings.Instance.ControlMainCharacter ||
                prevControlCompanions != ModSettings.Instance.ControlCompanions)
            {
                ModSettings.Save();
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// 전투 모드 설정
        /// </summary>
        private static void DrawCombatModeSettings()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("CombatModeSettings")}</b>", _boldLabelStyle);
            GUILayout.Space(5);

            bool prevTurnBased = ModSettings.Instance.EnableInTurnBased;
            bool prevRealTime = ModSettings.Instance.EnableInRealTime;
            float prevInterval = ModSettings.Instance.RealTimeDecisionInterval;

            // 턴제 모드
            ModSettings.Instance.EnableInTurnBased = DrawCheckbox(ModSettings.Instance.EnableInTurnBased, L("EnableInTurnBased"));
            GUILayout.Space(3);

            // 실시간 모드
            ModSettings.Instance.EnableInRealTime = DrawCheckbox(ModSettings.Instance.EnableInRealTime, L("EnableInRealTime"));
            GUILayout.Space(3);

            // 실시간 결정 간격
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{L("RealTimeDecisionInterval")}:", GUILayout.Width(200));
            ModSettings.Instance.RealTimeDecisionInterval = GUILayout.HorizontalSlider(
                ModSettings.Instance.RealTimeDecisionInterval, 0.2f, 2.0f, GUILayout.Width(150));
            GUILayout.Label($"<color=#00FF00>{ModSettings.Instance.RealTimeDecisionInterval:F1}s</color>", GUILayout.Width(50));
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color=#888888><size=11>{L("RealTimeDecisionIntervalDesc")}</size></color>", _descriptionStyle);

            // 변경시 저장
            if (prevTurnBased != ModSettings.Instance.EnableInTurnBased ||
                prevRealTime != ModSettings.Instance.EnableInRealTime ||
                Math.Abs(prevInterval - ModSettings.Instance.RealTimeDecisionInterval) > 0.01f)
            {
                ModSettings.Save();
            }

            GUILayout.EndVertical();
        }

        private static bool DrawCheckbox(bool value, string label)
        {
            GUILayout.BeginHorizontal();
            string checkIcon = value ? "<size=18><b><color=green>☑</color></b></size>" : "<size=18><b>☐</b></size>";

            if (GUILayout.Button(checkIcon, GUI.skin.box, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
                value = !value;

            GUILayout.Space(5);
            if (GUILayout.Button($"<size=13>{label}</size>", GUI.skin.label, GUILayout.Height(CHECKBOX_SIZE)))
                value = !value;

            GUILayout.EndHorizontal();
            return value;
        }

        private static void DrawCharacterSelection()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("PartyMembers")}</b>", _boldLabelStyle);
            GUILayout.Space(5);

            var characters = GetPartyMembers();
            if (characters.Count == 0)
            {
                GUILayout.Label($"<color=#D8D8D8><i>{L("NoCharacters")}</i></color>", _descriptionStyle);
                GUILayout.EndVertical();
                return;
            }

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{L("AI")}</b>", GUILayout.Width(50));
            GUILayout.Label($"<b>{L("Character")}</b>", GUILayout.Width(CHAR_NAME_WIDTH));
            GUILayout.Label($"<b>{L("Role")}</b>", GUILayout.Width(ROLE_LABEL_WIDTH));
            GUILayout.Label($"<b>{L("Range")}</b>", GUILayout.Width(RANGE_LABEL_WIDTH));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5);

            foreach (var character in characters)
                DrawCharacterRow(character);

            GUILayout.EndVertical();
        }

        private static void DrawCharacterRow(CharacterInfo character)
        {
            var settings = ModSettings.Instance.GetOrCreateSettings(character.Id, character.Name);

            GUILayout.BeginHorizontal("box");

            // AI Toggle
            string checkIcon = settings.EnableCustomAI ? "<size=16><b><color=green>☑</color></b></size>" : "<size=16><b>☐</b></size>";
            if (GUILayout.Button(checkIcon, GUI.skin.box, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
            {
                settings.EnableCustomAI = !settings.EnableCustomAI;
                ModSettings.Save();
            }

            // Character name (주인공/펫 표시)
            bool isSelected = _selectedCharacterId == character.Id;
            string marker = character.IsMainCharacter ? " <color=#FFD700>★</color>" :
                           character.IsPet ? $" <color=#90EE90>(Pet: {character.OwnerName})</color>" : "";
            string buttonText = isSelected ? $"<b>▼ {character.Name}{marker}</b>" : $"▶ {character.Name}{marker}";
            if (GUILayout.Button(buttonText, GUI.skin.button, GUILayout.Width(CHAR_NAME_WIDTH), GUILayout.Height(CHECKBOX_SIZE)))
            {
                if (isSelected) { _selectedCharacterId = ""; _editingSettings = null; }
                else { _selectedCharacterId = character.Id; _editingSettings = settings; }
            }

            // v0.2.3: Role
            string roleColor = GetRoleColor(settings.Role);
            GUILayout.Label($"<color={roleColor}><b>{Localization.GetRoleName(settings.Role)}</b></color>", GUILayout.Width(ROLE_LABEL_WIDTH), GUILayout.Height(CHECKBOX_SIZE));

            // Range
            string rangeColor = GetRangeColor(settings.RangePreference);
            GUILayout.Label($"<color={rangeColor}><b>{Localization.GetRangeName(settings.RangePreference)}</b></color>", GUILayout.Width(RANGE_LABEL_WIDTH), GUILayout.Height(CHECKBOX_SIZE));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (isSelected && _editingSettings != null)
            {
                GUILayout.BeginVertical("box");
                DrawCharacterAISettings();
                GUILayout.EndVertical();
            }
        }

        private static string GetRangeColor(RangePreference range) => range switch
        {
            RangePreference.Mixed => "#98FB98",   // Light Green
            RangePreference.Melee => "#FF6347",   // Red
            RangePreference.Ranged => "#32CD32",  // Green
            _ => "#FFFFFF"
        };

        // v0.2.3: AI Role color
        private static string GetRoleColor(AIRole role) => role switch
        {
            AIRole.DPS => "#FF4500",      // Orange Red
            AIRole.Tank => "#4169E1",     // Royal Blue
            AIRole.Support => "#32CD32",  // Lime Green
            _ => "#FFFFFF"
        };

        private static void DrawCharacterAISettings()
        {
            if (_editingSettings == null) return;

            GUILayout.Space(5);
            DrawRoleSelection();
            GUILayout.Space(5);
            DrawRangePreferenceSelection();
            GUILayout.Space(5);
        }

        // v0.2.3: Role selection UI
        private static void DrawRoleSelection()
        {
            GUILayout.Label($"<b>{L("RolePreference")}</b>", _boldLabelStyle);
            GUILayout.Label($"<color=#D8D8D8><i>{L("RolePreferenceDesc")}</i></color>", _descriptionStyle);
            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            foreach (AIRole role in Enum.GetValues(typeof(AIRole)))
            {
                string roleColor = GetRoleColor(role);
                bool isSelected = _editingSettings.Role == role;
                string roleName = Localization.GetRoleName(role);
                string btnText = isSelected ? $"<color={roleColor}><b>{roleName}</b></color>" : $"<color=#D8D8D8>{roleName}</color>";

                if (GUILayout.Toggle(isSelected, btnText, GUI.skin.button, GUILayout.Width(ROLE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)) && !isSelected)
                {
                    _editingSettings.Role = role;
                    ModSettings.Save();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
            GUILayout.Label($"<color=#D8D8D8><i>{Localization.GetRoleDescription(_editingSettings.Role)}</i></color>", _descriptionStyle);
        }

        private static void DrawRangePreferenceSelection()
        {
            GUILayout.Label($"<b>{L("RangePreference")}</b>", _boldLabelStyle);
            GUILayout.Label($"<color=#D8D8D8><i>{L("RangePreferenceDesc")}</i></color>", _descriptionStyle);
            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            foreach (RangePreference range in Enum.GetValues(typeof(RangePreference)))
            {
                string rangeColor = GetRangeColor(range);
                bool isSelected = _editingSettings.RangePreference == range;
                string rangeName = Localization.GetRangeName(range);
                string btnText = isSelected ? $"<color={rangeColor}><b>{rangeName}</b></color>" : $"<color=#D8D8D8>{rangeName}</color>";

                if (GUILayout.Toggle(isSelected, btnText, GUI.skin.button, GUILayout.Width(RANGE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)) && !isSelected)
                {
                    _editingSettings.RangePreference = range;
                    ModSettings.Save();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
            GUILayout.Label($"<color=#D8D8D8><i>{Localization.GetRangeDescription(_editingSettings.RangePreference)}</i></color>", _descriptionStyle);
        }

        private static List<CharacterInfo> GetPartyMembers()
        {
            try
            {
                if (Game.Instance?.Player == null) return new List<CharacterInfo>();

                var partyMembers = Game.Instance.Player.PartyCharacters;
                if (partyMembers == null || partyMembers.Count == 0) return new List<CharacterInfo>();

                var result = new List<CharacterInfo>();
                var addedIds = new HashSet<string>();

                // 1. 파티 멤버 추가
                foreach (var unitRef in partyMembers)
                {
                    var unit = unitRef.Value;
                    if (unit == null) continue;

                    string unitId = unit.UniqueId ?? "unknown";
                    if (addedIds.Contains(unitId)) continue;
                    addedIds.Add(unitId);

                    result.Add(new CharacterInfo
                    {
                        Id = unitId,
                        Name = unit.Descriptor?.CharacterName ?? "Unnamed",
                        Unit = unit,
                        IsMainCharacter = unit.IsMainCharacter,
                        IsPet = false
                    });

                    // 2. v0.2.20: 각 파티 멤버의 펫/동물 동료 추가
                    try
                    {
                        var pets = unit.Pets;
                        if (pets != null && pets.Count > 0)
                        {
                            foreach (var petRef in pets)
                            {
                                var pet = petRef.Entity;
                                if (pet == null) continue;

                                string petId = pet.UniqueId ?? "pet_unknown";
                                if (addedIds.Contains(petId)) continue;

                                addedIds.Add(petId);
                                result.Add(new CharacterInfo
                                {
                                    Id = petId,
                                    Name = pet.Descriptor?.CharacterName ?? "Pet",
                                    Unit = pet,
                                    IsMainCharacter = false,
                                    IsPet = true,
                                    OwnerName = unit.Descriptor?.CharacterName ?? "Unknown"
                                });
                            }
                        }
                    }
                    catch { /* 펫 조회 실패 무시 */ }
                }

                return result;
            }
            catch { return new List<CharacterInfo>(); }
        }

        private class CharacterInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "Unknown";
            public UnitEntityData Unit { get; set; }
            public bool IsMainCharacter { get; set; } = false;
            public bool IsPet { get; set; } = false;  // v0.2.20: 펫/동물 동료
            public string OwnerName { get; set; } = "";  // v0.2.20: 펫 주인 이름
        }
    }
}
