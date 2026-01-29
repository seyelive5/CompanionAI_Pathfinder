# **유니티 모딩 환경에서의 고도화된 AI 아키텍처: 유틸리티 시스템, 행동 트리 및 하이브리드 구현에 대한 심층 분석 보고서**

## **1\. 개요 (Executive Summary)**

현대 게임 개발, 특히 기존 게임의 로직을 수정하고 확장하는 모딩(Modding) 환경에서 인공지능(AI) 아키텍처의 설계는 게임플레이의 깊이와 시스템 성능을 결정짓는 가장 중요한 요소 중 하나로 작용한다. 본 보고서는 유니티(Unity) 엔진 기반의 게임 모딩 프로젝트에서 AI 행동 선택 메커니즘으로 현재 사용 중인 '스코어링 시스템(Scoring System)', 즉 \*\*유틸리티 AI(Utility AI)\*\*의 유효성을 검증하고, 이에 대한 구조적 의구심을 해소하기 위해 작성되었다.

사용자가 제기한 의문, 즉 "현재 AI의 행동을 점수 기반으로 선택하도록 관리하고 있는데 이것이 정말 괜찮은 방법론인가"에 대한 답은 결론적으로 \*\*"매우 유효하며, 현대적인 AAA 게임 개발의 흐름과 일치한다"\*\*이다. 그러나 단순한 점수 비교 방식은 확장성(Scalability)과 행동의 연속성(Coherence) 측면에서 치명적인 한계를 드러낼 수 있다. 이를 보완하기 위해 본 보고서는 유틸리티 AI를 단독으로 사용하는 대신, 구조적 안정성을 보장하는 \*\*행동 트리(Behavior Tree)\*\*와 결합한 \*\*하이브리드 아키텍처(Hybrid Architecture)\*\*를 제안한다.

또한, 유니티 모딩이라는 특수한 환경—소스 코드에 대한 제한적 접근, 런타임 코드 주입(Injection)의 필요성, 원본 게임과의 성능 조율—을 고려하여, **Harmony** 라이브러리를 활용한 훅(Hook) 기법, \*\*타임 슬라이싱(Time-Slicing)\*\*을 통한 연산 부하 분산, 그리고 \*\*영향력 맵(Influence Map)\*\*을 활용한 공간 추론 기술을 심도 있게 다룬다. 이 보고서는 단순한 방법론의 나열을 넘어, 각 아키텍처의 수학적 배경과 실제 구현 시나리오, 그리고 최적화 전략을 포괄적으로 분석하여 프로젝트의 AI 시스템을 한 단계 진화시키는 것을 목적으로 한다.

## ---

**2\. 게임 AI 아키텍처의 이론적 배경과 진화**

사용자가 현재 구현 중인 '스코어링 시스템'이 전체 AI 기술의 흐름에서 어디에 위치하는지를 이해하는 것은 아키텍처의 타당성을 평가하는 첫걸음이다. 게임 AI는 지난 수십 년간 '규칙 기반의 하드 코딩'에서 '데이터 주도적인 의사결정'으로 진화해 왔다.

### **2.1 유한 상태 기계 (Finite State Machines, FSM)의 한계**

가장 고전적인 AI 아키텍처인 유한 상태 기계(FSM)는 에이전트가 특정 시점에 단 하나의 '상태(State)'(예: 순찰, 추적, 공격)만을 가지며, 미리 정의된 '전이(Transition)' 조건에 따라 상태를 변경하는 방식이다.

* **구조적 특징:** 그래프 기반의 노드와 엣지로 구성된다. 구현이 직관적이며 가볍다.  
* **모딩 환경에서의 제약:** 행동의 가짓수가 늘어날수록 상태 간의 전이 조건이 기하급수적으로 증가하는 '스파게티 코드' 문제를 야기한다.1 특히 모딩의 경우, 원본 게임의 FSM에 새로운 상태를 끼워 넣는 것은 기존 전이 로직을 훼손할 위험이 크다.  
* **결론:** 단순한 적이나 NPC에게는 유효하지만, 복잡한 판단을 요구하는 현대적 AI에는 부적합하다.3

### **2.2 행동 트리 (Behavior Trees, BT)의 표준화**

2000년대 중반, *헤일로 2(Halo 2\)* 등의 게임을 통해 대중화된 행동 트리는 현재 유니티와 언리얼 엔진을 포함한 업계의 표준으로 자리 잡았다.

* **구조적 특징:** 계층적 트리 구조를 가지며, 루트(Root)에서 시작하여 자식 노드들을 탐색한다. 주요 노드로는 순차적 실행을 보장하는 \*\*시퀀스(Sequence)\*\*와 조건에 따라 분기하는 \*\*셀렉터(Selector)\*\*가 있다.1  
* **장점:** 모듈화가 강력하다. 하위 트리를 재사용할 수 있어 모딩 시 특정 행동(예: 엄폐 행동)만 교체하거나 추가하기 용이하다.  
* **한계:** 기본적으로 \*\*우선순위 기반(Priority-based)\*\*이다. 트리의 왼쪽 노드가 실행 가능하면 오른쪽 노드는 무시된다. 이는 "체력이 낮지만, 적을 처치할 절호의 기회"와 같은 딜레마 상황에서 유연한 대처를 어렵게 한다.2

### **2.3 목표 지향 행위 계획 (Goal-Oriented Action Planning, GOAP)**

\*F.E.A.R.\*에서 유명해진 GOAP는 에이전트에게 '목표(Goal)'를 부여하고, 그 목표를 달성하기 위한 행동들을 실시간으로 계획(Planning)하게 만든다.

* **특징:** 행동의 인과관계를 동적으로 연결한다. "총을 쏘려면 \-\> 총알이 있어야 하고 \-\> 총알을 얻으려면 \-\> 재장전을 해야 한다"는 식의 체인을 생성한다.5  
* **모딩 적합성:** 높은 유연성을 제공하지만, 탐색 알고리즘(A\*)의 연산 비용이 높고, 유니티의 가비지 컬렉션(GC) 환경에서 빈번한 계획 수립은 프레임 드랍을 유발할 수 있다.6

### **2.4 유틸리티 AI (Utility AI)와 스코어링 시스템**

사용자가 채택한 방식인 유틸리티 AI는 "가장 합리적인 선택은 무엇인가?"라는 경제학적 질문에서 출발한다. 모든 가능한 행동에 대해 현재 상황(Context)을 반영한 **효용(Utility)** 점수를 매기고, 가장 높은 점수의 행동을 선택한다.

* **핵심 메커니즘:** 입력값(예: 체력, 거리, 탄약 수)을 0.0에서 1.0 사이의 정규화된 점수로 변환하는 \*\*반응 곡선(Response Curve)\*\*을 사용한다.8  
* **강점:** 상충하는 여러 요인을 수학적으로 통합하여 '최적'의 판단을 내릴 수 있다. 이는 *심즈(The Sims)* 시리즈나 \*길드워 2(Guild Wars 2)\*와 같은 복잡한 에이전트 시뮬레이션에서 증명된 방식이다.8  
* **사용자의 의구심에 대한 답변:** 스코어링 시스템은 단순한 '편법'이 아니라, \*\*무한 축 유틸리티 시스템(Infinite Axis Utility System, IAUS)\*\*이라 불리는 고도화된 아키텍처의 기초이다. 따라서 방향성 자체는 매우 훌륭하다. 다만, '어떻게 점수를 산출하고 결합할 것인가'와 '행동의 지속성을 어떻게 보장할 것인가'가 기술적 과제이다.

