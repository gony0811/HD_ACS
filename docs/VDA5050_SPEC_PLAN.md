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
| 0 | 기반 계약 (버전·토픽·QoS·좌표·보안) | — | ⧗ 진행중 |
| 1 | connection & state (생존신호·텔레메트리) | 0 | ☐ |
| 2 | order 프로토콜 (주행 코어) | 1 | ☐ |
| 3 | action 카탈로그 & 생명주기 | 2 | ☐ |
| 4 | instantActions & 안전 | 3 | ☐ |
| 5 | error / factsheet / 진단 | 1–4 | ☐ |
| 6 | 검증 & 적합성 | 전체 | ☐ |

---

## Phase 0 — 기반 계약 <!-- 진행중 -->

전 단계가 의존하는 최상위 규약. 여기 확정 후 좌표/토픽/QoS 재논의를 없앤다.

| # | 결정 항목 | 선택지 | 권장(초안) | 상태 |
|---|---|---|---|---|
| 0.1 | 프로토콜 버전 | v2.0.0 고정 / 다버전 | **v2.0.0 고정** (현 구현 일치) | ☐ |
| 0.2 | 토픽 스킴 | `uagv/v2/{mfr}/{serial}/{ch}` 유지 / 변경 | **유지** (interface prefix=uagv, ver=v2) | ☐ |
| 0.3 | QoS | 0 / 1 / 2 (채널별) | **QoS 1** 전 채널 (현 state/conn 일치) | ☐ |
| 0.4 | Retained 정책 | connection·state·order 각각 | **connection=retained, state=retained, order/instantActions=non-retained** | ☐ |
| 0.5 | 좌표·단위 | m/rad, theta 기준 | **m·rad, theta=맵 X축 기준 CCW, z 미사용(평면)** | ☐ |
| 0.6 | mapId 의미 | 층=맵 바인딩 규칙 | **mapId = 층(floor) 1:1**, 형식 `{tank}-L{level}` | ☐ |
| 0.7 | 시각·헤더 | timestamp 포맷·headerId 규칙 | **ISO8601 UTC(`O`), headerId 채널별 단조증가** | ☐ |
| 0.8 | MQTT 브로커·보안 | 호스트·포트·TLS·인증 | **폐쇄망, TLS/인증은 브로커단(추후 확정)** | ☐ |
| 0.9 | 로봇 식별 | manufacturer/serialNumber 소스 | **ref.robot(Manufacturer·SerialNumber) = 토픽 요소** | ☐ |

**산출물**: 본 문서 Phase 0 절 확정 + `appsettings.json`(Acs:Mqtt, Acs:Vda) 정합.

---

## Phase 1 — connection & state <!-- 대기 -->

- 1.1 connection: ONLINE(retained) + **Last Will**(CONNECTIONBROKEN) + OFFLINE(정상종료) 절차
- 1.2 state 발행 트리거: 변화 시 즉시 + 주기(N초) — N 확정
- 1.3 state 필수 필드 범위: operatingMode, paused, nodeStates/edgeStates, batteryState, agvPosition, velocity, safetyState, errors, information, driving, distanceSinceLastNode …
- 1.4 재접속/재측위 후 상태 동기화 규칙

---

## Phase 2 — order 프로토콜 <!-- 대기 -->

- 2.1 orderId / **orderUpdateId** 증가·중복 처리 규칙
- 2.2 **stitching**(base 확장) 규칙 — 이어붙임 노드 일치 조건
- 2.3 **horizon**(released=false) 사용 여부·범위
- 2.4 node/edge 규격: allowedDeviationXY/Theta, actions 위치(node/edge)
- 2.5 order 검증·**거부** 규칙(부적합 update → error + 미수용)

---

## Phase 3 — action 카탈로그 & 생명주기 <!-- 대기 -->

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
| — | (아직 없음) | | |
