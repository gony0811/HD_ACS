---
project: HD_ACS
type: contract
status: documented
updated: 2026-09-22
tags:
  - hd-acs
  - llm-wiki
---

# AMR 인터페이스

저장소의 `docs/VDA5050_INTERFACE_SPEC.md`가 인터페이스 계약의 기준 문서입니다. 확인한 버전은 **1.2**, 최종 개정일은 **2026-09-03**, 기반 표준은 **VDA 5050 2.0**입니다. 이는 프로젝트 문서의 계약 상태이며 외부 표준 최신판 조사 결과가 아닙니다.

- ACS의 로봇측 상대는 HD_AMR 하나이며 VDA 5050 over MQTT를 사용합니다.
- 계획과 배차는 ACS, 주행·검사 자세·장비 실행은 HD_AMR의 책임입니다.
- 주요 채널은 order, instantActions, state, connection입니다.
- factsheet는 사양서상 예약 채널입니다.
- 상세 계약과 실제 구현 차이는 원문 각주를 확인합니다.

Area와 Task 자동생성도 이 책임 경계를 유지해야 합니다. 도면에서 작업 좌표를 만들었다고 협동로봇의 자세나 도달 가능성을 확정한 것은 아닙니다.

## startWeldInspection 의 drawingPos.wall_code

`startWeldInspection` 액션의 `position.drawingPos.wall_code`에 실제로 발행되는 값은 **`TankGeometry`가 자동 생성한 10개 면 코드 중 하나**입니다.

- 값 집합: `B`(바닥) · `SL`·`PL`(하부챔퍼 우/좌현) · `SM`·`PM`(수직벽 우/좌현) · `SU`·`PU`(상부챔퍼 우/좌현) · `T`(천장) · `F`(선수 마구리) · `A`(선미 마구리).
- 출처: 검사 작업이 등록된 **검사 영역의 소속 면 코드**(`ref.inspection_area.wall_code`, FK→`ref.wall`)를 발행 시 그대로 echo합니다. 자유 문자열이 아니며 DB FK로 이 10종에 강제됩니다.
- 발행 경로: `InspectionDispatcher`가 `area.WallCode`를 `WeldInspectionPayload.BuildPosition`에 넘겨 `drawingPos.wall_code`에 씁니다. `param_schema`는 문자열로만 검증(enum 미강제)하지만 값 도메인은 DB가 보장합니다.
- 역할: `wall_code`는 **면(surface) 식별자 = HD_AMR의 티칭 자세 선택 키**입니다. 용접선 **형상**은 별도 파라미터 `seamType`(아래 §seamType 참고)로 전달됩니다 — 둘은 다른 축입니다.
- 사양서 근거: `docs/VDA5050_INTERFACE_SPEC.md` §8.1·§8.2·§8.4(골든 예시)·§8.5.1(2) (개정 1.3b에서 값 도메인 명문화).

## startWeldInspection 의 params.seamType (용접라인 형태, 5값 카탈로그)

`params.seamType`은 **용접선의 형상**을 나타내는 형상 선택자입니다. ACS가 계획 단계에서 도면으로 추출해 그대로 발행하며, 값 집합은 검사 타입 카탈로그와 **1:1(5값)**입니다 (2026-09-15 확장, 사양서 개정 1.3c).

| `seamType` | 의미 |
|---|---|
| `LINE` | 직선 용접선 구간 |
| `CROSS3` | 3갈래 교차(T/Y자 — 용접선 3개 교차) |
| `CROSS4` | 4갈래 교차(十자 — 용접선 4개 교차) |
| `CORNER2` | 2면 코너(이면각 모서리 — 두 면 접합) |
| `CORNER3` | 3면 코너(삼면 접합) |

