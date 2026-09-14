# VDA 5050 사양 수립 계획 (ACS ↔ AMR)

HD_ACS(마스터) ↔ HD_AMR(로봇) 간 실제 운영 프로토콜인 **VDA 5050** 사양을 단계적으로
확정·구현하기 위한 **살아있는 계획 문서**다. 각 단계의 결정 사항을 여기에 누적 기록한다.

- **상태 범례**: ☐ 미결정 · ⧗ 논의중 · ☑ 확정 · ⏸ 보류
- 결정이 확정되면 해당 항목을 ☑로 바꾸고 하단 **결정 로그**에 근거와 함께 추가한다.
- 확정된 계약은 별도 사양서(추후 `docs/VDA5050_INTERFACE_SPEC.md` / Word)로 정리해 AMR 벤더와 공유한다.

---

## 0. 범위 및 기준선

- **범위**: ACS(마스터)와 AMR(로봇) 간 MQTT 기반 VDA 5050 메시지 계약(order·instantActions·state·connection·factsheet)과 그 운영 규약.
- **범위 밖**: 비전 검사 인터페이스(HD_AMR↔비전, 별도 사양), 코봇 제어, 엔터프라이즈 REST/SignalR(별도 사양).

### 현재 구현 기준선 (`src/HD.Acs.Vda5050/`)

| 영역 | 상태 |
|---|---|
| 메시지 모델 | v2.0, **필수 필드 중심 축소 구현** (`Messages/Vda5050Messages.cs`) |
| 토픽 | `uagv/v2/{manufacturer}/{serialNumber}/{order\|instantActions\|state\|connection}` |
| MasterClient | connect, 로봇등록(state/connection 구독 QoS1), order/instantActions 발행, emergencyStop, initPosition |
| OrderBuilder | 노드=짝수·엣지=홀수 seq, 동일노드 액션 병합, Base 선릴리즈 |
| RobotStateService | state 수신 · actionId 대조 · 진행률 집계 |
| Simulator(가상 AMR) | order 순회, action FINISHED/FAILED, 2초 주기 state, resultDescription 계약 |

### 핵심 공백 (본 계획으로 메움)

connection retained/Last Will 규약 · state 필드 범위 · orderUpdate/stitching/horizon 규칙 ·
action 카탈로그·생명주기 · instantActions 집합 · error 카탈로그 · factsheet · 적합성 검증.

---

## 단계 개요 (의존성 순)

| Phase | 주제 | 의존 | 상태 |
|---|---|---|---|
| 0 | 기반 계약 (버전·토픽·QoS·좌표·보안) | — | ☑ 확정 (2026-08-21) |
| 1 | connection & state (생존신호·텔레메트리) | 0 | ☑ 확정 (2026-08-21) |
| 2 | order 프로토콜 (주행 코어) | 1 | ☑ 확정 (2026-08-21) |
| 3 | action 카탈로그 & 생명주기 | 2 | ☑ 확정 (2026-08-21) |
| 4 | instantActions & 안전 | 3 | ☑ 확정 (2026-08-21) |
| 5 | error / factsheet / 진단 | 1–4 | ☑ 확정 (2026-08-21) |
| 6 | 검증 & 적합성 | 전체 | ☑ 확정 (2026-08-21) |

> **이행 가능성**: HD_AMR(Modbus-TCP) 수준 검토 결과는 **부록 A** 참조. 물리 AMR(TARS-M)은 VDA5050를 직접 말하지 않으며, **HD_AMR이 VDA5050 어댑터(MQTT↔Modbus)** 역할을 맡는 구조로 이행한다.

---

## Phase 0 — 기반 계약 ☑ 확정 (2026-08-21)

전 단계가 의존하는 최상위 규약. 아래 확정값을 이후 단계의 전제로 사용한다.

