# HD_ACS 개발 가이드 — 현황과 로드맵

> **대상 독자**: 개발을 이어갈 사람(그리고 Claude Code). "지금 어디까지 왔고, 다음에 무엇을 해야 하는가."
> 사용법은 `MANUAL.md`, 상세 구현 사양은 `src/SPEC_PHASE2_ACS.md` 참고.
> 최종 갱신: 2026-08-03 (커밋 `773441e` 기준 — 코드 대조하여 작성)

---

## 1. 프로젝트가 여기까지 온 경로 (기억 복원용 타임라인)

전체 개발은 두 축(PHASE)으로 재정리되어 있다 (2026-07-29 확정 표기 — "대분류"라 부르지 않음):

- **PHASE 1 = 용접 검사 오토 시퀀스 (HD_AMR 온보드) → 완료.**
  전제: AMR이 용접 위치에 도착해 있음. 8단계: ①코봇 검사대기 ②3D 카메라 이동 ③평탄면 탐색
  ④수평 얼라인(3점 레이저 3-Phase) ⑤코로게이션 탐색 ⑥용접라인 탐색 ⑦도면 로딩·검사 ⑧복귀.
- **PHASE 2 = AMR을 용접 TASK 위치로 이동시키는 관제 (HD_ACS, 본 저장소) → 진행 중.**
  확보 필요 기술 3종: (1) 좌표계 변환(T_W_D) (2) TASK 생성 기준(슬라이싱) (3) 표준 UI.

HD_ACS 저장소 타임라인:

| 시점 | 내용 |
|---|---|
| 2026-07-15 | 설계 집중일 — ADR-001~011 확정, DB 스키마(ref/run/hist/alarm/sys) 확정, 솔루션 골격 6프로젝트 생성 (상태머신·그래프·OrderBuilder·state 대조·층 게이트·비상정지) |
| 2026-07-29 | UI 본구현(Telerik Fluent + HelixToolkit, RadDocking 셸) · **PHASE 2 설계 확정 → `SPEC_PHASE2_ACS.md` 작성** (TASK 불변식, 앵커 공유 C안, T_W_D 등록 절차, payload 메타데이터) · WP-4 시뮬레이터 확장 + SimTest 구현·3시나리오 PASS |
| 2026-07-30 | WP-1 map calibration 구현 (DrawingTransform 최소자승 + API 5개 + xUnit 8테스트) |
| 2026-07-31 | WP-2/WP-2.5 구현 — weld_seam 스키마, SeamSlicer(+테스트), SeamPlanningService, generate-from-seams, 캘리브레이션 UI 패널 |
| 2026-08-04 | WP-5b — seam CRUD·스테이션 조회 API, 슬라이싱 시각화 UI 패널(SlicingView) |

---

## 2. ★ 절대 위반하면 안 되는 설계 불변식

새 코드를 쓰기 전에 이 목록과 충돌하지 않는지 먼저 확인한다. (출처: CLAUDE.md, SPEC §1, ADR)

1. **단일 상대 원칙 [ADR-001]** — ACS의 상대는 HD_AMR 하나, 인터페이스는 VDA 5050 하나.
   AMR/코봇/검사장비 개별 제어 코드, 자세·시퀀스 계산 코드를 관제에 넣지 않는다.
2. **TASK 불변식** — TASK 1개 = 용접라인 1개의 코봇 리치 안 1개 구간.
   TASK 1 = actionId 1 = inspection_result 1행 = 재시도 단위 1 = 단면 DXF·프로파일 1 = 진행방향 1.
   (액션 하나에 seams 배열을 싣는 설계는 **기각됨** — 다중 라인은 anchorGroupId 공유로 해결.)
3. **actionId는 ACS가 발급**(GUID)하고 DB에 보존 — AMR이 생성하지 않는다. state 대조 키.
4. **전송하지 않는 것** — Cobot BASE 좌표(온보드가 실측 pose로 변환), 재시도/스킵 정책(ACS policy가 판단),
   DXF 원문(사전 배포 + ID 참조), SLAM 맵 데이터(상호 불필요).
