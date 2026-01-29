using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityModManagerNet;

namespace CompanionAI_Pathfinder.Settings
{
    /// <summary>
    /// 언어 옵션
    /// </summary>
    public enum Language
    {
        English,
        Korean
    }

    /// <summary>
    /// 로컬라이제이션 시스템
    /// </summary>
    public static class Localization
    {
        public static Language CurrentLanguage { get; set; } = Language.English;

        private static readonly Dictionary<string, Dictionary<Language, string>> Strings = new()
        {
            // Header
            ["Title"] = new() { { Language.English, "CompanionAI - Pathfinder: WotR" }, { Language.Korean, "동료 AI - 패스파인더: 정의의 분노" } },
            ["Subtitle"] = new() { { Language.English, "Tactical AI for party companions" }, { Language.Korean, "파티 동료를 위한 전술 AI" } },

            // Global Settings
            ["GlobalSettings"] = new() { { Language.English, "Global Settings" }, { Language.Korean, "전역 설정" } },
            ["EnableDebugLogging"] = new() { { Language.English, "Enable Debug Logging" }, { Language.Korean, "디버그 로깅 활성화" } },
            ["Language"] = new() { { Language.English, "Language" }, { Language.Korean, "언어" } },

            // Unit Control Settings
            ["UnitControlSettings"] = new() { { Language.English, "Unit Control Settings" }, { Language.Korean, "유닛 제어 설정" } },
            ["ControlMainCharacter"] = new() { { Language.English, "Control Main Character" }, { Language.Korean, "주인공 제어" } },
            ["ControlMainCharacterDesc"] = new() {
                { Language.English, "Let AI control the main character in combat" },
                { Language.Korean, "전투에서 AI가 주인공을 제어합니다" }
            },
            ["ControlCompanions"] = new() { { Language.English, "Control Companions" }, { Language.Korean, "동료 제어" } },
            ["ControlCompanionsDesc"] = new() {
                { Language.English, "Let AI control companion characters in combat" },
                { Language.Korean, "전투에서 AI가 동료 캐릭터를 제어합니다" }
            },

            // Combat Mode Settings
            ["CombatModeSettings"] = new() { { Language.English, "Combat Mode Settings" }, { Language.Korean, "전투 모드 설정" } },
            ["EnableInTurnBased"] = new() { { Language.English, "Enable in Turn-Based Mode" }, { Language.Korean, "턴제 모드에서 활성화" } },
            ["EnableInRealTime"] = new() { { Language.English, "Enable in Real-Time Mode" }, { Language.Korean, "실시간 모드에서 활성화" } },
            ["RealTimeDecisionInterval"] = new() { { Language.English, "Real-Time Decision Interval" }, { Language.Korean, "실시간 결정 간격" } },
            ["RealTimeDecisionIntervalDesc"] = new() {
                { Language.English, "How often AI makes decisions in real-time mode (seconds)" },
                { Language.Korean, "실시간 모드에서 AI가 결정을 내리는 간격 (초)" }
            },

            // Party Members
            ["PartyMembers"] = new() { { Language.English, "Party Members" }, { Language.Korean, "파티원" } },
            ["AI"] = new() { { Language.English, "AI" }, { Language.Korean, "AI" } },
            ["Character"] = new() { { Language.English, "Character" }, { Language.Korean, "캐릭터" } },
            ["Range"] = new() { { Language.English, "Range" }, { Language.Korean, "거리" } },
            ["NoCharacters"] = new() { { Language.English, "No characters available. Load a save game first." }, { Language.Korean, "사용 가능한 캐릭터가 없습니다. 먼저 세이브 파일을 불러오세요." } },

            // Range Preference
            ["RangePreference"] = new() { { Language.English, "Range Preference" }, { Language.Korean, "거리 선호" } },
            ["RangePreferenceDesc"] = new() { { Language.English, "How should this character engage enemies?" }, { Language.Korean, "이 캐릭터가 적과 어떻게 교전할까요?" } },

            // Range names
            ["Range_Mixed"] = new() { { Language.English, "Mixed" }, { Language.Korean, "혼합" } },
            ["Range_Melee"] = new() { { Language.English, "Melee" }, { Language.Korean, "근접" } },
            ["Range_Ranged"] = new() { { Language.English, "Ranged" }, { Language.Korean, "원거리" } },

            // Range descriptions
            ["RangeDesc_Mixed"] = new() {
                { Language.English, "Uses both melee and ranged attacks depending on situation." },
                { Language.Korean, "상황에 따라 근접과 원거리 공격을 모두 사용합니다." }
            },
            ["RangeDesc_Melee"] = new() {
                { Language.English, "Prefers close combat. Actively moves toward enemies." },
                { Language.Korean, "근접 전투를 선호합니다. 적에게 적극적으로 접근합니다." }
            },
            ["RangeDesc_Ranged"] = new() {
                { Language.English, "Prefers ranged combat. Maintains distance from enemies." },
                { Language.Korean, "원거리 전투를 선호합니다. 적과 거리를 유지합니다." }
            },

            // v0.2.3: AI Role
            ["Role"] = new() { { Language.English, "Role" }, { Language.Korean, "역할" } },
            ["RolePreference"] = new() { { Language.English, "AI Role" }, { Language.Korean, "AI 역할" } },
            ["RolePreferenceDesc"] = new() { { Language.English, "What role should this character play?" }, { Language.Korean, "이 캐릭터가 어떤 역할을 수행할까요?" } },

            // Role names
            ["Role_DPS"] = new() { { Language.English, "DPS" }, { Language.Korean, "딜러" } },
            ["Role_Tank"] = new() { { Language.English, "Tank" }, { Language.Korean, "탱커" } },
            ["Role_Support"] = new() { { Language.English, "Support" }, { Language.Korean, "서포터" } },

            // Role descriptions
            ["RoleDesc_DPS"] = new() {
                { Language.English, "Focus on damage output. Attacks enemies, uses debuffs." },
                { Language.Korean, "공격력에 집중합니다. 적을 공격하고 디버프를 사용합니다." }
            },
            ["RoleDesc_Tank"] = new() {
                { Language.English, "Focus on survival and protection. Uses self-buffs, taunts enemies." },
                { Language.Korean, "생존과 보호에 집중합니다. 자기 버프와 도발을 사용합니다." }
            },
            ["RoleDesc_Support"] = new() {
                { Language.English, "Focus on healing and buffing allies. Heals wounded party members." },
                { Language.Korean, "아군 치료와 버프에 집중합니다. 부상당한 파티원을 치료합니다." }
            },

            // Status
            ["Status"] = new() { { Language.English, "Status" }, { Language.Korean, "상태" } },
            ["Enabled"] = new() { { Language.English, "Enabled" }, { Language.Korean, "활성화" } },
            ["Disabled"] = new() { { Language.English, "Disabled" }, { Language.Korean, "비활성화" } },
            ["TickBrainPatch"] = new() { { Language.English, "TickBrain Patch" }, { Language.Korean, "TickBrain 패치" } },
            ["CombatMode"] = new() { { Language.English, "Combat Mode" }, { Language.Korean, "전투 모드" } },
            ["CombatMode_TurnBased"] = new() { { Language.English, "Turn-Based" }, { Language.Korean, "턴제" } },
            ["CombatMode_RealTime"] = new() { { Language.English, "Real-Time" }, { Language.Korean, "실시간" } },
            ["CombatMode_None"] = new() { { Language.English, "Out of Combat" }, { Language.Korean, "전투 외" } },
            ["AITickCount"] = new() { { Language.English, "AI Tick Count" }, { Language.Korean, "AI Tick 횟수" } },
            ["ProcessedUnits"] = new() { { Language.English, "Processed Units" }, { Language.Korean, "처리된 유닛" } },
        };

