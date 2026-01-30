# CompanionAI Pathfinder - 아키텍처 문서

**버전**: v0.2.57
**작성일**: 2026-01-31

---

## 1. 시스템 개요

### 1.1 진입점 흐름

```
게임 AI (AiBrainController.TickBrain)
    ↓ Harmony Prefix 패치
CustomBrainPatch.TickBrain_Prefix
    ├── 턴제 전투 → TurnOrchestrator.ProcessTurn()
    └── 실시간 전투 → RealTimeController.ProcessUnit()
                            ↓
                    UnifiedDecisionEngine.DecideAction()
                            ↓
                    ActionCandidate (최적 행동)
                            ↓
                    ExecuteActionCandidate() → 게임 명령
```

### 1.2 핵심 컴포넌트

| 컴포넌트 | 역할 | 위치 |
|----------|------|------|
| **TurnOrchestrator** | 턴제 전투 관리 (라운드/턴 상태) | Core/TurnOrchestrator.cs |
| **RealTimeController** | 실시간 전투 관리 (스로틀링, 쿨다운) | GameInterface/RealTimeController.cs |
| **UnifiedDecisionEngine** | 모든 행동 후보 생성 및 평가 | Core/DecisionEngine/UnifiedDecisionEngine.cs |
| **UtilityScorer** | 후보별 점수 계산 (Geometric Mean) | Scoring/UtilityScorer.cs |
| **SituationAnalyzer** | 현재 전투 상황 분석 | Analysis/SituationAnalyzer.cs |
| **TargetScorer** | 타겟 우선순위 계산 | Analysis/TargetScorer.cs |

---

## 2. 스코어링 시스템 (중요!)

### 2.1 점수 계산 공식

```csharp
// ActionCandidate.cs:105-120
HybridFinalScore = GeometricMean * 100 + BonusScore + PriorityBoost

// 또는 (Consideration 없을 때)
FinalScore = (BaseScore * Effectiveness * Phase * Role) - ResourcePenalty + Bonus + Priority
```

### 2.2 Consideration 시스템 (Geometric Mean)

- 각 요소가 0~1 범위의 점수
- Veto (0에 가까움) = 전체 점수 0 (행동 불가)
- 모든 Consideration의 기하평균 계산

```csharp
// Consideration.cs:118-138
float logSum = 0f;
foreach (var c in _considerations)
    logSum += Mathf.Log(c.Score);
float logMean = logSum / count;
return Mathf.Exp(logMean);
```

### 2.3 보너스 점수 (BonusScore)

AttackScorer.Score()에서 추가:
- **Kill Bonus**: HP% < 70% 타겟에만 적용 (v0.2.57 수정)
- **Flanking Bonus**: +20점
- **Tank Allies Engaged**: TargetScorer에서 처리

### 2.4 역할별 가중치

| 역할 | HP가중치 | 거리가중치 | 위협가중치 | Hittable | AC취약 |
|------|----------|-----------|-----------|----------|--------|
| **Tank** | 0.3 | **1.0** | 0.8 | 0.8 | 0.4 |
| **DPS** | 0.8 | 0.3 | 0.5 | 0.6 | 0.6 |
| **Support** | 0.5 | 0.2 | 1.0 | 1.0 | 0.2 |

---

## 3. 타겟 스코어링 (TargetScorer.cs)

### 3.1 ScoreEnemyUnified() - 통합 스코어링 메서드

**위치**: `Analysis/TargetScorer.cs:106-259`

**사용처**:
- RealTimeController → SelectBestTarget() (레거시)
- AttackScorer.Score() → TargetScorer.ScoreEnemyUnified()
- SelectBestEnemy() (레거시 래퍼)

### 3.2 Tank 거리 보너스 (v0.2.56)

```csharp
// TargetScorer.cs:143-155
if (isTank)
{
    if (distance <= 3f)  distanceScore += 50f;   // 즉시 공격
    if (distance <= 6f)  distanceScore += 40f;   // 한 걸음
    if (distance <= 10f) distanceScore += 25f;   // 가까운 편
    if (distance <= 15f) distanceScore += 10f;   // 중거리
}
```

### 3.3 Tank 아군 위협 감지 (v0.2.56)

