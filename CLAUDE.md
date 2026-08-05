# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요 (Project Overview)

**HD_ACS** 는 **HD현대중공업 LNG 화물창 용접검사로봇**을 운영하기 위한 **관제 시스템(Mission Control System)** 프로젝트이다. (라이선스: Apache 2.0)

### 운용 환경: LNG 화물창 (LNG Cargo Containment System)
- 멤브레인 타입 LNG 화물창 내부 — 바닥/벽면이 주름진(corrugated) 스테인리스 멤브레인으로 구성된 요철 환경
- 밀폐/협소 공간, GPS 불가, 멤브레인 표면 손상 방지 요구 등 일반 실내 물류 환경과 다른 제약 존재
- 검사 대상: 멤브레인 용접부 (Weld Seam/Bead)

### 대상 로봇 시스템 구성
로봇 측은 **HD_AMR 통합 운영 S/W** 가 아래 하드웨어를 모두 통합 제어한다.
**HD_ACS는 개별 장치(AMR/협동로봇/검사장비)와 직접 통신하지 않으며, 유일한 상대는 HD_AMR이고 인터페이스는 VDA 5050 하나뿐이다.**
검사 시퀀스와 자세 제어는 전부 HD_AMR의 책임이다.

- **모바일 플랫폼**: 현대 AMR — 요철 멤브레인 바닥 위 자율 주행으로 검사 지점 이동
  - 플랫폼 측 기술 스택(참고, ACS 범위 외): 4WD 옴니휠 + Airless 타이어, Ouster OS0-32 3D LiDAR, FAST-LIO2 SLAM, ROS2 Humble + Nav2, Jetson AGX Orin
- **매니퓰레이터**: AMR 상단 협동로봇(Cobot) — 검사 자세/접근 경로 (HD_AMR이 제어)
- **검사 장비**: 엔드이펙터 카메라 및 측정 장비 — 용접 부위 촬영/측정 (HD_AMR이 제어)

### HD_ACS의 역할
HD_ACS는 위 하드웨어를 직접 제어하는 로봇 컨트롤러가 아니라, **검사 시나리오의 계획·배차·실행·모니터링·데이터 수집을 총괄하는 상위 관제 계층**이다.

핵심 책임:
1. **검사 시나리오 관리** — 검사 대상(용접 부위) 목록, 검사 순서, 검사 조건(카메라/측정 파라미터)을 시나리오 단위로 정의·저장·버전 관리
2. **미션 디스패치** — 시나리오를 VDA 5050 Order(검사 지점 노드 + 검사 액션)로 변환하여 HD_AMR에 전달. 검사 시퀀스·자세 제어의 실행 방법은 HD_AMR이 결정한다
3. **실시간 모니터링** — HD_AMR이 VDA 5050 state로 보고하는 로봇 위치/상태·검사 진행률·오류를 집계하여 다중 사용자에게 전파
4. **실행 기록 관리** — 촬영 명령의 위치·시각·성공/실패를 기록 (이미지 자체는 별도 검사 S/W 책임), 이력 관리 및 리포팅
5. **예외 처리** — 주행 실패, 검사 실패, 장비 오류 시 재시도/스킵/알람 등 운영 정책 실행

## 시스템 아키텍처 (System Architecture)

```
┌─────────────────────────────────────────────┐
│                 HD_ACS (관제)                 │
│ 시나리오 관리 │ 미션 오케스트레이터 │ 모니터링 │ 이력/리포트│
└──────────────────────┬──────────────────────┘
             VDA 5050 over MQTT
        (유일한 로봇측 인터페이스, 두절 허용)
┌──────────────────────▼──────────────────────┐
│      HD_AMR 통합 운영 S/W (로봇 온보드, 기존 존재) │
│  AMR 주행 │ 협동로봇 검사 시퀀스/자세 │ 검사장비 제어  │
└──────┬───────────────┬───────────────┬──────┘
   현대 AMR          협동로봇       카메라/측정장비
```

