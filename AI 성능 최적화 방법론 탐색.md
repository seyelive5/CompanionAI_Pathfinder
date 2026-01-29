# **유니티 기반 AI 모딩 및 실시간 연산 최적화를 위한 아키텍처 연구 보고서**

## **1\. 서론: 실시간 AI 연산과 성능의 역설**

현대 게임 개발, 특히 유니티(Unity) 엔진 환경에서의 인공지능(AI) 구현은 본질적인 '성능의 역설'에 직면해 있습니다. 사용자와 플레이어는 점점 더 정교하고 반응성 높은 AI를 요구하며, 이는 필연적으로 매 프레임(Tick)마다 복잡한 의사결정 트리를 순회하고, 물리 환경을 감지(Sensing)하며, 경로를 탐색하는 고비용 연산을 수반합니다. 그러나 하드웨어 자원은 유한하며, AI 서브시스템은 렌더링, 물리 시뮬레이션, 게임플레이 로직과 한정된 프레임 타임(Frame Time, 60FPS 기준 약 16.6ms)을 공유해야 합니다.

귀하께서 제기하신 문제는 "유니티 매니저로 관리되는 모드(Mod) 개발 환경"에서 "실시간으로 AI의 행동을 틱마다 계산"할 때 발생하는 성능 병목입니다. 이는 대규모 시뮬레이션이나 오픈 월드 게임, 그리고 기존 게임의 구조 위에 새로운 로직을 덧입히는 모딩(Modding) 환경에서 가장 빈번하게 발생하는 기술적 난제입니다. 특히 모딩 환경은 기존 게임의 최적화되지 않은 레거시 코드와 상호작용해야 하거나, 엔진의 소스 코드에 직접 접근할 수 없는 제약이 존재하므로, 더욱 고도화된 아키텍처 접근이 필요합니다.

본 보고서는 단순한 코드 레벨의 수정을 넘어, **아키텍처 관점에서의 최적화 방법론**을 심층적으로 다룹니다. 특히 귀하께서 언급하신 '캐싱(Caching)'의 개념을 메모이제이션(Memoization), 더티 플래그(Dirty Flag), 공간 분할(Spatial Partitioning) 등으로 확장하여 분석하고, 시분할(Time Slicing) 및 데이터 지향 설계(Data-Oriented Design)와 결합하여 CPU 부하를 획기적으로 줄이는 방안을 제시합니다. 이 분석은 유니티 엔진의 내부 작동 원리인 인터롭(Interop) 오버헤드, 가비지 컬렉션(GC), 메모리 계층 구조(Memory Hierarchy)에 대한 이해를 바탕으로 합니다.

## **2\. 유니티 라이프사이클과 성능 병목의 해부**

최적화 전략을 수립하기에 앞서, Update() 루프에 의존하는 기존 AI 구현이 왜 성능 저하를 일으키는지에 대한 근본적인 원인 분석이 선행되어야 합니다. 많은 개발자들이 "로직이 복잡해서" 느리다고 판단하지만, 실제 프로파일링 결과는 엔진 구조적 문제에서 기인하는 경우가 많습니다.

### **2.1 네이티브-매니지드 인터롭(Interop)의 숨겨진 비용**

유니티 엔진의 코어는 고성능 C++로 작성되어 있으며, 스크립팅 레이어는 C\#으로 구동됩니다. MonoBehaviour를 상속받은 개별 AI 객체가 Update() 메서드를 가질 때, 엔진은 매 프레임마다 C++ 영역에서 C\# 영역으로 제어권을 넘기는 '컨텍스트 스위칭'을 수행합니다. 이를 인터롭(Interop) 또는 마샬링(Marshalling)이라고 합니다.1

만약 1,000명의 AI 에이전트가 각각 자신의 Update() 함수를 가지고 있다면, 유니티는 매 프레임 1,000번의 인터롭 호출을 발생시킵니다. 설령 Update() 함수 내부가 비어 있다 하더라도, 이 호출 자체만으로도 상당한 CPU 사이클을 소모합니다. 이는 특히 모드 개발 시, 다수의 객체를 생성하여 제어할 때 프레임 드랍의 주범이 됩니다.

**아키텍처적 해결책: 중앙 집중형 매니저 패턴 (Centralized Manager Pattern)** 귀하께서 이미 "유니티 매니저"를 사용하고 계신다는 점은 매우 긍정적인 출발점입니다. 최적화의 첫 단계는 개별 MonoBehaviour의 Update()를 제거하고, 하나의 중앙 매니저가 일반 C\# 클래스나 구조체(Struct)로 이루어진 에이전트 리스트를 순회하며 로직을 실행하는 것입니다.1 이렇게 하면 인터롭 비용은 1,000회에서 1회(매니저의 Update)로 줄어듭니다.

### **2.2 메모리 레이아웃과 캐시 미스 (Cache Miss)**

최신 CPU의 성능은 연산 속도보다 메모리 접근 속도에 의해 결정되는 경향이 큽니다. CPU는 메인 메모리(RAM)에서 데이터를 가져올 때, 인접한 데이터를 캐시 라인(Cache Line, 보통 64바이트) 단위로 미리 가져오는(Prefetching) 기능을 수행합니다.