## ---

**3\. 심층 분석: 무한 축 유틸리티 시스템 (IAUS)의 수학적 모델링**

단순히 "체력이 낮으니 힐을 한다"는 식의 if-else 구조를 점수화하는 것으로는 유틸리티 AI의 잠재력을 끌어낼 수 없다. 전문적인 유틸리티 시스템은 **IAUS** 아키텍처를 따르며, 이는 사용자가 구현 중인 시스템을 개선할 수 있는 핵심적인 방법론이다.

### **3.1 고려 사항(Consideration)과 축(Axis)의 설계**

행동의 점수를 결정하는 각각의 요인을 \*\*고려 사항(Consideration)\*\*이라 한다. 하나의 행동(Action)은 여러 개의 고려 사항(Axes)을 가질 수 있다.

* **입력(Input)의 정규화:** 모든 입력 데이터는 0과 1 사이의 실수로 변환되어야 한다.  
  * 예: 내 탄약 수 / 최대 탄약 수, 1 \- (적과의 거리 / 최대 사거리)  
* **반응 곡선(Response Curves)의 적용:** 입력값과 효용 점수는 선형적이지 않다. 체력이 100%에서 90%로 떨어질 때의 위기감과, 20%에서 10%로 떨어질 때의 위기감은 다르다. 이를 모델링하기 위해 \*\*로지스틱 함수(Logistic Function)\*\*나 **로짓(Logit) 곡선**을 사용한다.10

**\[수식 1\] 로지스틱 반응 곡선 (Logistic Response Curve)**

**![][image1]**

* **![][image2]**: 정규화된 입력값 (Input)  
* ![][image3]: 기울기(Slope) \- 변화의 급격함을 조절  
* ![][image4]: 중간점(Midpoint) \- 곡선이 변곡하는 지점 (x축 이동)  
* ![][image5]: 수직 스케일링  
* ![][image6]: 수직 이동

이러한 수학적 모델링을 통해 디자이너나 모더(Modder)는 코드를 수정하지 않고 파라미터(![][image7])만 조절함으로써 AI의 성격(공격적, 방어적 등)을 튜닝할 수 있다.12

### **3.2 점수 결합 방식: 기하 평균(Geometric Mean)의 중요성**

여러 고려 사항의 점수를 합산하여 최종 행동 점수를 산출할 때, 단순 평균이나 가중 합(Weighted Sum)을 사용하는 것은 위험하다.

* **가중 합의 문제점:** "거리가 적절함(0.8)" \+ "체력이 충분함(0.9)" \+ "총알이 없음(0.0)"인 상황에서 평균을 내면 ![][image8]이 된다. 총알이 없음에도 불구하고 공격 행동이 선택될 수 있다.  
* **해결책 \- 기하 평균 및 곱셈 연산:** IAUS는 각 고려 사항의 점수를 곱하는 방식을 사용한다. 하나라도 0점이 나오면 전체 점수가 0이 되는 **비토(Veto, 거부권)** 효과를 가진다.  
* **보정 계수 (Compensation Factor):** 단순 곱셈은 고려 사항이 많을수록 점수가 0에 수렴하는 문제가 있다. 이를 방지하기 위해 ![][image9]제곱근을 취하는 기하 평균을 사용한다.10

**\[수식 2\] 기하 평균을 이용한 최종 점수 산출**

**![][image10]**  
여기서 ![][image11]는 각 고려 사항의 점수, ![][image9]은 고려 사항의 개수, $W\_{action}$은 행동 자체의 기본 가중치이다.

## ---

**4\. 제기된 의구심과 구조적 해결책: 하이브리드 아키텍처**

사용자가 느낀 "정말 괜찮은 건지 의심이 간다"는 직관은 정확하다. 순수 유틸리티 시스템(Pure Utility System)은 \*\*구조적 결핍(Lack of Structure)\*\*이라는 치명적인 단점을 가진다.

### **4.1 순수 유틸리티 시스템의 한계**

1. **행동의 단절 (Jittering):** 매 프레임(또는 틱)마다 점수를 재계산하므로, '공격(0.51)'과 '이동(0.49)'의 점수가 비슷할 경우 에이전트가 두 행동 사이에서 끊임없이 진동하는 현상이 발생한다. 이를 막기 위해 히스테리시스(Hysteresis, 관성) 로직을 추가해야 하지만, 코드가 복잡해진다.  
2. **시퀀싱(Sequencing)의 부재:** "엄폐물로 이동 \-\> 앉기 \-\> 재장전 \-\> 사격"과 같은 일련의 행동을 유틸리티 점수만으로 연결하기는 매우 어렵다. 각 단계마다 인위적인 점수 조작이 필요하며, 이는 FSM보다 더 관리하기 힘든 상태 의존성을 만든다.4  
3. **디버깅의 어려움:** AI가 엉뚱한 행동을 했을 때, 그것이 수십 개의 부동소수점 연산 결과 중 어디에서 기인했는지 추적하기가 난해하다.14

### **4.2 대안: 유틸리티 기반 행동 트리 (Utility-Driven Behavior Tree)**

가장 이상적인 대안은 **행동 트리(BT)의 구조적 제어 능력**과 **유틸리티 AI의 판단 능력**을 결합하는 것이다. 이를 **하이브리드 아키텍처**라고 하며, 특히 **유틸리티 셀렉터(Utility Selector)** 노드를 활용하는 방식이 업계의 최신 트렌드이다.2

#### **아키텍처 구조**

이 구조에서는 행동 트리의 상위 분기(Branch) 결정을 유틸리티 시스템이 담당하고, 하위 세부 실행은 행동 트리의 시퀀스가 담당한다.

1. **루트(Root) \- 유틸리티 셀렉터:** 최상위 노드는 일반적인 셀렉터(우선순위 순차 실행)가 아니라, 자식 노드들의 유틸리티 점수를 계산하여 가장 높은 점수의 자식을 실행하는 '유틸리티 셀렉터'이다.  
2. **자식 노드 \- 고수준 전략(Strategy):** '전투', '생존', '대기', '상호작용'과 같은 큰 단위의 전략들이 자식으로 연결된다. 각 전략 노드는 자신의 유틸리티 점수를 계산하는 **Scorer** 컴포넌트를 가진다.  
3. **리프(Leaf) \- 행동 시퀀스:** 선택된 전략(예: 전투) 내부에서는 표준 행동 트리의 **시퀀스(Sequence)** 노드를 사용하여 "조준 \-\> 발사 \-\> 대기"와 같은 원자적 행동들을 순차적으로 실행한다.