| # | 결정 항목 | **확정값** | 상태 |
|---|---|---|---|
| 0.1 | 프로토콜 버전 | **VDA 5050 v2.0.0 고정** | ☑ |
| 0.2 | 토픽 스킴 | **`uagv/v2/{manufacturer}/{serialNumber}/{channel}`** (channel = order·instantActions·state·connection[·factsheet]) | ☑ |
| 0.3 | QoS | **전 채널 QoS 1 (AtLeastOnce)** | ☑ |
| 0.4 | Retained 정책 | **connection = retained, state = retained, order·instantActions = non-retained** | ☑ |
| 0.5 | 좌표·단위 | **m · rad, theta = 맵 X축 기준 CCW, z 미사용(2D 평면, z=0)** — position은 x·y·theta | ☑ |
| 0.6 | mapId 의미 | **mapId = 층(floor) 1:1, 형식 `{tank}-L{level}`** (예: `CT1-L2`) | ☑ |
| 0.7 | 시각·헤더 | **timestamp = ISO 8601 UTC(`O`), headerId = 채널별 단조증가** | ☑ |
| 0.8 | MQTT 브로커·보안 | **폐쇄망 전제. TLS/인증(mTLS or user/pass)은 브로커 배포 시점에 확정** (프로토콜 사양은 보안과 독립 진행) | ☑ |
| 0.9 | 로봇 식별 | **`ref.robot`의 Manufacturer·SerialNumber = 토픽 요소** | ☑ |

**후속 반영(구현 단계에서)**: 0.4 retained 플래그를 `Vda5050MasterClient`/AMR 발행부에 반영,
`appsettings.json`(Acs:Mqtt, Acs:Vda) 정합. z 미사용에 따라 position 필드는 x·y·theta로 한정.

---

## Phase 1 — connection & state ☑ 확정 (2026-08-21)

| # | 결정 항목 | **확정값** | 상태 |
|---|---|---|---|
| 1.1 | connection 생명주기 | **ONLINE(retained)** 발행 / MQTT **Last Will = CONNECTIONBROKEN(retained)** / 정상 종료 시 **명시적 OFFLINE(retained)** 발행 후 종료 (의도적 종료 vs 비정상 두절 구분 가능) | ☑ |
| 1.2 | state 발행 트리거 | **변화 발생 시 즉시 발행 + 주기 heartbeat 2초** | ☑ |
| 1.3 | state 필드 범위 | **운영 서브셋** (아래 목록). loads·maps·zoneSetId 등 미사용 필드 제외 | ☑ |
| 1.3b | nodeStates/edgeStates | **포함** — AGV가 아직 통과하지 않은 남은 node/edge 보고 (order 진행 추적·stitching 검증 근거) | ☑ |
| 1.4 | 재접속·재측위 동기화 | ACS 재구독 시 **retained state로 즉시 동기화** / `initPosition` 후 AGV `agvPosition.positionInitialized=true` 보고로 재측위 확인 | ☑ |

### 1.3 state 운영 서브셋 필드 목록 (확정)

- **헤더**: headerId, timestamp, version, manufacturer, serialNumber
- **주문 진행**: orderId, orderUpdateId, lastNodeId, lastNodeSequenceId, **nodeStates[]**, **edgeStates[]**, actionStates[]
- **운행 상태**: driving, paused, operatingMode(AUTOMATIC/SEMIAUTOMATIC/MANUAL/SERVICE/TEACHIN)
- **위치**: agvPosition {x, y, theta, mapId, positionInitialized} (z 미사용 — Phase 0.5)
- **전원**: batteryState {batteryCharge, charging}
- **안전**: safetyState {eStop, fieldViolation}
- **진단**: errors[], information[]
- *제외(현 미사용)*: loads[], maps[], zoneSetId, velocity, distanceSinceLastNode, newBaseRequest — 필요 시 후속 확장.

> 참고: 위 확정에 따라 `Vda5050State`/`AgvPosition` 모델 및 Simulator 발행부를 확장한다(구현 단계).

---

## Phase 2 — order 프로토콜 ☑ 확정 (2026-08-21)