- **분류 규율**: `CROSS`(교차)는 갈래 수(3/4), `CORNER`(코너)는 접합 면 수(2/3)로 세분합니다. 종전 3값(`LINE`·`CROSS`·`CORNER`)에서 `CROSS`→`CROSS3`/`CROSS4`, `CORNER`→`CORNER2`/`CORNER3`으로 세분한 것입니다.
- **레시피 선택**: HD_AMR은 `(seamType, wall_code)` 조합으로 검사 레시피를 유도합니다. 면 위 형상(LINE/CROSS3/CROSS4)은 면 자세(바닥/천장/수직벽/하부챔퍼/상부챔퍼)별 레시피로, 코너(CORNER2/CORNER3)는 면 자세 무관 단일 레시피로 매핑됩니다(사양서 §8.5.1). ACS는 레시피 개수를 몰라도 되며 두 필드만 보냅니다.
- **구현 범위(중요)**: ACS는 5값을 **수용·발행·계획 UI 드롭다운 지정**까지 구현했습니다. `param_schema` 검증(발행 직전)도 5값을 통과시킵니다. 단 HD_AMR의 `startWeldInspection`은 여전히 **스텁**이라 `CROSS*`/`CORNER*`의 실제 검사 시퀀스는 미실행 — 값은 **계획 데이터로 저장·전달만** 됩니다(레시피 매핑·실행은 [N13] 확정 후 2차 연동).
- **거부값**: `POLYLINE`은 계속 거부됩니다(2점 계약으로 세그먼트 방향 불명 → AMR FAILED). 꺾인 직선은 ACS가 세그먼트별 `LINE`으로 분할합니다.
- **적용 지점**: `param_schema` 3곳 동기화(`db/schema.sql` · `src/HD.Acs.App/Services/ActionCatalogSeed.cs` · `db/migrations/2026-09-15_seamtype_5values.sql`), 등록 게이트(`POST /api/areas/{id}/tasks`), 계획 UI VM(`AreaPlanningViewModel.SeamTypes`, WPF·Avalonia 두 헤드 공유 드롭다운).
- 상세 카탈로그·레시피 매핑: `docs/INSPECTION_TYPES.md` §3·§5, `docs/VDA5050_INTERFACE_SPEC.md` §8.5·§8.5.1, `docs/HD_AMR_INSPECTION_RECIPE_INTEGRATION.md`.

## startWeldInspection 의 params.taskId·attempt (작업 식별 키)

2026-09-22 ACS 선반영(사양서 **개정 1.4**, 협의 `[N14]`). `params.taskId`는 ACS `ref.area_task.task_id`(uuid)를, `params.attempt`는 이번 실행의 **재시도 회차**(1부터)를 발행한 값이며 **둘 다 선택 필드**입니다(`required` 아님 — 기존 AMR 파서는 무시해도 계약 위반이 아닙니다).

왜 필요한가 — 종전 계약의 식별자만으로는 계획 작업을 안정적으로 가리킬 수 없었습니다.

| 키 | 성질 | 한계 |
|---|---|---|
| `actionId` | ACS 발급 uuid, 상태 대조 정본 | **배차마다 새로 발급** — 재시도하면 같은 용접라인도 다른 값. 실행 인스턴스 ID |
| `jobRef` | `JOB-{tank}-L{level}-{wall}-{영역명}-{seq}` | 영역 이름·순번 파생 → **계획을 수정하면 값이 바뀜**. 사양서상 "로깅 외 해석 불요" |
| `params.taskId` | `ref.area_task.task_id` (uuid) | 계획이 살아있는 한 불변 — 검사 결과·이미지 대조 키로 쓸 수 있음 |
| `params.attempt` | 이번 실행의 회차(1부터) = `work_item.attempts` + 1 | 같은 `taskId`의 재검사 여부를 알려줌 — 재촬영 이미지를 덮어쓰지 않도록 구분 |

ACS 내부는 `run.order_action.task_id`로 역추적이 되므로 ACS만 보면 종전에도 문제가 없었고, 이 필드는 **AMR·검사 S/W 쪽에 안정 키를 주기 위한 것**입니다(ADR-004·미결 Q2의 위치/시각 키 규약과 연결).

구현 범위: ACS는 두 값을 항상 채워 발행하고(회차는 재배차 시점에 계산), 시뮬레이터는 결과(`resultDescription`)에 `taskId=`·`attempt=`를 echo합니다. **HD_AMR의 소비(결과 보고·검사 S/W 전달 시 echo, 1차 대조 키 채택, 회차별 이미지 저장 여부)는 N14 확정 후 2차 연동**입니다. 재시도 상한(`Acs:Dispatch:MaxRetries`, 기본 2)과 스킵 판정은 ACS 책임이라 AMR 동작은 회차와 무관합니다.

## seamType 과 Surface 의 구분

`seamType`(용접라인 형태)과 `Surface`(경유점 표면 형상)는 다른 계층입니다 — 혼동하지 않습니다.

- `seamType`: 용접라인 1개 단위. **HD_ACS**가 도면에서 추출해 `params.seamType`로 **전송**. AMR이 어떤 **레시피를 로딩**할지 결정.
- `Surface`(Flat/Corner/Corrugation): 레시피 내 경유점 1개 단위. **HD_AMR** 내부값이라 **전송 안 함**. 경유점별 촬영/조명 선택에 사용.

관련: [[HD_ACS - 시스템 구조]] · [[HD_ACS - Area와 Task]] · [[HD_ACS - 좌표계와 10개 면]] · [[HD_ACS - 근거 목록]]
