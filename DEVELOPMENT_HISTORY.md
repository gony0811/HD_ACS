# DEVELOPMENT_HISTORY.md

HD_ACS 개발 작업 요약 기록. 각 작업은 **날짜 · 제목 · 배경 · 변경 내용 · 검증 · 후속**으로 정리한다.
(상세 커밋 단위 이력은 `CLAUDE.md`의 "변경 이력" 절, 아키텍처 결정은 `docs/ARCHITECTURE_DECISIONS.md` 참고)

---

## 2026-08-28 — "이 노드로 이동" 추가 — 노드 등록 좌표로 정확 이동

### 배경
- 기존 "여기로 이동"은 우클릭한 **픽셀 지점**으로 보내 노드를 대략만 겨냥. 등록된 노드의 정확한 좌표로 이동하는 전용 명령 요청.

### 변경 내용
- `TankViewModel.GotoNodeAsync(px,py)`: 우클릭 지점 임계(16px) 내 노드를 `PickNodeAt`으로 집고, 클릭 픽셀이 아니라 그 노드의 **등록 도면 좌표**(`NodeDto.DrawingX/DrawingY`)로 `GotoAsync` 호출. 가드(층 L{n} 선택·로봇 선택)는 GotoHere와 동일, 노드 없으면 안내. 노드는 선택 층만 렌더되므로 mapId 층 일치 보장.
- 우클릭 메뉴에 "이 노드로 이동"(`OnPlanGotoNodeClick`) 추가.
- 서버 경로는 기존 `ManualGotoAsync` 재사용 → 층 게이트 + NOGO/HAZARD 목표점 게이트 그대로 적용. 노드↔목표 사이 **경로 계획은 HD_AMR 책임**(단일 노드 Order).

### 검증
- UI 빌드 0 error(temp 출력). 실동작은 App+UI 재기동 후 로봇·층 선택 상태에서 확인.

### 후속
- 여러 노드를 순서대로 경유(그래프 경로, multi-node Order)는 별도 작업. 현재는 단일 노드 직접 이동.

---

## 2026-08-28 — 낙상 위험(HAZARD) 구역 추가 — 필수 회피 존

### 배경
- "꼭 피해야 하는 지점(낙상 위험, 예: 엘리베이터 샤프트 개구부)"을 운영상 구분·표시하고 목표점 이동을 차단할 수단 요청.
- **안전 원칙 명확화**: 낙상 방지의 실질 보증은 물리 조치 + AMR 온보드(코스트맵 keep-out·cliff 감지)에 있음. ACS의 벽·NOGO·HAZARD는 AMR에 전달되지 않는 **자문(advisory) 계층**이며, HAZARD는 그 위의 운영자 인지 + 미션 목표 차단 역할. (실제 회피를 위한 AMR 맵-동기화는 VDA 5050 밖 협의사항 — 후속)