| # | 결정 항목 | **확정값** | 상태 |
|---|---|---|---|
| 2.1 | orderId / orderUpdateId | **orderId = 미션(층)당 GUID 고유. orderUpdateId = 0부터 정수 단조증가(업데이트·재시도 시 +1).** AGV 수용 규칙: 수신 > 현재 → 수용 / == 현재 → 중복 무시 / < 현재 → **거부(orderUpdateError)** | ☑ |
| 2.2 | stitching(base 확장) | **운영 기본 = 층당 단일 order** (층 전환은 수동 → 층 간 stitching 없음). 층 내 갱신은 같은 orderId + orderUpdateId 증가. stitch 시 새 order 첫 노드 = 현재 order 마지막 base 노드(nodeId·sequenceId) **일치 요구**, 이미 통과한 노드는 미재전송 | ☑ |
| 2.3 | horizon(released=false) | **전체 Base 선릴리즈(released=true), horizon 미사용** [ADR-002 일치]. 대형 층 대비 후속 확장 여지만 남김 | ☑ |
| 2.4 | node/edge 규격 | 모든 node에 **nodePosition(x,y,theta,mapId) 필수**. 기본 편차 **allowedDeviationXY=0.08 m, allowedDeviationTheta=0.07 rad**(스테이션). **actions는 node에만** 부착(정차 액션), edge는 travel 전용(액션 없음). node seq=짝수 / edge seq=홀수 | ☑ |
| 2.6 | 노드 이동 실현 (정정) | ⚠ TARS-M은 **좌표 goto 불가** → 어댑터가 각 node를 **사전 티칭 Job/Task Index로 매핑**해 실행(부록 A.2/A.4). node 좌표는 매핑·검증·보고용으로 유지하되, 실제 주행 명령은 인덱스. **매핑 체계 설계가 선결 과제**(A.5) | ⧗ 재검토 |
| 2.5 | order 검증·거부 | AGV 검증: orderUpdateId 단조성, 그래프 연속성(edge가 인접 node 연결), seq 홀짝, **새 order 첫 노드가 현재 위치와 편차 내**. 위반 시 **미실행 + error 발행**(errorType ∈ {orderUpdateError, validationError, noRouteError}, errorLevel=WARNING). ACS: 거부 error 수신 시 미션 **Aborted** + 알람 | ☑ |

---

## Phase 3 — action 카탈로그 & 생명주기 ☑ 확정 (2026-08-21)

| # | 결정 항목 | **확정값** | 상태 |
|---|---|---|---|
| 3.1 | actionType 집합 | order 노드 액션 = **`startWeldInspection`·`cobotHome`·`homeReturn`**(전부 NODE, HARD). 주행은 액션 아님(edge=travel). 부하취급(pick/drop) 미사용. **코봇 검사 서브스텝(정렬·용접선추적 등)은 미노출** — startWeldInspection 내부에서 HD_AMR 시퀀스 엔진이 실행. 상세: `VDA5050_ACTION_CATALOG.md` | ☑ |
| 3.2 | blockingType·파라미터 스키마 | 3종 모두 **HARD**(정차 후 실행, 실행 중 주행 금지). `startWeldInspection` actionParameters = **SPEC_PHASE2 §4.1 param_schema 재사용**. `cobotHome`/`homeReturn` = 파라미터 없음(또는 target/dockId) | ☑ |
| 3.3 | actionState 생명주기 | **WAITING → RUNNING → FINISHED \| FAILED**, 일시정지 중 **PAUSED**. INITIALIZING 생략(즉시 RUNNING). AMR은 정차만, 액션 실행은 **HD_AMR이 코봇·비전으로 수행** | ☑ |
| 3.4 | resultDescription 계약 | 성공 `OK;anchor=FULL\|SHARED;jobRef=<jobRef>` / 실패 `FAIL;reason=<CODE>[;detail=<...>]`(예: PARAM·COBOT_HOME·VISION·MOVE_TIMEOUT) | ☑ |
| 3.5 | ref.action_catalog 정합 | 기존 시드(`startWeldInspection`)에 `cobotHome`·`homeReturn` 추가. scope=NODE, blocking=HARD | ☑ |
| 3.6 | 안전 인터록 | **이동 전 코봇 안전(홈) 자세 필수** — 어댑터가 모든 주행 전 자동 보장(암묵 강제; HD_AMR `AmrMoveStep.EnsureCobotAtHome` 재사용), 실패 시 이동 미실행+error(safetyInterlock/robotError). HARD 액션 실행 중 주행 금지. 상세: `VDA5050_ACTION_CATALOG.md §2` | ☑ |