        public static string Get(string key)
        {
            if (Strings.TryGetValue(key, out var translations))
            {
                if (translations.TryGetValue(CurrentLanguage, out var text))
                    return text;
                if (translations.TryGetValue(Language.English, out var fallback))
                    return fallback;
            }
            return key;
        }

        public static string GetRangeName(RangePreference range) => Get($"Range_{range}");
        public static string GetRangeDescription(RangePreference range) => Get($"RangeDesc_{range}");

        // v0.2.3: AI Role localization
        public static string GetRoleName(AIRole role) => Get($"Role_{role}");
        public static string GetRoleDescription(AIRole role) => Get($"RoleDesc_{role}");
    }

    /// <summary>
    /// 거리 선호도
    /// </summary>
    public enum RangePreference
    {
        Mixed,      // 혼합 - 상황에 따라
        Melee,      // 근접 - 적에게 접근
        Ranged      // 원거리 - 거리 유지
    }

    /// <summary>
    /// AI 역할
    /// </summary>
    public enum AIRole
    {
        DPS,        // 딜러 - 공격 우선
        Tank,       // 탱커 - 방어, 도발
        Support     // 서포터 - 힐, 버프
    }

    /// <summary>
    /// 개별 캐릭터 설정
    /// </summary>
    public class CharacterSettings
    {
        public string CharacterId { get; set; } = "";
        public string CharacterName { get; set; } = "";
        public bool EnableCustomAI { get; set; } = true;
        public RangePreference RangePreference { get; set; } = RangePreference.Mixed;
        public AIRole Role { get; set; } = AIRole.DPS;