그러나 전통적인 객체 지향 프로그래밍(OOP) 방식, 즉 List\<AgentClass\> 형태의 관리는 메모리 상에 흩어져 있는 객체들의 참조(Reference)를 저장합니다. 매니저가 리스트를 순회할 때마다 CPU는 힙(Heap) 메모리의 무작위 위치를 참조해야 하므로, 캐시 적중률(Cache Hit Rate)이 급격히 떨어집니다.3 이는 틱마다 계산되는 AI 로직의 실행 속도를 수십 배 느리게 만듭니다.

**최적화 방향:** 데이터 지향 설계(DOD)를 도입하여, AI의 핵심 데이터(위치, 체력, 상태 등)를 클래스가 아닌 struct 배열로 관리함으로써 메모리 연속성을 보장해야 합니다.4

## ---

**3\. 시간적 최적화(Temporal Optimization): 시분할과 부하 분산**

귀하의 질의에서 언급된 "틱마다 계산"이라는 요구사항은 반드시 재고되어야 합니다. 인간의 반응 속도는 약 0.2초이며, 게임 내 AI가 매 프레임(0.016초)마다 의사결정을 내릴 필요는 없습니다. 시간적 최적화는 AI의 연산을 여러 프레임에 걸쳐 분산시키는 기법입니다.

### **3.1 인터벌 기반 스로틀링 (Interval-Based Throttling)**

가장 기초적인 접근은 모듈로 연산(%)을 사용하여 특정 프레임에만 로직을 실행하는 것입니다.

C\#

if (Time.frameCount % interval \== 0) { RunLogic(); }

하지만 이 방식은 모든 에이전트가 동일한 프레임에 동시에 연산을 수행하게 하여, 간헐적인 '프레임 스파이크(Spike)'를 유발합니다.1

**위상차(Phase Staggering) 기법:**

이를 해결하기 위해 각 에이전트에게 고유한 오프셋(Offset)을 부여하여 연산 시점을 분산시켜야 합니다.

![][image1]  
예를 들어 Interval이 10이라면, 에이전트 ID에 따라 매 프레임 전체 에이전트의 10%씩만 로직을 수행하게 됩니다. 이는 CPU 부하를 평탄화(Flattening)하여 안정적인 프레임레이트를 보장합니다.6

### **3.2 동적 예산 할당 (Dynamic Time Budgeting) 매니저**

고정된 인터벌은 게임의 상황 변화(전투 중 vs 대기 중)에 유연하게 대처하지 못합니다. 더 진보된 방식은 매니저에게 '시간 예산(Time Budget)'을 부여하는 것입니다.

**구현 로직:**

1. 매니저는 매 프레임 AI 처리에 사용할 수 있는 시간(예: 2ms)을 할당받습니다.  
2. Stopwatch를 사용하여 에이전트 로직을 순차적으로 실행합니다.  
3. 할당된 시간이 소진되면, 현재 처리 중인 에이전트의 인덱스를 저장하고 루프를 중단(Break)합니다.  
4. 다음 프레임에 해당 인덱스부터 처리를 재개합니다.

이 방식은 렌더링 부하가 높은 상황에서는 AI 연산량을 줄여 프레임 방어를 우선시하고, 여유가 있을 때는 더 자주 업데이트하는 자동 조절 기능을 제공합니다.6 이는 모딩 환경과 같이 사용자의 PC 사양을 예측할 수 없는 경우에 필수적인 안정 장치입니다.

### **3.3 코루틴 매니저 (Coroutine Manager) 패턴**

유니티의 네이티브 코루틴(StartCoroutine)은 각각이 객체로 생성되어 가비지 컬렉션(GC) 부담을 주며, 수천 개의 코루틴을 동시에 실행하는 것은 비효율적입니다.7

대신, 중앙 매니저가 단일 루프 내에서 에이전트들의 상태 머신(State Machine)을 관리하는 '커스텀 코루틴' 형태를 구현해야 합니다. 에이전트의 Tick() 함수가 작업 완료 여부를 반환하고, 매니저는 완료되지 않은 에이전트만 다음 프레임에 다시 호출하는 방식입니다. 이는 컨텍스트 스위칭 비용 없이 비동기적 동작을 시뮬레이션할 수 있는 강력한 패턴입니다.

## ---

**4\. 논리적 캐싱(Logical Caching)과 메모이제이션**

귀하께서 직접적으로 문의하신 '캐싱'은 AI 아키텍처에서 \*\*메모이제이션(Memoization)\*\*과 **더티 플래그(Dirty Flag)** 패턴으로 구체화될 수 있습니다. 이는 "이미 계산된 답이 유효하다면, 다시 계산하지 않는다"는 원칙에 기반합니다.

### **4.1 센서 데이터의 시한부 캐싱 (Timed Memoization)**

AI 성능 저하의 가장 큰 원인은 Physics.Raycast, OverlapSphere와 같은 물리 엔진 쿼리입니다. 적을 탐지하기 위해 매 틱마다 레이캐스트를 쏘는 것은 자원 낭비입니다.9

**방법론:**

센서 데이터에 유효시간(Expiration Time)을 부여합니다.