### 변경 내용
1. **백엔드**: `POST /api/map-annotations`가 `HAZARD` kind 허용(다각형 ≥3점, NOGO와 동일 검증). `MissionService.ManualGotoAsync` 목표점 게이트를 NOGO+HAZARD 모두 검사하도록 확장(HAZARD 안으로 goto 시 거부, 메시지에 "낙상 위험 구역" 표기). `ref.map_annotation.kind` 주석에 HAZARD 명시(CHECK 제약 없어 스키마 변경 불필요).
2. **UI**: `PlanTool.Hazard` + `StartHazardTool`(4점 다각형, NOGO 패턴 재사용), `PlanHazards` 렌더 컬렉션(주황 #E67E22 실선·굵은 테두리로 NOGO 빨강 점선과 구분), 우클릭 메뉴 "낙상 위험 지역", 등록 요소 필터에 "낙상 위험"·배지(낙상위험/주황)·hover 노랑 하이라이트·삭제 경로 연결.

### 검증
- App·UI 빌드 각 0 error(temp 출력으로 exe 잠금 회피). DB CHECK 제약 없어 마이그레이션 불필요.
- API/DB·UI 실동작은 App+UI 재기동 후 확인 필요.

### 후속
- ③ 접근 알람(로봇 state 위치가 HAZARD 근접/진입 시 알람·비상정지 후보)·그래프 경로 도입 시 HAZARD 통과 노드/엣지 배제.
- ④ ACS HAZARD 기하를 AMR keep-out 지도로 넘기는 맵-동기화 — HD_AMR과 인터페이스 협의(VDA5050_INTERFACE.md 미결 항목).

---

## 2026-08-28 — 등록 요소 타입 필터 + hover 시 평면도 오브젝트 노랑 하이라이트

### 배경
- "등록 요소" 탭에서 노드/엣지/벽/이동불가를 **구분해 보고**, 목록 항목에 **마우스를 올리면 어떤 오브젝트인지** 평면도에서 노랑으로 알 수 있게 요청.

### 변경 내용
1. **타입 필터**: `TankViewModel`에 `ElementFilters`(전체/노드/엣지/벽/이동 불가)·`SelectedElementFilter`. 전체 행은 `_allRows`에 두고 `ApplyElementFilter`가 선택 카테고리로 `ElementRows` 필터. `OperationView` "등록 요소" 탭 상단에 필터 콤보 추가.
2. **hover 하이라이트**: 목록 항목 Border `MouseEnter`/`MouseLeave` → `Tank.SetHighlight(id)`/`null`. `SetHighlight`가 id로 렌더 컬렉션(PlanNodes/Walls/NoGos/Edges)에서 오브젝트를 찾아 평면도에 노랑 오버레이(노드=링+채움, 벽/엣지=굵은 선, 이동불가=닫힌 폴리라인) 표시. `PlanEdgeVm`에 `EdgeId` 추가(하이라이트 매칭용), TankView에 노랑 하이라이트 레이어(Polyline+Ellipse) 추가. `OperationView.xaml.cs`에 hover 핸들러.

### 검증
- `HD.Acs.UI` 빌드 0 error. 백엔드/DB 무변경. 라이브는 UI 재기동 후.

---

## 2026-08-28 — (버그수정) 노드가 좌상단 구석에 몰리는 렌더 버그

### 원인
- 노드 `ItemsControl`(Canvas 패널)에서 `Canvas.Left/Top`을 **DataTemplate 안 Ellipse**에 걸었음. ItemsControl은 항목을 `ContentPresenter`로 감싸므로 위치 부착 속성은 **컨테이너**에 걸려야 Canvas가 배치한다 → 무시되어 모든 노드가 (0,0)에 겹침. (벽/구역/엣지는 `Points` 절대좌표라 무영향이었음.)

### 변경 내용
- `TankView` 노드 ItemsControl에 `ItemContainerStyle`(TargetType=ContentPresenter)로 `Canvas.Left={Binding LeftX}`·`Canvas.Top={Binding TopY}` 지정, Ellipse에서는 제거. 서버 데이터·좌표 변환은 정상이었음(검증: GET 응답 도면좌표 -12.5/10.6로 정상 분산).

### 검증
- `HD.Acs.UI` 빌드 0 error. 라이브는 UI 재기동 후.

---

## 2026-08-28 — 2D 평면도 네비게이션 그래프 편집(노드·엣지 등록)

### 배경
- 평면도에서 **실제 네비 그래프**(ref.node/ref.edge)를 등록하고 싶다는 요청. 결정: 실제 그래프(VDA order 경로 생성에 사용), **엣지=두 노드 연결**.

### 변경 내용
1. **백엔드** `GraphEditService`(신규): 노드 좌표는 **맵 프레임**(VDA nodePosition)으로 저장하며, 평면도 도면 좌표 ↔ 맵 좌표는 층 유효 **T_W_D**로 변환(캘리브레이션 없으면 항등 — 기존 goto/마커와 동일). `ref.map` 없으면 자동 생성(FK). 엣지는 두 노드 연결(자기연결·다른층·중복 가드). 노드 삭제 시 인접 엣지 정리. REST `POST/GET/DELETE /api/nodes`·`/api/edges`(노드 X/Y=도면 좌표 입력→서버가 맵 좌표 변환·응답에 도면 좌표 동봉해 렌더 지원), DI 등록. `ref.map/node/edge`는 기존 스키마라 마이그레이션 불필요.
2. **UI 클라이언트**: `NodeDto`/`EdgeDto` + `Get/Create/Delete Node·Edge`.
3. **UI (TankViewModel)**: 등록 도구에 `Node`(단일 클릭 생성, 모드 유지)·`Edge`(노드 2개 클릭 연결, 시작 노드 하이라이트) 추가. 노드 픽(16px 임계 최근접), 그래프 로드/렌더(`PlanNodes`/`PlanEdges`, 도면 좌표→px), Shift 축 정렬은 노드 배치에도 적용. "등록 요소" 탭을 **통합 목록**(`ElementRows`: 벽·이동불가·노드·엣지)으로 재편 + 카테고리별 삭제(`DeleteElementCommand`).
4. **UI (뷰)**: `TankView` 컨텍스트 메뉴에 "노드 생성"·"엣지 연결" + 노드(원)·엣지(선)·시작노드 링 렌더. `OperationView` "등록 요소" 탭 배지/삭제를 4종(WALL/NOGO/NODE/EDGE)으로 확장.

### 검증
- `HD.Acs.App`/`HD.Acs.UI` 빌드 **0 error**. DB `ref.map→node→edge` FK 체인 삽입/삭제 검증(2노드·1엣지). 라이브 UI/E2E는 App+UI 재기동 후.

### 참고(경계)
- 등록 노드/엣지는 `OrderBuilder`+`MapGraph`(그래프 기반 Order 경로)에서 소비된다. 현재 활성 배차(`InspectionDispatcher`)는 정차 단위 단일 노드 Order라 이 그래프를 직접 쓰지 않음 — 그래프 기반 경로(seam/inspection_point 경로) 활용 시 반영.

### 후속
- 노드 타입 선택 UI(현재 WAYPOINT 고정)·엣지 방향/타입(양방향 TRAVEL 고정)·노드 이동/편집·.hdacs 파일 포함.

---

## 2026-08-28 — 2D 평면도 등록 Shift 축 정렬(수평/수직 스냅)

### 배경
- 벽·이동 불가 구역 선택 중 Shift를 누르면 직전 꼭짓점 기준 수평/수직으로 정렬되게 해달라는 요청(CAD 관행).

### 변경 내용
- `TankViewModel.SnapDrawing(px,py,shift)`: 포인터→도면 좌표 역투영 후 Shift+직전 꼭짓점이 있으면 우세 축으로 스냅(|Δx|≥|Δy|→수평[Δy=0], 아니면 수직[Δx=0]). `PlanToolClickAsync`(클릭 확정)·`PlanHover`(러버밴드·리드아웃) 모두 동일 스냅 적용 → 미리보기 끝점=실제 클릭 위치 일치. 리드아웃에 "⇥ 정렬" 표시. `ToPx` 헬퍼 추출.
- `TankView`: MouseMove/좌클릭 핸들러가 `Keyboard.Modifiers`의 Shift 상태를 VM에 전달.

### 검증
- `HD.Acs.UI` 빌드 0 error. 백엔드/DB 무변경.

---

## 2026-08-28 — 2D 평면도 등록 도구 러버밴드(마지막 꼭짓점→포인터 선)

### 배경
- 벽·이동 불가 구역 등록 중, 다음 점을 찍기 전에 직전 꼭짓점과 포인터 사이 선을 보고 싶다는 요청.

### 변경 내용
- `TankViewModel.PlanHover`: 등록 도구 활성 + 확정 점이 있으면 `PlanDraft`(기존 미리보기 폴리라인)를 "확정 점들 + 현재 포인터"로 갱신 → 마지막 꼭짓점→포인터 러버밴드 실시간 표시. `PlanHoverLeave`는 도구 활성 시 `UpdateDraft`로 러버밴드 제거(확정 점만 유지). 렌더는 기존 `PlanDraft` 폴리라인 재사용(XAML 무변경).

### 검증
- `HD.Acs.UI` 빌드 0 error. 백엔드/DB 무변경.

---

## 2026-08-28 — 2D 평면도 마우스 hover 좌표 리드아웃

### 배경
- 평면도에서 포인터 위치의 도면 좌표를 즉시 확인하고 싶다는 요청.

### 변경 내용
- `TankViewModel.PlanHover(px,py)`: 캔버스 px를 도면 좌표(m)로 역투영(기존 투영의 역변환) → `PlanHoverText`("x=…, y=… m")·포인터 옆 라벨 위치(`PlanHoverLabelX/Y`)·`PlanHoverVisible` 갱신. `PlanHoverLeave`로 숨김.
- `TankView`: 평면도 캔버스 `MouseMove`/`MouseLeave` 핸들러 + 포인터 옆 반투명 좌표 라벨(`IsHitTestVisible=False`로 클릭 방해 없음). Viewbox 안이라 라벨도 함께 스케일.

### 검증
- `HD.Acs.UI` 빌드 0 error. 백엔드/DB 무변경(순수 뷰 계층). 라이브는 UI 재기동 후.

---

## 2026-08-28 — 2D 평면도 맵 주석: 벽 생성 · 이동 불가 구역 + 등록 요소 탭 (DB 백엔드)

### 배경
- 2D 평면도에서 운영자가 **우클릭 메뉴로 벽(WALL)·이동 불가 구역(NOGO)을 등록**하고, 우측 패널에서 목록으로 관리하고 싶다는 요청.
- 결정(사용자 확인): **DB 백엔드**(영속·다중 사용자), **이동 불가 구역은 "여기로 이동"(goto) 차단**.

### 변경 내용
1. **DB/백엔드**: `ref.map_annotation`(annotation_id·tank_id·level·kind[WALL|NOGO]·name·points jsonb·created_at) 신설 — `db/schema.sql` + `db/migrations/2026-08-28_map_annotation.sql`(dev-postgres 적용). `MapAnnotationEntity`/DbSet/매핑(jsonb+인덱스). REST `POST/GET/DELETE /api/map-annotations`(kind 검증·최소 점수[WALL 2·NOGO 3] 검증·감사로그 MAP_ANNOTATION_ADD). 좌표는 **도면 프레임 [[x,y]…]**.
2. **goto 게이트**: `MissionService.ManualGotoAsync`에 NOGO 검사 추가 — 대상 지점(도면 좌표)이 그 층 NOGO 다각형 안이면 `NoGoZoneException`(신설) → **409**(이동 거부). `AreaGeometry.PointInPolygon`(단위테스트됨) 재사용, `ParseMapId`로 mapId→(tank,level).
3. **UI 클라이언트**: `MapAnnotationDto` + `IAcsApiClient.GetMapAnnotations/CreateMapAnnotation/DeleteMapAnnotation` + 구현.
4. **UI (TankViewModel)**: 등록 도구 상태머신(`PlanTool` None/Wall/NoGo) — `StartWallTool`/`StartNoGoTool`/`CancelTool` 명령, `PlanToolClickAsync`(캔버스 px→도면 좌표 역투영·점 누적, WALL 2점/NOGO 4점 도달 시 등록), 진행 미리보기(`PlanDraft`). 렌더 컬렉션 `PlanWalls`(선분)·`PlanNoGos`(다각형)를 `BuildPlan` 투영에 맞춰 갱신(선택 층 필터). `AnnotationRows`(전 층 목록) + `DeleteAnnotation` 명령.
5. **UI (뷰)**: `TankView` 평면도 캔버스에 우클릭 메뉴(여기로 이동/벽 생성/이동 불가 지역/취소) + 좌클릭(도구 활성 시 점 지정)·ESC 취소 + 벽/구역/미리보기 렌더. `OperationView` 우측 패널을 탭으로 전환(**알람·이벤트** / **등록 요소**=벽·구역 목록+삭제, DataContext=Tank).

### 검증
- `HD.Acs.App`/`HD.Acs.UI` 빌드 **0 error**. 마이그레이션 dev-postgres 적용. `ref.map_annotation` **jsonb 왕복 검증**(INSERT/SELECT/DELETE, jsonb_array_length=4).
- goto NOGO 게이트는 단위테스트된 `PointInPolygon` 재사용. 라이브 UI/E2E는 App+UI **재기동 후** 확인(현재 실행 중인 :5100 App·UI는 구빌드).

### 후속
- .hdacs 프로젝트 파일에 맵 주석 포함(내보내기/가져오기), 벽의 goto/네비 반영 여부(현재 벽=시각 주석), 이름 지정 UI.

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