        // 전투 행동
        public int HealAtHPPercent { get; set; } = 30;
        public bool UseBuffsBeforeAttack { get; set; } = true;
        public bool AvoidFriendlyFire { get; set; } = true;
    }

    /// <summary>
    /// 전역 모드 설정
    /// </summary>
    public class ModSettings
    {
        public static ModSettings Instance { get; private set; }
        private static UnityModManager.ModEntry _modEntry;

        // 로깅
        public bool EnableDebugLogging { get; set; } = true;
        public Language UILanguage { get; set; } = Language.English;

        // 유닛 제어 설정
        public bool ControlMainCharacter { get; set; } = false;
        public bool ControlCompanions { get; set; } = true;

        // 전투 모드 설정
        public bool EnableInTurnBased { get; set; } = true;
        public bool EnableInRealTime { get; set; } = true;
        public float RealTimeDecisionInterval { get; set; } = 0.5f;

        // 캐릭터별 설정
        public Dictionary<string, CharacterSettings> CharacterSettings { get; set; } = new();

        /// <summary>
        /// 캐릭터 설정 가져오기 (없으면 생성)
        /// </summary>
        public CharacterSettings GetOrCreateSettings(string characterId, string characterName = null)
        {
            if (string.IsNullOrEmpty(characterId))
                return new CharacterSettings();

            if (!CharacterSettings.TryGetValue(characterId, out var settings))
            {
                settings = new CharacterSettings
                {
                    CharacterId = characterId,
                    CharacterName = characterName ?? characterId,
                    EnableCustomAI = true,
                    RangePreference = RangePreference.Mixed
                };
                CharacterSettings[characterId] = settings;
            }

            if (!string.IsNullOrEmpty(characterName))
                settings.CharacterName = characterName;

            return settings;
        }

        #region Save/Load

        private static string GetSettingsPath()
        {
            return Path.Combine(_modEntry.Path, "settings.json");
        }

        public static void Load(UnityModManager.ModEntry modEntry)
        {
            _modEntry = modEntry;
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonConvert.DeserializeObject<ModSettings>(json);
                    if (settings != null)
                    {
                        Instance = settings;
                        Main.Log("Settings loaded successfully");
                    }
                    else
                    {
                        Instance = new ModSettings();
                    }
                }
                else
                {
                    Main.Log("Settings file not found, creating default");
                    Instance = new ModSettings();
                    Save();
                }
            }
            catch (Exception ex)
            {
                Main.Error($"Failed to load settings: {ex.Message}");
                Instance = new ModSettings();
            }
        }

        public static void Save()
        {
            if (Instance == null || _modEntry == null) return;

            try
            {
                string path = GetSettingsPath();
                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(path, json);
                Main.Verbose("Settings saved");
            }
            catch (Exception ex)
            {
                Main.Error($"Failed to save settings: {ex.Message}");
            }
        }

        #endregion
    }
}
