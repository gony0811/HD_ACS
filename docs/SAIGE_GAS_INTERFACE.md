# HD_ACS ↔ SAIGE 포밍가스 기밀 검사 연동 — ACS 구현 대조표

기준 문서: SAIGE *HD_ACS ↔ SAIGE 포밍가스 기밀 검사 연동 규격 (초안 v0.1, 2026-11-03)*.
AMR 구간(ACS ↔ HD_AMR)은 HD_AMR `docs/GAS_DETECTOR_XL3000FLEX.md` §5 와 `docs/VDA5050_INTERFACE_SPEC.md` §8.1·N25.
규격은 초안이다 — 이노로보틱스·SAIGE 와 확정되면 이 표를 함께 고친다.

## 1. 전체 흐름

```
INFICON 센서 → AMR ──(Task 완료 시 결과 JSON, startWeldInspection resultDescription)──▶ ACS ──POST gas/results──▶ SAIGE
                AMR ◀──(instantAction setGasThreshold)── ACS ◀──PUT /api/gas/threshold── SAIGE
                AMR ──(state.information gasSensor)──▶ ACS ──POST robot-health-check(sensor)──▶ SAIGE
```

도면 조회는 용접검사와 같은 API(`/api/tanks/{id}/geometry`·`walls`·`/api/areas`·`/api/tasks`)를 그대로 쓴다(규격 §1.1).

## 2. 설정 (`appsettings.json` — 현장 서버는 보존되는 파일에 직접 넣는다)

| 키 | 가스검사 현장 값 | 설명 |
|---|---|---|
| `Acs:Saige:Enabled` | `true` | 로봇·장비 상태 전송 |
| `Acs:Saige:BaseUrl` | `http://<SAIGE_HOST>:<PORT>` | 결과·상태 전송 공용 |
| `Acs:Saige:HealthPath` | `/admin/v3/external/robot-health-check` | **가스검사는 경로가 다르다**(용접검사 기본값 `/agent/v3/…`) |
| `Acs:Saige:MaxBackoffSec` | `5` (= 전송 주기) | 규격 §4 "다음 주기에 최신 상태로" — 기본 60 이면 연결 실패 뒤 최대 60초 쉰다 |
| `Acs:Saige:InternalErrorRetries` | `0` | 규격 §4 는 500 을 다음 주기에 다시 보낸다(기본 2 = 같은 주기 안에서 즉시 재시도) |
| `Acs:Saige:Gas:Enabled` | `true` | 가스 측정 결과 전송. 꺼져 있으면 저장만 한다 |
| `Acs:Saige:Gas:ResultPath` | `/admin/v3/external/gas/results` | |
| `Acs:Saige:Gas:ThresholdAckTimeoutSec` | `5` | 임계값을 AMR 이 확인해 줄 때까지 기다리는 시간 |
| `Acs:Api:ListenPort` | `5100` | 규격 §3 의 ACS 포트. ACS 기본은 5199 — 현장에서 맞추거나 SAIGE 쪽 주소를 5199 로 |
| `Acs:Saige:TrafficLogDir` | (기본 실행 폴더의 `logs`) | 세이지 송수신 기록 파일 위치. `TrafficLogEnabled=false` 면 파일을 만들지 않는다 |

## 3. 규격 대조

### §2 가스 측정 결과 전송 (ACS → SAIGE)