- 상세 아키텍처: `docs/ARCHITECTURE.md` 참고
- 검사 시나리오 모델 및 상태 머신: `docs/INSPECTION_SCENARIO.md` 참고
- 용어 정의: `docs/GLOSSARY.md` 참고

## 검사 워크플로우 (표준 시나리오)

1. 운영자가 HD_ACS에서 검사 시나리오 선택/생성 (검사 대상 용접 부위 목록 + 순서)
2. HD_ACS가 미션 생성 → AMR에 첫 검사 지점으로 이동 명령
3. HD_AMR이 도착 확인 후 해당 지점의 검사 작업(자세 시퀀스 + 촬영/측정)을 자율 실행 — 실행 방법은 전적으로 HD_AMR 책임
4. HD_ACS는 촬영 명령 액션에 위치 정보를 전달하고 성공/실패 응답을 기록 [ADR-004]
5. HD_AMR의 state 보고로 진행률 갱신 → 다음 검사 지점으로 반복
6. 시나리오 완료 → 검사 리포트 생성

각 단계는 상태 머신으로 관리되며, 실패 시 운영 정책(재시도 N회 → 스킵 → 알람)에 따라 처리한다.

## 기술 스택 (Tech Stack)

- **관제 서버**: C# / ASP.NET Core — REST API + SignalR(실시간 푸시)
- **주 운영 UI**: WPF 데스크톱 앱 (화물창 3D 뷰 + 전개도)
- **보조 UI**: Web 대시보드, 태블릿 (REST API + SignalR 공용)
- **로봇 통신**: VDA 5050 over MQTT (로봇 측 통합 운영 S/W와 연동, 온보드 실행형)
- **메시징**: MQTT 브로커 (서버 내 배치, 제품 선정 미결)
- **데이터베이스**: PostgreSQL + EF Core (NAMUGA_ACS 자산 승계)
- **배포**: 현장 이동식 서버 1대, 폐쇄망 — HD_ACS 앱(단일 프로세스) + Mosquitto + PostgreSQL을 OS 서비스로 등록·자동 재시작, 이중화 미도입 (ADR-011)

- **기반 플랫폼**: 사내 NAMUGA_ACS 플랫폼 참조 개발 — .NET 8, Autofac 모듈러 DI, Serilog, Quartz
- **미션 오케스트레이터**: Stateless 라이브러리 기반 명시적 상태머신 + EF Core 이벤트 로그 (ADR-010 확정, Elsa 제외)

핵심 아키텍처 결정과 근거는 `docs/ARCHITECTURE_DECISIONS.md` (ADR-001~010) 참고.
참조 플랫폼 분석·재사용 전략은 `docs/REFERENCE_NAMUGA_ACS.md` 참고. 미결 항목(Q1~Q8)은 ADR 문서에서 관리한다.

## 저장소 구조 (Repository Structure)

> 현재 저장소는 초기 상태(README, LICENSE만 존재)이다. 코드 추가 시 실제 구조를 반영하여 갱신할 것.

