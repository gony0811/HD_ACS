# DEVELOPMENT_HISTORY.md

HD_ACS 개발 작업 요약 기록. 각 작업은 **날짜 · 제목 · 배경 · 변경 내용 · 검증 · 후속**으로 정리한다.
(상세 커밋 단위 이력은 `CLAUDE.md`의 "변경 이력" 절, 아키텍처 결정은 `docs/ARCHITECTURE_DECISIONS.md` 참고)

---

## 2026-08-27 — ADR-013 신설 (검사 액션 계약 + 정렬 책임 경계)

### 배경
- "같은 정차 연속 검사 시 정렬은 AMR 내부 처리" 결정을 아키텍처 결정으로 정식 등재 요청.

### 변경 내용
- `docs/ARCHITECTURE_DECISIONS.md`에 **ADR-013**(검사 액션 계약 startWeldInspection 확정 + 정렬 책임 경계 = AMR) 추가. flat 5필드 계약·정렬/자세/법선의 AMR 책임 경계·근거(ADR-001 연장)·결과 명시.
- **ADR-008**(VDA 5050 프로파일 🔶) 세부 상태 갱신 — 버전 2.0 ✅, 액션 카탈로그 🔶(startWeldInspection 확정), MQTT 🔶(QoS1/retain/Last Will 확정) 반영, VDA5050_INTERFACE·ADR-013 참조.
- 미결 질문 **Q1** → 🔶 부분 해소로 갱신.

### 검증
- 문서 작업(코드 무변경).

---

## 2026-08-27 — startWeldInspection 액션 카탈로그 계약 확정·간소화 (앵커 그룹 은퇴)

### 배경
- AMR로 전달할 검사 액션 파라미터를 운영자 의도에 맞게 재정의: **검사대상 WallId · 용접라인 시작/끝 위치 · 수평/수직 · 검사도면 타입(디폴트 선형)**.
- 기존 계약(WP-3)은 무거웠음: `jobRef`+`position{seamStartW,seamEndW,drawingPos}`+`params{seamType,sectionDxfId,inspectionProfileId,standoffMm,anchorGroupId,seqInGroup}`. **앵커 그룹(FULL/SHARED 정렬 공유) 모델을 은퇴**하고 flat 5필드로 축소.
- 결정(사용자 확인): wallId=**면 코드**(예 "SM"), enum은 **최소 집합**(orientation=H|V, patternType=LINEAR 단일).

### 변경 내용 (계약 = flat actionParameters 5필드)
`wallId`(면 코드·AMR 티칭 키) · `seamStart`/`seamEnd`(맵 좌표 [x,y,z] m) · `orientation`(H|V) · `patternType`(디폴트 LINEAR).
- **DB**: `db/schema.sql` `ref.action_catalog` param_schema 교체(draft-07) + `db/migrations/2026-08-27_startweld_action_schema.sql`(ON CONFLICT UPDATE).
- **빌더**(`HD.Acs.Core/Planning/WeldInspectionPayload.cs`): `BuildPosition`+구 `BuildActionParameters` 제거 → 신 `BuildActionParameters(T_W_D, WeldDrawingData, patternType)`가 flat 5필드 방출(seam x,y는 T_W_D 적용·z 통과). `Orientation(start,end)` 유도(|Δz| > 수평변위 → V, 아니면 H, 프레임 무관). `wallId`=WallCode.
- **활성 경로**(`InspectionDispatcher`): 영역→작업 큐 저장 시 새 params(flat) 저장, 발행 시 params 각 키를 actionParameter로 전개(구 jobRef/position/params 제거). `taskId`는 내부 대조용(AMR 미전송).
- **휴면 경로**(`MissionService.ReleaseMissionAsync`): seam 기반 릴리즈도 flat 계약으로.
- **시뮬레이터**: `WeldInspectionParams.Validate`를 새 필드 검증으로(vec3 seamStart/End·orientation H|V·wallId·patternType), 앵커 공유 판정 삭제 → 단일 검사, `resultDescription="OK;wall=..;orient=..;pattern=.."`.
- **SimTest**: `Inspection` 헬퍼·S1(앵커 → **유효검사** 수평/수직 모두 FINISHED)·S2(**seamStart 누락+orientation 오류** → PARAM)·S3·S6 호출 갱신.
- **테스트**: `WeldInspectionPayloadTests` 골든을 새 계약으로 재작성(+orientation 유도 테스트).
- **문서**: `docs/VDA5050_INTERFACE.md` §6 계약 확정판·§13 시험 매핑 갱신.