1. 에이전트가 주변 환경을 스캔합니다.  
2. 결과(감지된 적 리스트)를 저장하고, NextScanTime \= Time.time \+ 0.5f로 설정합니다.  
3. 다음 틱부터 0.5초가 지날 때까지는 물리 연산 없이 저장된 리스트를 그대로 반환합니다.

이 기법은 실제 게임플레이에서 플레이어가 눈치채지 못할 정도의 미세한 지연(Latency)만을 허용하면서, 연산 비용을 수십 배 절감합니다.10 특히 센서의 종류에 따라 주기를 다르게 설정(시각 0.2초, 청각 0.5초, 후각 2.0초)하여 리얼리즘과 성능의 균형을 맞출 수 있습니다.13

### **4.2 더티 플래그 (Dirty Flag) 패턴**

상태가 변하지 않았음에도 값을 재계산하는 것을 방지하기 위해 '더티 플래그'를 사용합니다.14 이는 이벤트 기반 아키텍처와 밀접한 관련이 있습니다.

**적용 사례: 경로 탐색 (Pathfinding)**

에이전트의 목표 지점이 변경되지 않았다면, 경로를 다시 계산할 필요가 없습니다.

* **Target:** 목표물이 이동할 때만 HasMoved 이벤트를 발생시킵니다.  
* **AI:** HasMoved 플래그가 true일 때만 NavMeshAgent.SetDestination을 호출하거나 A\* 알고리즘을 수행합니다. 그 외에는 기존 경로를 따릅니다.  
* 이는 매 프레임 "목표가 움직였나?"를 검사하는 폴링(Polling) 방식을 "움직였을 때만 반응"하는 리액티브(Reactive) 방식으로 전환하는 것입니다.16

### **4.3 블랙보드(Blackboard) 캐싱**

행동 트리(Behavior Tree)나 GOAP(Goal-Oriented Action Planning) 시스템에서 에이전트들은 '블랙보드'라는 공유 저장소를 통해 데이터를 주고받습니다. 만약 블랙보드의 특정 키(예: "PlayerPosition")를 읽을 때마다 GameObject.Find나 Transform.position을 호출한다면, 수많은 에이전트가 동일한 API를 중복 호출하게 됩니다.

**최적화:** 블랙보드 시스템 자체에 캐싱 레이어를 둡니다. 매니저가 프레임 시작 시 중요한 데이터(플레이어 위치, 세계 상태 등)를 한 번 읽어 블랙보드에 씁니다. 이후 수천 명의 에이전트는 엔진 API가 아닌, 메모리에 캐싱된 블랙보드 값을 읽습니다. 이는 API 호출 오버헤드를 획기적으로 줄여줍니다.18

## ---

**5\. 공간적 최적화(Spatial Optimization): 필요 없는 연산 버리기**

"보이지 않는 것은 계산하지 않는다"는 그래픽스의 원칙은 AI 로직에도 동일하게 적용됩니다. 이를 AI LOD (Level of Detail)라고 합니다.

### **5.1 CullingGroup API 활용**

유니티는 CullingGroup이라는 강력한 API를 제공합니다.20 이 API는 카메라의 시야(Frustum)와 객체의 거리(Distance Band)를 기준으로 이벤트를 발생시킵니다.

**AI LOD 단계 설정:**

* **Band 0 (근거리/화면 내):** 틱마다 업데이트, 정교한 애니메이션, 레이캐스트 사용.  
* **Band 1 (중거리):** 10프레임마다 업데이트, 간소화된 물리 연산.  
* **Band 2 (원거리):** 60프레임마다 업데이트, 물리 연산 중지, 논리적 위치만 갱신.  
* **Invisible (화면 밖):** 로직 일시 정지(Suspend) 또는 추상화된 시뮬레이션으로 전환.

이 API를 활용하면 플레이어의 주의가 집중되지 않는 곳의 AI 연산 비용을 거의 0에 가깝게 줄일 수 있습니다. 특히 모딩 환경에서는 기존 게임의 최적화 수준을 넘어설 수 있는 핵심 기법입니다.

### **5.2 공간 해싱 (Spatial Hashing)**

에이전트들이 서로를 피하거나(Local Avoidance) 무리 지어 다닐 때(Flocking), 주변 이웃을 찾는 연산은 $O(N^2)$의 복잡도를 가집니다. 1,000명의 에이전트가 서로를 확인하면 1,000,000번의 거리 계산이 필요합니다.

**그리드 기반 캐싱:** 공간을 격자(Grid)로 나누고, 각 에이전트가 자신이 속한 셀(Cell)을 알게 합니다.23

* 에이전트는 자신의 셀과 인접한 8개의 셀에 있는 이웃만 검색합니다.  
* 이웃 목록은 에이전트가 셀을 이동할 때만 갱신(캐싱)됩니다. 이 기법은 탐색 범위를 전역에서 국소 영역으로 좁혀 연산 비용을 ![][image2] 수준으로 낮춥니다.25

## ---

**6\. 병렬 처리: 유니티 잡 시스템(Job System)과 버스트(Burst) 컴파일러**

C\# 스크립트 최적화만으로 한계가 있다면, 하드웨어의 모든 코어를 활용하는 병렬 처리를 도입해야 합니다. 유니티의 잡 시스템과 버스트 컴파일러는 이를 위한 표준 솔루션입니다.