```
HD_ACS/
├── CLAUDE.md              # 이 파일 — Claude 작업 시 최우선 참고
├── README.md              # HD현대중공업 LNG 화물창 용접검사로봇 관제 시스템
├── LICENSE                # Apache License 2.0
├── docs/                  # 프로젝트 문서
│   ├── PROJECT_OVERVIEW.md    # 프로젝트 배경/목표/범위 상세
│   ├── ARCHITECTURE.md        # 시스템 아키텍처 (확정 결정 반영판)
│   ├── ARCHITECTURE_DECISIONS.md # ADR — 아키텍처 결정 기록 (결정/근거/미결 추적)
│   ├── INSPECTION_SCENARIO.md # 검사 시나리오 모델/상태 머신
│   ├── TANK_WALL_LAYOUT.md    # 화물창 전개도 구성·벽면 naming rule (위치 주소 체계 기준)
│   ├── GRAPH_DATA_MODEL.md    # 그래프 자료구조·DB 설계 (VDA 5050 Order 생성 기반)
│   ├── DB_SCHEMA.md           # DB 스키마 카탈로그 (28테이블+2뷰, ERD)
│   └── GLOSSARY.md            # 용어 정의
├── db/
│   └── schema.sql             # 통합 DDL (PostgreSQL — ref/run/hist/alarm/sys 스키마 + snake_case)
└── src/                       # .NET 8 솔루션 (HD.Acs.sln)
    ├── HD.Acs.Core/           # 도메인 — MissionStateMachine(Stateless), MapGraph(Dijkstra)
    ├── HD.Acs.Data/           # EF Core 엔티티/DbContext (ref/run/hist/alarm/sys), GraphLoader
    ├── HD.Acs.Vda5050/        # VDA 5050 메시지·마스터 MQTT 클라이언트·OrderBuilder
    ├── HD.Acs.App/            # ASP.NET Core 호스트 — REST + SignalR + VDA 브릿지 [ADR-011]
    ├── HD.Acs.Simulator/      # VDA 5050 로봇(HD_AMR) 시뮬레이터
    └── HD.Acs.UI/             # WPF 운영 앱 (Telerik UI for WPF·Fluent + HelixToolkit 3D, MVVM/Generic Host DI)
        ├── Models/                # 백엔드 페이로드 미러 DTO
        ├── Services/              # IAcsApiClient(REST) · IMonitoringClient(SignalR) · TankLayout
        ├── ViewModels/            # Shell + 로봇상태/미션/알람/수동층변경/Tank (CommunityToolkit.Mvvm)
        ├── Views/                 # UserControl (Telerik 컨트롤) · TankView(3D+전개도)
        └── MainWindow.xaml        # RadDocking 셸 (좌: 화물창 뷰, 우: 운영 패널)
```

## Claude 작업 가이드라인

Claude가 이 저장소에서 작업할 때 지켜야 할 원칙:

1. **문서 우선 참조**: 새로운 기능 설계/구현 전에 `docs/` 하위 문서, 특히 `ARCHITECTURE_DECISIONS.md`의 확정 결정(ADR)과 미결 항목(Q1~Q7)을 먼저 확인할 것. 확정 ADR과 충돌하는 설계를 하지 말고, 미결 항목에 대한 결정이 내려지면 ADR 문서를 갱신할 것
2. **단일 상대 원칙**: HD_ACS의 로봇측 상대는 HD_AMR 하나뿐이며 인터페이스는 VDA 5050 하나뿐이다. AMR/협동로봇/검사장비를 개별 제어하는 코드, 자세·시퀀스를 계산하는 코드를 관제에 절대 넣지 말 것 — 그것은 HD_AMR의 책임이다
3. **시나리오는 데이터**: 검사 시나리오는 코드에 하드코딩하지 않고 데이터(설정/DB)로 정의하여 운영자가 수정 가능하게 유지
4. **상태 머신 기반 설계**: 미션/검사 단계의 상태 전이는 명시적 상태 머신으로 구현하고, 모든 실패 경로에 대한 처리 정책을 정의할 것
5. **한국어 문서화**: 프로젝트 문서와 주요 주석은 한국어로 작성 (기술 용어는 영문 병기 가능)
6. **문서 동기화**: 아키텍처나 인터페이스가 변경되면 관련 `docs/` 문서와 이 CLAUDE.md를 함께 갱신할 것
7. **참조 플랫폼 우선**: 새 구성요소 설계 전 NAMUGA_ACS에 동일/유사 자산이 있는지 먼저 확인하고(`docs/REFERENCE_NAMUGA_ACS.md` 4절 재사용 전략), 재사용 가능한 것을 새로 만들지 말 것. 프로젝트별 `*.claude.md` 문서화 관행도 승계한다

## 변경 이력