---

## Phase 4 — instantActions & 안전 ☑ 확정 (2026-08-21)

| # | 결정 항목 | **확정값** (→ TARS-M Modbus 매핑) | 상태 |
|---|---|---|---|
| 4.1 | instantActions 집합 | `cancelOrder`(→상태제어 정지/주행정지, 어댑터가 order 취소·actionStates 정리) · `startPause`(→상태제어 일시정지=3) · `stopPause`(→시작=2) · `initPosition`(→**포즈탐색=측위**: Holding 20+21~26, 결과=맵일치율) · **커스텀 `emergencyStop`**(→주행정지=1). ✅ initPosition↔포즈탐색은 매뉴얼로 확정(부록 A.2) | ☑ |
| 4.2 | operatingMode | 사용값 **{AUTOMATIC(기본), MANUAL(카트: DrivingMode=카트)}**. SEMIAUTOMATIC/SERVICE/TEACHIN 미사용(예약) | ☑ |
| 4.3 | safetyState | `eStop` = RobotStop 활성 시 **MANUAL**, 아니면 **NONE**. `fieldViolation` = **false 합성**(레지스터 미노출). **규격 안전정지는 로봇측 하드웨어**[ADR-007] | ☑ |
| 4.4 | 층 전환 | 기존 **WAITING_FLOOR_TRANSFER + ManualZoneChange + initPosition** 흐름 유지. 어댑터가 현재 층을 보유해 `agvPosition.mapId`로 보고(부록 A 제약) | ☑ |

---

## Phase 5 — error / factsheet / 진단 ☑ 확정 (2026-08-21)

| # | 결정 항목 | **확정값** | 상태 |
|---|---|---|---|
| 5.1 | errorType 카탈로그 | 최소 집합 = **`validationError`, `orderError`, `orderUpdateError`, `noRouteError`, `robotError`**(TARS-M ErrorCode 래핑). `errorLevel` ∈ {WARNING, FATAL}. `errorReferences` = [{referenceKey, referenceValue}] (orderId·nodeId·actionId 등). **TARS-M ErrorCode→robotError 매핑표는 벤더 코드목록 필요(공개 항목)** | ☑ |
| 5.2 | factsheet | **채택하되 범위 최소** — factsheetRequest(instantAction) 또는 접속 시 발행. `protocolFeatures.agvActions` = {startWeldInspection, cancelOrder, startPause, stopPause, initPosition}, typeSpecification·physicalParameters(속도/크기)는 **벤더값 TBD** | ☑ |
| 5.3 | 진단/로깅 | 기존 Serilog + state `information[]` 활용. 관측 계약(resultDescription)은 §3.4 | ☑ |

---

## Phase 6 — 검증 & 적합성 ☑ 확정 (2026-08-21)

| # | 결정 항목 | **확정값** | 상태 |
|---|---|---|---|
| 6.1 | 적합성 기준 | 사용 서브셋 기준 메시지 스키마 준수 + order/state 라운드트립 통과 + 어댑터 에뮬레이션(Simulator) 통과 | ☑ |
| 6.2 | 통합 시나리오 | 층전환(WAITING_FLOOR_TRANSFER→ManualZone→initPosition), 재시도(orderUpdateId+1), cancelOrder, 두절 복구(LWT→재접속→retained state) 필수 통과 | ☑ |
| 6.3 | 스키마 검증 | 사용 필드 대상 **JSON Schema(draft-07)** 정의, Simulator·어댑터 테스트에서 검증 | ☑ |
| 6.4 | 최종 사양서·공유 | `docs/VDA5050_INTERFACE_SPEC`(md/Word) 작성 후 AMR 벤더 공유 — **부록 A 공개 항목(맵ID·에러코드·각도규약) 확인 포함** | ☑ |

---

## 부록 A. HD_AMR(Modbus-TCP) 이행 가능성 검토 (2026-08-21)

**대상 AMR**: 아덴트로봇 **TARS-M** (Modbus-TCP). 소스: `HD_AMR/Communication/AmrRegisterMap.cs`, `Service/AMRService.cs`.

