# HD_AMR → HD_ACS: VDA 5050 인터페이스 사양서 회신

| 항목 | 내용 |
|---|---|
| 작성일 | 2026-08-28 |
| 대상 문서 | `VDA5050_INTERFACE_SPEC.md` v1.0 (draft) |
| 회신 주체 | HD_AMR 통합 운영 S/W 개발팀 (로봇 온보드) |
| 상태 | **N1~N9, N11 확정 / N10 보류(실물 치수 대기)** — §4 확인 요청 3건 ACS 전건 OK 회신 반영 (2026-08-28) |

## 1. 협의 항목 회신 (§10)

| # | 항목 | HD_AMR 회신 |
|---|---|---|
| N1 | headerId 채번 | **동의·확정** — 토픽별 단조 증가. AMR측 채번은 **세션(프로세스) 단위**로 재기동 시 1부터 리셋 — ACS 확인 완료(headerId 미소비, 2026-08-28) |
| N2 | timestamp 포맷 | **동의** — 발행은 ISO 8601 UTC 밀리초 3자리+`Z`, 수신은 오프셋 표기(+00:00, 7자리 소수)도 수용하도록 구현함 |
| N3 | edge.actions | **동의** — 단일 노드형에서 edges는 빈 배열로 수수. 엣지 객체가 실릴 경우에도 파서가 수용 |
| N4 | state 표준 필수 필드 | **표준대로 발행** — operatingMode/paused/newBaseRequest/edgeStates/information/safetyState 포함 전체 필드 발행. safetyState.eStop은 기능값("NONE") |
| N5 | initPosition 파라미터 | **평면 key 4개(mapId/x/y/theta) 채택**. 방어적으로 `pose` 객체 1개 형태도 수용 구현함 |
| N6 | errors.errorType 코드 체계 | **아래 §2 코드 목록 제안** (구현 완료 — `VdaErrorTypes`) |
| N7 | actionParameters.value 직렬화 | **JSON object 그대로 수용** (기본). 문자열(JSON string) 폴백도 파서가 재파싱 수용 — 폴백 스위치 불요 |
| N8 | 두절 구간 상세 이력 | **동의** — 최신 state 스냅샷으로 충분. 소급 재전송 미구현 |
| N9 | MQTT 보안 | **동의** — 평문 :1883 (폐쇄망 전제). TLS/계정 필요 판단 시 재협의 |
| N10 | 정차 이격(standoff) | **보류** — 로봇 실물 치수·코봇 리치 확정 후 회신 예정. 잠정 0.8 m 수용 |
| N11 | Order 거부 보고 | **동의** — 폐기 + `errors[]`에 `orderValidationError`(WARNING, orderId·사유 명시), 기존 실행 중 Order 유지. 액션 단위 문제는 actionStatus FAILED로 구분 |

## 2. errorType 코드 목록 제안 (N6)

| errorType | 분류 | 의미 / 발생 조건 | errorLevel |
|---|---|---|---|
| `orderValidationError` | Order 거부 | 검증 실패로 Order 폐기 — description에 orderId·사유 | WARNING |
| `drivingFailed` | 주행 실패 | 목표 도달 불가·이동 미시작·주행 타임아웃 (해당 Order의 전 액션 FAILED 종결 처리) | WARNING |
| `inspectionFailed` | 검사 실패 | 검사 액션 실패 — description에 actionId·사유 (actionStatus FAILED와 병기) | WARNING |
| `equipmentError` | 장비 오류 | 코봇/카메라/레이저/비전/Modbus 등 온보드 장비 이상 | WARNING (지속 불가 시 FATAL) |
| `localizationLost` | 측위 상실 | 맵 일치율 저하·재측위 실패·initPosition 거부 | WARNING |
| `emergencyStopActive` | 기능 정지 | emergencyStop 수신에 의한 기능 정지 중 | WARNING |
| `batteryLow` | 배터리 | 배터리 부족 (선택 보고) | WARNING/FATAL |

같은 errorType은 최신 1건만 유지 보고한다(누적 방지). 해소 시 목록에서 제거.

## 3. 로봇측 구현 방식 고지 (계약 준수 방식)

### 3.1 주행 — 좌표 goto

TARS-M REST(`POST /api/v3/robot/go`)로 nodePosition 좌표 주행을 수행한다. **경로 계획·장애물 회피는 로봇 자율**(§4.4 책임 경계 수용). 도달 불가 시 진입을 강행하지 않고 `drivingFailed` 보고 후 전 액션 FAILED 종결 → ACS 실패 정책(§9.5) 경로.