5. **좌표 규약** — VDA 5050 구간은 m/rad(theta = 맵 X축 CCW). 도면 mm는 입력 시점에 m로 정규화.
   z는 2D 변환 대상 아님(통과). m↔mm 환산은 로봇 온보드 한 곳으로 통일.
6. **ACS는 SLAM 맵 원본을 보유하지 않는다** — 맵 좌표계 접근은 T_W_D(층별 tx,ty,yaw)로만.
   T_W_D는 `mapId + ref.map.version`에 바인딩 — 맵 재생성 시 자동 무효, 재등록 강제.
7. **robot-is-truth** — 실행 상태의 진실은 로봇의 state 보고. ACS DB는 이를 반영할 뿐이다.
8. **명시적 실패** — 유효 T_W_D 없음 등 전제 미충족 시 조용한 기본값 금지, 사유와 함께 거부.
9. **시나리오는 데이터** — 검사 시나리오·정책을 코드에 하드코딩하지 않는다.
10. **한국어 문서화 + 문서 동기화** — 아키텍처/인터페이스 변경 시 docs/와 CLAUDE.md를 함께 갱신.

---

## 3. 구현 현황 (2026-08-03, 코드 대조 결과)

### 완료 ✅

| 영역 | 내용 |
|---|---|
| 골격 전체 | 미션 상태머신(Stateless), MapGraph(Dijkstra), OrderBuilder(노드=짝수/엣지=홀수 seq, 동일노드 액션 병합), MissionService(층별 분해·릴리즈 가드), RobotStateService(actionId 대조), 비상정지, 수동 층 변경 |
| WP-1 T_W_D | `DrawingTransform`(2D 강체 최소자승, 잔차 RMS/Max) + calibration API 5개 + 맵버전 바인딩 + 감사로그 + xUnit 테스트 |
| WP-2 슬라이싱 | `ref.weld_seam` 스키마, `SeamSlicer`(LINE/POLYLINE 분할, 스테이션 병합·anchorGroup·seqInGroup) + 테스트, `SeamPlanningService`(STATION 노드·TRAVEL 엣지·Point/Task 생성, T_W_D 필수 가드) |
| WP-4 시뮬레이터 | 파라미터 검증, 앵커 공유(FULL/SHARED), 실패 주입, 관측 계약(resultDescription) + `HD.Acs.SimTest` 3시나리오 + `run_simtest.sh` — 전체 PASS 확인됨 |
| WP-5 일부 | 캘리브레이션 패널(CalibrationView), 슬라이싱 시각화 패널(SlicingView), seam CRUD·스테이션 조회 API |
| UI 셸 | Telerik Fluent RadDocking, 로봇상태/미션/알람/수동층변경 패널, REST/SignalR 계약 레이어 |

### 미완료 / 잔여 ⬜ (다음 작업 후보 — 우선순위순)

> (해소됨) ~~WP-3 릴리즈 payload 빌드~~ — 2026-08-04 완성(T_W_D 적용·스키마 검증·시드, wallNormalW는 SPEC v2에서 계약 제거).
> 2026-08-28 `VDA5050_INTERFACE_SPEC.md` 계약으로 확장 완료: drawingPos u,v, greedy 배차 경로 params 완전 채움+발행 전 검증.
> (해소됨) ~~E2E (PostgreSQL 포함)~~ — 2026-08-28 **DB 포함 풀 E2E 최초 통과** (영역→run→greedy 단일노드 Order→시뮬레이터 검증
> 통과→앵커 FULL/SHARED→work_item DONE→run COMPLETED + 실패 주입→재시도 2회→SKIPPED+INSPECTION_SKIPPED 알람).
> 이 과정에서 잠복 결함 3건 수정: order_node PK 충돌(정차마다 seq=0 재INSERT), alarm.spec 시드 누락(FK 위반),
> state 핸들러 예외 미격리(MQTT 수신 체인 사망). seam 경로(dormant) 기준 SPEC §7 E2E는 미수행.
3. **TASK 관리 3-Pane UI (WP-5 본편)** — 트리 / 벽면 전개도 / 상세.
   표현 규칙 확정분: 영역(anchorGroup)=반투명 박스, TASK=선분 오버레이(상태색+방향 화살표+seqInGroup 배지),
   박스 클릭=그룹 선택·선분 클릭=TASK 선택, 영역 상태=자식 집계, 그룹 첫 TASK "정렬 포함"/이후 "정렬 공유" 배지.
