# CompanionAI - Pathfinder: Wrath of the Righteous

AI Companion Mod for Pathfinder: Wrath of the Righteous

## Overview

CompanionAI completely replaces the default companion AI with a **Utility Scoring** based tactical decision system. Your companions will make smarter, more tactical decisions in combat.

## Features

- **Geometric Mean Scoring System** - Balanced multi-factor decision making
- **Role-based AI** - DPS, Tank, Support roles with different priorities
- **Combat Phase Awareness** - Opening, Midgame, Cleanup, Desperate phases
- **Smart Target Selection** - Prioritizes threats and low HP enemies
- **Buff/Heal Intelligence** - Knows when to buff vs attack vs heal
- **Real-time & Turn-based** - Works in both combat modes

## Requirements

- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
- Pathfinder: Wrath of the Righteous

## Installation

1. Download `CompanionAI_Pathfinder_vX.X.X.zip` from [Releases](https://github.com/seyelive5/CompanionAI_Pathfinder/releases)
2. Extract to `Pathfinder Wrath of the Righteous/Mods/CompanionAI_Pathfinder/`
3. Enable in Unity Mod Manager

## Configuration

Access mod settings through Unity Mod Manager (Ctrl+F10 in-game):
- Enable/disable AI per character
- Set character roles (DPS/Tank/Support)
- Adjust aggression and resource conservation

## Architecture

```
AI Decision Pipeline
        │
        ▼
┌─────────────────────┐
│  SituationAnalyzer  │ → Combat snapshot
├─────────────────────┤
│  - AbilityClassifier│
│  - TargetScorer     │
└─────────────────────┘
        │
        ▼
┌─────────────────────┐
│    TurnPlanner      │ → Role-based strategy
├─────────────────────┤
│  - DPSPlan          │
│  - TankPlan         │
│  - SupportPlan      │
└─────────────────────┘
        │
        ▼
┌─────────────────────┐
│   UtilityScorer     │ → Geometric Mean scoring
├─────────────────────┤
│  - AttackScorer     │
│  - BuffScorer       │
│  - DebuffScorer     │
│  - MovementScorer   │
└─────────────────────┘
        │
        ▼
┌─────────────────────┐
│   ActionExecutor    │ → Execute best action
└─────────────────────┘
```

## Building from Source

```powershell
dotnet build CompanionAI_Pathfinder.csproj -c Release
```

Output: `Pathfinder Wrath of the Righteous/Mods/CompanionAI_Pathfinder/CompanionAI_Pathfinder.dll`

## License

MIT License

## Credits

- Developed with assistance from Claude (Anthropic)
- Based on patterns from [CompanionAI for Rogue Trader](https://github.com/seyelive5/CompanionAI)