- `allowedDeviationXY/Theta`: **도착 판정 허용 오차로만 사용**하며 주행 정밀도 자체를 오더별로 제어하지는 않음. 미지정 시 기본 0.1 m / 0.1 rad.

### 3.2 층(mapId) 운용 — 통합 맵 (내부 구현, ACS 투명)

로봇은 층별 맵 4장을 물리 맵 1장에 타일 배치한 **통합 맵**으로 운용한다. **인터페이스 계약(층별 mapId·층별 좌표)은 그대로 준수** — 좌표 변환은 AMR 내부에서 수행하므로 ACS 변경 사항 없음.

- **mapId 보고 규칙**: 층 전환(수동 엘리베이터 + 운영자 층 선택 또는 initPosition) 후 **재측위 → 맵 일치율 임계값 통과 검증을 거친 경우에만** 새 mapId를 보고한다. 실패 시 기존 mapId 유지 + `positionInitialized=false` + `localizationLost` 보고 → ACS 층 게이트(§9.2)가 닫힌 채 유지된다.
- initPosition 성공 판정을 state.mapId 변경으로 하는 §5.2 방식과 정합. initPosition의 actionState 보고는 생략(§5.2 허용).
- 운영 절차: 층 전환 시 운영자는 HD_AMR UI에서 층 선택(초기 위치 재측위)을 수행한다. ACS initPosition도 동일 경로로 멱등 처리되므로 순서 무관.

### 3.3 Order 수명주기

- 신규 orderId 수신 = 이전 Order 즉시 폐기(진행 중 미션 취소 후 교체) — §4.5.1 폐기 규칙 구현.
- 동일 orderId 재수신(QoS1 중복)은 무시.
- 완결 후 마지막 orderId·최종 actionStates를 다음 Order까지 유지 보고(§4.5.4). `orderId:""`는 부팅 후 미수신 상태만.
- cancelOrder 미구현(§4.5.3 계약대로).

### 3.4 emergencyStop

기능적 정지로 구현: 주행 정지 + 코봇 즉시 정지 + 진행 중 액션 FAILED("stopped by emergencyStop") 보고 + `emergencyStopActive` 오류 병기. 안전 규격(PL/SIL) 정지가 아님을 재확인(§5.1) — 안전은 로봇 자체 안전 체인 책임.

### 3.5 두절/재접속

MQTT Last Will(connection/CONNECTIONBROKEN/retain) 설정, 접속 직후 ONLINE(retain), 정상 종료 시 OFFLINE(retain). 두절 중 진행 Order 자율 계속, 재접속 시 즉시 최신 state 발행(§9.3 robot-is-truth).

## 4. ACS 확인 요청 사항 — 회신 완료 (2026-08-28, 전건 OK)

1. **N1 보충** — AMR headerId 재기동 시 리셋: **OK, 문제없음** (ACS는 headerId를 소비하지 않음).
2. **allowedDeviation** — §3.1의 "도착 판정 전용" 취급: **OK, 충분함**.
3. **state 발행 주기** — 2초 주기 + 이벤트(노드 도달·actionStatus 변화·오류·Order 수신) 즉시 발행: **OK, 확인됨**.

## 5. 추가 회신 — seamType·검사 방향 (2026-08-29)

### 5.1 seamType은 LINE 한정 — POLYLINE 미지원

현 계약의 `position.seamStartW/seamEndW`(2점)로는 꺾인 용접선의 세그먼트별 방향을 알 수 없어,
AMR은 **`seamType: "POLYLINE"` 액션을 `actionStatus: FAILED`로 보고**한다(Order 거부 아님 — §4.5.2 액션 단위 실패).

**ACS 제안**: POLYLINE 용접선은 ACS가 **세그먼트별 LINE 액션 N개로 분할 발행** —
인터페이스 변경 없이 해결되며, 같은 정차·같은 `anchorGroupId`로 실으면 정렬 재수행도 생략된다.
분할 발행이 곤란하면 `params.points` 포맷(§8.2 스키마에 선언만 존재)을 협의해 확장한다.

### 5.2 검사 방향(툴 회전각)은 seam 벡터로 자동 유도

검사 시 툴을 용접선 진행방향에 수직으로 회전시키는 목적은 **촬상 영상에서 용접라인이 항상
화면 수평이 되도록** 하는 것이다. AMR은 별도 방향 플래그 없이 `seamStartW→seamEndW` 벡터를
노드 `theta`(벽 정면 방향, §4.4) 기준 벽면-로컬로 투영해 기울기 각도를 액션별로 계산한다
— 수평·수직·대각선 모두 커버되므로 **ACS는 방향 정보를 추가로 보낼 필요 없음**.
전제: 노드 theta = 벽 정면 방향(§4.4)이 유지될 것.