4. **FAILED 정책 엔진** — 기본 동작(재시도 N회→스킵→알람)은 디스패처에 구현·E2E 검증됨(2026-08-28,
   재발행은 orderUpdateId+1 대신 **신규 orderId** — VDA5050_INTERFACE_SPEC §4.1 계약). 잔여: scenario policy
   jsonb 해석(재시도 횟수 등 시나리오별 정책 외부화), errors 유형 코드별 분기(사양서 협의 N6 후).
5. **이력/리포트·알람 API** — UI가 방어적 빈 상태로 처리 중인 미구현 백엔드(알람 조회, 검사 이력, 리포트).
   inspection_result와 NodeId 조인 정리 포함.
6. **Tank 3D/전개도 본구현** — TankView는 골격 상태(HelixToolkit 도입만 완료).
7. **인증 미들웨어, Web 대시보드/태블릿 클라이언트** — 후순위.

### 미결 질문 (ADR 문서에서 추적, `docs/ARCHITECTURE_DECISIONS.md` §미결)

| # | 내용 | 상태 |
|---|---|---|
| Q1 | 커스텀 액션 카탈로그 | ✅ 해소 — `VDA5050_INTERFACE_SPEC.md`로 이관(v2.0·startWeldInspection·param_schema u,v 시드). HD_AMR 회신 대기 항목은 사양서 §10 |
| Q2 | 검사 S/W와 위치/시각 키 규약 | ⬜ 좌표계·타임스탬프 기준 협의 필요 |
| Q4 | 배포 방식 (Windows 서비스 vs Docker) | ⬜ |
| Q6 | 화물창 맵 데이터 소스 | 사실상 방향 확정 — 도면 좌표 + T_W_D (ADR 갱신 필요) |
| Q7 | 안전 요구사항 명세 | ⬜ 하드웨어 E-Stop 체계와의 관계 문서화 |

### HD_AMR(로봇 측) 대응 필요 사항 — ACS 코드 기준 인터페이스 요구

실장비 통합 시 로봇 측에 필요한 것 (SimTest 시나리오를 그대로 수용 기준으로 재사용 가능):

- VDA 5050 로봇 구현: `order`/`instantActions` 구독, `state` 2초 주기 발행(**agvPosition.mapId 포함** — 층 게이트·캡처의 근거), `connection` Last Will
- `startWeldInspection` 액션 핸들러: payload만으로 자기완결 실행(하이브리드 — jobRef는 역추적 전용),
  실측 T_W_A·T_A_B로 seam을 BASE 변환 → PHASE 1의 8단계 시퀀스 진입
- 앵커 캐시: 동일 anchorGroupId 연속 + 주행 없음 → 정렬(①~④) 스킵. 무효화 조건: 주행 발생 / 직전 보정 실패 / 그룹 변경 / 재시도 Order 재수신
- 다중 라인 구분: payload의 seam 기하 피드포워드로 기대위치 산출 → 최근접 매칭 + 이격 게이트
- 선행 잔여: 오일러 ZYX 규약 실장비 검증, m/rad↔mm/deg 환산 지점 통일, T_W_D 실측 등록

---

## 4. 코드 지도 (어디를 고치면 되는가)