**\[표 1\] 순수 유틸리티 AI vs. 행동 트리 vs. 하이브리드 아키텍처 비교**

| 특성 | 순수 유틸리티 AI (현재 방식) | 전통적 행동 트리 (BT) | 하이브리드 (Utility Selector) |
| :---- | :---- | :---- | :---- |
| **의사결정 방식** | 점수 기반 (Fuzzy Logic) | 우선순위 기반 (Priority) | 점수 기반 분기 \+ 순차 실행 |
| **장점** | 상황에 따른 최적의 판단 | 행동의 연속성 및 시각화 용이 | 최적 판단과 안정적 실행의 결합 |
| **단점** | 행동의 연결성 부족, 떨림 현상 | 유연성 부족 (경직된 우선순위) | 구현 복잡도 상승 |
| **모딩 적합성** | 높음 (데이터 튜닝 용이) | 중간 (구조 변경 필요) | **최상 (구조적 안정성 \+ 튜닝)** |

이 하이브리드 방식을 채택하면, 사용자는 현재 개발한 스코어링 시스템을 폐기하지 않고 행동 트리의 **선택(Selector) 노드 로직**으로 이식함으로써 시스템을 고도화할 수 있다.

## ---

**5\. 유니티 모딩 환경에서의 기술적 구현 전략**

유니티 매니저를 통한 모딩 개발이라는 특수한 환경을 고려할 때, 코드 주입(Injection)과 성능 최적화는 AI 알고리즘만큼이나 중요하다.

### **5.1 Harmony 라이브러리를 이용한 런타임 주입 (Runtime Injection)**

모딩은 원본 게임의 소스 코드를 직접 수정하는 것이 아니라, 컴파일된 어셈블리에 자신의 로직을 끼워 넣는 과정이다. 이를 위해 **Harmony** 라이브러리가 필수적으로 사용된다.15

* **진입점(Entry Point) 확보:** BepInEx나 Unity Doorstop과 같은 모드 로더를 통해 어셈블리를 로드한다.  
* **Update 루프 훅(Hook):** AI 매니저가 작동하려면 매 프레임 호출이 필요하다. 원본 게임의 GameManager나 AIController의 Update 메서드에 \[HarmonyPrefix\] 또는 \[HarmonyPostfix\]를 사용하여 커스텀 AI 매니저의 Tick() 함수를 호출하도록 연결한다.15  
* **완전 대체(Replacement) 전략:** 만약 원본 AI를 완전히 끄고 내 AI를 적용하고 싶다면, \[HarmonyPrefix\]에서 return false;를 반환하여 원본 메서드의 실행을 차단하고 커스텀 로직만 실행되게 할 수 있다.17

### **5.2 중앙집중식 AI 매니저와 최적화**

유니티의 MonoBehaviour.Update()는 C++ 엔진 영역과 C\# 스크립트 영역 간의 컨텍스트 스위칭(Interop) 오버헤드를 발생시킨다. 수백 명의 AI 에이전트가 각각 Update()를 호출하는 것은 성능에 치명적이다.19