### 검증
- 비-UI 전 프로젝트(Core/App/Simulator/SimTest/Core.Tests) 빌드 **0 error**.
- **Core.Tests 51개 전부 통과**(payload 골든·orientation 유도·스키마 검증 포함).
- 라이브 SimTest(브로커+시뮬레이터) 및 DB 마이그레이션 적용은 수동 검증 대기.

### 후속
- `patternType` enum 확장(곡선·코너)·추가 검사 액션·`wallId`↔AMR 티칭 키 규약을 AMR과 합의(문서 §6 [협의]).

---

## 2026-08-27 — VDA 5050 인터페이스 사양서 초안 (docs/VDA5050_INTERFACE.md)

### 배경
- AMR로의 VDA 5050 사양서가 필요. 단, HD_ACS에는 **인터페이스가 이미 구현돼 있어** 사양서는 "설계"가 아니라 **구현된 계약 추출 + AMR과 합의할 항목 명시** 작업.
- `docs/`에 VDA 5050 전용 인터페이스 문서가 부재 → 신규 작성. 용도: **내부 개발·합의용 작업 문서**.

### 변경 내용
- `docs/VDA5050_INTERFACE.md` 신설(한국어, ICD 형식). 각 항목을 **[고정]**(코드 truth) / **[협의]**(AMR 합의 필요)로 태깅.
- 섹션: 전송 계층(MQTT·토픽·QoS1·retain·Last Will), 공통 헤더, order(노드/엣지 시퀀싱·Base 선릴리즈·nodePosition·mapId), instantActions(emergencyStop·initPosition), **액션 카탈로그(startWeldInspection, Q1 최우선 합의)**, state(agvPosition·batteryState·actionStates·층 검증 게이트), connection(재접속/재동기화 ADR-002), 좌표계·단위·맵/층 모델, 오류 처리, 시퀀스, 미결 항목, 준수 시험(SimTest S1~S6 매핑), 코드 참조 부록.
- 실제 코드에서 필드·토픽·액션·retain/Last Will 규약을 추출해 JSON 예시 포함(`Vda5050Messages.cs`·`Vda5050Topics.cs`·`Vda5050MasterClient.cs`·`OrderBuilder.cs`·`RobotStateService.cs`).

### 검증
- 문서 작업(코드 무변경). 메시지 모델·토픽·액션 규약을 소스와 대조해 정확도 확보.

### 후속(문서의 [협의] 항목 = AMR과 확정)
- 액션 카탈로그 전체 목록·JSON Schema(Q1), actionStatus 값 집합·error 코드 체계, order 거부/FAILED 재시도 정책, factsheet 지원, MQTT 보안, master측 자동 재접속.

---

## 2026-08-27 — 2D 평면도 우클릭 "여기로 이동" (수동 지점 이동, 층 게이트)

### 배경
- 2D 평면도에서 운영자가 **우클릭 → 이동**으로 선택 로봇을 그 지점으로 보내는 수동 조작 요청.
- 안전 요구: **로봇이 대상 층에 없으면 이동 금지**(층 불일치 시 명령 반려). 층 이동은 엘리베이터 수동 운영(Q9)이므로 같은 층 내 이동만 허용.
- HD_ACS의 로봇측 인터페이스는 VDA 5050 하나뿐 — 이동은 **단일 노드 Order**로 표현(경로 계획·자세는 HD_AMR 책임).

### 변경 내용
1. **백엔드** (`HD.Acs.App`)
   - `MissionService.ManualGotoAsync(robotId, mapId, drawingX, drawingY, theta?, userId)`: ① 층 게이트 — 로봇 `RobotContext.ReportedMapId` == 대상 mapId 일 때만 허용, 아니면 `FloorMismatchException`(신설) → **이동 금지**. ② 도면→맵 변환 — 대상 맵의 유효 T_W_D(맵버전 일치)가 있으면 `DrawingTransform.DrawingToMap` 적용, 없으면 항등(도면≈맵 placeholder, 3D 마커와 동일). ③ 액션 없는 **단일 노드 Order** 발행(`Vda5050MasterClient.PublishOrderAsync`) + 감사 로그 `MANUAL_GOTO`.
   - REST `POST /api/robots/{robotId}/goto` — 성공 200{mapX,mapY}, 층 불일치 **409**{error,reportedMapId,requestedMapId}, robot 없음 400. `GotoRequest` record.
