# VDA 5050 인터페이스 사양 (HD_ACS ↔ HD_AMR)

> **문서 성격**: 내부 개발·합의용 작업 문서. HD_ACS(관제, master)와 HD_AMR(로봇 온보드, AGV) 간
> **유일한 인터페이스**인 VDA 5050 over MQTT의 계약을 정의한다. HD_ACS는 개별 장치(AMR/협동로봇/검사장비)와
> 직접 통신하지 않으며, 상대는 HD_AMR 하나·인터페이스는 VDA 5050 하나뿐이다. 검사 시퀀스·자세 제어는 HD_AMR 책임.
>
> **표기 규약**: 각 항목을 **[고정]**(HD_ACS에 이미 구현·확정 — 코드가 truth) / **[협의]**(HD_AMR과 합의 필요)로 태깅한다.
> 본 문서는 VDA 5050 v2.0 표준 전체를 재작성하지 않고, **표준 대비 본 프로젝트 프로파일(커스텀 액션·retain·mapId 규약 등)** 만 명시한다.
>
> 관련 문서: `ARCHITECTURE.md`, `ARCHITECTURE_DECISIONS.md`(ADR-001·002·004·007), `GRAPH_DATA_MODEL.md`(층=맵), `SPEC_PHASE2_ACS.md`(startWeldInspection·T_W_D).

---

## 1. 목적 · 범위 · 용어

- **목적**: ACS↔AMR 메시지 계약을 양측이 동일하게 구현·시험할 수 있도록 고정한다.
- **범위**: MQTT 전송, 4개 채널(order/instantActions/state/connection), 공통 헤더, 액션 카탈로그, 오류·재접속 절차.
- **범위 외**: AMR 내부의 경로 계획·SLAM·협동로봇 자세 시퀀스·검사장비 저수준 제어(전부 HD_AMR 책임). 이미지 저장은 별도 검사 S/W 책임(ADR-004).

| 용어 | 의미 |
|---|---|
| master | HD_ACS (order/instantActions 발행, state/connection 구독) |
| AGV | HD_AMR (order 수행, state/connection 발행) |
| 층(level) = 맵(map) | 화물창 데크 1개 = VDA 맵 1개 = `mapId`. 층간 이동은 엘리베이터 **수동** 운영(Q9) |
| T_W_D | 도면→맵 2D 강체변환(캘리브레이션). **ACS 내부 처리**이며 AMR에는 항상 맵 좌표로 전달 |

---

## 2. 전송 계층 (MQTT)

| 항목 | 값 | 상태 | 근거 |
|---|---|---|---|
| 프로토콜 | MQTT (VDA 5050 v2.0 over MQTT) | [고정] | ADR-001 |
| 브로커 | 서버 내 배치(폐쇄망, 현장 이동식 서버 1대). 개발: Mosquitto/RabbitMQ MQTT 플러그인 :1883 | [고정] | ADR-011 |
| QoS | **1 (AtLeastOnce)** — 전 채널 | [고정] | `Vda5050MasterClient.RegisterRobotAsync` |
| master clientId | `hd-acs-master` | [고정] | `Vda5050MasterClient.ConnectAsync` |
| **retain** | AMR의 `state`·`connection`은 **retain 발행** → master 재기동/재접속 시 즉시 회수(재동기화) | [고정] | ADR-002, 시뮬레이터 |
| **Last Will** | AMR은 `connection` 토픽에 `connectionState=CONNECTIONBROKEN`(retain) Last Will 등록 → 급단절 시 브로커가 대신 발행 | [고정] | ADR-002 |
| keepAlive · 보안(TLS/인증) | — | [협의] | 폐쇄망 정책에 따라 결정 |

### 토픽 네임스페이스 [고정]
```
uagv/v2/{manufacturer}/{serialNumber}/{channel}
channel ∈ { order, instantActions, state, connection }
```
- `manufacturer`/`serialNumber`는 `ref.robot` 등록값과 정확히 일치해야 한다(예: `HHI` / `AMR-01`).
- 발행 방향: **master→AGV** = order·instantActions / **AGV→master** = state·connection.

---

## 3. 공통 헤더 [고정]

모든 메시지 공통(`Vda5050Header`):

| 필드 | 타입 | 의미 |
|---|---|---|
| `headerId` | int | 채널별 증가 카운터 |
| `timestamp` | string | ISO 8601 (`O` 포맷, UTC) |
| `version` | string | `"2.0.0"` |
| `manufacturer` | string | 제조사 코드 |
| `serialNumber` | string | 로봇 시리얼 |

> **[협의]** 헤더 `version` 문자열 표기(`"2.0.0"` vs `"2.0"`)를 양측 파서가 동일하게 취급하는지 확인.

---

## 4. order (master → AGV)

