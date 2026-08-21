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
| 3 | action 카탈로그 & 생명주기 | 2 | ⧗ 다음 |
| 4 | instantActions & 안전 | 3 | ☐ |
| 5 | error / factsheet / 진단 | 1–4 | ☐ |
| 6 | 검증 & 적합성 | 전체 | ☐ |

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
| 2.5 | order 검증·거부 | AGV 검증: orderUpdateId 단조성, 그래프 연속성(edge가 인접 node 연결), seq 홀짝, **새 order 첫 노드가 현재 위치와 편차 내**. 위반 시 **미실행 + error 발행**(errorType ∈ {orderUpdateError, validationError, noRouteError}, errorLevel=WARNING). ACS: 거부 error 수신 시 미션 **Aborted** + 알람 | ☑ |

---

## Phase 3 — action 카탈로그 & 생명주기 ⧗ 다음

- 3.1 actionType 집합 확정(주행/검사 `startWeldInspection`/일시정지/재측위/…)
- 3.2 각 action의 blockingType(HARD/SOFT/NONE) · **파라미터 스키마**
- 3.3 actionState 생명주기(WAITING→INITIALIZING→RUNNING→PAUSED→FINISHED/FAILED)
- 3.4 **resultDescription 계약**(성공/실패 표기, 앵커·jobRef 등)
- 3.5 기존 `ref.action_catalog`·SPEC_PHASE2와 정합

---

## Phase 4 — instantActions & 안전 <!-- 대기 -->

- 4.1 instantActions 집합: cancelOrder · startPause/stopPause · initPosition · (커스텀)emergencyStop
- 4.2 **operatingMode**(AUTOMATIC/SEMIAUTOMATIC/MANUAL/…) 의미·전이
- 4.3 **safetyState**(protective stop / emergency stop) 매핑
- 4.4 층 전환(MANUAL_TRANSFER / WAITING_FLOOR_TRANSFER) 흐름 정합

---

## Phase 5 — error / factsheet / 진단 <!-- 대기 -->

- 5.1 **errorType 카탈로그**(errorLevel WARNING/FATAL, errorReferences 규격)
- 5.2 **factsheet**(AGV capability) 채택 여부·필드 범위
- 5.3 진단/로깅·관측 계약

---

## Phase 6 — 검증 & 적합성 <!-- 대기 -->

- 6.1 적합성 기준·체크리스트
- 6.2 통합 시나리오(층전환·재시도·취소·두절 복구) 테스트
- 6.3 메시지 스키마 검증 방식(JSON Schema 등)
- 6.4 **최종 사양서** 작성 + AMR 벤더 공유

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