### **6.1 잡 시스템을 통한 로직 병렬화**

기존 Update()는 메인 스레드 하나에서만 실행됩니다. 잡 시스템을 사용하면 워커 스레드(Worker Thread)들로 작업을 분산시킬 수 있습니다.26

* **적용 대상:** 시야각 계산(Field of View), 조향력(Steering Force) 계산, 대량의 레이캐스트 명령(RaycastCommand) 등 수학적 연산이 많은 부분.  
* **주의점:** 잡 시스템 내에서는 GameObject나 Transform 같은 참조 타입에 접근할 수 없습니다. 따라서 매니저가 데이터를 NativeArray(값 타입 배열)로 추출하여 잡에 전달하고, 잡이 완료되면 결과를 다시 객체에 반영하는 과정이 필요합니다.28

### **6.2 버스트 컴파일러의 위력**

잡 시스템으로 작성된 코드는 버스트 컴파일러를 통해 고도로 최적화된 기계어(SIMD 명령어 포함)로 변환됩니다. 이는 일반 C\# 코드(Mono/IL2CPP)보다 10배에서 100배 빠른 성능을 보여줍니다.29 특히 AI의 감지 로직이나 경로 탐색 후처리(Smoothing)와 같은 무거운 연산을 버스트 잡으로 처리하면, 메인 스레드의 부담을 획기적으로 덜어낼 수 있습니다.

**데이터 흐름:**

1. **프레임 시작:** 매니저가 에이전트들의 위치/방향을 NativeArray에 복사.  
2. **잡 스케줄링:** 시야 판단 및 이동 벡터 계산 잡을 스케줄링.  
3. **병렬 실행:** 메인 스레드가 렌더링 준비를 하는 동안, 워커 스레드에서 AI 로직 수행.  
4. **프레임 종료(LateUpdate):** 잡 완료 대기(Complete) 후 결과 값을 에이전트 Transform에 반영.

## ---

**7\. 비교 분석 및 구현 로드맵**

다음은 제안된 최적화 기법들의 특성과 도입 난이도, 예상되는 성능 이득을 비교한 표입니다.

| 최적화 기법 | 구현 난이도 | 성능 개선 효과 | 적용 권장 상황 |
| :---- | :---- | :---- | :---- |
| **중앙 매니저 패턴** | 낮음 | 높음 (인터롭 감소) | 다수의 AI 객체를 관리하는 기본 구조 |
| **시분할 (인터벌)** | 낮음 | 중간 | 에이전트 반응 속도가 중요하지 않을 때 |
| **시한부 메모이제이션** | 중간 | 높음 (물리 연산 감소) | 레이캐스트 등 센서 비용이 높을 때 |
| **CullingGroup (LOD)** | 중간 | 매우 높음 | 오픈 월드, 대규모 군중 시뮬레이션 |
| **공간 해싱** | 높음 | 높음 (![][image3] 문제 해결) | 에이전트 간 상호작용이 많을 때 |
| **Job System & Burst** | 매우 높음 | 극도로 높음 (10x 이상) | 수학적 연산량이 CPU 한계를 초과할 때 |

### **모드 개발자를 위한 단계별 적용 가이드**

귀하의 상황(모드 개발)에 맞춰, 현실적인 적용 순서를 제안합니다.

1. **1단계: 구조 변경 (Manager Pattern)**  
   * 개별 스크립트의 Update를 비우고, 매니저가 리스트를 순회하며 호출하도록 변경하십시오.  
   * 이때 List 대신 가능한 Array를 사용하고, 데이터 접근을 최적화하십시오.  
2. **2단계: 시분할 도입 (Time Slicing)**  
   * 매니저 루프 내에 '예산(Budget)' 개념을 도입하십시오. 프레임당 처리할 에이전트 수를 제한하거나 시간을 체크하여 루프를 조기 종료하십시오.  
3. **3단계: 결과 캐싱 (Caching Results)**  
   * 가장 무거운 연산(주로 적 탐색 로직)을 식별하고, 해당 함수의 결과값을 0.2\~0.5초 동안 캐싱하도록 수정하십시오. 딕셔너리나 구조체 필드를 활용해 마지막 계산 시간과 결과를 저장하면 됩니다.  
4. **4단계: 거리 기반 최적화 (Distance Culling)**  
   * 플레이어와의 거리를 제곱 거리(sqrMagnitude, 루트 연산 회피)로 계산하여, 일정 거리 밖의 AI는 업데이트 빈도를 1/10로 줄이거나 로직을 정지시키십시오.

## **8\. 결론**

"실시간으로 틱마다 계산되는 AI"의 성능 문제는 단순히 코드를 빠르게 만드는 것이 아니라, **불필요한 계산을 영리하게 생략하고 지연시키는 아키텍처**로 해결해야 합니다.

중앙 집중형 매니저를 통해 실행 제어권을 확보하고, 시분할을 통해 부하를 분산시키며, 메모이제이션을 통해 중복 연산을 제거하고, 공간 분할을 통해 관심 밖의 객체를 무시하는 전략을 단계적으로 적용하신다면, 유니티 엔진의 한계 내에서 수천 명의 에이전트가 활동하는 고성능 AI 시스템을 구축하실 수 있을 것입니다.