```csharp
// TargetScorer.cs:218-251
if (isTank)
{
    int alliesEngaged = CountAlliesEngaged(target, attacker, situation);
    bool isTargetingAlly = IsEnemyTargetingAlly(target, attacker, situation);
    bool isApproachingAllies = IsEnemyApproachingAllies(target, attacker, situation);

    if (alliesEngaged > 0) tankBonus += alliesEngaged * 15f;
    if (isTargetingAlly) tankBonus += 25f;
    if (isApproachingAllies && tankBonus == 0) tankBonus += 12f;
}
```

**주의**: `EngagedUnits`는 근접 교전 후에만 채워짐 → 전투 초반에는 비어있음

---

## 4. 알려진 문제점 및 수정 예정

### 4.1 Kill Bonus 문제 (v0.2.57 수정됨)

**문제**: HP 150% 적에게도 Kill Bonus 적용
**원인**: `CalculateKillBonus()`가 HP%를 확인하지 않음
**수정**: HP% >= 70% 타겟에게는 Kill Bonus 0

```csharp
// AttackScorer.cs:347-353 (v0.2.57)
if (targetHPPercent >= 70f)
{
    return 0f;  // 건강한 적에게는 킬보너스 없음
}
```

### 4.2 레거시 코드 (제거 예정)

| 파일 | 레거시 코드 | 대체 |
|------|------------|------|
| RealTimeController.cs:812-848 | `SelectBestTarget()` → `TargetScorer.ScoreTarget()` | `ScoreEnemyUnified()` 사용 권장 |
| RealTimeController.cs:622-800 | 역할별 로직 (DPS/Tank/Support) | `UnifiedDecisionEngine` |
| TargetScorer.cs:431-443 | `ScoreTarget()`, `ScoreEnemy()` | `ScoreEnemyUnified()` |

### 4.3 EstimateDamage 부정확 (개선 필요)

**위치**: `AttackScorer.cs:362-381`

```csharp
// 현재: 매우 단순한 추정
if (candidate.Classification != null)
{
    int level = candidate.Classification.SpellLevel;
    return 5f + level * 8f;  // 버프도 데미지로 계산됨!
}
```

**문제**: Smite Evil 같은 버프가 21 데미지로 계산됨

---

## 5. 결정 흐름 상세

### 5.1 UnifiedDecisionEngine.DecideAction()

```
1. SituationAnalyzer.Analyze() → Situation 스냅샷
2. CombatPhaseDetector.DetectPhase() → Opening/Midgame/Cleanup/Desperate
3. GenerateCandidates() → List<ActionCandidate>
   ├── GenerateEscapeCandidates() (CC/AoE 탈출)
   ├── GenerateAttackCandidates() (능력 + BasicAttack)
   ├── GenerateBuffCandidates()
   ├── GenerateHealCandidates()
   ├── GenerateDebuffCandidates()
   ├── GenerateMovementCandidates()
   └── EndTurn (항상 폴백)
4. UtilityScorer.ScoreAll() → 점수 계산
5. OrderByDescending(HybridFinalScore) → 최고 점수 선택
```

### 5.2 AttackScorer.Score() 흐름

```
1. BuildConsiderations() → ConsiderationSet 구축
   - HasTarget (Veto)
   - InRange (Veto for abilities)
   - TargetValue (HP + Threat)
   - RoleFit
   - PhaseFit
   - Resource
   - RangePref
   - Hittable
   - ChargeDistance (돌격 특수)

2. TargetScorer.ScoreEnemyUnified() → 타겟 점수 (× 0.4 가중치)

3. ScoreAbility() 또는 ScoreBasicAttack()

4. CalculateKillBonus() → BonusScore (HP% < 70%만)

5. ScoreDistance() → 거리 보너스

6. Flanking Check → +20 BonusScore
```

---

## 6. 설정 시스템

### 6.1 캐릭터 설정 (CharacterSettings)

```csharp
// Settings/ModSettings.cs
public class CharacterSettings
{
    public bool EnableCustomAI;        // AI 활성화
    public AIRole Role;                 // Tank, DPS, Support
    public RangePreference RangePreference;  // Melee, Ranged, Mixed
    // ...
}
```

### 6.2 역할 변환

```csharp
// TargetScorer.cs
Tank ↔ Melee (RangePreference)
DPS ↔ Mixed
Support ↔ Ranged
```