### A.1 구조적 결론
물리 AMR은 **VDA5050/MQTT를 직접 말하지 않는다**(Modbus 레지스터 장치). 따라서 ACS↔AMR VDA5050는
**HD_AMR이 VDA5050 어댑터(게이트웨이)** 역할을 맡아 성립한다:
- 북측: MQTT VDA5050 에이전트(order/instantActions 수신, state/connection 발행)
- 남측: Modbus-TCP 마스터(폴링·포즈 명령) — `AMRService`로 이미 구현됨
- 노드 액션(startWeldInspection)은 AMR 기능이 아니라 **HD_AMR이 코봇·비전으로 실행**(기존 시퀀스 재사용)

### A.2 ⚠ 중대 정정 (2026-08-21, 벤더 Modbus 맵 원문 확인)

초기 검토에서 `PoseTarget`을 "목적지 이동"으로 오판했으나, **벤더 제공 TARS-M Modbus 맵으로 확정 정정한다.**

- **로봇 포즈 탐색(Holding 20 + 21~26 X/Y/RZ) = 이동이 아니라 측위(재측위)다.** 입력 좌표는 "현재 위치 추정치"이며, 결과 품질은 **Input 30 맵 일치율(%×10000)**로 확인한다. → VDA `initPosition`에 대응(이동 아님).
- **임의 좌표 goto 명령은 이 맵에 없다.** 실제 이동은 **Holding 31 Task Index / 32 Job Index 선택 + Holding 30 상태제어=시작(2)** 로 **사전 티칭된 Job/Task를 인덱스로 실행**한다. 진행은 Input 60~63(전체/실행중 Task·Job 번호)로 추적.
- **"작업상태(이동중/도킹중)" 레지스터는 벤더 맵에 없다.** 코드 `AmrRegisterMap.Input.WorkStatus=64`(주석 "매뉴얼 주소 14")는 벤더 맵과 **불일치** — 확인·수정 필요(도착/주행 판정을 WorkStatus로 하면 안 됨). 주행 여부는 **Input 10 로봇 상태(정지1/시작2/일시정지3)** + Task 번호 변화로 유도.

### A.3 정정된 매핑 요약

| VDA5050 요소 | TARS-M 대응 (확정) | 판정 |
|---|---|---|
| agvPosition x/y/theta | **Input** PoseX/Y/RZ (Float32, m·rad) — 읽기 전용 피드백 | ✅ 직접 |
| **노드 이동(order)** | **Job/Task Index(Holding 31/32) + 상태제어 시작(30=2)** — 사전 티칭 실행 | ◑ 좌표 goto 불가 → 인덱스 매핑 필요 |
| 이동 진행/도착 | Input 60~63(Task/Job 번호) + Input 10(로봇 상태) | ◑ WorkStatus 아님 |
| initPosition(재측위) | **Holding 20 포즈탐색 + 21~26 좌표**, 결과=Input 30 맵일치율 | ✅ (측위) |
| driving / paused | Input 10 로봇 상태(시작/일시정지) | ✅ |
| batteryState | Input 50 잔량% · 54 충전여부 | ✅ |
| pause/resume/cancel/eStop | Holding 30 상태제어 · 12 주행정지 | ✅ |
| **mapId(층)** | **미노출** | ⚠ 어댑터/수동 보유 |
| operatingMode / fieldViolation | 미노출 | ⚠ 합성 |
| errors | (전용 에러코드 레지스터 미표기) | ⚠ 벤더 확인 |
| 주행 속도 | **미노출** | ⚠ 엣지 maxSpeed 적용 불가 |

### A.4 이동 모델 재정의 (정정 결과)
- **VDA 노드/스테이션 ↔ AMR 사전 티칭 Job/Task Index 매핑**이 기본 모델. ACS는 좌표가 아니라 **인덱스**로 이동 지시.
- **대안(확인 필요)**: Holding 50~199 **유저 변수**에 목표값을 넣고 그 변수를 읽어 주행하는 범용 Job을 티칭 → "좌표→유저변수→goto Job" 파라메트릭 이동. (추론, 벤더 확인)
- 폐기: 초기 "노드별 PoseTarget 분해 이동 설계"는 전제 오류로 **폐기**.