이 보고서가 귀하의 모드 개발 프로젝트에 실질적인 해결책이 되기를 바랍니다.

---

*(Note: The following sections provide the in-depth technical elaboration required to meet the comprehensive length and depth requirements of the report, expanding on the core concepts summarized above.)*

## **9\. 심층 분석: 유니티 메모리 아키텍처와 가비지 컬렉션(GC) 전략**

성능 최적화에서 CPU 연산만큼 중요한 것이 메모리 관리입니다. 특히 C\#과 같은 매니지드 언어 환경에서는 가비지 컬렉션(GC)으로 인한 프레임 드랍(Hiccup)이 치명적일 수 있습니다. AI 로직은 본질적으로 많은 임시 변수와 객체를 생성하는 경향이 있어 GC의 주요 원인이 됩니다.

### **9.1 힙(Heap) vs 스택(Stack) 할당과 AI 데이터 구조**

AI 에이전트의 상태 정보를 클래스(class)로 설계하면 모든 객체는 힙 메모리에 할당됩니다. 힙에 할당된 메모리는 GC의 관리 대상이 되며, 할당과 해제 시 오버헤드가 발생합니다. 반면, 구조체(struct)는 값 타입(Value Type)으로, 스택에 할당되거나 다른 객체의 내부에 인라인(Inline)으로 포함되어 GC 부담이 없습니다.4

**권장 패턴:**

AI의 핵심 데이터(Position, Velocity, TargetID, StateEnum 등)를 'Blittable'한 구조체로 정의하고, 이를 NativeArray나 미리 할당된 배열(Pre-allocated Array)로 관리하십시오. 이는 메모리 파편화를 줄이고 CPU 캐시 효율을 극대화합니다.

### **9.2 임시 할당 회피 (Allocation-Free Coding)**

AI 스크립트 작성 시 흔히 범하는 실수는 루프 내부에서 임시 객체를 생성하는 것입니다.

* **나쁜 예:** var neighbors \= Physics.OverlapSphere(pos, range);  
  * 이 코드는 호출될 때마다 새로운 배열을 힙에 할당합니다.  
* **좋은 예:** int count \= Physics.OverlapSphereNonAlloc(pos, range, resultsBuffer);  
  * NonAlloc 버전의 API를 사용하고, resultsBuffer 배열을 미리 할당해두고 재사용하십시오.9

이러한 'Zero-Allocation' 원칙은 틱마다 실행되는 AI 로직에서 필수적입니다. 수백 개의 에이전트가 매 프레임 조금씩 가비지를 생성하면, 몇 초마다 GC가 발동하여 게임이 멈칫거리는 현상이 발생합니다.

## **10\. 경로 탐색(Pathfinding) 시스템의 아키텍처적 최적화**

유니티의 내장 내비게이션(NavMesh) 시스템은 강력하지만, 대규모 유닛을 제어할 때는 여전히 병목이 될 수 있습니다. 특히 SetDestination 호출은 경로 계산 요청을 발생시키며, 이는 비동기적으로 처리되지만 요청이 폭주하면 대기열(Queue)이 밀리게 됩니다.

### **10.1 경로 요청 스로틀링과 캐싱**

**경로 캐싱 (Path Caching):**

많은 AI 에이전트가 비슷한 위치에서 비슷한 목적지로 이동하는 경우(예: 스폰 지점에서 기지로 이동), 경로를 공유할 수 있습니다.

* **플로우 필드(Flow Field) / 벡터 필드:** 개별 경로를 계산하는 대신, 목표 지점으로부터 맵 전체의 '이동 방향 벡터'를 미리 계산해두는 방식입니다. 모든 에이전트는 단순히 해당 타일의 벡터를 따라가기만 하면 되므로 경로 탐색 비용이 $O(1)$이 됩니다.32  
* **요청 큐 관리:** 매니저가 '경로 계산 요청 큐'를 관리하여, 한 프레임에 처리할 경로 계산 횟수를 제한합니다. 우선순위가 높은 에이전트(화면에 보임, 플레이어 추격 중)의 요청을 먼저 처리합니다.

### **10.2 내비게이션 메쉬 쿼리 최적화**

NavMeshAgent 컴포넌트 자체가 무겁다면, 이를 사용하지 않고 NavMesh.CalculatePath API만 사용하여 경로 데이터(Vector3 배열)만 얻어낸 뒤, 이동 로직은 가벼운 커스텀 코드로 처리하는 방법도 있습니다. 이는 에이전트가 물리적인 충돌이나 복잡한 회피 기동이 필요 없는 경우(예: 배경의 군중) 매우 효율적입니다.33

## **11\. 모드(Mod) 개발 환경에서의 특수 고려사항**

모드 개발은 원본 게임의 소스 코드를 수정할 수 없는 경우가 많아 제약이 큽니다. 이러한 환경에서 성능을 확보하기 위한 '기생적 최적화(Parasitic Optimization)' 전략이 필요합니다.

### **11.1 섀도우 매니저 (Shadow Manager)**

원본 게임의 객체들은 그대로 두고, 모드 내에서 별도의 '경량화된 데이터 모델'을 유지하는 방식입니다.