- 2026-07-15: 프로젝트 개요 초안 작성 (코드 미작성 단계, 기술 스택 TBD)
- 2026-07-15: 저장소 확인 후 운용 환경(HD현대중공업 LNG 화물창) 및 라이선스(Apache 2.0) 반영
- 2026-07-15: 아키텍처 핵심 결정 반영 — VDA 5050/온보드 실행, 두절 내성+재접속 동기화, C#/.NET, API-First(WPF+Web+태블릿), 관제 비상정지 (docs/ARCHITECTURE_DECISIONS.md)
- 2026-07-15: 참조 플랫폼 NAMUGA_ACS 분석 반영 — ADR-009 추가, DB PostgreSQL 확정, UI 프레임워크 재검토(Q5′)·Elsa 채택 여부(Q8) 등재 (docs/REFERENCE_NAMUGA_ACS.md)
- 2026-07-15: UI는 WPF로 재확정(Q5′ 해소), 오케스트레이터는 Elsa 경험 한계에 따라 대안 4종 비교 후 Stateless 상태머신 권장 (ADR-010)
- 2026-07-15: 오케스트레이터 Stateless 확정(ADR-010). 시스템 구성 정정 — HD_ACS는 개별 장치를 제어하지 않으며 유일한 상대는 HD_AMR(VDA 5050 단일 인터페이스), 검사 시퀀스·자세 제어는 HD_AMR 전담
- 2026-07-15: 아키텍처 핵심 결정 확정 (ADR-001~007: VDA 5050, 오프라인 내성, 폐쇄망 이동식 서버, 검사 S/W 경계, WPF+REST API, C#/.NET, 관제 비상정지) — `docs/ARCHITECTURE_DECISIONS.md` 신설, ARCHITECTURE.md 개정
- 2026-07-15: 화물창 전개도 구성 및 벽면 naming rule 문서화 (docs/TANK_WALL_LAYOUT.md) — 전개도 UI·검사 지점 주소 체계·검사 S/W 위치 키 규약(Q2)의 공통 기준
- 2026-07-15: 그래프 자료구조·DB 4계층 설계 문서화 — 정적 그래프/액션 카탈로그/시나리오/런타임 스냅샷, Order 빌더·상태 대조 규칙 (docs/GRAPH_DATA_MODEL.md)
- 2026-07-15: NAMUGA_ACS 실제 DB 구조(NA_{계층}_ 네이밍, NODE/LINK/STATION 레이어, 뷰 평탄화) 분석 및 HD_ACS 스키마 대응표 추가 — Station(링크 오프셋) 계층은 VDA 5050 노드 정차 모델로 대체 (docs/GRAPH_DATA_MODEL.md 7절)
- 2026-07-15: 화물창 4층 슬라이스 + 엘리베이터 층간 이동 구조 반영 — 층=맵(mapId) 모델, ELEVATOR 특수 엣지, ZONE 계층 초기 도입, 엘리베이터 제어 주체 Q9 등재 (GRAPH_DATA_MODEL.md 8절)
- 2026-07-15: 엘리베이터 수동 운영 확정(Q9 해소) — 미션 층 단위 분할, WAITING_FLOOR_TRANSFER 상태, 로봇 층(존) 수동 변경 UI + initPosition + mapId 검증 게이트, robot_context 테이블 추가
- 2026-07-15: 통합 DB 스키마 확정판 작성 — HD_{계층}_ 네이밍 22테이블+2뷰, 층 단위 미션(SCENARIO_RUN/MISSION 분리), robot_context, 감사 로그 (db/schema.sql, docs/DB_SCHEMA.md)
- 2026-07-15: DB 네이밍 C안 확정 — PostgreSQL 스키마 네임스페이스(ref/run/hist/alarm/sys) + snake_case로 전환, DDL 구문 검증 완료
- 2026-07-15: 프로세스 구조 확정(ADR-011) — 단일 프로세스 모놀리스, NAMUGA식 슈퍼바이저/이중화 미도입 (robot-is-truth로 서버 장애 영향 최소화, 무상태 앱으로 추후 active-standby 전환 가능)
- 2026-07-15: 솔루션 골격 코드 생성 — 6개 프로젝트(Core/Data/Vda5050/App/Simulator/UI), 상태머신·그래프·Order 빌더·state 대조·층 검증 게이트·수동 존 변경·비상정지 구현. 폐쇄 샌드박스로 NuGet 복원 불가하여 패키지 무관 파일만 컴파일 검증(0 errors), 전체 빌드는 로컬 확인 필요
- 2026-07-29: HD.Acs.UI 본구현 — Telerik UI for WPF 2025.3.813(Fluent 테마) + HelixToolkit.Wpf 3.1.2 3D 도입(Q5 해소). MVVM(CommunityToolkit) + Generic Host DI(백엔드와 일관되게 MS.DI), REST/SignalR 계약 레이어(IAcsApiClient/IMonitoringClient — 기존 code-behind SignalR 이관), RadDocking 셸(좌: 3D+전개도, 우: 로봇상태/미션/알람/수동층변경), 비상정지 툴바. 미구현 백엔드 API(알람·이력 등)는 방어적 빈 상태로 처리. Telerik은 전용 NuGet 피드(nuget.telerik.com) 자격증명 필요. 루트 nuget.config는 두 소스만 등록(packageSourceMapping은 두지 않음 — Telerik.Licensing=nuget.org 공개 / MediaFoundation=Telerik 피드 전용으로 나뉘어 매핑이 복원을 깨뜨림). 전체 솔루션(6개 프로젝트) 빌드 검증 완료(0 error). 겸사로 기존 App/Program.cs 비상정지 감사로그 네임스페이스 오타(Data.Entities→HD.Acs.Data.Entities) 수정
- 2026-07-30: PHASE2 WP-1 map calibration 구현(docs/spec_phase2_acs.md) — 층별 도면→맵 강체변환 T_W_D. ref.map_calibration(_point) 스키마·엔티티, HD.Acs.Core/Geometry/DrawingTransform.cs(2D 강체 최소자승 Solve, 잔차 RMS/Max), calibration REST API 5개(기준점 캡처 409 가드·solve RMS 경고·맵버전 유효성 404·감사로그 CALIBRATION_CAPTURE), appsettings Acs:Calibration:RmsWarnM. 신규 HD.Acs.Core.Tests(xUnit) — DrawingTransform 8 테스트 통과. 전체 솔루션 빌드 0 error. API/DB E2E는 PostgreSQL 필요로 수동 검증 대기. WP-2~5(seam 슬라이싱·payload·시뮬레이터·UI)는 후속
- 2026-08-04: PHASE2 WP-3 완성(SPEC §4.1/§4.2) — 릴리즈 시점 유효 T_W_D(맵버전 일치) 적용해 startWeldInspection payload의 seamStartW/seamEndW/wallNormalW 생성(x,y 변환·z 통과, 법선 yaw 회전+정규화)·drawingPos echo, 유효 T_W_D 없으면 릴리즈 거부. HD.Acs.Core/Planning/WeldInspectionPayload.cs(순수 빌더·ResolveTransform 맵버전 가드·JsonSchema.Net 발행 전 검증), MissionService.ReleaseMissionAsync 확장(weld task 분기·발행 직전 param_schema 검증 실패 시 중단), db/schema.sql의 startWeldInspection param_schema를 §4.1 스키마로 교체(ON CONFLICT DO UPDATE), /api/runs·release-next 400 매핑. HD.Acs.Core.csproj에 JsonSchema.Net 7.3.4. WeldInspectionPayloadTests 4건(부록 A 골든 필드 일치·스키마 통과/실패·맵버전 불일치 거부) 포함 Core.Tests 16건 통과. OrderBuilder 무수정(§4.3)
- 2026-08-06: 정차각 완전 자동화 — 벽면 `facing_yaw`(수동 정차각) 제거, 정차각을 **영역·작업 seam 기하에서 자동 산출**(`theta = atan2(정차 위치 → 영역 seam 점들의 중심)`; seam이 벽 위에 있으므로 그 방향이 곧 "벽 바라봄"). `station_theta` 오버라이드 최우선, degenerate(정차≈seam 중심)면 generate 400. `ref.wall`은 레지스트리+티칭 키로 축소(tank,level,wall_code,description). 신규 `AreaGeometry.AreaCenter`·`FacingYawToward`(degenerate=null), `AreaPlanningService`(seam centroid로 theta 산출), `/api/walls`·WallDto·UI 벽면 등록에서 yaw 입력 삭제(코드·설명만). 문서: TANK_WALL_LAYOUT §6 재작성(배치 규율: seam을 벽쪽에)·DB_SCHEMA·MANUAL, `db/migrations/...facing_yaw.sql` 갱신(wall에서 facing_yaw 제거). Core.Tests 20건 통과(geometry 3건 신규), 전체 컴파일 0 error. 이전 SPEC v2의 수동 facing_yaw를 대체
- 2026-08-06: 벽면 등록 500 오류 수정 — 원인은 코드가 아니라 구버전 `ref.wall`(SPEC v2 미적용) DB. 앱은 자동 마이그레이션을 하지 않으므로 신규 `db/migrations/2026-08-06_spec_v2_wall_facing_yaw.sql`(wall/inspection_area/area_task를 현재 정의로 재생성 + startWeldInspection param_schema 최신화)로 수동 적용. 겸사로 `Program.cs`에 전역 예외 핸들러 추가 — 처리되지 않은 예외(특히 DbUpdateException)를 `{error}` JSON(500)+스키마 힌트로 변환해 UI에 원인 노출(기존 typed 400/409 무영향). App 컴파일 0 error
- 2026-08-05: SPEC PHASE2 v2 — 법선 계약 제거·티칭 기반 자세(facing_yaw 전환). `wallNormalW`를 startWeldInspection payload 계약에서 완전 제거(툴 자세는 HD_AMR이 wall_code 티칭으로 결정, ACS는 위치·정차각만 책임): WeldInspectionPayload(WallNormal/wallNormalW 삭제)·param_schema 시드·MissionService.ParseWeldDrawing·Simulator Validate·SimTest 골든/S2(누락 대상 seamStartW로 교체)·Core.Tests 골든 동기화. `ref.wall`을 normal_drawing → `facing_yaw`(도면 yaw rad)로 재정의·PK `(tank,level,wall_code)`+description. 영역 정차각 = `station_theta ?? facing_yaw`, 둘 다 없으면 generate 시 명시적 400(reasons, 조용한 기본값 금지). `AreaGeometry.DefaultStationPose`(중앙+facing_yaw), `AreaPlanningService`(facing dict·Position에서 wallNormalDrawing 제거). `ref.area_task` 컬럼 `seam_start/end`→`start_drawing/end_drawing`+`name`. REST `/api/walls`(facingYaw+level, DELETE `/{tank}/{level}/{wall}`). UI: WallDto(FacingYaw+Level)·AreaDto normal 제거·벽면 등록 폼을 Lv+정차yaw[rad]로 교체(축정렬 예시 툴팁). 문서: TANK_WALL_LAYOUT §6 재작성·DB_SCHEMA·SPEC §4.1/부록A·신규 docs/MANUAL.md. Core.Tests 18건 통과, 전체 컴파일 0 CS error. API/DB·SimTest E2E는 PostgreSQL/시뮬레이터 수동. 이전 "Wall 법선 승격(normal 벡터)" 결정을 대체
- 2026-08-05: 벽면 법선을 Wall 속성으로 승격(자동 도출) — 법선은 영역이 아니라 벽면의 속성이므로 신규 `ref.wall`(tank_id,wall_code 탱크 공유, normal_drawing)로 올리고 영역은 상속. `inspection_area`에서 normal_drawing 컬럼 제거 + ref.wall FK 추가, 영역 등록 시 법선 입력 제거(값은 순수 데이터—벽면당 1회, 하드코딩 방향 규약 없음). 신규 `WallEntity`/DbSet/EF설정, `AreaPlanningService`의 법선 출처를 area→wall dict로 전환(generate/GetAreas), REST `POST/GET/DELETE /api/walls`(영법선 400·참조영역 삭제 409)+`POST /api/areas`에 미등록 벽면 400. UI: 신규 `WallDto`/`CreateWallAsync`·`GetWallsAsync`·`DeleteWallAsync`, AreaManagementView에 "①벽면 등록(법선)" 박스+목록·영역 폼의 법선란을 벽면 드롭다운으로 대체(②영역·③작업 재번호), VM에 WallDefs·RegisterWall/DeleteWall. 정차 로직(atan2(-ny,-nx))·payload·상태머신 무변경. 문서: TANK_WALL_LAYOUT §6 재작성(Floor→Wall→Area, 법선=벽면 단위·방향만 사용·seam/nz 정차 무관)·§5 갱신, DB_SCHEMA ref.wall(27→28). Core.Tests 18건 통과, 전체 컴파일 0 CS error(실행 중 App/UI가 exe 잠금—copy만 실패). API/DB E2E는 PostgreSQL 수동
- 2026-08-05: 영역(Area) 입력 좌표 규약 명문화 — `min/max/법선`을 어떤 기준으로 입력하는지 코드에만 암묵적이던 규약을 문서화. TANK_WALL_LAYOUT.md 신규 §6(도면 좌표계·m, min/max 축정렬 AABB[좌하/우상·seam 포함·중앙=디폴트 정차], 법선=벽→내부 방향[로봇은 −법선 바라봄]·nz=0 수직벽·자동 정규화) 추가 및 §5 "벽면 로컬 좌표계" 미결 해소(실제 기준=층 도면 프레임). DB_SCHEMA.md 카탈로그에 누락돼 있던 inspection_area/area_task/weld_seam 3테이블 반영(24→27). UI(AreaManagementView) 등록 폼에 도움말 한 줄 + min/max/법선 툴팁, AreaPlanningViewModel.RegisterAreaAsync에 사전검증 2건(경계 역전·영법선 벡터) 추가. 런타임 로직·DB 스키마 무변경
- 2026-08-05: 영역(Area) 이름 벽면 내 유일성 API 강제 — `POST /api/areas`에 `(tank, level, wall_code, name)` 중복 사전검사 추가, 중복 시 500(미처리 DbUpdateException) 대신 **409 Conflict + 한국어 메시지** 반환. 기존 DB `UNIQUE(tank,level,wall,name)` 제약은 최종 백스톱으로 유지. UI(AreaPlanningViewModel.RegisterAreaAsync)에 로드된 `Areas`로 라운드트립 전 즉시 중복 안내 추가(서버 409가 최종 판정). `Level`을 조건에 포함해 층 간 벽면 중복은 그대로 허용. 스키마·DTO·뷰 무변경(App/UI 2파일)
- 2026-08-04: PHASE2 개정 — 자동 슬라이싱(WP-2 SeamSlicer)을 보류(dormant)하고 **영역(Area) LAYER + 검사 작업 수동 정의** 체계로 전환(현장 코로게이션 배치 기준 불명). 영역 1개=STATION 1개=anchorGroup 1개, 작업 1개=TASK 1개. 신규: ref.inspection_area/area_task(schema.sql), InspectionArea/AreaTaskEntity, HD.Acs.Core/Planning/AreaGeometry.cs(디폴트 정차=영역중앙+−법선, 경계판정), AreaPlanningService(generate-from-areas — SeamPlanningService 미러, Position/Params 형태 동일해 WP-1/3/4 무변경 승계, Position에 areaBounds 추가), REST 7개(/api/areas·area-tasks CRUD + generate-from-areas, 경계·T_W_D 400), appsettings Acs:Area. UI: AreaPlanningView(구 SlicingView 대체 — 영역/작업 등록 폼 + 전개도[영역=박스·정차=마커(채움/링)·작업=선분+seq]), 셸 교체. SeamSlicer/seams API/SlicingView는 코드 보존(dormant). AreaGeometryTests 2건 포함 Core.Tests 18건 통과. E2E: 영역→작업(경계밖 400)→generate→릴리즈 시 WP-3가 부록 A 월드좌표 재현, 무T_W_D 층 400. 전체 솔루션 빌드 0 error