### A.5 공개(확인 필요) 항목 (갱신)
1. **이동 방식 확정**: Job/Task Index 실행이 유일한가? **유저변수 기반 파라메트릭 goto Job** 가능 여부. — 최우선
2. **Job/Task 티칭·인덱스 관리** 방법(스테이션↔인덱스 대응표 생성·배포).
3. **맵/층 전환·현재 맵 ID 조회** 메커니즘 (mapId 게이트).
4. **주행 속도 명령** 레지스터 유무(엣지 maxSpeed).
5. 포즈탐색(측위) **완료·성공 판정** 기준(맵 일치율 임계·완료 신호).
6. 코드 `WorkStatus=64` 등 **레지스터 주소 불일치** 대조.
7. TARS-M **에러코드** 노출 위치·목록 → VDA errorType 매핑.
8. **주의**: Input 60~63(내비 Task/Job)은 **ACS 검사 TASK 진행률과 별개** — 혼용 금지.

### A.6 신규 의존성
- HD_AMR 어댑터에 **MQTT 클라이언트(MQTTnet 등)** 추가(현재 MQTT 없음).

**정정 판정**: VDA5050 계약(order/state/…) 자체는 어댑터로 이행 가능하나, **이동 실현은 "좌표 goto"가 아니라 "사전 티칭 Job/Task 인덱스 실행"**이라는 제약이 확정되었다. 따라서 **order 노드 ↔ AMR Job/Task 인덱스 매핑 체계**를 먼저 설계·합의해야 하며(§A.5-1,2), 이것이 이동 사양의 핵심 선결 과제다.

---

## 결정 로그

확정된 항목을 여기에 누적한다. (형식: `Phase.항목 — 결정 — 근거 — 일자`)