1. **Read Phase:** 원본 게임 객체들의 상태(위치, 체력)를 읽어와 모드의 내부 구조체 배열에 복사합니다. (이 과정은 최소한으로 수행)  
2. **Simulation Phase:** 모드의 AI 로직은 이 복사된 데이터를 바탕으로 고속으로 연산을 수행합니다.  
3. **Write Phase:** 결정된 행동(이동 명령, 공격 함수 호출)만 원본 게임 객체에 전달합니다.

이 방식은 원본 게임의 비효율적인 구조에 모드 로직이 종속되는 것을 방지합니다.

### **11.2 이벤트 훅(Hook)과 폴링의 균형**

원본 게임이 이벤트를 제공하지 않는다면, 어쩔 수 없이 폴링을 해야 합니다. 이때 '변경 추적자(Change Tracker)' 패턴을 사용하십시오.

매니저가 원본 객체의 해시(Hash)나 중요 변수의 스냅샷을 저장해두고, 현재 값과 비교하여 변경이 감지될 때만 AI 로직을 트리거하는 방식입니다. 이는 개별 AI가 각자 폴링하는 것보다 중앙에서 일괄 비교하는 것이 캐시 효율상 훨씬 유리합니다.

---

본 보고서는 유니티 환경에서의 대규모 AI 최적화를 위한 포괄적인 아키텍처를 제시하였습니다. 귀하의 프로젝트가 단순한 스크립트 수정을 넘어, 견고한 시스템 설계를 통해 쾌적한 플레이 경험을 제공하는 모드로 거듭나기를 기원합니다. 추가적인 기술적 세부 사항이나 특정 알고리즘의 구현 예시가 필요하시다면, 본 보고서의 각 섹션에서 언급된 키워드(CullingGroup, NativeArray, Job System 등)를 중심으로 심화 연구를 진행하시기를 권장합니다.

#### **참고 자료**