이동+검사 명령. **층 단위**로 미션을 분할하며, 각 order는 nodes/edges로 구성된 경로다.

| 필드 | 타입 | 의미 | 상태 |
|---|---|---|---|
| `orderId` | string | 미션 식별자(또는 수동 이동 `GOTO-*`) | [고정] |
| `orderUpdateId` | int | 동일 orderId 갱신 번호(재시도 시 +1) | [고정] |
| `nodes[]` | OrderNode | **sequenceId 짝수**(0,2,4…) | [고정] |
| `edges[]` | OrderEdge | **sequenceId 홀수**(1,3,5…) | [고정] |

- **Base 선릴리즈** [ADR-002]: 노드·엣지 모두 `released=true`로 전체 경로를 한 번에 릴리즈(horizon 미사용).
- **OrderNode**: `nodeId` · `sequenceId` · `released` · `nodePosition` · `actions[]`
- **OrderEdge**: `edgeId` · `sequenceId` · `released` · `startNodeId` · `endNodeId`

### nodePosition [고정]
| 필드 | 타입 | 의미 |
|---|---|---|
| `x`, `y` | double | **맵 좌표(m)** — ACS가 T_W_D로 변환해 전달 |
| `theta` | double? | 방향(rad, 맵 x축 기준 CCW) |
| `allowedDeviationXY` | double? | 허용 위치 편차 |
| `allowedDeviationTheta` | double? | 허용 방향 편차 |
| `mapId` | string | 층 = 맵 (예 `CT1-L2`) |

> **[협의]** `allowedDeviation*` 기본값/누락 시 AMR 처리, 방향 회전 규약(CCW·rad) 확인.

### 예시 — 단일 노드 이동(수동 goto) [고정]
```json
{
  "headerId": 12, "timestamp": "2026-08-27T01:00:00.000Z", "version": "2.0.0",
  "manufacturer": "HHI", "serialNumber": "AMR-01",
  "orderId": "GOTO-3f2a...", "orderUpdateId": 0,
  "nodes": [
    { "nodeId": "goto-target", "sequenceId": 0, "released": true,
      "nodePosition": { "x": 12.4, "y": 3.1, "theta": 0.0, "mapId": "CT1-L2" },
      "actions": [] }
  ],
  "edges": []
}
```

---

## 5. instantActions (master → AGV)

order와 무관하게 즉시 수행. 현재 확정 2종:

### emergencyStop [고정]
- `blockingType=HARD`. **기능적 정지**이며 안전 규격(Cat.0/1) 정지가 아님 — 안전 정지는 로봇측 하드웨어 책임 [ADR-007].
```json
{ "actions": [ { "actionType": "emergencyStop", "actionId": "<uuid>", "blockingType": "HARD" } ] }
```

### initPosition [고정]
- 수동 층 변경 후 재측위(Q9). 파라미터: `mapId` · `x` · `y` · `theta`.
```json
{ "actions": [ { "actionType": "initPosition", "actionId": "<uuid>", "blockingType": "HARD",
  "actionParameters": [
    {"key":"mapId","value":"CT1-L2"}, {"key":"x","value":10.0},
    {"key":"y","value":2.0}, {"key":"theta","value":0.0} ] } ] }
```

---

## 6. 액션 카탈로그 (커스텀 액션)  ← 최우선 합의 항목 [Q1]

검사 액션은 order의 노드 `actions[]`에 부착된다. HD_ACS는 `ref.action_catalog`의 `param_schema`(JSON Schema)로 **발행 직전 검증**하며, 위반 시 릴리즈를 거부한다.

### 공통 규약 [고정]
| 필드 | 규약 |
|---|---|
| `actionType` | 카탈로그 등록된 문자열 |
| `actionId` | **ACS가 UUID 발급** → state의 `actionStates[].actionId`와 대조하는 키 |
| `blockingType` | 현재 `HARD` |
| `actionParameters[]` | `{key, value}` 목록 |

### startWeldInspection [고정 — 계약 확정 2026-08-27]
단일 용접라인 검사. **flat actionParameters** 5개(`db/schema.sql` `ref.action_catalog` param_schema = draft-07):

| 키 | 타입 | 의미 |
|---|---|---|
| `wallId` | string | 검사 대상 **면 코드**(B/SL/PL/SM/PM/SU/PU/T/F/A) = **AMR 티칭 자세 선택 키** |
| `seamStart` | `[x,y,z]` | 용접라인 시작 — **맵 좌표(m)**. ACS가 도면→맵(T_W_D) 변환·z(높이) 통과 |
| `seamEnd` | `[x,y,z]` | 용접라인 끝 — 맵 좌표(m) |
| `orientation` | enum | **수평 `"H"` / 수직 `"V"`**. ACS가 seam 기하(Δz vs 수평변위)에서 유도 |
| `patternType` | enum | 검사 도면 타입. 디폴트 `"LINEAR"`(선형) |