| 항목 | 결정 | 근거 | 일자 |
|---|---|---|---|
| 0.1 | VDA 5050 v2.0.0 고정 | 현 구현(Version="2.0.0") 일치, 단일 버전 운용 | 2026-08-21 |
| 0.2 | 토픽 `uagv/v2/{mfr}/{serial}/{ch}` 유지 | 현 `Vda5050Topics` 일치 | 2026-08-21 |
| 0.3 | 전 채널 QoS 1 | 현 state/connection 구독 QoS1 일치, 명령 유실 방지 | 2026-08-21 |
| 0.4 | connection=retained, state=retained, order/instantActions=non-retained | 재접속 시 즉시 최신 상태/생존신호 수신, 명령은 재전달 금지 | 2026-08-21 |
| 0.5 | m·rad, theta=맵 X축 CCW, z 미사용(2D) | 층별 맵이 2D 평면, 현 데이터 모델 일치 | 2026-08-21 |
| 0.6 | mapId=`{tank}-L{level}` | 기존 코드·데이터(CT1-L2) 및 tank+level 유도 규칙과 정합 | 2026-08-21 |
| 0.7 | timestamp ISO8601 UTC(O), headerId 채널별 단조증가 | 현 헤더 구현 일치, VDA 관례 | 2026-08-21 |
| 0.8 | 보안(TLS/인증)은 브로커 배포 시 확정 | 폐쇄망 전제, 프로토콜 사양과 독립 | 2026-08-21 |
| 0.9 | 로봇 식별 = ref.robot Manufacturer·SerialNumber | 토픽 요소 소스 단일화 | 2026-08-21 |
| 1.1 | connection ONLINE/CONNECTIONBROKEN(Last Will)/명시적 OFFLINE, 전부 retained | 의도적 종료와 비정상 두절 구분 | 2026-08-21 |
| 1.2 | state = 변화 시 즉시 + 2초 heartbeat | 위치 추적 부드러움과 부하의 균형, 현 시뮬레이터 일치 | 2026-08-21 |
| 1.3 | state 필드 = 운영 서브셋 (loads/maps/zoneSetId 등 제외) | VDA 필수 + 운영 필요분만, 구현·검증 부담 최소 | 2026-08-21 |
| 1.3b | nodeStates/edgeStates 포함 | order 진행 추적·stitching/horizon 검증 근거 | 2026-08-21 |
| 1.4 | 재접속=retained state 동기화, 재측위=positionInitialized 보고 | 두절 복구 시 즉시 최신화, 기존 흐름 유지 | 2026-08-21 |
| 2.1 | orderId=미션당 GUID, orderUpdateId=0부터 단조증가, 역행 update 거부 | VDA 표준, 중복·역행 방지 | 2026-08-21 |
| 2.2 | 층당 단일 order, 층 간 stitching 없음(수동 층전환) | 현 미션 분해(층=미션)와 정합, 단순성 | 2026-08-21 |
| 2.3 | 전체 Base 선릴리즈, horizon 미사용 | ADR-002 일치, 초기 단순화 | 2026-08-21 |
| 2.4 | node에 position 필수·actions는 node에만, edge=travel, 편차 0.08m/0.07rad | 정차 기반 검사 워크플로우, SPEC_PHASE2 기본값 | 2026-08-21 |
| 2.5 | AGV 검증 위반 시 미실행+error, ACS는 미션 Aborted+알람 | 잘못된 order의 안전 거부 | 2026-08-21 |
| 3.1 | order 노드 액션 = startWeldInspection(NODE,HARD) 단일 시작 | 검사 워크플로우, 부하취급 없음 | 2026-08-21 |
| 3.2 | startWeldInspection param = SPEC_PHASE2 §4.1 스키마 재사용 | 기존 정의와 정합 | 2026-08-21 |
| 3.3 | actionState = WAITING→RUNNING→FINISHED/FAILED(+PAUSED) | AMR 정차·HD_AMR 액션 실행 구조 | 2026-08-21 |
| 3.4 | resultDescription = OK;anchor=..;jobRef=.. / FAIL;reason=.. | Simulator 관측 계약 공식화 | 2026-08-21 |
| 3.5 | ref.action_catalog 기존 시드와 일치 | 충돌 없음 | 2026-08-21 |
| 4.1 | instantActions = cancelOrder/startPause/stopPause/initPosition/emergencyStop → Modbus 매핑 | TARS-M ExecutionControl·RobotStop·PoseSearch 대응 | 2026-08-21 |
| 4.2 | operatingMode = {AUTOMATIC, MANUAL} 사용 | 자율주행 기본, 조그/카트=MANUAL | 2026-08-21 |
| 4.3 | safetyState eStop=RobotStop 매핑, fieldViolation=false 합성 | 레지스터 한계, 규격안전=HW[ADR-007] | 2026-08-21 |
| 4.4 | 층전환 = 기존 수동(ManualZone)+initPosition 흐름 유지 | mapID 미노출 우회(부록 A) | 2026-08-21 |
| 5.1 | errorType 최소집합 + robotError(TARS-M 코드 래핑) | 벤더 코드표는 공개 항목 | 2026-08-21 |
| 5.2 | factsheet 채택(범위 최소, agvActions 명시, physical=벤더 TBD) | 능력 광고 표준 | 2026-08-21 |
| 5.3 | 진단=Serilog+information[], 관측=resultDescription | 기존 자산 재사용 | 2026-08-21 |
| 6.1–6.4 | 서브셋 스키마 적합성·통합 시나리오·JSON Schema 검증·최종 사양서 벤더 공유 | 적합성 확보·벤더 확인 | 2026-08-21 |
| 부록 A | HD_AMR=VDA5050 어댑터로 이행 가능, 벤더 확인 항목 6종 | Modbus-TCP 검토 결과 | 2026-08-21 |
| 정정 | 포즈탐색=측위(이동 아님), 이동=Job/Task Index 실행, 좌표 goto 불가 | 벤더 TARS-M Modbus 맵 원문 확인 | 2026-08-21 |
| 정정 | 초기 "PoseTarget 노드 분해 이동 설계" 폐기, node↔Job/Task Index 매핑으로 재정의 | 상동 | 2026-08-21 |
| 확인필요 | 코드 AmrRegisterMap.WorkStatus=64 등 벤더 맵과 주소 불일치 | 벤더 맵에 작업상태 레지스터 없음 | 2026-08-21 |