1. Advanced programming and code architecture \- Unity, 1월 29, 2026에 액세스, [https://unity.com/how-to/advanced-programming-and-code-architecture](https://unity.com/how-to/advanced-programming-and-code-architecture)  
2. AgentAI: A Comprehensive Survey on Autonomous Agents in Distributed AI for Industry 4.0, 1월 29, 2026에 액세스, [https://www.researchgate.net/publication/392346025\_AgentAI\_A\_Comprehensive\_Survey\_on\_Autonomous\_Agents\_in\_Distributed\_AI\_for\_Industry\_40](https://www.researchgate.net/publication/392346025_AgentAI_A_Comprehensive_Survey_on_Autonomous_Agents_in_Distributed_AI_for_Industry_40)  
3. Pathfinding with Unity Jobs and Burst is slower than without \- Stack Overflow, 1월 29, 2026에 액세스, [https://stackoverflow.com/questions/76254051/pathfinding-with-unity-jobs-and-burst-is-slower-than-without](https://stackoverflow.com/questions/76254051/pathfinding-with-unity-jobs-and-burst-is-slower-than-without)  
4. Structs vs Classes in Unity: The Memory Decision That Killed My Frame Rate (And How I Fixed It) \- Outscal, 1월 29, 2026에 액세스, [https://outscal.com/blog/unity-structs-vs-classes-memory-performance](https://outscal.com/blog/unity-structs-vs-classes-memory-performance)  
5. Optimize your mobile game performance: Tips on profiling, memory, and code architecture from Unity's top engineers, 1월 29, 2026에 액세스, [https://unity.com/blog/games/optimize-your-mobile-game-performance-tips-on-profiling-memory-and-code-architecture-from](https://unity.com/blog/games/optimize-your-mobile-game-performance-tips-on-profiling-memory-and-code-architecture-from)  
6. Game Programming: Time Slicing | Ming-Lun "Allen" Chou | 周明倫, 1월 29, 2026에 액세스, [https://allenchou.net/2021/05/time-slicing/](https://allenchou.net/2021/05/time-slicing/)  
7. Write and run coroutines \- Unity \- Manual, 1월 29, 2026에 액세스, [https://docs.unity3d.com/6000.3/Documentation/Manual/Coroutines.html](https://docs.unity3d.com/6000.3/Documentation/Manual/Coroutines.html)  
8. c\# \- How does StartCoroutine / yield return pattern really work in Unity? \- Stack Overflow, 1월 29, 2026에 액세스, [https://stackoverflow.com/questions/12932306/how-does-startcoroutine-yield-return-pattern-really-work-in-unity](https://stackoverflow.com/questions/12932306/how-does-startcoroutine-yield-return-pattern-really-work-in-unity)  
9. Optimize raycasts and other physics queries \- Unity \- Manual, 1월 29, 2026에 액세스, [https://docs.unity3d.com/6000.3/Documentation/Manual/physics-optimization-raycasts-queries.html](https://docs.unity3d.com/6000.3/Documentation/Manual/physics-optimization-raycasts-queries.html)  
10. Implementing memoization in C\# \[closed\] \- Stack Overflow, 1월 29, 2026에 액세스, [https://stackoverflow.com/questions/53285304/implementing-memoization-in-c-sharp](https://stackoverflow.com/questions/53285304/implementing-memoization-in-c-sharp)  
11. Improving API Response Using Timed Memoization: Practical Insights in C\# | by Lagu, 1월 29, 2026에 액세스, [https://medium.com/@hanxuyang0826/improving-api-responsiveness-with-timed-memoization-b52675481018](https://medium.com/@hanxuyang0826/improving-api-responsiveness-with-timed-memoization-b52675481018)  
12. Time-Based Memoization \- Josh Mottaz, 1월 29, 2026에 액세스, [https://www.jmottaz.com/posts/time-based-memoization/](https://www.jmottaz.com/posts/time-based-memoization/)  
13. Unity AI Tutorial: Hearing & vision sensors \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=ku1sBjizDeI](https://www.youtube.com/watch?v=ku1sBjizDeI)  
14. E-book update: More design patterns and SOLID principles \- Unity, 1월 29, 2026에 액세스, [https://unity.com/blog/game-programming-patterns-update-ebook](https://unity.com/blog/game-programming-patterns-update-ebook)  
15. Dirty Flag \- Game Programming Patterns, 1월 29, 2026에 액세스, [https://gameprogrammingpatterns.com/dirty-flag.html](https://gameprogrammingpatterns.com/dirty-flag.html)  
16. Event-Driven Architecture in Game Development: Unity & GameMaker \- Medium, 1월 29, 2026에 액세스, [https://medium.com/@ahmadrezakml/event-driven-architecture-in-game-development-unity-gamemaker-c76915361ff0](https://medium.com/@ahmadrezakml/event-driven-architecture-in-game-development-unity-gamemaker-c76915361ff0)  
17. When should you use Polling versus Events for input? : r/godot \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/godot/comments/t8r6ha/when\_should\_you\_use\_polling\_versus\_events\_for/](https://www.reddit.com/r/godot/comments/t8r6ha/when_should_you_use_polling_versus_events_for/)  
18. Using the Blackboard in Unity Behavior | Unity Tutorial \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=YWGYv95gSfY](https://www.youtube.com/watch?v=YWGYv95gSfY)  
19. Blackboard Pattern in Games : r/gamedev \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/gamedev/comments/wijfwj/blackboard\_pattern\_in\_games/](https://www.reddit.com/r/gamedev/comments/wijfwj/blackboard_pattern_in_games/)  
20. Introduction to the CullingGroup API \- Unity \- Manual, 1월 29, 2026에 액세스, [https://docs.unity3d.com/Manual/CullingGroupAPI.html](https://docs.unity3d.com/Manual/CullingGroupAPI.html)  
21. Unity Tricks They Don't Tell You: CullingGroup API for Smarter Objects \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=eYM3Wv3R6uA](https://www.youtube.com/watch?v=eYM3Wv3R6uA)  
22. CullingGroup API \- Unity \- Manual, 1월 29, 2026에 액세스, [https://docs.unity3d.com/2020.1/Documentation/Manual/CullingGroupAPI.html](https://docs.unity3d.com/2020.1/Documentation/Manual/CullingGroupAPI.html)  
23. Unity-Programming-Patterns/\_text/19-spatial-partition.md at master \- GitHub, 1월 29, 2026에 액세스, [https://github.com/Habrador/Unity-Programming-Patterns/blob/master/\_text/19-spatial-partition.md](https://github.com/Habrador/Unity-Programming-Patterns/blob/master/_text/19-spatial-partition.md)  
24. A simple and fast C\# spatial hash implementation \- GitHub, 1월 29, 2026에 액세스, [https://github.com/benjitrosch/spatial-hash](https://github.com/benjitrosch/spatial-hash)  
25. Insanely FAST Spatial Hashing in Unity with Jobs & Burst \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=vxZx\_PXo-yo](https://www.youtube.com/watch?v=vxZx_PXo-yo)  
26. Burst compilation \- Unity \- Manual, 1월 29, 2026에 액세스, [https://docs.unity3d.com/Manual/script-compilation-burst.html](https://docs.unity3d.com/Manual/script-compilation-burst.html)  
27. Write multithreaded code with the job system \- Unity \- Manual, 1월 29, 2026에 액세스, [https://docs.unity3d.com/6000.3/Documentation/Manual/job-system.html](https://docs.unity3d.com/6000.3/Documentation/Manual/job-system.html)  
28. Improve Performance with C\# Job System and Burst Compiler in Unity | by Eric Hu | Medium, 1월 29, 2026에 액세스, [https://realerichu.medium.com/improve-performance-with-c-job-system-and-burst-compiler-in-unity-eecd2a69dbc8](https://realerichu.medium.com/improve-performance-with-c-job-system-and-burst-compiler-in-unity-eecd2a69dbc8)  
29. Unity Burst and the kernel theory of video game performance | Sebastian Schöner, 1월 29, 2026에 액세스, [https://blog.s-schoener.com/2024-12-12-burst-kernel-theory-game-performance/](https://blog.s-schoener.com/2024-12-12-burst-kernel-theory-game-performance/)  
30. Jobs System Line of Sight Checking | AI Series Part 40 | Unity Tutorial \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=dHLNqbKrJdg](https://www.youtube.com/watch?v=dHLNqbKrJdg)  
31. 025\. Efficient Raycasting \- Three Rules to Save Milliseconds on CPU \- YouTube, 1월 29, 2026에 액세스, [https://m.youtube.com/watch?v=Op961-oplYo](https://m.youtube.com/watch?v=Op961-oplYo)  
32. Advanced pathfinding caching (DOTS, ECS) : r/unity \- Reddit, 1월 29, 2026에 액세스, [https://www.reddit.com/r/unity/comments/1h10nvy/advanced\_pathfinding\_caching\_dots\_ecs/](https://www.reddit.com/r/unity/comments/1h10nvy/advanced_pathfinding_caching_dots_ecs/)  
33. Setup 3D Pathfinding Agents In Minutes Using Unity \- YouTube, 1월 29, 2026에 액세스, [https://www.youtube.com/watch?v=mJu-zdZ9dyE](https://www.youtube.com/watch?v=mJu-zdZ9dyE)

[image1]: <data:image/png;base64,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>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAZCAYAAAB3oa15AAACmklEQVR4Xu2XP2hUQRCHR4ygmEoFEVOYkEa0sxBBG1GIhY2VkNJCC6sIioIgiIVgIWIlWliIIBZCECJY2Cla2CgGQbgiEFBEECwU/DNfZlf3ze3evtyZQrkPfty7mb19O29n5u2JDBky5L9lleqU6oHqsep8053lhthYD3NtVG0JGm26l8DGuBxvVKe9sRf7xX70SHVMdVn1XnVPtSEZl8LNF1ST3qFsV31U/Qxi7s2NESI3gy9VZLfqQ/K9CDd/pTqrWu188FxsYv+k1qlmpXtRnvuqObE5+PQcVnW8MbBDLMgR70h5K/kFRk6K+Xc6+x7VV2fL8VJskcyRG39JLMgcrOmTdN97CZwXVd9VB50vZZfqi9iTSOmoXjhbjoeqtWIpRBDjTbfMq/Y6W8qi6pk3QlwY20o6lIjjbjv7D9UdZ/Ow9WfCNZ8EwI6msEObnC2FBvHZG4HiY8IJ73CcExt3zdmxHXE2D092a7hmx6+LBT4VbOzM8XBdIgbeRaz69d7hIAUYN53YuDG2Q4ktB4tLC5C64Xd3w/cx6Z0+wH17BlCDHGQcbTFC/yatSK8SLNwXJ6nKXBQmHJDe6QOxAXTRJgAWyhjfPdoEwJN97Y3yp5h5IOxujYECIMcZc9XZ2wQwI/kFXhCbk5qiA9UoBhCfRKkDcUTAn3vLAj4mL8H7hRTxkFrxDVzrYlAs4hNijtw7IOYqQZbAH1tkjndiRZojFnOtA0EMNssa1TexATzxW2Jt7qmU38yRjuRfZDE1o4423b/h+FIrYKCJPPHGFFoiW00A5CUnyDbQCn1xrwS1ne6btoe5QeAwRwOpZUPf7BOrpZWARbP4bc7+1yn9oRkUGsiy/tD0C43gilTO7MuE4w2Lz/0/+ff5BSZ1mX0PQvluAAAAAElFTkSuQmCC>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADEAAAAWCAYAAABpNXSSAAACfUlEQVR4Xu2WT4hPURTHj1Dk/59GSinZkLIQk7KwMMpiLNgo9iysWIhSI8kWWWlSFjYSFhSxmGYlGxaKLSkrGzVTyJ/zmfPuvHPP786b9wxlMp/6Nr97zn33vXPuueeOyBxz/HesV91QXYiO2cIu1VPVdtUj1WnVvGzGH2aF6k40zpB3qs9u/FN1yo0fut89EC1RP1A9U53P3UWYx7aX6BMri6TIykqRq6onbkwQZ9x4o/s9yWrVD9W9YF8itgAfWuKwaiAaHevEPp410O7cPREAJYPvpmqN9JZNv1iSFgZ7xmbVa9VZ1fzggxdiL4mLk2WeWxzskVWq+2JrXJfedWBEtTQaxUqVBJLMKWESi49Gh2OP6pvqeLA/Vn0IthJHxT5wSOxdJzOvyFrVgWADMp8Su0W1P3cbZOSi6rs0l8QO1ZhqONix3Q22Eteqv9vEgnjufLBTtSnY0tlMu0YCD9XuGraWRclUE4Ni824FOzZ8TZDBV278Rey5BdWYj7xduyehO6VzhD5Kb6ATUApMKDod58TmpYwCNToulsUmSNCIG1OCrMWuAKX0snZ3J0U5HWSBeWQ1QcchW6W26aEpHHRjutWbSvymTLjMfpu2QaR5qQSgbRCUUtzpIbH1joidqUuZtyNtguAwMedKsLcNwpdggmSwJvfSW9WG3N2N6YJI9wDtl1bsoZY/SV5iEe4HX0oeAuDdlNKi4OvECbGFSu2VCwwftTsV+Ju6E/fBsmisoJR4fl90dIXLhOv8veRdZqvYgaSlLnf2CB9B54rsFSujr6rLUnciTzrgMyolD9tJRgiIl/P/Sxu4tNrc2P80lEvTmZo1HJPymfrr/ALH/YCbJxAMPwAAAABJRU5ErkJggg==>