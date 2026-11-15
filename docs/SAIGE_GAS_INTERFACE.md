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

## 3. 규격 대조

### §2 가스 측정 결과 전송 (ACS → SAIGE)

| 규격 | ACS 구현 | 상태 |
|---|---|---|
| `POST /admin/v3/external/gas/results`, JSON UTF-8 | `SaigeGasResultSender` (Content-Length 명시) | 충족 |
| 요청 1건 = Task 1개·회차 1개 | 검사 액션 1개가 끝날 때 받은 본문 1건 = 요청 1건 (`hist.gas_result`) | 충족 |
| `taskId` = 도면의 taskId | ACS 가 발급한 taskId 를 **도면 조회 API 와 같은 표기**(`Acs:Saige:TaskIdLittleEndian`)로 덮어써서 보낸다 | 충족 |
| `round` 필수 (1·2) | 가스검사 선창은 회차 없이 미션·재검사를 시작할 수 없다(400). 화면 선택도 1회차·2회차뿐 | 충족 |
| 판정은 `OK`·`NG` | 보내기 전에 규격 검사 — 그 밖의 값(`ERROR`)·빈 `scans`·`retry` 범위 밖이면 **보내지 않고** REJECTED + 알람 | ACS 는 걸러 냄 — **AMR 협의 필요**(아래 4) |
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

- **측정 실패 표기**: AMR 은 통신 실패로 값이 없는 측정을 빼고, 그런 포인트·Task 의 판정을 `"ERROR"` 로 보낸다. 규격은 `OK`·`NG` 만이고 `scans` 는 최소 1건이라 그대로는 400 이다.
  ACS 는 지금 그런 결과를 보내지 않고 REJECTED 로 남긴다. 정할 것: AMR 이 그 Task 를 실패(FAILED)로 끝낼지, 규격에 `ERROR` 를 넣을지.
- **검사 시작의 주인**: 규격 §3 은 SAIGE 화면의 시작 버튼이 임계값을 보내며 검사를 시작한다고 본다. ACS 미션은 ACS 화면에서 시작한다 — SAIGE 에서 시작하지 않고 돌리면 결과가 409 로 거부된다(재전송 API 로 되살릴 수 있다). 시작 순서를 운영 절차로 정하거나, ACS 미션 시작을 SAIGE 요청에 묶을지 협의.
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
| AMR 없이 흐름 보기 | 시뮬레이터 `SIM_GAS=1`(결과 JSON 보고) — 임계값 확인·센서 상태는 흉내 내지 않는다 |