| 하고 싶은 일 | 위치 |
|---|---|
| REST API 추가/변경 | `HD.Acs.App/Program.cs` (minimal API + record DTO가 파일 하단) |
| Order 생성·릴리즈 로직 | `HD.Acs.App/Services/MissionService.cs` (payload 조립 ~L133) + `HD.Acs.Vda5050/OrderBuilder.cs` |
| state 수신·대조 | `HD.Acs.App/Services/RobotStateService.cs` |
| 슬라이싱·스테이션 산출 | `HD.Acs.Core/Planning/SeamSlicer.cs` + `HD.Acs.App/Services/SeamPlanningService.cs` |
| 좌표 변환 | `HD.Acs.Core/Geometry/DrawingTransform.cs` |
| DB 스키마/엔티티 | `db/schema.sql` ↔ `HD.Acs.Data/Entities/*` + `AcsDbContext` (snake_case 매핑) — **양쪽 동시 갱신** |
| 시뮬레이터 동작 | `HD.Acs.Simulator/Program.cs` / 검증 드라이버 `HD.Acs.SimTest/` |
| UI 패널 추가 | `HD.Acs.UI.Core/ViewModels`(프레임워크 중립 VM — System.Windows/Avalonia 금지) + 뷰는 **두 헤드 모두**(`HD.Acs.UI/Views` WPF, `HD.Acs.UI.Desktop/Views` Avalonia) + DI 등록(`App.xaml.cs` / `AppHost.cs`) + `IAcsApiClient` 계약. UI 스레드·대화상자는 `Abstractions/IUiDispatcher·IDialogService·IProjectDialogService` 경유. Avalonia 뷰는 `HD.Acs.UI.Desktop.Tests` 헤드리스 스모크에 x:Name 그리드/탭을 등록 |

---

## 5. Claude Code로 개발을 재개하는 방법

이 저장소는 처음부터 Claude Code와의 대화로 개발되었고, 그 전제로 문서가 구조화되어 있다.

1. **컨텍스트 주입 순서**: `CLAUDE.md`(자동 로드) → 작업이 PHASE 2 범위면 `src/SPEC_PHASE2_ACS.md`를 읽게 할 것 —
   이 사양서는 "Claude Code가 이 문서만으로 구현 가능"하도록 작성된 계약 문서다.
   설계 판단이 필요하면 `docs/ARCHITECTURE_DECISIONS.md`의 확정 ADR·미결 Q를 먼저 확인시킨다.
2. **작업 지시 예시**: "SPEC_PHASE2_ACS.md §4.2 기준으로 MissionService 릴리즈 payload에 T_W_D 적용과
   param_schema 검증을 구현해줘. 부록 A golden fixture와 필드 단위 일치하는 테스트 포함." —
   섹션 번호로 지시하면 사양서가 곧 요구사항 명세가 된다.
3. **검증 루프**: 단위 테스트는 `dotnet test src/HD.Acs.Core.Tests`·`dotnet test src/HD.Acs.UI.Core.Tests`·`dotnet test src/HD.Acs.UI.Desktop.Tests`(헤드리스 UI), 통신 검증은 `src/run_simtest.sh`.
   payload 변경 시 SPEC 부록 A와의 golden test를 깨뜨리지 않는지 확인.
4. **완료 후 의무**: 결정이 새로 내려지면 ADR 갱신, 인터페이스가 바뀌면 해당 docs/ 문서와 CLAUDE.md 변경 이력에 한 줄 추가.
   이 관행 덕분에 대화 기억이 사라져도 문서로 복원된다 — 이 가이드 자체가 그 산물이다.
5. **환경 주의**: Mac에서는 UI 프로젝트(net8.0-windows) 제외 빌드. Telerik 피드는 자격증명 필요.
   폐쇄망 전제 — 외부 인터넷 의존 요소를 추가하지 말 것 [ADR-003].

---

## 6. 권장 다음 스프린트 (제안)

1. **WP-3 마무리** — 릴리즈 시 T_W_D 적용 + param_schema 시드·검증 (§3 잔여 1번). 이것이 끝나야
   "도면 좌표 등록 → 맵 좌표 payload 전송"이 실제로 닫힌다.
2. **DB 포함 E2E 1회 완주** — SPEC §7 E2E 체크리스트 4항목 통과 기록 남기기 (스크립트화 권장).
3. **TASK 관리 3-Pane UI** — 확정된 표현 규칙으로 구현, 운영자 시연 가능 상태 확보.
4. **FAILED 정책 엔진** — 재시도→스킵→알람 경로를 시뮬레이터 실패 주입으로 검증.
5. 이후: 이력/리포트 API → Tank 전개도 본구현 → 실장비 HD_AMR 통합 (SimTest 시나리오 = 수용 기준).