| 규격 | ACS 구현 | 상태 |
|---|---|---|
| `POST /admin/v3/external/gas/results`, JSON UTF-8 | `SaigeGasResultSender` (Content-Length 명시) | 충족 |
| 요청 1건 = Task 1개·회차 1개 | 검사 액션 1개가 끝날 때 받은 본문 1건 = 요청 1건 (`hist.gas_result`) | 충족 |
| `taskId` = 도면의 taskId | ACS 가 발급한 taskId 를 **도면 조회 API 와 같은 표기**(`Acs:Saige:TaskIdLittleEndian`)로 덮어써서 보낸다 | 충족 |
| `round` 필수 (1·2) | **2026-11-24 변경**: ACS 가 그 구역(영역)의 검사 횟수(1부터)를 세어 싣는다. 화면 회차 선택 없음. 같은 구역을 세 번째 검사하면 3 이 나간다 — 규격의 1·2 범위를 넘으므로 세이지 확인 필요 | **협의 필요** |
| 판정은 `OK`·`NG` | 판정은 OK · NG · FAIL 세 가지로 본다. **FAIL(측정 실패)이 낀 Task 는 AMR 이 결과를 보고하지 않고 검사 액션을 FAILED 로 끝낸다** — ACS 는 실패로 기록하고(작업 현황 "실패", 재검사 대상) SAIGE 로는 보내지 않는다. 그래도 규격 밖 본문이 오면 보내기 전 검사에서 걸러 REJECTED + 알람 | 충족 |
| 200 까지 보관·재전송 | DB 에 쌓고 받은 순서대로, 성공할 때까지 | 충족 |
| 400·404·409 = 재시도 안 함, 로그·알람 | 그 건만 REJECTED + `SAIGE_BAD_REQUEST` 알람, 다음 건은 계속 | 충족 |
| 500·연결 거부·타임아웃 = 간격을 두고 재시도 | 그 건에서 멈추고 백오프(5 s → 최대 60 s) 뒤 같은 건부터 | 충족 |
| (규격 밖) 거부된 결과 되살리기 | `POST /api/integrations/saige/gas/resend {runId?, resultIds?}` — 409(SAIGE 에서 검사 미시작)로 거부된 결과를 SAIGE 시작 뒤 다시 보낼 때 | 추가 |

### §3 임계값 전달 (SAIGE → ACS → AMR)

| 규격 | ACS 구현 | 상태 |
|---|---|---|
| `PUT /api/gas/threshold` `{tankId, value, unit, requestedBy, requestedAt}` | `GasThresholdService` | 충족 (포트는 설정) |
| ACS 는 AMR 로 전달 | 그 선창의 층을 보고 중인 연결된 AMR 로 instantAction `setGasThreshold`(같은 필드) | 충족 |
| 200 = 받은 값 그대로 + `timestamp` | **AMR 이 적용을 확인한 뒤에만** 200 (state.information `gasThreshold` 의 `lastActionId`·`lastAccepted`) | 충족 |
| 400 = 값 검증 실패 | 필수 값 누락·`value ≤ 0`·없는 선창, AMR 이 거부한 경우(단위 불일치·가스 모드 아님) | 충족 |
| 503 = AMR 에 전달하지 못함 | 연결된 AMR 없음·발행 실패·확인 시간 초과 | 충족 |
| 양측이 로그로 남긴다 | 감사로그 `GAS_THRESHOLD_SET`(성공)·`GAS_THRESHOLD_FAILED` — 요청자·요청 시각·tankId·값 | 충족 |
| 결과의 `threshold` = 그 Task 에 실제로 쓴 값 | AMR 이 Task 시작 때 잡은 값을 본문에 싣고 ACS 는 그대로 전달 | 충족 (AMR) |

ACS 는 임계값을 저장하지 않는다 — AMR 이 받아 보관하고(재시작 후에도 유지) 판정에 쓴다.

### §4 로봇·장비 상태 전송 (ACS → SAIGE)

| 규격 | ACS 구현 | 상태 |
|---|---|---|
| `POST /admin/v3/external/robot-health-check` | 경로는 설정(`Acs:Saige:HealthPath`) | 설정 필요 |
| 본문 = 용접검사 형식 + `sensor` | AMR 이 최근 30초 안에 `gasSensor` 를 보고했으면 `sensor{state, errorCode, calibrated?}` 를 싣는다 | 충족 |
| 5초마다, 최신 상태만 | `SaigeHealthReporter` | 충족 |
| 400 = 재시도 안 함, 로그·알람 | 같음 | 충족 |
| 500·연결 실패 = 다음 주기에 최신 상태로 | 기본값은 용접검사 규격(§9.5)대로 즉시 재시도·백오프 — 위 2장 설정으로 맞춘다 | 설정 필요 |