2. **UI 클라이언트** — `IAcsApiClient.GotoAsync` + 구현(`EnsureSuccessOrThrowAsync`로 409/400 {error} 메시지 노출).
3. **UI (2D 평면도)** — `TankViewModel`에 `SelectedRobotId`·`PlanGotoStatus`·`GotoHereAsync(canvasPxX,py)`(캔버스 px→도면 좌표 역투영 = BuildPlan 투영의 역변환, 대상 mapId=`{TankId}-L{level}`; '전체' 뷰/로봇 미선택 시 거부, 서버 오류 메시지 표시). `TankView.xaml` 평면도 캔버스에 **우클릭 컨텍스트 메뉴 "여기로 이동"** + 안내/상태 텍스트. `TankView.xaml.cs` `OnPlanRightDown`(px 캡처)·`OnPlanGotoClick`(VM 호출). `ShellViewModel`이 운영 바 `Mission.SelectedRobotId`를 `Tank.SelectedRobotId`로 동기화.

### 검증
- `HD.Acs.App` / `HD.Acs.UI` 컴파일 **0 error**(별도 출력 경로 빌드).
- 층 게이트 로직: 로봇 보고 층 ≠ 대상 층이면 서버 409 → UI "이동 불가: …" 표시(같은 층일 때만 Order 발행). 라이브 E2E(App+PostgreSQL+시뮬레이터, 로봇이 해당 층 보고 중)는 수동 검증 대기.

### 후속
- 도면↔맵 정합을 위해 대상 층 캘리브레이션(T_W_D) 선행 권장(미보정 시 항등 매핑).
- 이동 취소/도착 확인 상태 표시(현재는 발행까지). 다중 로봇 시 대상 로봇 명시 UI 강화.

---

## 2026-08-27 — 운영 화물창 뷰에 "평면도(2D)" 탭 추가 (층별 로봇 이동 가능 구역)

### 배경
- 운영자가 **층마다 로봇이 움직일 수 있는 구역**을 위에서 내려다보는 2D로 확인하고 싶다는 요청.
- 3D 뷰는 형상 파악에 좋지만 카메라 조작이 필요해, 층별 이동 영역을 한눈에 보긴 불편.
- 노드/엣지/존 API는 미구현이라 **네비게이션 그래프 데이터는 없음** → 이동 구역은 선창 지오메트리에서 유도한 **상면(top-down) footprint** 로 표현(현재 데이터로 가능한 유일·정확한 방법). 백엔드/DB/VDA 5050 무변경, UI 렌더 계층만.

### 변경 내용
1. **`TankViewModel`** (`HD.Acs.UI/ViewModels/TankViewModel.cs`) — 상면 투영 로직 추가
   - `BuildPlan()`: 도면 x-y 평면에 선창을 상면 투영. **전폭 엔벨로프**(L×B, 점선)와 **선택 층 데크의 이동 가능 구역**(L×2·HalfWidth(deckZ), 초록 채움)을 캔버스 px로 산출. 데크 높이 z=`LevelZ[level-1]`에서 팔각 단면 반폭 `HalfWidth(z)`(하부챔퍼/수직벽/상부챔퍼 구간별 선형, 3D `HalfWidth`와 동일 정의) 적용 → 층마다 폭이 달라짐(바닥층 좁음→중간 전폭→천장층 좁음).
   - `BuildPlanRobot()`: 로봇 현재 위치(`RobotX/Y`)를 footprint와 **동일 변환**으로 마커 px 산출, 다른 층이면 흐리게(opacity 0.35). 3D 뷰와 동일한 도면-직접-매핑 placeholder(캘리브레이션 T_W_D는 후속).
   - 원점(바닥 중심) 마커·방위 라벨(+y 좌현/+x 선수)·치수 캡션 노출. 트리거: `LoadAsync`(성공·실패), `OnSelectedViewModeChanged`(층 변경), `OnRobotState`(로봇 갱신).
2. **`TankView.xaml`** — 3D 뷰 탭 **바로 옆에 "평면도(2D)" 탭** 신설. `Viewbox`+고정 `Canvas`(900×520, 정축척) 위에 엔벨로프(점선)·이동 구역(초록)·원점(노랑)·로봇(빨강, `BooleanToVisibilityConverter`로 표시 제어) 렌더. 상단 헤더의 기존 "뷰(전체/L1~L4)" 콤보를 그대로 공유 → 층 선택 시 평면도도 함께 갱신.

### 검증
- `HD.Acs.UI` 컴파일 **0 error**(실행 중 exe 잠금으로 최종 복사만 실패 → 별도 출력 경로 빌드로 0 error 확인).
- 실 데이터 렌더는 App+PostgreSQL 기동 + 로봇 state 수신 상태에서 육안 확인 필요(구 UI는 재기동 후 반영).