```json
{ "wallId": "SM",
  "seamStart": [12.40, 3.10, 1.85], "seamEnd": [12.40, 5.60, 1.85],
  "orientation": "H", "patternType": "LINEAR" }
```

- **툴 자세·법선은 ACS가 보내지 않는다** — AMR이 `wallId` 티칭으로 결정(SPEC v2: `wallNormal` 계약 제거).
- **정렬(anchor) 재사용은 AMR 내부 책임** [결정 2026-08-27] — 같은 정차에서 연속 검사 시 정렬을 재사용/공유할지는 AMR이 내부적으로 판단한다. ACS는 앵커 그룹/순서(`anchorGroupId`·`seqInGroup`)를 **전달하지 않으며**, 각 검사 액션을 독립적으로 발행한다. (기존 ACS측 FULL/SHARED 모델 은퇴)
- 위치·시각·성공/실패만 ACS가 기록하며 이미지 자체는 검사 S/W 책임 [ADR-004].
- 실패/누락 필드는 AMR이 `actionStatus=FAILED`, `resultDescription="FAIL;reason=PARAM(<필드>)"`로 보고(시뮬레이터 검증 규약).

### 합의 필요 [협의]
- `patternType` **enum 확장**(선형 외 곡선·코너 등)을 AMR 검사 도면 유형과 합의.
- `wallId` = 면 코드 규약을 AMR **티칭 키**와 일치시키기(현재: 면 코드 문자열).
- `startWeldInspection` 외 **추가 검사 액션**(촬영·측정·조명 등) 필요 여부와 각 스키마.
- `blockingType` 사용 규약(HARD/SOFT/NONE)과 다중 액션 순서.
- factsheet 토픽으로 AMR이 지원 액션을 광고할지 여부(현재 미지원).

---

## 7. state (AGV → master)

AMR이 보고하는 상태. **retain 발행**(재동기화 근거).

| 필드 | 타입 | 의미 | 상태 |
|---|---|---|---|
| `orderId` / `orderUpdateId` | string/int | 현재 수행 중 order | [고정] |
| `lastNodeId` / `lastNodeSequenceId` | string/int | 마지막 통과 노드 → 진행률 대조 | [고정] |
| `driving` | bool | 주행 중 여부 | [고정] |
| `agvPosition` | obj | `x`·`y`·`theta`·`mapId`·`positionInitialized` — **층 검증 게이트 근거** | [고정] |
| `batteryState` | obj | `batteryCharge`·`charging` | [고정] |
| `actionStates[]` | obj | `actionId`·`actionType`·`actionStatus`·`resultDescription` | [고정] |
| `nodeStates[]` | obj | `nodeId`·`sequenceId`·`released` | [고정] |
| `errors[]` | obj | `errorType`·`errorLevel`·`errorDescription` | [고정] |

- **actionStatus 값**: 기본 `WAITING`. ACS는 `FINISHED`/`FAILED`를 종결로 처리한다.
  - **[협의]** 전체 값 집합(`WAITING`/`INITIALIZING`/`RUNNING`/`PAUSED`/`FINISHED`/`FAILED`) 및 전이 규약 확정.
- **[협의]** state **보고 주기/트리거**(주기 발행 vs 변화 시 발행), `errorType`·`errorLevel` 코드 체계.

### 층 검증 게이트 [고정]
ACS는 다음 미션(층)을 릴리즈하기 전에 `agvPosition.mapId == 미션 mapId` 를 확인한다. 불일치 시 릴리즈하지 않고 `WAITING_FLOOR_TRANSFER` 상태로 대기(엘리베이터 수동 이동 후 `initPosition` → 재개). 수동 지점 이동(goto)도 동일 게이트로 **다른 층이면 거부**한다.

---

## 8. connection (AGV → master)  [고정]

| connectionState | 의미 |
|---|---|
| `ONLINE` | 정상 접속 |
| `OFFLINE` | 정상 종료 통지 |
| `CONNECTIONBROKEN` | **비정상 단절** — MQTT Last Will로 브로커가 대신 발행(retain) |

### 재접속 · 재동기화 절차 [고정, ADR-002]
1. AMR 급단절 → 브로커가 retain된 Last Will(`CONNECTIONBROKEN`) 발행.
2. AMR 재접속 → 재구독 + `connection=ONLINE` + 현재 `state`(retain) 재발행.
3. master는 retain된 state로 **진행 중 order를 즉시 회수**하여 상태를 재동기화.
4. `WAITING_FLOOR_TRANSFER`로 대기하던 Run은 `ONLINE` 수신 시 릴리즈를 재시도.

> **[협의]** master측 자동 재접속: 현재 `Vda5050MasterClient`는 기동 시 1회 접속만 구현 — 브로커 두절 시 재접속/재구독 로직 필요(후속 TODO).