* **매니저 패턴(Manager Pattern):** 사용자가 언급한 '유니티 매니저'는 올바른 접근이다. 단일 AIManager가 Update() 루프를 한 번만 돌면서, 등록된 모든 AI 에이전트의 Tick()을 순차적으로 호출해야 한다.  
* **POCO (Plain Old C\# Object):** 개별 AI 로직 클래스는 MonoBehaviour를 상속받지 않는 일반 C\# 클래스로 구현하여 오버헤드를 줄인다.

### **5.3 타임 슬라이싱 (Time-Slicing) 스케줄링**

복잡한 유틸리티 점수 계산(수십 개의 곡선 연산)을 매 프레임 모든 에이전트에게 수행하는 것은 불가능에 가깝다. 이를 해결하기 위해 **타임 슬라이싱** 기법을 적용해야 한다.19

* **구현 원리:** 전체 에이전트 리스트를 ![][image12]개의 그룹으로 나누고, 매 프레임 한 그룹씩만 사고(Think) 과정을 수행한다.  
* **예시:** 60 FPS 게임에서 AI 판단 주기를 0.1초(10 FPS)로 설정한다면, 프레임당 전체 에이전트의 1/6만 업데이트한다.  
* **프레임 분산 코드 패턴:**  
  C\#  
  void Update() {  
      int batchSize \= agents.Count / 6; // 6프레임 분산  
      int start \= (Time.frameCount % 6) \* batchSize;  
      int end \= start \+ batchSize;

      for (int i \= start; i \< end; i++) {  
          agents\[i\].Think(); // 고비용 연산 (유틸리티 계산)  
      }

      for (int i \= 0; i \< agents.Count; i++) {  
          agents\[i\].Move(); // 저비용 연산 (위치 이동, 애니메이션)은 매 프레임 수행  
      }  
  }

  이 방식은 *길드워 2*의 "Building a Better Centaur" 강연에서 소개된 대규모 AI 최적화의 핵심 기법이다.19

## ---

**6\. 공간 추론의 통합: 영향력 맵 (Influence Maps)**

유틸리티 AI가 "어떤 행동을 할 것인가"를 결정한다면, "어디서 그 행동을 할 것인가"는 공간 정보에 달려 있다. 단순한 거리 계산을 넘어 전술적인 위치 선정을 위해 **영향력 맵(Influence Maps)** 도입을 적극 권장한다.9

### **6.1 영향력 맵의 개념과 구조**

영향력 맵은 게임 월드를 격자(Grid)나 그래프로 나누고, 각 셀(Cell)에 전술적 정보를 점수화하여 저장한 것이다.

* **위협 맵 (Threat Map):** 적 유닛 주변에 음수(-) 점수를 퍼뜨린다.  
* **관심 맵 (Interest Map):** 플레이어, 자원, 목표물 주변에 양수(+) 점수를 퍼뜨린다.  
* **전파 (Propagation):** 점수는 중심에서 멀어질수록 감쇠(Decay)한다. 이를 통해 "적과는 멀지만 공격 가능한 위치"와 같은 고차원적인 전술 지점을 수학적으로 찾을 수 있다.

### **6.2 유틸리티 시스템과의 결합**

영향력 맵의 데이터는 유틸리티 시스템의 강력한 \*\*입력(Input)\*\*이 된다.

* **예시:** '엄폐 이동' 행동의 점수 계산 시, GetInfluence(position) 함수를 통해 현재 위치와 목표 위치의 전술적 가치를 입력으로 사용한다. 이를 통해 AI는 단순히 가까운 엄폐물이 아니라, "적의 사선에서 벗어나 있고 아군과 가까운 엄폐물"을 선택하게 된다.24

## ---

**7\. 결론 및 제언 (Conclusion and Recommendations)**

사용자가 현재 개발 중인 \*\*스코어링 시스템(유틸리티 AI)\*\*은 모딩 환경에서 매우 강력하고 유효한 접근법이다. 이는 FSM의 경직성을 탈피하고, 상황에 맞는 유동적인 AI를 구현할 수 있는 토대이다. 그러나 점수 계산만으로는 행동의 연속성과 디버깅의 용이성을 보장하기 어렵다.

따라서 본 보고서는 다음과 같은 로드맵을 제안한다:

1. **하이브리드 전환:** 기존의 스코어링 로직을 유지하되, 이를 **행동 트리(Behavior Tree)의 유틸리티 셀렉터 노드** 내부로 캡슐화하라. 이를 통해 '전략적 판단(유틸리티)'과 '전술적 실행(행동 트리 시퀀스)'을 분리하여 관리할 수 있다.  
2. **수학적 정교화:** 단순 가중 합 대신 **로지스틱 반응 곡선**과 **기하 평균**을 도입하여 점수 산출의 변별력을 높이고 비토(Veto) 로직을 강화하라.  
3. **관리자 패턴 최적화:** Harmony를 통해 주입된 단일 **AI 매니저**가 **타임 슬라이싱** 방식으로 에이전트들의 사고(Think) 주기를 분산 처리하도록 설계하라. 이는 유니티 모딩의 성능 병목을 해결하는 핵심 열쇠이다.  
4. **공간 정보 통합:** 여유가 된다면 **영향력 맵**을 도입하여 유틸리티 시스템에 공간적 맥락(Context)을 제공함으로써 AI의 지능을 한 차원 높일 것을 권장한다.

이러한 접근은 *The Sims*, *F.E.A.R.*, *Guild Wars 2* 등 유수의 명작들이 채택한 검증된 아키텍처이며, 유니티 모딩 환경에서도 충분히 구현 가능한 현실적인 솔루션이다.

# ---

**1\. 서론: 유니티 모딩 환경과 AI 아키텍처의 과제**

게임 모딩(Modding)은 기존 게임의 수명 주기를 연장하고 새로운 경험을 창출하는 창의적인 활동이지만, 기술적 관점에서는 "제약 속의 창조"라는 독특한 도전 과제를 안고 있다. 특히 인공지능(AI) 모딩은 단순히 텍스처나 수치를 바꾸는 것을 넘어, 게임의 핵심 로직인 의사결정 시스템에 개입해야 하므로 고도의 아키텍처 설계가 요구된다.

## **1.1 연구의 배경 및 목적**

본 보고서는 유니티(Unity) 엔진 기반의 게임 모딩 프로젝트를 진행 중인 개발자의 질의에 응답하기 위해 작성되었다. 해당 개발자는 현재 AI의 행동 선택을 위해 **'스코어링 시스템(Scoring System)'**, 학술적 용어로 \*\*유틸리티 AI(Utility AI)\*\*를 구현하여 사용 중이다. 그러나 개발 과정에서 "이 방식이 과연 올바른가?"라는 근본적인 의구심을 가지게 되었으며, 더 나은 방법론이나 개선된 아이디어를 모색하고 있다.

이러한 의구심은 매우 타당하다. 점수 기반 시스템은 강력한 유연성을 제공하지만, 프로젝트의 규모가 커질수록 튜닝의 난이도가 급상승하고 행동의 인과관계를 추적하기 어려워지는 단점이 있기 때문이다. 본 보고서의 목적은 사용자의 현재 접근 방식을 이론적으로 검증하고, 유틸리티 시스템의 한계를 극복할 수 있는 \*\*하이브리드 아키텍처(Hybrid Architecture)\*\*와 유니티 모딩 환경에 특화된 **최적화 구현 전략**을 제시하는 데 있다.

## **1.2 유니티 모딩의 특수성**

일반적인 게임 개발과 달리, 모딩은 다음과 같은 제약 사항을 고려해야 한다.

1. **제한된 소스 코드 접근:** 원본 게임의 전체 소스 코드를 볼 수 없는 경우가 많으며, 리플렉션(Reflection)이나 디컴파일을 통해 로직을 유추해야 한다.  
2. **런타임 주입(Runtime Injection):** 에디터 상에서 컴포넌트를 드래그 앤 드롭하는 방식 대신, 런타임에 코드를 메모리에 주입하여 기존 메서드를 가로채거나(Hooking) 대체해야 한다. 이는 Harmony와 같은 라이브러리의 숙련된 사용을 요구한다.15  
3. **성능 예산(Performance Budget):** 모드는 원본 게임 위에서 돌아가기 때문에, 추가적인 연산 부하를 최소화해야 한다. 특히 AI 연산은 CPU 비용이 높으므로 효율적인 스케줄링이 필수적이다.

# ---

**2\. 유틸리티 AI (Utility AI) 심층 분석: 스코어링 시스템의 정체**

사용자가 "스코어링 시스템"이라고 칭한 것은 게임 AI 분야에서 **유틸리티 이론(Utility Theory)** 또는 **효용 기반 AI**로 불리는 정통 아키텍처이다. 이는 *심즈(The Sims)*, *문명(Civilization)*, *길드워 2(Guild Wars 2\)* 등 고도의 자율성을 가진 에이전트가 등장하는 게임에서 핵심적으로 사용된 기술이다.8

## **2.1 핵심 원리: 합리적 선택 이론**

유틸리티 AI는 경제학의 '합리적 선택 이론'을 게임 에이전트에 적용한 것이다. 에이전트는 매 순간 가능한 모든 행동(Action)의 \*\*효용(Utility)\*\*을 계산하고, 그중 가장 높은 효용을 가진 행동을 선택한다.

* **맥락 인식 (Context Awareness):** FSM(유한 상태 기계)이 if (거리 \< 10\) 공격과 같이 경직된 조건을 사용한다면, 유틸리티 AI는 효용 \= f(거리) \* f(체력) \* f(무기)와 같이 연속적인 상황 변수를 종합적으로 고려한다.  
* **창발적 행동 (Emergent Behavior):** 개발자가 명시적으로 지정하지 않아도, 상황의 조합에 따라 의외의(그러나 합리적인) 행동이 나타난다. 예를 들어, 체력이 낮아도 적이 등을 보이고 있고 거리가 가깝다면 '도망' 대신 '기습'을 선택하는 식이다.

## **2.2 무한 축 유틸리티 시스템 (IAUS)**

단순한 점수 합산 방식의 한계를 극복하기 위해, 데이브 마크(Dave Mark)와 케빈 딜(Kevin Dill)은 \*\*IAUS(Infinite Axis Utility System)\*\*라는 고도화된 아키텍처를 정립했다.25 사용자의 시스템이 "괜찮은지" 확인하려면, 이 IAUS의 기준을 따르고 있는지 점검해야 한다.

### **2.2.1 입력의 정규화와 반응 곡선**

IAUS의 가장 중요한 특징은 원본 데이터(Raw Data)를 그대로 점수에 반영하지 않고, 반드시 \*\*반응 곡선(Response Curve)\*\*을 거쳐 0.0\~1.0 사이의 값으로 변환한다는 점이다.

* **선형(Linear)의 한계:** 점수 \= 체력 / 100으로 설정하면, 체력이 50일 때 점수는 0.5다. 하지만 실제 게임에서 체력 50%는 아직 안전한 상태일 수 있고, 20% 미만부터 급격히 위험해질 수 있다. 선형 모델은 이러한 심리적 임계치를 반영하지 못한다.  
* **로지스틱 곡선(Logistic Curve)의 도입:** S자 형태의 곡선은 특정 임계치를 기준으로 점수가 급격히 변하도록 만든다. 이는 생물학적 자극-반응 모델이나 인간의 의사결정 패턴과 유사하다.10

### **2.2.2 점수 결합의 수학: 기하 평균**

여러 고려 사항(Consideration)의 점수를 합칠 때, 덧셈 평균보다는 \*\*곱셈(Multiplication)\*\*이나 \*\*기하 평균(Geometric Mean)\*\*이 우월하다.

* **비토(Veto) 효과:** 곱셈을 사용하면 어떤 하나의 요소가 0점(불가능)일 때 전체 점수가 0이 된다. 예를 들어 "사거리에 적이 있음(1.0)"이어도 "탄약이 없음(0.0)"이라면 총합은 0이 되어야 한다. 덧셈 평균은 이를 0.5로 계산하여 잘못된 행동을 유발할 수 있다.26  
* **점수 희석 방지:** 단순히 곱하기만 하면 ![][image13]와 같이 고려 사항이 많을수록 점수가 낮아지는 현상이 발생한다. IAUS는 이를 보정하기 위해 ![][image9]제곱근을 취하는 기하 평균을 사용하여, 고려 사항의 개수와 무관하게 공정한 점수 비교를 가능하게 한다.10

# ---

**3\. 경쟁 아키텍처 비교: FSM, BT, GOAP**

사용자가 "더 좋은 아이디어"를 물었을 때, 이를 답변하기 위해서는 다른 아키텍처들이 왜 사용되며 어떤 장단점이 있는지를 명확히 비교해야 한다.

## **3.1 유한 상태 기계 (Finite State Machines, FSM)**

* **개요:** 상태(Node)와 전이(Transition)로 구성된 그래프.  
* **모딩 관점:** 가장 피해야 할 아키텍처이다. 기존 게임의 FSM에 새로운 상태를 추가하려면, 기존의 모든 상태에서 새로운 상태로 가는 전이 조건을 일일이 하드코딩해야 한다. 이는 유지보수가 불가능한 스파게티 코드를 양산한다.1

## **3.2 행동 트리 (Behavior Trees, BT)**

* **개요:** *헤일로(Halo)*, *언차티드(Uncharted)* 등 현대 AAA 게임의 표준. 트리 구조로 행동을 계층화한다.  
* **장점:** 모듈화가 뛰어나다. '전투'라는 하위 트리를 통째로 떼어내거나 교체하기 쉽다. 실행 흐름이 시각적으로 명확하여 디버깅이 쉽다.  
* **단점 (순수 BT):** **우선순위의 경직성**. BT의 셀렉터(Selector) 노드는 왼쪽부터 순서대로 실행 가능성을 체크한다. \[치료, 공격, 순찰\] 순서라면, 치료가 필요한 조건(예: 체력 \< 90%)이 만족되는 한, 절대 공격하지 않는다. 적이 코앞에 있어도 치료만 하려 드는 답답한 AI가 될 수 있다.4

## **3.3 목표 지향 행위 계획 (GOAP)**

* **개요:** \*F.E.A.R.\*에서 사용된 방식. 에이전트에게 목표(Goal)를 주고, 스스로 행동 계획(Plan)을 짜게 한다.  
* **장점:** 최고의 유연성. 개발자는 "무엇을 할 수 있는지(Action)"만 정의하면, AI가 "어떻게 할지"를 알아서 조합한다.  
* **모딩 관점:** 매력적이지만 연산 비용이 너무 높다. 유니티의 C\# 환경에서 실시간으로 A\* 계획 탐색을 수행하면 가비지 컬렉션(GC) 부하와 프레임 드랍을 유발하기 쉽다. 또한 월드 상태(World State)를 완벽하게 정의해야 하므로 구현 난이도가 높다.6

# ---

**4\. 최적의 솔루션: 하이브리드 아키텍처 (Hybrid Architecture)**

연구 결과, 현대 게임 AI의 정답은 단일 아키텍처가 아닌 **결합 모델**에 있다. 사용자의 스코어링 시스템(유틸리티)은 훌륭하지만, 구조적 제어가 부족하다. 행동 트리는 구조는 좋지만 판단력이 부족하다. 이 둘을 합친 \*\*유틸리티 기반 행동 트리(Utility-Driven Behavior Tree)\*\*가 바로 사용자가 찾는 "더 좋은 방법론"이다.2

## **4.1 유틸리티 셀렉터 (Utility Selector) 노드**

기존 행동 트리의 Selector 노드는 우선순위 기반(왼쪽 우선)이지만, 이를 \*\*Utility Selector\*\*로 대체한다.

* **작동 방식:**  
  1. 이 노드는 자식 노드들을 순서대로 실행하지 않는다.  
  2. 모든 자식 노드(하위 트리)에 부착된 \*\*Scorer(유틸리티 평가자)\*\*를 호출하여 점수를 계산한다.  
  3. 가장 높은 점수를 받은 자식 노드를 실행한다.

## **4.2 구조적 통합 예시**

이 아키텍처를 사용하면 다음과 같은 계층 구조가 만들어진다.

1. **최상위 (Brain):** 유틸리티 셀렉터  
   * 자식 1: **전투 (Combat)** \- 점수: f(적 거리) \* f(무기)  
   * 자식 2: **생존 (Survival)** \- 점수: f(체력) \* f(위협도)  
   * 자식 3: **대기 (Idle)** \- 점수: 기본값 0.1  
2. **하위 트리 (Execution):** 표준 행동 트리 시퀀스(Sequence)  
   * **전투 노드 내부:** \[적 탐색 \-\> 엄폐물 이동 \-\> 조준 \-\> 사격\]  
   * 이 부분은 점수가 아니라 순차적으로 실행된다.

**이점:**

* **판단의 유연성:** 유틸리티 시스템 덕분에 상황에 따라 유동적으로 전략을 바꾼다 (상위 레벨).  
* **행동의 안정성:** 행동 트리 덕분에 "재장전 중에 갑자기 순찰을 시작하는" 식의 튀는 행동(Jitter)을 방지하고, 행동의 순서를 보장한다 (하위 레벨).4

# ---

**5\. 유니티 모딩 실전 구현 가이드**

이론적인 아키텍처를 실제 유니티 모드(Mod)로 구현하기 위해서는 구체적인 기술적 전략이 필요하다.

## **5.1 Harmony를 이용한 코드 주입 전략**

유니티 모딩의 핵심은 **Harmony** 라이브러리이다. 이는 런타임에 C\# 메서드의 메모리 주소를 가로채어(Patch) 사용자의 코드를 실행하게 해준다.15

### **5.1.1 매니저의 진입점 확보**

모드 개발 시 개별 MonoBehaviour를 프리팹에 일일이 붙이는 것은 비효율적이며 호환성 문제를 일으킬 수 있다. 대신 **중앙 매니저(Centralized Manager)** 패턴을 사용한다.

* **초기화:** BepInEx 등의 로더를 통해 DLL이 로드될 때, new GameObject("MyAIManager")를 생성하고 DontDestroyOnLoad로 설정하여 영구적인 매니저를 만든다.  
* **Update 훅:** 게임의 메인 루프에 기생해야 한다. 예를 들어 원본 게임의 GameManager.Update() 메서드에 \[HarmonyPostfix\]를 걸어, 원본 업데이트가 끝난 직후 MyAIManager.Instance.Tick()이 호출되도록 한다.

### **5.2 성능 최적화: 타임 슬라이싱 (Time-Slicing)**

유틸리티 AI의 점수 계산은 비용이 많이 든다. 에이전트가 100명일 때, 100명 모두가 매 프레임(60 FPS) 수십 개의 반응 곡선을 계산하면 CPU 과부하가 걸린다. 이를 해결하기 위해 **타임 슬라이싱**을 적용해야 한다.19

**구현 로직:**

1. 모든 에이전트에게 고유 ID 혹은 인덱스를 부여한다.  
2. 프레임마다 업데이트할 에이전트의 그룹을 나눈다.  
   * 예: 에이전트를 10개 그룹으로 나누면, 0번 그룹은 0프레임에, 1번 그룹은 1프레임에 판단(Think) 로직을 수행한다.  
3. **결과:** 각 에이전트는 초당 6번(60FPS / 10)만 사고하지만, 플레이어는 이를 거의 눈치채지 못한다. 이동(Move)이나 애니메이션 처리는 매 프레임 부드럽게 업데이트하고, 무거운 판단 로직만 분산시키는 것이 핵심이다.

**\[표 2\] 최적화 전후 성능 비교 예시**

| 항목 | 매 프레임 업데이트 (Naive) | 타임 슬라이싱 적용 (Optimized) |
| :---- | :---- | :---- |
| **연산 빈도** | 60회/초 | 6\~10회/초 (조절 가능) |
| **CPU 부하** | 스파이크 발생 (Lag) | 균일하게 분산됨 (Smooth) |
| **반응 속도** | 즉각적 (0.016초) | 약간의 지연 (0.1초 내외) \- 게임플레이에 지장 없음 |

### **5.3 가비지 컬렉션(GC) 최소화**

유틸리티 계산 과정에서 List\<float\>나 임시 객체를 매번 new로 생성하면 힙 메모리 할당이 발생하여 GC 스파이크를 유발한다. 유니티 모딩에서는 \*\*Struct(구조체)\*\*를 적극 활용하고, 리스트는 미리 할당(Pre-allocate)해 둔 뒤 Clear()하여 재사용하는 **오브젝트 풀링(Object Pooling)** 기법을 적용해야 한다.19

# ---

**6\. 공간적 맥락의 통합: 영향력 맵 (Influence Maps)**

마지막으로, AI의 행동 선택을 더욱 지능적으로 만들기 위해 **영향력 맵**의 도입을 제안한다. 사용자의 스코어링 시스템이 "무엇을 할지"를 정한다면, 영향력 맵은 "어디서 할지"를 정하는 데이터를 제공한다.9

## **6.1 개념과 구현**

게임 월드를 격자(Grid)로 나누고, 각 칸에 전술적 점수(Influence)를 부여한다.

* 적 유닛 위치: 위협도(Threat) 점수 전파 (주변으로 퍼짐).  
* 엄폐물 위치: 방어(Defense) 점수 전파.

## **6.2 유틸리티 시스템과의 시너지**

유틸리티 AI의 고려 사항(Input)으로 영향력 맵의 값을 사용한다.

* 행동: **"이동"**  
* 고려 사항: **"위치 안전도"** (영향력 맵에서 해당 좌표의 위협도 값을 가져옴).  
* 결과: AI는 단순히 플레이어 반대편으로 도망가는 것이 아니라, 맵 전체의 전술적 상황을 고려하여 "가장 안전하면서도 유리한 위치"로 이동하게 된다. 이는 *길드워 2*의 켄타우로스 AI가 보여준 군집 전술의 핵심 기술이다.22

# ---

**7\. 결론**

사용자가 현재 사용 중인 스코어링 시스템(유틸리티 AI)은 결코 틀린 방법이 아니며, 오히려 현대적인 AI 아키텍처의 핵심 요소이다. 다만, 이를 단독으로 사용할 때 발생하는 구조적 불안정성을 해결하기 위해 **행동 트리와의 하이브리드 아키텍처**로 발전시키는 것이 바람직하다.

**최종 제언:**

1. **하이브리드 구조 채택:** 최상위 판단은 유틸리티 시스템(점수)으로, 세부 실행은 행동 트리(시퀀스)로 이원화하십시오.  
2. **수학적 모델 강화:** 점수 계산 시 로지스틱 곡선과 기하 평균을 사용하여 판단의 정교함을 높이십시오.  
3. **Harmony & 타임 슬라이싱:** 중앙 매니저를 통해 AI 연산을 프레임별로 분산시켜 원본 게임의 성능을 저하시키지 않도록 최적화하십시오.

이러한 접근은 유니티 모딩이라는 제약 환경 속에서도 상용 게임 수준, 혹은 그 이상의 깊이 있는 AI를 구현할 수 있는 가장 확실한 로드맵이 될 것이다.

#### **참고 자료**

1. AI (FSM, Behavior Tree, GOAP, Utility AI) | Nez framework documentation, 1월 29, 2026에 액세스, [https://anshuman-kumar.gitbook.io/nez-doc/ai-fsm-behavior-tree-goap-utility-ai](https://anshuman-kumar.gitbook.io/nez-doc/ai-fsm-behavior-tree-goap-utility-ai)  
2. Is there any benefit to using a Behavior Tree for AI design vs Unity's Visual Scripting State Machine? : r/gamedev \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gamedev/comments/13mzcug/is\_there\_any\_benefit\_to\_using\_a\_behavior\_tree\_for/](https://www.reddit.com/r/gamedev/comments/13mzcug/is_there_any_benefit_to_using_a_behavior_tree_for/)  
3. Build AI Behaviour Trees FAST in Unity with C\# \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=aR6wt5BlE-E](https://www.youtube.com/watch?v=aR6wt5BlE-E)  
4. Building Utility Decisions into Your Existing Behavior Tree \- Game AI Pro, 1월 29, 2026에 액세스, [http://www.gameaipro.com/GameAIPro/GameAIPro\_Chapter10\_Building\_Utility\_Decisions\_into\_Your\_Existing\_Behavior\_Tree.pdf](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter10_Building_Utility_Decisions_into_Your_Existing_Behavior_Tree.pdf)  
5. Game AI Planning: GOAP, Utility, and Behavior Trees \- Toño Game Consultants, 1월 29, 2026에 액세스, [https://tonogameconsultants.com/game-ai-planning/](https://tonogameconsultants.com/game-ai-planning/)  
6. AI Related: GOAP \+ Behavior Trees. Is it Viable? : r/gamedev \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gamedev/comments/z6af79/ai\_related\_goap\_behavior\_trees\_is\_it\_viable/](https://www.reddit.com/r/gamedev/comments/z6af79/ai_related_goap_behavior_trees_is_it_viable/)  
7. Is GOAP really that bad? : r/gameai \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gameai/comments/175adnc/is\_goap\_really\_that\_bad/](https://www.reddit.com/r/gameai/comments/175adnc/is_goap_really_that_bad/)  
8. Utility system \- Wikipedia, 1월 29, 2026에 액세스, [https://en.wikipedia.org/wiki/Utility\_system](https://en.wikipedia.org/wiki/Utility_system)  
9. Building a Better Centaur: AI at Massive Scale \- GDC Vault, 1월 29, 2026에 액세스, [https://www.gdcvault.com/play/1021848/building-a-better-centaur-ai](https://www.gdcvault.com/play/1021848/building-a-better-centaur-ai)  
10. Smarter Game AI with Infinite Axis Utility Systems \- Toño Game Consultants, 1월 29, 2026에 액세스, [https://tonogameconsultants.com/infinite-axis-utility-systems/](https://tonogameconsultants.com/infinite-axis-utility-systems/)  
11. Best approach to implement/design response curves for utility ai? : r/gameai \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gameai/comments/cnxzt9/best\_approach\_to\_implementdesign\_response\_curves/](https://www.reddit.com/r/gameai/comments/cnxzt9/best_approach_to_implementdesign_response_curves/)  
12. Choosing Effective Utility-Based Considerations \- Game AI Pro, 1월 29, 2026에 액세스, [http://www.gameaipro.com/GameAIPro3/GameAIPro3\_Chapter13\_Choosing\_Effective\_Utility-Based\_Considerations.pdf](http://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter13_Choosing_Effective_Utility-Based_Considerations.pdf)  
13. Utility AI \- Any merit to getting Axis/Consideration average with multiplications \+ offsets vs sum/divide all? : r/gameai \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gameai/comments/nmx4ko/utility\_ai\_any\_merit\_to\_getting\_axisconsideration/](https://www.reddit.com/r/gameai/comments/nmx4ko/utility_ai_any_merit_to_getting_axisconsideration/)  
14. Utility AI vs BT for enemies : r/gamedev \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gamedev/comments/196392u/utility\_ai\_vs\_bt\_for\_enemies/](https://www.reddit.com/r/gamedev/comments/196392u/utility_ai_vs_bt_for_enemies/)  
15. Introduction \- Harmony, 1월 29, 2026에 액세스, [https://harmony.pardeike.net/articles/intro.html](https://harmony.pardeike.net/articles/intro.html)  
16. pardeike/Harmony: A library for patching, replacing and decorating .NET and Mono methods during runtime \- GitHub, 1월 29, 2026에 액세스, [https://github.com/pardeike/Harmony](https://github.com/pardeike/Harmony)  
17. Prefix \- Patching \- Harmony, 1월 29, 2026에 액세스, [https://harmony.pardeike.net/articles/patching-prefix.html](https://harmony.pardeike.net/articles/patching-prefix.html)  
18. Harmony basics | Raft Modding, 1월 29, 2026에 액세스, [https://api.raftmodding.com/modding-tutorials/harmony-basics](https://api.raftmodding.com/modding-tutorials/harmony-basics)  
19. Advanced programming and code architecture \- Unity, 1월 29, 2026에 액세스, [https://unity.com/how-to/advanced-programming-and-code-architecture](https://unity.com/how-to/advanced-programming-and-code-architecture)  
20. Unity Performance: CPU Slicing Secrets | TheGamedev.Guru, 1월 29, 2026에 액세스, [https://thegamedev.guru/unity-performance/cpu-slicing-secrets/](https://thegamedev.guru/unity-performance/cpu-slicing-secrets/)  
21. 013\. CPU Slicing in Unity \- Smooth Spikes, Hit Your Frame Budget \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=4\_LH4IaJd0s](https://www.youtube.com/watch?v=4_LH4IaJd0s)  
22. GDC AI Summit Video \-- Building a Better Centaur: AI at Massive Scale : r/gamedev \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gamedev/comments/394fu0/gdc\_ai\_summit\_video\_building\_a\_better\_centaur\_ai/](https://www.reddit.com/r/gamedev/comments/394fu0/gdc_ai_summit_video_building_a_better_centaur_ai/)  
23. Modular Tactical Influence Maps \- Game AI Pro, 1월 29, 2026에 액세스, [https://www.gameaipro.com/GameAIPro2/GameAIPro2\_Chapter30\_Modular\_Tactical\_Influence\_Maps.pdf](https://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter30_Modular_Tactical_Influence_Maps.pdf)  
24. Using Dave Mark's "Imap" Influence Map Architecture with ranged units and line of sight? : r/gameai \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gameai/comments/nn7n71/using\_dave\_marks\_imap\_influence\_map\_architecture/](https://www.reddit.com/r/gameai/comments/nn7n71/using_dave_marks_imap_influence_map_architecture/)  
25. Intrinsic Algorithm Game Techs \- IAUS, 1월 29, 2026에 액세스, [https://www.gameai.com/iaus.php](https://www.gameai.com/iaus.php)  
26. Help understanding how to combine scorers in Utility AI : r/gameai \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gameai/comments/qbuuob/help\_understanding\_how\_to\_combine\_scorers\_in/](https://www.reddit.com/r/gameai/comments/qbuuob/help_understanding_how_to_combine_scorers_in/)  
27. Thoughts on hybrid AI architectures like GOBT (BT \+ GOAP \+ Utility)? : r/gamedev \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gamedev/comments/1kr47ap/thoughts\_on\_hybrid\_ai\_architectures\_like\_gobt\_bt/](https://www.reddit.com/r/gamedev/comments/1kr47ap/thoughts_on_hybrid_ai_architectures_like_gobt_bt/)

[image1]: <data:image/png;base64,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>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAsAAAAaCAYAAABhJqYYAAAAsUlEQVR4XmNgGAUjEDACcRYQXwTip0D8GogTgPgtEP8E4ilwlUCQAcQHoWxhID7HANGowADR/BwqBwYgnSDTQUAGiJ8AsQ1U7BMQZ0LlwIAHie0CxP+AWBxJDCtgAeI1QPwfXQIbkAbiB0D8G0kMZKsAjANy1x8GiLthTgC5GQZA4ukwDshtd4DYCohvAfEXIL4OlVMB4mAoGwxAJv9igJgWBMQmDJBwBvFfQuVHAQYAADDnIRrY29qmAAAAAElFTkSuQmCC>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABEAAAAZCAYAAADXPsWXAAABD0lEQVR4Xu2SoYpCQRSGj6ggKBgWFNTirkVYMFgNIrLsFoMYfIR9A2Gr+BxGi1UsBo2aLGI1WMVkWtb1/+/M6LnXbBDuB1+4/zlz7swwIiEhz0gSfsC4/Y7CN1iHCZsR1juwrDKPCOyLWfgPp7Bh8094hiu4gAW7ZgCHtscjD9/F/JFDjq4AqvBk87TK23AOUy74sh8cxuZvVwBNMTvhIM0PnIj/qB49OBNzPw42cjC37yjCvZgf+IjBsfibyU7MkJbKujbLqsyjBn/hayBn81zU2cFBzBEJL/5a41G4QDe7iw7ujtlGzO5HurCEfzoAFbiV+23zCbB3DUu6kJPbG3DwDbwEMsL3lJHbwwx5FBeALS0iflou3AAAAABJRU5ErkJggg==>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAAaCAYAAACKER0bAAAAdUlEQVR4XmNgGAUDBLqB+BcQ7wDiB0DsiSypCcRfgdgPyvcG4hMwyV1A/B+IWaB8YSA+DcRBMAX/oBgrEGeA6F6DLgEDkgwQBVXoEjAAMwFuHxJQgDHeA/EthDiDFRCfAmIJmIA9EN8B4idA/AjKDoNJDn0AAI0UFqOCK5mpAAAAAElFTkSuQmCC>

[image5]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAsAAAAZCAYAAADnstS2AAAA4UlEQVR4XuWRrQpCQRCFR1QQDGI0GEQUTL6GFhGr0SJGQfQFTLeZBfEZrAajYNVoNojR5O+ZnRXmjvsG98DH3Zk5d5idJUqQcqAGuqBtan9ago9na2pBnUjMHVsI6Q5uoGELVimSrgtbCKlIYu6rXB7USRrFxKYDyU9psAJlkgZT5XPibbCBjROQJVkhm8fK57px1xGYgcjnm2AHSj526pF0ePrvkQJz/jQnMWVAC1zBMOZQuoCHivmy/Iq8jQ2ZR3qDs4r59mt/3oOKqrkRBiougBfJOHyOqUqyMpvjB0mOvlI+KKSC1UnrAAAAAElFTkSuQmCC>

[image6]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAAaCAYAAACKER0bAAAAtElEQVR4XmNgGE7gNBD/h+KtaHJgIAzEGgwQBeVocnDgB8QPgFgaTRwOWhkgxnOgS4AADxAfYCBgPMj+VUD8DYjvAPFHZAUg40EKlgIxHwPE0aeA2BCm4DkQ/4NxoGAhEO+BcUC6nyDkGLgZIJIHQByQq0EKqpAUuDBATAQ7GuQDkAJPJAUwN+nAFHwFYmMkBSegChhhAn+BeBZUAORlUNzIwCRBIAuIfwHxKyD+wwBx5DACAHoSJwViE1UVAAAAAElFTkSuQmCC>

[image7]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACIAAAAZCAYAAABU+vysAAABoUlEQVR4Xu2UzSuFQRTGj1CKkiiFCCtRFsJGkiQ2VhbKP2Bhb6ukLP0HFlbKTlJSPlZkY2drQRaSKBvfz3PPzH3PPffi1uWu3l89i3nemTNnzpx3RFJSUlJS/odaaBKqDuNKqBsag2qCR3qh2fC9FBhzSDRe3FMqoBXR4J/QHjQe/CnoA1qDjuOCMG/DjIuFce+gwTBehN6hKg5aoT7RLLnBQ5hEBqBn6ACqNz7nHZlxsTC2jf8kGiuTyDRUJ5oQzYVknkyIVmTYeITzdp33G22i65aNdyNaoRyWRE/OfolwMy62dIomxySLpQE6F60iD/0tLM02tOr8K8lPZA66hJqd/xP9otfg4+cxAr1CXc73vdAD3UtSjX1JTsju54Zsck9MJDapxd5A5lq4qS1bbF57ivngNYlWkV1PuPl6+MaKedjop9CM85nEpoRmJWeiv5GFp/BXwID8vW+hC+MTNv0bdOj8CJN9hF6ga9G5o8HP0iLa1RZOaHQe4XvD5LIPkYGl3/GmgWvaoQ7JfSj/nC0pfDVl50QKV7Gs5HR/Sql8AZ+LR8NmqJhjAAAAAElFTkSuQmCC>

[image8]: <data:image/png;base64,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>

[image9]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAYCAYAAADOMhxqAAAAuElEQVR4XmNgGAUjC1wA4mNAvAeI+YH4KhA/AOJfQPwRoQwCGIG4GYhZgfg/EG8HYieouAcQ/wNiYbhqIHABYh4glmaAaMhAkwNpkEQSY+CA0jZA/ByIlZDkyhkghnAjiYEBCxCvAeJWNPH3QPwTTQwMQM55AMR+aOIg008wQPwjhCxRBJUE+QUG9IH4KxAbA3EOEC9HkgM7B6QBGWgC8UMGiIdvAbEZsqQUEMsgC0CBAANaCI1IAADzbh48IcrNawAAAABJRU5ErkJggg==>

[image10]: <data:image/png;base64,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>

[image11]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAaCAYAAABVX2cEAAABIElEQVR4Xu2UsWoCURBFr0SLQNBCO5tgETstrLQSIWCVOkU+IYVprCwEf0Hs/AA7e0G2s7VQBGMpCPmEWJg7jAvPUXc3miKEHDgIM+P1OfoW+OfvU6ATuqMz2qce/aQ3tElz/nAQSWiIvDFtehU6hvbvTO+IJXQwZRsOa+hMIBIgQxIYhEc3tugSox1o2IvpWby9Z+lCg15t4wRVmrVFF38PkX6hMCQodKlRiRomu02Y2ht9cAtRw2SncVOrwnzAFuFh93Rqi6fwoGH2H+8ygl4jH7lWpf3rAbIL+QoS2HPqMvhI3+mtUxda0Ks3wJnAFTTwgw6h93NBa86cT5GW6bNtXEKGzqGHuJo69OR52j5sfZ8n6M1p4IdOF/pM+x18ATLaNZF3YT+sAAAAAElFTkSuQmCC>

[image12]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABIAAAAYCAYAAAD3Va0xAAABCklEQVR4Xu2SMWpCQRCGR7RQEGxEEQXB2iNYpsgFAuksPYKQKofwADZiYyuEVJZeQFIkVYhpU6VV///Nru4O62tFeB984Jt5zs7MW5GCm1GFncBynM6eWyaWpC5aYA+PcAsbQd4X+oX/sC/6n6t8ihY5wEeTIxs4tUFLRfQldsKu/uJ0xgds26ClCR/cbxailpXogblM5PLSq2ih53NWpAdHwXMSFuBpnqHoaG+w5mLsll3nwpN2JsZls6uZ6PVYx+k0HCvsiPil/8AB/IrTafg1/KJDWITF5nARp9JwrK4Nio7FQrxX7DoXLvPFBh38CEvRg64umtf+Cb7Dbzh2MQuXzrFKNlFwr5wACyYqq+CtpAsAAAAASUVORK5CYII=>

[image13]: <data:image/png;base64,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>