### 후속
- 로봇 위치·이동 구역을 **맵 프레임 정합**(캘리브레이션 T_W_D 역변환)으로 전환 시 3D·2D 모두 실좌표 정확도 향상.
- 노드/엣지/존 데이터·API가 생기면 footprint 대신 **실제 주행 가능 영역(그래프/존)** 오버레이로 확장.
- 검사 영역·정차점을 평면도에 함께 표시(선택).

---

## 2026-08-27 — ACS↔AMR 통신 프로토콜 두절/재접속/재동기화 E2E 하네스

### 배경
- 로봇측(HD_AMR) 유일 인터페이스는 VDA 5050 over MQTT이며, ADR-002는 **두절 내성 + 재접속 동기화**를 요구.
- 통신 계층(메시지 모델·마스터 클라이언트·Order 빌더·시뮬레이터)은 이미 구현돼 있었으나,
  기존 E2E 하네스(SimTest S1~S3 = 앵커 공유·파라미터 검증·실패 주입)는 **통신 프로토콜 견고성(두절/재접속/state 재동기화)을 전혀 검증하지 않는 공백**이 있었음.
- 이번 작업은 그 공백을 채우는 **연동 테스트·시뮬레이터** 범위. VDA 5050 메시지 계약·마스터 클라이언트·상태머신·DB는 무변경(테스트 계층만).

### 변경 내용
1. **시뮬레이터** (`src/HD.Acs.Simulator/Program.cs`) — 재접속 가능 구조로 리팩터
   - 인라인 핸들러/접속 로직을 로컬 함수로 추출: `OnMessageAsync` · `ConnectAndAnnounceAsync` · `DropAndReconnectAsync` (`client`를 가변화해 재접속 시 재생성).
   - **하네스 전용 제어 채널** `acs-sim/control/{manufacturer}/{serial}` 신설(VDA 5050 외부, 테스트 오케스트레이션 전용):
     `{"cmd":"drop","downMs":N}` 수신 → `client.Dispose()`로 소켓 급단절 → 브로커가 retain된 **Last Will(connection=CONNECTIONBROKEN)** 발행 → downMs 후 자동 재접속·재구독·`ONLINE`+state 재발행.
   - `state`를 **retain 발행**으로 전환 → 마스터(ACS) 재기동/재접속 시 진행 중 Order를 즉시 회수(= ADR-002 재동기화 근거).
   - `PublishAsync`를 `connected` 게이트 + try/catch로 가드 → 급단절 창에서 발행만 스킵, 진행 중 Order 실행 태스크는 메모리에서 계속 진행 → 재접속 후 이어서 완료 state 발행(연속성 관측 계약).
2. **SimTest 드라이버** (`src/HD.Acs.SimTest/Program.cs`) — connection 토픽 구독 + 관측 순서 추적 추가, 시나리오 신설
   - **S4 conn-lifecycle**: 구독 직후 retain된 `connection=ONLINE` 회수 확인.
   - **S5 disconnect**: 제어 drop → `CONNECTIONBROKEN` 관측(두절 감지) → 자동 재접속 `ONLINE` 복귀.
   - **S6 reconnect-sync**: 4노드 Order 진행 중 두절 주입 → 재접속 후 전 액션 `FINISHED` 수렴 + `state.orderId=SIMTEST-S6` 보존(재동기화).
3. **런너 주석** (`src/run_simtest.sh`) — 3개 → 6개 시나리오로 갱신.

### 검증
- `HD.Acs.Simulator` / `HD.Acs.SimTest` 빌드 0 error.
- **라이브 MQTT 브로커(localhost:1883)에서 E2E 6/6 PASS.** 시뮬레이터 로그로 *mid-order 두절 → Last Will → 자동 재접속 → retain state 재동기화(order=SIMTEST-S6) → Order 완료* 육안 확인.

### 후속(남은 통신 프로토콜 공백)
- **마스터측 자동 재접속 미구현**: `Vda5050MasterClient`는 기동 시 1회만 접속 — 브로커 두절 시 재접속·재구독 없음(ADR-002와 여전히 충돌). `MqttClient.DisconnectedAsync` 핸들러 + 백오프 재접속 + 재구독 필요(→ retain된 state/connection으로 자연 재동기화).
- 스키마 위반 메시지 알람 발행 TODO(`Vda5050MasterClient` 수신부).
- Order 거부/FAILED → 재시도(orderUpdateId+1)/스킵/알람 정책 TODO(`RobotStateService`).
- VDA 5050 factsheet 토픽 미지원.