---

## 7. 명령 실행

### 7.1 게임 명령 발행

```csharp
// RealTimeController.cs
var command = new UnitUseAbility(ability, targetWrapper);
unit.Commands.Run(command);
SetNextCommandTime(unit, command);  // 쿨다운 설정
```

### 7.2 NextCommandTime (스팸 방지)

```csharp
// RealTimeController.cs:1663-1695
Standard/Move: 0.3~0.4초 딜레이
Free/Swift: 0.15초 딜레이 (v0.2.28)
```

---

## 8. 디버깅 팁

### 8.1 로그 확인

```
[UtilityScorer] 유닛명 Top 3 decisions:
  1. [AbilityAttack] 능력명 -> 타겟명 (Hybrid=XX, GM=0.XXX, Bonus=XX)

[TargetScorer] 타겟명: Tank threat bonus (engaged=X, targeting=True/False, approaching=True/False) +XX
```

### 8.2 주요 로그 위치

| 로그 | 위치 | 내용 |
|------|------|------|
| `[DecisionEngine]` | UnifiedDecisionEngine.cs | 최종 결정 |
| `[UtilityScorer]` | UtilityScorer.cs | 상위 후보 목록 |
| `[TargetScorer]` | TargetScorer.cs | 타겟 점수 상세 |
| `[AttackScorer]` | AttackScorer.cs | 공격 점수 상세 |
| `[RT]` | RealTimeController.cs | 실시간 명령 발행 |

---

## 9. 향후 개선 방향

### 9.1 단기 (v0.2.58+)

1. **EstimateDamage 개선**: 버프/디버프 구분, 실제 데미지 계산
2. **레거시 코드 제거**: RealTimeController의 역할별 로직 정리
3. **Tank 거리 보너스 테스트**: 현재 값이 충분한지 확인

### 9.2 중기

1. **통합 스코어링 완료**: 모든 호출처에서 `ScoreEnemyUnified()` 사용
2. **Consideration 확장**: 더 많은 요소 Veto/점수화
3. **성능 최적화**: TargetAnalyzer 캐시 활용 극대화

---

## 10. 파일 구조 요약

```
CompanionAI_Pathfinder/
├── Main.cs                     # 진입점, Harmony 패치 초기화
├── Core/
│   ├── TurnOrchestrator.cs     # 턴제 관리
│   ├── TurnState.cs            # 턴 상태 추적
│   ├── TurnPlan.cs             # 턴 계획 (레거시)
│   ├── PlannedAction.cs        # 계획된 행동
│   ├── ExecutionResult.cs      # 실행 결과
│   └── DecisionEngine/
│       └── UnifiedDecisionEngine.cs  # 핵심 결정 엔진
├── Analysis/
│   ├── Situation.cs            # 전투 상황 스냅샷
│   ├── SituationAnalyzer.cs    # 상황 분석
│   ├── TargetScorer.cs         # 타겟 스코어링 (중요!)
│   ├── TargetAnalyzer.cs       # 타겟 분석 (캐시)
│   ├── AbilityClassifier.cs    # 능력 분류
│   └── AoeDangerAnalyzer.cs    # AoE 위험 분석
├── Scoring/
│   ├── ActionCandidate.cs      # 행동 후보 (중요!)
│   ├── Consideration.cs        # Geometric Mean 시스템
│   ├── UtilityScorer.cs        # 통합 스코어러
│   ├── ResponseCurves.cs       # 점수 곡선
│   └── Scorers/
│       ├── AttackScorer.cs     # 공격 점수 (중요!)
│       ├── BuffScorer.cs       # 버프 점수
│       ├── DebuffScorer.cs     # 디버프 점수
│       └── MovementScorer.cs   # 이동 점수
├── GameInterface/
│   ├── CustomBrainPatch.cs     # Harmony 패치
│   └── RealTimeController.cs   # 실시간 전투 (중요!)
├── Planning/
│   ├── MovementPlanner.cs      # 이동 계획
│   └── TurnPlanner.cs          # 턴 계획 (레거시)
└── Settings/
    └── ModSettings.cs          # 설정 관리
```

---

*이 문서는 코드베이스 분석을 통해 자동 생성되었습니다. 코드 변경 시 업데이트가 필요합니다.*