---

## 9. 좌표계 · 단위 · 맵/층 모델

- 단위: 길이 m, 각도 rad. AMR에는 **항상 맵 좌표**로 전달(도면 좌표는 ACS 내부에서 T_W_D로 변환).
- `mapId` 규약: `{TankId}-L{level}` (예 `CT1-L2`). **[협의]** 명명 규칙·맵 버전 동기화(맵 재생성 시 캘리브레이션 무효 처리).
- 층간 이동: 엘리베이터 **수동** 운영. ACS는 층 단위로 미션을 분할하고 `initPosition`으로 재측위 유도.

---

## 10. 오류 · 예외 처리

| 상황 | 처리 | 상태 |
|---|---|---|
| action `FAILED` | 재시도(`orderUpdateId+1`) → 스킵 → 알람(운영 정책) | [협의] — ACS측 정책 엔진 TODO |
| order 거부 | AMR 거부 응답 표현·ACS 재발행 규약 | [협의] |
| 스키마 위반 메시지 | ACS 수신부에서 알람 발행 | [협의] — TODO |
| 통신 두절 | §8 재동기화 | [고정] |

---

## 11. 시퀀스 (합의 시나리오 = 시험 케이스)

1. **정상 검사**: order(층 Base) 릴리즈 → AMR 이동·검사 → state로 `actionStates` FINISHED 보고 → 다음 정차/층.
2. **두절·재접속**: order 진행 중 단절 → Last Will `CONNECTIONBROKEN` → 재접속 → retain state로 재동기화 → order 이어서 완료.
3. **수동 층 변경**: 층 게이트 불일치 → `WAITING_FLOOR_TRANSFER` → 운영자 엘리베이터 이동 → `initPosition` → `ONLINE`/게이트 통과 → 재개.
4. **비상정지**: `emergencyStop`(HARD) instantAction → AMR 기능적 정지.
5. **수동 지점 이동**: 2D 평면도 우클릭 → 단일 노드 order(같은 층만) → AMR 이동.

---

## 12. 미결 · 합의 항목 (요약)

- **액션 카탈로그** — `startWeldInspection` 계약은 확정(§6). 추가 검사 액션·`patternType` enum 확장은 합의 필요.
- `actionStatus` 값 집합·전이, `errorType`/`errorLevel` 코드 체계.
- order 거부/`FAILED` 재시도·스킵·알람 정책(ACS측 구현 포함).
- state 보고 주기/트리거, factsheet 지원 여부.
- MQTT 보안(TLS/인증)·keepAlive, master측 자동 재접속.
- `mapId` 명명·맵 버전 동기화, 좌표 프레임/단위 최종 확인.

---

## 13. 준수 시험 (SimTest 매핑)

`HD.Acs.Simulator` + `HD.Acs.SimTest`가 라이브 브로커에서 아래를 검증한다(`src/run_simtest.sh`):

| 케이스 | 검증 내용 | §참조 |
|---|---|---|
| S1 | 유효 검사 액션(수평 H·수직 V) 모두 FINISHED | 6 |
| S2 | 파라미터 검증(seamStart 누락·orientation 오류 → PARAM) | 6 |
| S3 | 액션 실패 주입(FAILED) | 10 |
| S4 | connection retain 회수(ONLINE) | 8 |
| S5 | 두절→CONNECTIONBROKEN→자동 ONLINE | 8 |
| S6 | order 진행 중 두절→재접속 후 전 액션 FINISHED 수렴·orderId 보존 | 8·11 |

---

## 14. 부록 — 코드 참조

| 계약 | 구현 위치 |
|---|---|
| 메시지 모델 | `src/HD.Acs.Vda5050/Messages/Vda5050Messages.cs` |
| 토픽 | `src/HD.Acs.Vda5050/Vda5050Topics.cs` |
| master 클라이언트(발행/구독) | `src/HD.Acs.Vda5050/Vda5050MasterClient.cs` |
| order 빌더(시퀀싱) | `src/HD.Acs.Vda5050/OrderBuilder.cs` |
| state/connection 처리 | `src/HD.Acs.App/Services/RobotStateService.cs` |
| 액션 카탈로그·스키마 | `db/schema.sql` (`ref.action_catalog`), `SPEC_PHASE2_ACS.md` §4.1 |

---

## 변경 이력
- 2026-08-27: 초안 작성 — 구현된 계약을 추출하고 [고정]/[협의] 태깅 (내부 개발·합의용).
- 2026-08-27: §6 `startWeldInspection` 계약 확정·간소화 — `wallId·seamStart·seamEnd·orientation·patternType` flat 5필드(앵커 그룹·프로필 계약 은퇴). DB param_schema·payload 빌더·시뮬레이터·SimTest·테스트 동기화.
