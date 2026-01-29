# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview
- **Game**: Pathfinder: Wrath of the Righteous
- **Type**: Unity Mod Manager mod (C# .NET 4.8)
- **Purpose**: Complete replacement of companion AI with tactical decision-making system
- **Version**: 0.2.21 (tracked in `Info.json`)

## Reference Resources
- **Rogue Trader CompanionAI v3.5.7**: `C:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7`
  - Reference for complete AI replacement architecture
  - Key patterns: Behaviour Tree replacement, single decision point, no basic attacks
- **Pathfinder Decompiled Source**: `c:\Users\veria\Downloads\pathfinder_decompile`
  - Game internals reference for accurate API usage

## Build Command
```powershell
dotnet build CompanionAI_Pathfinder.csproj -c Release
```
Output deploys directly to: `C:\Program Files (x86)\Steam\steamapps\common\Pathfinder Second Adventure\Mods\CompanionAI_Pathfinder\`

## Architecture

### AI Decision Pipeline
```
AiBrainController.TickBrain (Harmony Prefix)
        ↓
TurnOrchestrator (singleton)
        ├─ SituationAnalyzer → Situation snapshot
        │   ├─ AbilityClassifier (categorize abilities)
        │   └─ TargetScorer (rank targets)
        ├─ TurnPlanner → TurnPlan (ordered PlannedActions)
        │   └─ Role strategies: DPSPlan, TankPlan, SupportPlan
        └─ ActionExecutor → ExecutionResult
```

### Key Folders
- **Core/**: Turn orchestration - `TurnOrchestrator`, `TurnState`, `TurnPlan`, `PlannedAction`, `ExecutionResult`
- **Abstraction/**: Game API isolation - `IGameUnit`, `IAbilityData`, `ICombatState` with Pathfinder adapters
- **Analysis/**: Combat analysis - `SituationAnalyzer`, `AbilityClassifier`, `TargetScorer`, `Situation`
- **Planning/**: Role-based strategies - `TurnPlanner`, `BasePlan` (abstract), `DPSPlan`, `TankPlan`, `SupportPlan`
- **Execution/**: Action execution - `ActionExecutor`
- **GameInterface/**: Harmony patches - `CustomBrainPatch`, `RealTimeController`
- **Settings/**: Per-character configuration with bilingual support (EN/KR)
- **UI/**: Unity Mod Manager GUI

### Action Economy
Pathfinder action types: Standard (1), Move (1), Swift (variable), Full-Round (2)
Tracked per turn in `TurnState`.

### Critical Design Decisions
- **GUID-based ability identification** for multilingual compatibility (not string matching)
- **Type-based blacklisting** for abilities (not name-based)
- **Adapter pattern** isolates game API dependencies
- **Dual combat modes**: Turn-based and real-time handled separately

## Critical Architecture Notes

### Basic Attack Problem (v0.2.21 Issue)
**DO NOT** use `UnitAttack` (basic attack) as fallback in AI decisions:
- Basic attack = Standard Action = shares cooldown with skills
- Pattern: Basic attack → Standard cooldown → skills blocked → repeat
- **Solution**: Like RT v3.5.7, remove basic attacks from AI consideration entirely

### RT v3.5.7 vs PF Differences
| Aspect | Rogue Trader v3.5.7 | Pathfinder Current |
|--------|---------------------|-------------------|
| AI Entry | `PartUnitBrain.Tick()` Behaviour Tree replacement | `AiBrainController.TickBrain()` Prefix patch |
| Basic Attacks | Completely excluded from TurnPlanner | Used as fallback (PROBLEM) |
| Decision Point | Single `CompanionAIDecisionNode` | Scattered in RealTimeController |
| Command Queue | `Commands.Empty` strict check | Partial check |

### Pathfinder AI System (from decompile)
- `AiBrainController.Tick()` → `ForceTick()` → `TickBrain()` (static)
- `TickBrain()` checks `NextCommandTime` before issuing commands
- `UnitCommands.Run()` replaces same-type commands (Standard replaces Standard)
- Action cooldown: `CombatState.HasCooldownForCommand(ActionType)`

## Development Guidelines

### Code Changes
- Update `Info.json` version before release
- Mark changes with version comments: `// ★ v0.2.22:`
- Entry point: `Main.cs` → `Main.Load()` initializes Harmony patches

### Harmony Patching
- Main patch: `CustomBrainPatch` prefixes `AiBrainController.TickBrain`
- Uses manual patching via `AccessTools` for private methods
- Harmony instance ID: `CompanionAI_Pathfinder`

### Adding New Strategies
1. Create class inheriting `BasePlan` in `Planning/Plans/`
2. Override `CreatePlan(Situation)` returning `TurnPlan`
3. Register in `TurnPlanner.SelectPlan()`

### Claude Behavior Guidelines
- **Investigate before concluding** - use decompiled sources when game behavior is unclear
- **Complete solutions** - avoid partial fixes or "implement later" suggestions
- **Refactor proactively** - eliminate duplication when found
- **Side effects matter** - consider impact across turn-based and real-time modes