`sensor.state` 값은 AMR 보고값 그대로다(MEASURING·STANDBY·CALIBRATING·WARMUP·ERROR·EMISSION_OFF·DISCONNECTED) — 규격은 "장비 확정 후 정의".
`sensor.calibrated` 는 AMR 이 주지 않으면 싣지 않는다(지금은 교정 상태 조회 방법 미확정).

## 4. 협의·확인이 남은 것

- **측정 실패(FAIL)를 SAIGE 에도 보일지**: 결정(2026-11-15) — 판정은 OK · NG · FAIL, FAIL 이 낀 Task 는 검사 실패로 끝낸다(HD_AMR `feature/gas-inspection`). SAIGE 는 그 Task 의 결과를 받지 않으므로 SAIGE 화면에서는 "결과 없음"으로 보인다. SAIGE 에도 FAIL 을 남기려면 규격의 판정 값에 `FAIL` 을 넣는 협의가 필요하다.
- **검사 시작 순서**: 규격 §2 응답 표의 `409 · 40901` = "SAIGE 에서 검사가 시작되지 않음" — **SAIGE 서버가** ACS 가 올린 가스 결과를 받지 않겠다고 답하는 코드다. SAIGE 는 자기 화면의 [시작] 버튼(이때 임계값을 ACS 로 보낸다, §3)을 누른 선창의 결과만 받는다. 작업자가 SAIGE 에서 시작을 누르지 않고 ACS 에서 미션부터 돌리면 검사는 정상적으로 진행되지만 결과가 SAIGE 에 쌓이지 않는다(ACS 에는 REJECTED 로 남고, SAIGE 시작 뒤 재전송 API 로 다시 보낼 수 있다). 운영 절차: **SAIGE 에서 검사 시작 → ACS 에서 미션 시작**. ACS 미션 시작을 SAIGE 요청에 묶을지는 협의.
- **측정 위치의 높이 `h`** (2026-11-24 추가, 규격 초안에 없음): 가스검사 AMR 이 측정마다 `u`·`v` 옆에 `h`(면에서의 높이 mm 정수, 0 = 벽 표면, + = 선창 안쪽 — 용접검사 촬영 요청 `PosZ` 와 같은 값, 측정 거리가 아니라 검사 프로필 경유점의 Z)를 실어 보낸다. ACS 는 그대로 전달하고, 값이 있으면 0 이상의 정수인지만 본다. SAIGE 가 이 필드를 받는지·필드 이름은 확인 전.
- **robotId 표기**: 결과 본문의 `robotId` 는 AMR 의 VDA 시리얼 번호, 상태 전송의 `robotId` 는 ACS 로봇 ID 다(지금은 둘 다 `AMR-01`, 규격 예시는 `AMR01`).
- **ACS 포트**: 규격 5100 / ACS 기본 5199.
- AMR 구간(`params.round`·결과 JSON·`setGasThreshold`·`state.information`)은 HD_AMR 문서에도 협의안으로 적혀 있다.

## 5. 확인 방법

| 무엇을 | 어떻게 |
|---|---|
| 결과가 쌓이고 나가는지 | `GET /api/integrations/saige/gas` (보낼 것·보낸 것·거부된 것, 마지막 오류) |
| run 의 결과·판정·전송 상태 | `GET /api/runs/{runId}/gas-results` (`?body=true` 면 AMR 본문 포함) |
| 상태 전송 | `GET /api/integrations/saige` |
| 임계값 변경 기록 | 감사로그 `GAS_THRESHOLD_SET`·`GAS_THRESHOLD_FAILED` |
| 세이지와 주고받은 본문·응답 | 서버 `logs/saige-날짜.log`(전부) · 앱 로그(정상 상태 전송 제외) · `GET /api/integrations/saige/traffic?limit=50`(최근 200건) |
| AMR 없이 흐름 보기 | 시뮬레이터 `SIM_GAS=1`(결과 JSON 보고) — 임계값 확인·센서 상태는 흉내 내지 않는다 |
