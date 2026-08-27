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
│   ├── TANK_RENDERING.md      # 선창 도면 렌더링 방법 (3D 셸·전개도·좌표 3단·역투영)
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

- 2026-08-27: 선창 도면 렌더링 방법 문서화 — `docs/TANK_RENDERING.md` 신설. 기하 모델(SPEC §1~§3)과 별개로 **화면에 그리는 방법**만 코드에서 역으로 정리: 좌표 3단(도면 3D→면-로컬(u,v)→캔버스 px)과 (u,v)→3D 식이 `WallPose.LocalToDrawing` 정본 + UI 4곳 복제라는 점, 격벽(F/A) 팔각 반폭 함수가 `TankView.HalfWidth`·`TankViewModel.FaceOutlineUv`·`AreaPlanningViewModel.HalfWidthU` 3중복인 점, 3D 레이어 법선 오프셋 규칙(셸 0 / 층밴드 0.02m / 오버레이 0.03m, 법선 내부향이라 부호 반전), 격리 모드에서 반투명 채움을 생략하는 이유(WPF 3D 반투명 깊이 컬링), 전개도 2종(운영 탭=WrapPanel 면별 격자·셀마다 축척 다름 / 계획 캔버스=선택 면 1개 600px·레이어 z-order), 층-로컬 v 규약(`VOff`/`SliceH`, API 경계에서만 ±변환), 캔버스 역투영식. **확인된 문서↔구현 불일치 3건 기록**: ① 전개도가 ADR-005/TANK_WALL_LAYOUT의 방사형 배치가 아니라 WrapPanel 격자이며 `TankLayout.WallCode.NormX/NormY`는 미바인딩 사문화 ② 3D 로봇 마커가 맵 좌표를 도면 씬에 직접 매핑(T_W_D 역변환 누락, 코드에 placeholder 주석) ③ `A`(선미 격벽) U축이 코드는 +y(우현→좌현)인데 대외 정본 `surface_id_enum.docx`·비전 v3 §5는 좌현→우현 — 후벽 촬영 u 좌우 반전 우려, 3자 동시 개정 필요. 겸사로 `SPEC_AREA_TASK_MANUAL.md` §3 표의 폐기된 잠정 코드(FL/BC-S/SW-P/CL/FW/AW…)를 채택 코드(B/SL/PL/SM/PM/SU/PU/T/F/A)로 교체하고 Surface ID 열·격벽 P0 실값·③ 경고를 추가. 코드 무변경(문서만)
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
- 2026-08-07: 전개도 실시간 미리보기 + 폼·플롯 동시 표시 — "영역·작업 관리" 패널에 (u,v) 전개도(AreaLayoutView)를 임베드해 좌=입력 폼·우=캔버스를 한 화면에(기존엔 별도 탭이라 동시에 안 보였음). VM에 `DraftAreas`(입력 중 영역 점선 박스) 추가 + `U/V min·max`·`AreaName` 변경 시 `Project()` 재계산 → 영역 u/v·용접선 좌표 타이핑이 캔버스에 실시간 반영(등록 전=점선, 후=실선). MainWindow의 중복 "영역 전개도" 탭 제거, 면 목록 그리드 제거(드롭다운+SelectedWallInfo로 대체). UI 빌드 0 error
- 2026-08-07: (개정) 전개도 캔버스 클릭 입력 제거 — 사용자 요청으로 (u,v) 캔버스 클릭→용접선 지정 기능 삭제(`AreaLayoutView` 핸들러·VM `CanvasClick` 제거). 용접선은 좌측 폼의 시작/끝 (u,v) **숫자 입력만**으로 등록. 캔버스 렌더(영역·정차점=영역 중심 마커·작업·숫자 입력 점선 미리보기)는 유지. 아래 인터랙티브 항목의 클릭 부분은 무효
- 2026-08-07: 영역 (u,v) 캔버스 인터랙티브 작업 등록 + 정차점 규칙 확정 — "영역 전개도" 캔버스에서 영역 선택 후 클릭으로 용접선 시작→끝 지정(면 범위 클램프, 좌측 숫자 폼과 실시간 동기화·점선 미리보기; AreaLayoutView.xaml.cs `PlotCanvas_MouseLeftButtonDown`→VM `CanvasClick`, 역투영 px→(u,v)). **정차점 = 영역 중심**으로 확정(이전 §5 법선+standoff 대체) — 캔버스에 정차점=보라 마커(영역 중심) 표시, 생성(§7) 시 station=To3D(영역중심).xy·방향=facing_yaw(천장 T는 수동 지정). VM에 StationMarkers/DraftSegments·CanvasClick 추가. MANUAL §3 갱신. UI 빌드 0 error
- 2026-08-07: SPEC v3 §4 "영역·검사 작업 (벽면-로컬 u,v) 등록" + 등록 UI 구현 — 자동 생성된 면(ref.wall) 위에서 (u,v)로 영역/작업 등록. `ref.inspection_area`(u_min/v_min/u_max/v_max, level, station 오버라이드; FK (tank,wall_code)→ref.wall, UNIQUE(tank,wall,name))·`ref.area_task`(start_u/v·end_u/v) 재도입(`db/migrations/2026-08-07_spec_v3_areas.sql`, 적용·검증). REST `POST/GET/DELETE /api/areas`·`/api/areas/{id}/tasks`·`/api/area-tasks/{id}`(면 범위 400·경계 400·중복 409·면없음 404; `AreaGeometry.InBounds` 재사용). UI: AreaManagementView에 면 선택→영역 등록(면 범위 안내·정차 오버라이드)→작업 등록 폼+목록, AreaLayoutView=선택 면 (u,v) 캔버스(영역 박스·작업 선분 auto-fit). AreaDto/AreaTaskDto(u,v)·API client. Core.Tests 33건 통과, 전체 컴파일 0 error. 참고: 직전 "조회실패 404"는 배포 불일치(구 UI가 제거된 /api/walls 호출) — App+UI 함께 재빌드 필요. generate(§7)·정차 standoff(§5)·payload u,v(§6)는 후속
- 2026-08-07: SPEC v3(docs/SPEC_AREA_TASK_MANUAL.md) §2+§3 "선창 3D 정의" 구현 — 팔각 단면 파라미터에서 10면 자동 생성. v2 영역/벽면 모델 대체(영역/작업 u,v 등록·정차·generate·전개도 UI는 §4~§9 후속). 신규 `HD.Acs.Core/Planning/TankGeometry.cs`(유도값 B/W_ceil/H, 검증, `GenerateWalls()` 10면=B/SL/PL/SM/PM/SU/PU/T/F/A 프레임+내부향 법선+facing_yaw; Phase① `Vec3`/`WallPose`=`To3D` 재사용). `ref.tank_geometry` 신설·`ref.wall` v3(PK tank_id,wall_code / origin·u/v_axis·normal jsonb·u/v_len·facing_yaw·generated; FK tank_geometry) — v2 inspection_area/area_task DROP(`db/migrations/2026-08-06_spec_v3_tank_geometry.sql`, 적용·검증 완료). `TankGeometryService`+REST `POST/GET /api/tanks/{id}/geometry`·`GET .../walls`(각도 deg 입력→rad, 검증 400 reasons). v2 `/api/walls`·`/api/areas*`·generate-from-areas·AreaPlanningService 철거. UI: AreaManagementView=파라미터 폼+생성 면 목록, AreaLayoutView=§9 후속 placeholder, WallDto/TankGeometryDto·API client 교체. Core.Tests 33건 통과(TankGeometry 8건 신규: 유도값·프레임 직교/단위·인접모서리 정합·To3D·facing_yaw·검증). 전체 컴파일 0 error. API E2E는 앱 재기동 후
- 2026-08-06: UI 버그 수정 — AreaManagementView의 `RadNumericUpDown`이 증감 버튼만 보이고 입력칸이 안 뜨던 문제. 원인은 뷰 리소스의 **키 없는 `<Style TargetType="TextBox">`** 가 RadNumericUpDown 내부 편집 TextBox에 누수(WPF/Telerik 함정). 스타일에 `x:Key="TxtField"` 부여(implicit→explicit)하고 실제 TextBox 4개(시나리오명·벽면코드·설명·영역이름)에 명시 적용. pose 입력칸 폭 소폭 상향. UI 빌드 0 error
- 2026-08-06: 벽면-로컬 좌표 모델 구현 착수 — Phase ①(벽면 pose 기반). ADR-012를 "채택·단계적 도입"으로 전환. 신규 `HD.Acs.Core/Geometry/Vec3.cs`(3D 벡터 수학 + `WallPose`: LocalToDrawing(u,v)→도면[x,y,z]·Normal·HorizontalNormal·FromThreePoints 정규직교화). `ref.wall`에 pose 컬럼(origin/u_axis/v_axis jsonb) 비파괴 추가(`db/migrations/2026-08-06_wall_pose.sql`, ADD COLUMN — 데이터 유지). `POST/GET /api/walls`에 pose(3점) 입출력(degenerate 400), UI 벽면 등록 폼에 pose 3점 입력·그리드 pose 표시. 영역·작업·generate·payload·T_W_D는 무변경(정차 standoff는 Phase ②). Core.Tests 25건 통과(3D 수학 5건 신규), 전체 컴파일 0 error, 마이그레이션 적용·검증 완료. 후속: ② 법선+standoff 정차 → ③ (u,v) 입력 → ④ generate 통합 → ⑤ 전개도 UI
- 2026-08-08: 픽 모드 ESC 해제 — `AreaPlanningViewModel`에 `[RelayCommand] CancelPick()`(PickMode=false), `AreaManagementView`에 `UserControl.InputBindings > KeyBinding Key=Escape → CancelPickCommand`. 이 뷰가 폼·전개도 캔버스를 모두 포함·같은 VM 공유라 포커스 위치와 무관하게 ESC로 해제(우클릭 해제 병행). UI 2파일, 빌드 0 error
- 2026-08-08: 전개도 "도면에서 4점 선택" 픽 모드(크로스헤어·우클릭 해제) — 캔버스 좌클릭이 항상 코너를 지정하던 것을 토글로 제어. `AreaPlanningViewModel`에 `PickMode`(ObservableProperty) + `OnPickModeChanged`(켜면 CornerIndex=0), `CanvasClick`을 `if(!PickMode) return`으로 게이트. `AreaManagementView`의 4점 입력 폼 바로 위에 `RadToggleButton "도면에서 4점 선택"`(IsChecked=PickMode). `AreaLayoutView` PlotCanvas에 커서 Style DataTrigger(PickMode→Cross) + `MouseRightButtonDown` 핸들러(PickMode=false, e.Handled). 좌클릭은 VM에서 PickMode 아니면 무시(오조작 방지), 숫자 4점 입력은 항상 가능. UI 3파일, 백엔드/DB 무변경, 빌드 0 error
- 2026-08-08: TankView 전개도 탭을 각 면의 실제 2D 도면(면별 그리드)으로 — 정적 카드 스키매틱(`TankLayout.Walls` 방사형)을 실제 치수·형상으로 대체. `TankViewModel`에 `record FacePlot`(코드·치수·Outline·Areas·Tasks)+`FacePlots`·`BuildFacePlots`: `ShellWalls` 각 면을 고정 셀(240×150)에 auto-fit 투영(사각형; 마구리 F/A는 `Geometry`로 팔각), `Overlays`(면-전체 v 코너)를 그 면 칸에 영역 폴리곤(`AreaPoly`)·작업 용접선(`TaskSeg`)으로 오버레이(`ShowOverlays` 토글 반영, `LoadOverlaysAsync` 말미·토글 변경 시 재빌드). `TankView.xaml` 전개도 탭을 ScrollViewer+ItemsControl(WrapPanel) 셀 그리드(헤더 코드·"WxH m" + Canvas: Outline `<Polygon>` + 중첩 ItemsControl 영역/작업)로 교체. 전개도는 전 면·전 층 표시(3D 층 필터와 별개). 렌더 레코드(AreaPoly/TaskSeg) 재사용, UI 2파일, 백엔드/DB 무변경, 빌드 0 error
- 2026-08-08: 검사 영역을 임의 4점 사각형(quad)으로 — 축정렬 사각형(u/v_min·max, 대각 2점)만 되던 영역을 회전·비축정렬 4점 사각형으로 확장. **DB**: `ref.inspection_area`에 `corners jsonb`(4점) 추가, u/v_min·max는 서버가 코너에서 유도한 bbox로 유지(층 유도·하위호환); 마이그레이션 `db/migrations/2026-08-08_area_corners.sql`(ADD+기존행 bbox→코너 backfill+NOT NULL, dev-postgres 적용). `InspectionAreaEntity.Corners`+jsonb 매핑. **Core**: `AreaGeometry`에 `PointInPolygon`(ray-cast+변 위 포함)·`Centroid`·`Bbox` 추가(테스트 2건, Core.Tests 46건). **API**: `POST /api/areas` corners 검증(전 코너 면범위·비퇴화 bbox)→bbox 유도→`AreaZRange(min v,max v)`로 층 유도→corners+bbox 저장(Corners 없으면 min/max 폴백); `GET`은 corners+bbox 반환; `POST tasks` 경계검증을 InBounds→`PointInPolygon`으로. **UI**: `AreaDto.Corners`, `CreateAreaAsync(corners)`, 폼을 P1~P4 8칸+전개도 캔버스 클릭 4점(`CanvasClick` 역투영, `AreaLayoutView` MouseLeftButtonDown), `AreaBox`→`AreaPoly`(PointCollection+라벨 앵커) 폴리곤 렌더(전개도 3개 ItemsControl), 정차마커=centroid, v-offset은 **전 코너 v**에 적용, 3D `BuildOverlays`는 코너 직접(PolygonMesh), `.hdacs` AreaDoc corners+포맷 v2(구파일 bbox 폴백). `generate-from-areas`·payload는 미구현이라 무변경. 전체 빌드 0 error, E2E(5199): 회전 사각형 등록→corners 왕복·bbox 유도·층 유도·작업 point-in-polygon(내부 성공/밖 400) 확인. DB_SCHEMA 갱신. 실행 중 App/UI는 재기동 후 반영(구 App은 corners NOT NULL로 신규 영역 등록 불가—재빌드 필요)
- 2026-08-08: 전개도 전체 벽면 표시 + 선택 층만 활성(나머지 회색 음영) — 직전 "슬라이스만 표시"로 어느 면인지 파악이 어려워, 전개도를 **면 전체**로 되돌리고 선택 층 도달 밴드만 활성(밝게)·나머지는 회색 음영 처리. 입력 좌표(층-로컬 v)는 유지(순수 표시 조정). `AreaPlanningViewModel`: `RefreshAreasAsync`가 그 면 **모든 층** 영역을 `_allAreas`(면-전체 v)로 로드하고 그리드 `Areas`는 선택 층+로컬 v 파생. `Project()`를 면 전체(VLen) 좌표로 복귀 — `FaceOutline`=면 전체 폴리곤(회색), 신규 `ActiveBand`=선택 층 [VOff,VOff+SliceH] 클리핑 폴리곤(활성), `AreaBoxes`(선택 층=녹색)+`InactiveAreaBoxes`(타 층=회색), 입력 draft·작업은 로컬 v `+VOff`로 활성 밴드에 표시. `FaceUvPolygon`→`FaceClipPolygon(w,vLo,vHi)`(재원점 없이 클리핑, 마구리 팔각). `AreaLayoutView.xaml`: 면 회색(#33AAB0B7)+활성 밴드(#1A2ECC71)+회색 영역 박스 ItemsControl. 백엔드/DB·입력/저장 규약 무변경. UI 2파일, 빌드 0 error
- 2026-08-08: 영역·작업 입력을 "선택 층 기준 면-로컬 좌표"로 — 통짜 면에서 층별 영역 등록 시 v를 0부터 입력하도록 (0,0)=그 층 도달 구간 좌하단으로 재원점. `AreaPlanningViewModel`이 층-로컬 v 공간에서 동작하고 API 경계에서만 `±VOff`(=`SelectedWall.ReachableVBand[0]`, SliceH=vHi−vLo) 변환: 로드 시 `GetAreasAsync(…, level)`로 그 층 영역만 + record `with`로 `VMin/VMax−VOff`(작업도 `StartV/EndV−VOff`) → 그리드·전개도 로컬 표시, 등록 시 `+VOff`로 면-전체 저장(서버 z→층 유도 일치). `Project()`는 SliceH로 스케일·재원점(ReachBands 전폭 하이라이트 생략), `FaceUvPolygon`을 v∈[VOff,VOff+SliceH] 클리핑·재원점(`HalfWidthU`로 마구리 팔각 슬라이스=중간층 사각형·L1 하부챔퍼/L4 상부챔퍼 사다리꼴), `SelectedWallInfo`에 "Ln 로컬 v∈[0,SliceH]" 안내. 바닥 B·천장 T·마구리 중간층은 VOff=0이라 현행과 동일. 백엔드/DB/스키마·payload 무변경(순수 입력/표시 계층 변환, 저장은 면-전체 좌표). UI 1파일, 빌드 0 error
- 2026-08-08: 전개도 마구리(F/A) 팔각·사다리꼴 형상 반영 — 영역·작업 (u,v) 전개도(`AreaLayoutView`)에서 선수/선미 면을 직사각형이 아닌 **챔퍼로 잘린 팔각**(상·하 사다리꼴) 윤곽으로 표시. `AreaPlanningViewModel`이 `ApplyGeometry`에서 파생값(B/W_ceil/H) 저장, `Project()`에서 선택 면 경계 폴리곤을 `PointCollection FaceOutline`로 산출(F/A+지오메트리 로드 시 8정점 팔각: 바닥 W_floor·천장 W_ceil 좁고 중앙 전폭 B, 기존 `Proj(u,v)` 재사용; 그 외 직사각형 4정점). `AreaLayoutView.xaml`에 `<Polygon Points="{Binding FaceOutline}">` 추가(배경 위·영역 박스 뒤). 순수 전개도 렌더 — 등록·검증·payload·DB·3D 무변경. UI 2파일, 빌드 0 error
- 2026-08-07: 3D 도면에 영역·작업(용접선) 오버레이 — 등록된 영역(u,v 사각형)·작업(용접선 시작/끝 u,v)을 3D 셸 위에 표시. 각 항목을 벽면 `WallDto` 프레임으로 `To3D(u,v)=Origin+u·U+v·V` 변환(추가 백엔드 없이 기존 `GetAreasAsync`/`GetAreaTasksAsync` + 벽면 프레임 재사용). 영역=녹색 사각형(외곽선+옅은 채움)+영역명 라벨, 작업=주황 용접선+시작(녹)/끝(빨) 마커(PointsVisual3D)+seq 라벨(BillboardTextVisual3D), 외부향 offset으로 셸 위 또렷. 표시 범위: 전체 뷰=모든 영역, L{n}=그 층(유도 level)만. `TankViewModel`에 `AreaOverlay`·`Overlays`·`LoadOverlaysAsync`·`ShowOverlays` 토글, `TankView.xaml`에 "영역·작업 표시" 체크박스+`OverlayModel` 컨테이너, `TankView.xaml.cs` `BuildOverlays`+`TryPoint`. 자동 동기화: `AreaPlanningViewModel.PlanningChanged`(RefreshAreasAsync 말미) → `ShellViewModel`이 `Tank.LoadOverlaysAsync` 호출(2D 등록/삭제 즉시 3D 반영). UI 4파일, 백엔드/DB 무변경. 빌드 0 error
- 2026-08-07: 3D 마구리(선수 F/선미 A) 팔각 단면 모따기 렌더링 — 마구리를 직사각형(B×H 박스 끝면)이 아닌 실제 **팔각 단면 윤곽**(하부챔퍼·수직벽·상부챔퍼로 모따기된 8정점)으로 그림. 순수 3D 시각화 개선(데이터 모델·WallDto 직사각형 가정 유지 — SPEC §3 "마구리 평면 가정"). `TankViewModel`이 `GetTankGeometryAsync`로 `Geometry`(팔각 치수) 노출, `TankView.xaml.cs`에 `BulkheadPolygon`(z∈[zLo,zHi] 클리핑 팔각 다각형)·`HalfWidth`(구간별 반폭 y(z))·`PolygonMesh`(삼각형 팬)·`AddClosedOutline` 추가. BuildShell/BuildLevelHighlight가 F/A면은 팔각 다각형(+z-밴드 클리핑), 그 외는 직사각형으로 분기. 지오메트리 미로드 시 직사각형 폴백. UI 2파일, 백엔드/DB 무변경. 빌드 0 error
- 2026-08-07: 3D z-밴드 가시성 수정 + 층별 분리 뷰(전체/L1~L4) — z-밴드 강조가 끝단 마구리에서만 보이던 문제 해결. 원인=WPF 3D에서 반투명 면도 깊이 버퍼를 기록해 앞쪽 반투명 면(천장 등)이 뒤의 골드 밴드를 깊이 컬링. 해결=모드별 렌더링으로 가림 자체 제거: TankView 상단 콤보를 "뷰"(전체/L1~L4)로 변경, **전체**=반투명 타입별 셸+와이어(개관), **L{n}**=반투명 채움 면 생략(가림 원인 제거)하고 팔각 와이어프레임 + 그 층 `reachableVBand` 서브밴드를 불투명 근접 골드(DiffuseMaterial+EmissiveMaterial 발광)+굵은 외곽선(LinesVisual3D)으로 격리 표시(외부향 offset). TankViewModel: `ViewModes`("전체"+Floors)·`SelectedViewMode`·파생 `SelectedLevel`/`IsolateLevel`, 이벤트를 `ViewChanged` 하나로 통합(셸+강조 재빌드), `RobotOnSelectedFloor`은 SelectedLevel 기반. TankView.xaml.cs BuildShell(전체만 채움)·BuildLevelHighlight(밝은 골드+외곽선) 모드 분기. UI 3파일(TankViewModel/TankView.xaml/.cs), 백엔드·DB 무변경. 빌드 0 error
- 2026-08-07: 선창 3D 셸 렌더링 — TankView "3D 뷰" 탭의 placeholder 박스를 지오메트리 API 실제 10면으로 조합한 **반투명 팔각 프리즘 셸**로 대체(HelixToolkit.Wpf). 각 면 = `WallDto(Origin/UAxis/VAxis/ULen/VLen)`의 4코너로 `MeshGeometry3D`(삼각형 2개+면 법선) 생성, 면 타입별(바닥/벽/챔퍼/천장/마구리) 연한 반투명 브러시 + BackMaterial(내부 로봇 마커 가시) + 팔각 모서리 `LinesVisual3D`. TankView의 기존 L1~L4 층 선택기 연동 — 선택 층의 `GetWallsAsync(tankId, level)` 결과 `reachableVBand`로 **도달 z-밴드 서브사각형을 골드 반투명 오버레이**(법선 +2cm offset로 z-fighting 회피). `TankViewModel`에 `IAcsApiClient` 주입·`ShellWalls`/`LevelWalls`·`LoadAsync`·`ShellChanged`/`LevelHighlightChanged` 이벤트, `TankView.xaml.cs`가 이벤트 구독해 코드비하인드에서 메시 빌드(로봇 마커 갱신 패턴 재사용)·`ZoomExtents`, `ShellViewModel.InitializeAsync`/새프로젝트/열기에서 `Tank.LoadAsync()` 훅. 백엔드/DB/스키마/payload·"전개도" 탭 무변경(UI 4파일). MeshBuilder는 HelixToolkit.Wpf 3.1.2에 없어 MeshGeometry3D 직접 생성. 전체 솔루션 빌드 0 error(별도 출력), `GET /walls`·`/walls?level=1|4` 응답 검증(L1=바닥+하부, L4=천장 포함). 실행 중 UI는 구빌드라 재기동 후 확인
- 2026-08-07: SPEC v3.1 층 자동 유도 + UI 층 필터 구현(docs/SPEC_AREA_TASK_MANUAL.md §2/§5-A/§8/§9) — 층(level)을 운영자 입력에서 **유도값**으로 전환("0층+천장" 불가능 조합 원천 차단). 신규 `HD.Acs.Core/Planning/LevelBands.cs`(순수 — 층 도달 밴드 `Compute`[최상층 상한=H 폐구간], 영역 z범위→층 `Derive`[ε=5mm, 밴드밖=도달불가/경계걸침 사유], 면×층 교차 `ReachableVBand`, `AreaZRange`), `TankGeometry`에 선택 `ReachZMin/Max` + `LevelBandList()`. `ref.tank_geometry`에 reach_z_min/max 컬럼(비파괴 ADD COLUMN, `db/migrations/2026-08-07_spec_v3.1_reach_z.sql`·schema.sql), `TankGeometryEntity` 2필드. 백엔드: `POST /api/areas`가 요청 level 무시하고 면 pose(origin.z·vAxis.z)+지오메트리로 층 유도→실패 시 400{reason}·성공 시 `{areaId,level}` 반환, `GET /walls?level=`이 그 층 도달 가능 면만+`reachableVBand` 부착, geometry 등록에 reach_z 전달. UI: 영역 폼의 자유 "Lv" 스피너 제거→**층 필터 콤보(L1~LN, level_z 길이 동적)**, 층 선택 시 면 콤보 제한+전개도 도달 v구간 노랑 밴드 하이라이트(AreaLayoutView ReachBands), 등록 후 유도 층 표시. NewProjectDialog에 reach_z 입력, ProjectDoc/GeometryDoc 왕복 보존, `IAcsApiClient.CreateAreaAsync`가 유도 층 반환(level 파라미터 제거)·`GetWallsAsync(level)`. `LevelBandsTests` 11건 포함 Core.Tests 44건 통과, 전체 솔루션 빌드 0 error(별도 출력으로 실행 중 exe 잠금 회피). API/DB E2E는 PostgreSQL 수동. HD_AMR 인터페이스(VDA 5050)·payload 계약 무변경
- 2026-08-07: 메뉴바(파일 메뉴) + 전용 이진 프로젝트 파일(.hdacs) 도입(docs/menubar.txt) — MainWindow에 RadMenu(파일 ▸ 새 프로젝트/열기/저장/다른 이름으로 저장), Title은 WindowTitle 바인딩(현재 파일명 표시). **새 프로젝트**=팝업(NewProjectDialog)으로 선창 3D 파라미터 입력→선창/면 등록→저장 위치 선택 시 파일 생성. 기존 AreaManagementView의 ① 선창/면 등록 폼을 제거하고 이 팝업으로 대체(② 영역·③ 작업·전개도·목록 유지, 상단에 현재 선창 요약 배너). 프로젝트 파일 = **지오메트리+영역+작업 전체 스냅샷**의 이진 컨테이너(매직 "HDACSPRJ"+버전바이트 + GZip(UTF-8 JSON), 매직/버전 불일치 시 "이 프로그램의 파일 아님" 예외 — 전용 포맷). DB가 런타임 truth이며 파일은 내보내기/가져오기: 저장=API로 현재 상태 조회→직렬화, 열기=역직렬화→RegisterTankGeometry(면 재생성)→영역→작업 순 재적재 후 화면 갱신. 신규 Models/ProjectDoc, Services/IProjectService+ProjectService·IProjectDialogService+ProjectDialogService(Win32 SaveFileDialog/OpenFileDialog·필터 *.hdacs), Views/NewProjectDialog, ShellVM 명령 4개(New/Open/Save/SaveAs)+WindowTitle, AreaPlanningVM.TryRegisterGeometryAsync(성공 bool 반환) 추출. App DI에 두 서비스 등록. 백엔드/DB/스키마 무변경(API 경유). 전체 솔루션 컴파일 0 error(실행 중 App/UI가 exe 잠금—copy만 실패). 열기/저장은 API/DB 필요로 서버 기동 상태에서 E2E 수동 검증
- 2026-08-06: 벽면-로컬 2D 좌표계 제안 검토·보류(ADR-012 신설) — 제안의 대부분(영역·TASK 동일 프레임, T_W_D→AMR 정차, 월드좌표→HD_AMR)은 현행 층 도면(floor-plan) 프레임에서 이미 동작하고, T_A_B(코봇 base)는 HD_AMR 책임(경계). 진짜 신규는 벽면-로컬 2D뿐이며 전제(벽면 3D pose·2D→3D 변환·AMR 바닥 투영)가 필요. 화물창 CAD 기하는 확정되어 데이터 제약은 해소됐으나, 현행 모델이 end-to-end 동작하므로 **설계 결정으로 현행 유지**(재검토 트리거: 챔퍼 3D 정확도·전개도 입력 편의). §5 갱신. 코드 무변경
- 2026-08-06: 정차각 완전 자동화 — 벽면 `facing_yaw`(수동 정차각) 제거, 정차각을 **영역·작업 seam 기하에서 자동 산출**(`theta = atan2(정차 위치 → 영역 seam 점들의 중심)`; seam이 벽 위에 있으므로 그 방향이 곧 "벽 바라봄"). `station_theta` 오버라이드 최우선, degenerate(정차≈seam 중심)면 generate 400. `ref.wall`은 레지스트리+티칭 키로 축소(tank,level,wall_code,description). 신규 `AreaGeometry.AreaCenter`·`FacingYawToward`(degenerate=null), `AreaPlanningService`(seam centroid로 theta 산출), `/api/walls`·WallDto·UI 벽면 등록에서 yaw 입력 삭제(코드·설명만). 문서: TANK_WALL_LAYOUT §6 재작성(배치 규율: seam을 벽쪽에)·DB_SCHEMA·MANUAL, `db/migrations/...facing_yaw.sql` 갱신(wall에서 facing_yaw 제거). Core.Tests 20건 통과(geometry 3건 신규), 전체 컴파일 0 error. 이전 SPEC v2의 수동 facing_yaw를 대체
- 2026-08-06: 벽면 등록 500 오류 수정 — 원인은 코드가 아니라 구버전 `ref.wall`(SPEC v2 미적용) DB. 앱은 자동 마이그레이션을 하지 않으므로 신규 `db/migrations/2026-08-06_spec_v2_wall_facing_yaw.sql`(wall/inspection_area/area_task를 현재 정의로 재생성 + startWeldInspection param_schema 최신화)로 수동 적용. 겸사로 `Program.cs`에 전역 예외 핸들러 추가 — 처리되지 않은 예외(특히 DbUpdateException)를 `{error}` JSON(500)+스키마 힌트로 변환해 UI에 원인 노출(기존 typed 400/409 무영향). App 컴파일 0 error
- 2026-08-05: SPEC PHASE2 v2 — 법선 계약 제거·티칭 기반 자세(facing_yaw 전환). `wallNormalW`를 startWeldInspection payload 계약에서 완전 제거(툴 자세는 HD_AMR이 wall_code 티칭으로 결정, ACS는 위치·정차각만 책임): WeldInspectionPayload(WallNormal/wallNormalW 삭제)·param_schema 시드·MissionService.ParseWeldDrawing·Simulator Validate·SimTest 골든/S2(누락 대상 seamStartW로 교체)·Core.Tests 골든 동기화. `ref.wall`을 normal_drawing → `facing_yaw`(도면 yaw rad)로 재정의·PK `(tank,level,wall_code)`+description. 영역 정차각 = `station_theta ?? facing_yaw`, 둘 다 없으면 generate 시 명시적 400(reasons, 조용한 기본값 금지). `AreaGeometry.DefaultStationPose`(중앙+facing_yaw), `AreaPlanningService`(facing dict·Position에서 wallNormalDrawing 제거). `ref.area_task` 컬럼 `seam_start/end`→`start_drawing/end_drawing`+`name`. REST `/api/walls`(facingYaw+level, DELETE `/{tank}/{level}/{wall}`). UI: WallDto(FacingYaw+Level)·AreaDto normal 제거·벽면 등록 폼을 Lv+정차yaw[rad]로 교체(축정렬 예시 툴팁). 문서: TANK_WALL_LAYOUT §6 재작성·DB_SCHEMA·SPEC §4.1/부록A·신규 docs/MANUAL.md. Core.Tests 18건 통과, 전체 컴파일 0 CS error. API/DB·SimTest E2E는 PostgreSQL/시뮬레이터 수동. 이전 "Wall 법선 승격(normal 벡터)" 결정을 대체
- 2026-08-05: 벽면 법선을 Wall 속성으로 승격(자동 도출) — 법선은 영역이 아니라 벽면의 속성이므로 신규 `ref.wall`(tank_id,wall_code 탱크 공유, normal_drawing)로 올리고 영역은 상속. `inspection_area`에서 normal_drawing 컬럼 제거 + ref.wall FK 추가, 영역 등록 시 법선 입력 제거(값은 순수 데이터—벽면당 1회, 하드코딩 방향 규약 없음). 신규 `WallEntity`/DbSet/EF설정, `AreaPlanningService`의 법선 출처를 area→wall dict로 전환(generate/GetAreas), REST `POST/GET/DELETE /api/walls`(영법선 400·참조영역 삭제 409)+`POST /api/areas`에 미등록 벽면 400. UI: 신규 `WallDto`/`CreateWallAsync`·`GetWallsAsync`·`DeleteWallAsync`, AreaManagementView에 "①벽면 등록(법선)" 박스+목록·영역 폼의 법선란을 벽면 드롭다운으로 대체(②영역·③작업 재번호), VM에 WallDefs·RegisterWall/DeleteWall. 정차 로직(atan2(-ny,-nx))·payload·상태머신 무변경. 문서: TANK_WALL_LAYOUT §6 재작성(Floor→Wall→Area, 법선=벽면 단위·방향만 사용·seam/nz 정차 무관)·§5 갱신, DB_SCHEMA ref.wall(27→28). Core.Tests 18건 통과, 전체 컴파일 0 CS error(실행 중 App/UI가 exe 잠금—copy만 실패). API/DB E2E는 PostgreSQL 수동
- 2026-08-05: 영역(Area) 입력 좌표 규약 명문화 — `min/max/법선`을 어떤 기준으로 입력하는지 코드에만 암묵적이던 규약을 문서화. TANK_WALL_LAYOUT.md 신규 §6(도면 좌표계·m, min/max 축정렬 AABB[좌하/우상·seam 포함·중앙=디폴트 정차], 법선=벽→내부 방향[로봇은 −법선 바라봄]·nz=0 수직벽·자동 정규화) 추가 및 §5 "벽면 로컬 좌표계" 미결 해소(실제 기준=층 도면 프레임). DB_SCHEMA.md 카탈로그에 누락돼 있던 inspection_area/area_task/weld_seam 3테이블 반영(24→27). UI(AreaManagementView) 등록 폼에 도움말 한 줄 + min/max/법선 툴팁, AreaPlanningViewModel.RegisterAreaAsync에 사전검증 2건(경계 역전·영법선 벡터) 추가. 런타임 로직·DB 스키마 무변경
- 2026-08-05: 영역(Area) 이름 벽면 내 유일성 API 강제 — `POST /api/areas`에 `(tank, level, wall_code, name)` 중복 사전검사 추가, 중복 시 500(미처리 DbUpdateException) 대신 **409 Conflict + 한국어 메시지** 반환. 기존 DB `UNIQUE(tank,level,wall,name)` 제약은 최종 백스톱으로 유지. UI(AreaPlanningViewModel.RegisterAreaAsync)에 로드된 `Areas`로 라운드트립 전 즉시 중복 안내 추가(서버 409가 최종 판정). `Level`을 조건에 포함해 층 간 벽면 중복은 그대로 허용. 스키마·DTO·뷰 무변경(App/UI 2파일)
- 2026-08-04: PHASE2 개정 — 자동 슬라이싱(WP-2 SeamSlicer)을 보류(dormant)하고 **영역(Area) LAYER + 검사 작업 수동 정의** 체계로 전환(현장 코로게이션 배치 기준 불명). 영역 1개=STATION 1개=anchorGroup 1개, 작업 1개=TASK 1개. 신규: ref.inspection_area/area_task(schema.sql), InspectionArea/AreaTaskEntity, HD.Acs.Core/Planning/AreaGeometry.cs(디폴트 정차=영역중앙+−법선, 경계판정), AreaPlanningService(generate-from-areas — SeamPlanningService 미러, Position/Params 형태 동일해 WP-1/3/4 무변경 승계, Position에 areaBounds 추가), REST 7개(/api/areas·area-tasks CRUD + generate-from-areas, 경계·T_W_D 400), appsettings Acs:Area. UI: AreaPlanningView(구 SlicingView 대체 — 영역/작업 등록 폼 + 전개도[영역=박스·정차=마커(채움/링)·작업=선분+seq]), 셸 교체. SeamSlicer/seams API/SlicingView는 코드 보존(dormant). AreaGeometryTests 2건 포함 Core.Tests 18건 통과. E2E: 영역→작업(경계밖 400)→generate→릴리즈 시 WP-3가 부록 A 월드좌표 재현, 무T_W_D 층 400. 전체 솔루션 빌드 0 error
- 2026-08-10: 검사 순서 = 층별 greedy 최근접 동적 배차 MVP(단일 로봇) — "미검사 영역을 모두 큐에 넣고 유휴 로봇 최근접 작업 할당"을 ACS 디스패치로 구현. 기존 고정 Seq·층당 전체 Base Order 대신 **정차 단위 단일 노드 Order를 완료마다 순차 발행**. **Phase 0(브리지)**: 운영자 영역/작업(Area)은 그동안 실행 경로가 없어(area_task에 action_type 없음·generate 미존재) run 시작 시 **선창 전체 영역→작업 큐**로 전개 — 정차 맵좌표=코너 centroid→`WallPose.LocalToDrawing`→`DrawingTransform.DrawingToMap`(층 유효 T_W_D), 작업=`startWeldInspection`(`WeldInspectionPayload` 재사용). **Phase 1(큐)**: `run.work_item`(PENDING/DISPATCHED/DONE/FAILED/SKIPPED·맵좌표·actions jsonb) + `order_action.work_item_id` 신설(`WorkItemEntity`/DbSet/매핑, `db/migrations/2026-08-10_work_item.sql` 적용). **Phase 2(디스패처)**: 신규 순수 정책 `IInspectionOrderingPolicy`+`GreedyNearestPolicy`(맵 프레임 제곱 유클리드, Core.Tests 4건)+App `InspectionDispatcher`(현재 층 PENDING 중 로봇 최근접 1건→단일 정차 Order 발행, `mission.OrderId` 갱신으로 state 대조 유지; 층 소진 시 미션 Completed→다음 층 남으면 WAITING_FLOOR_TRANSFER·전부 소진이면 run COMPLETED = 완료 시 자동 층 진행도 함께 해소). `MissionService.StartRunAsync`를 큐 전개+첫 배차로 교체, `TryReleaseNextMissionAsync`는 work_item 있으면 디스패처로 위임(수동 층 변경 후 재개). `RobotStateService` 완료 훅: 정차 액션 종결 시 work_item DONE/FAILED 판정 후 다음 배차. **Phase 3(실패정책)**: FAILED 시 attempts++·`Acs:Dispatch:MaxRetries` 미만이면 재큐잉·초과면 SKIPPED+알람(FAILED TODO 구현). REST `GET /api/runs/{id}/work-items`. 전체 솔루션 빌드 0 error, Core.Tests 50건 통과, 마이그레이션 dev-postgres 적용. 후속: 시뮬레이터 E2E(영역·캘리브레이션 세팅 필요)·운영 화면 층 레일/오버레이를 work_item 상태로 정밀화·serpentine 정책·다중 로봇(원자적 클레임). 시나리오↔영역은 MVP상 "run=선창 전체 영역". 기존 seam/inspection_point 경로는 보존(비활성)
- 2026-08-09: 시나리오 생성/삭제 UI(계획 ▸ 시나리오 탭) + 삭제 백엔드 신설 — 운영 콤보는 선택 전용이라 시나리오를 만들/지울 곳이 없던 문제 해결. **배치=계획 모드 관리 섹션**(시나리오는 계획 데이터), **삭제=하드 삭제+가드**. 단일 소스 원칙으로 운영 콤보가 바인딩하는 `MissionViewModel`에 추가(동일 `Scenarios` 컬렉션 공유→운영/계획 자동 동기화): `NewScenarioName`·`TankId`(Shell이 열기/새프로젝트/초기화 시 동기화)·`CreateScenarioCommand`(기존 `POST /api/scenarios` 재사용)·`DeleteScenarioCommand`(성공/409 메시지는 `StatusMessage`). 백엔드 `DELETE /api/scenarios/{id}` 신설(Program.cs) — `run.scenario_run.scenario_id`는 FK 미설정이라 코드 가드 필수: 참조 run 있으면 **409**, 없으면 삭제(ref.inspection_point/task는 FK ON DELETE CASCADE). 클라이언트 `IAcsApiClient.DeleteScenarioAsync`(EnsureSuccessOrThrowAsync로 409 메시지 노출). UI: `PlanningView`에 "시나리오" 탭 추가(RadGridView 목록[이름/Ver/상태/선창]+이름 입력+생성/삭제+상태 안내). 운영 `OperationView` 콤보 무변경(동일 소스 자동 반영). 전체 솔루션 빌드 0 error, App(:5100)+PostgreSQL E2E: REST 생성→목록(12→13)→삭제→목록(13→12) 왕복 확인, UI 계획▸시나리오 탭 목록·생성·삭제 동작 확인. `SlicingView`(구 생성 UI 잔재) 무변경
- 2026-08-09: UI "모드 분리 운영 콘솔" 재구성 1단계(셸+운영+계획) — 단일 RadDocking(계획·운영 혼재)을 상단 **모드 탭(운영/계획/이력)** + 모드별 워크스페이스로 전환. 시안(claude.ai 아티팩트) 방향을 WPF에 반영, 셸/레이아웃 리팩터만(백엔드·DB·VDA 5050 무변경, 기존 뷰·VM 재사용). `ShellViewModel`에 `AppMode`(신규 enum)·`CurrentMode`·`SetModeCommand` 추가; 신규 `EnumToVisibilityConverter`/`EnumToBooleanConverter`(Converters.cs). `MainWindow.xaml`을 앱바(브랜드·파일 메뉴·모드 탭 RadToggleButton[Command+CommandParameter=x:Static AppMode, IsChecked OneWay]·연결칩·상시 ■비상정지) + 본문 3뷰(Visibility 전환, 모두 로드 유지→TankView 3D·SignalR 구독 보존)로 교체. 신규 `Views/OperationView`(고정 3열: 좌 RobotStatusView 카드+층 진행 레일/수동 층 변경 탭, 중앙 TankView 히어로+미션 컨트롤 바[시나리오·로봇 콤보·시작·다음 층 릴리스·새로고침], 우 AlarmsView 피드)·`Views/PlanningView`(RadTabControl: 영역·작업=AreaManagementView / 캘리브레이션=CalibrationView)·`Views/HistoryView`(플레이스홀더+후속 백엔드 안내). `MissionViewModel`에 파생 `FloorProgress`(Missions를 층별 done/run/wait/fail로 coarse 분류, 검사점 %는 백엔드 미노출)·`ProgressSummary` 추가. `Themes/Brushes.xaml`에 `AppAccentBrush`/`AppAccentSoftBrush`/`AppGoodBrush` 추가. DI 무변경(신규 뷰는 DataContext=Shell 상속). UI 컴파일 0 error, 실행 확인(운영 화면 3열·모드 탭 동작). **이력 모드는 조회 백엔드(GET /api/runs 목록·hist.inspection_result 조회 API) 부재로 플레이스홀더** — 후속. MissionView.xaml은 보존(미배치)
- 2026-08-08: UI 다크 테마 적용 — 이전 다크 테마 작업이 PC 종료로 유실(디스크에 흔적 0)되어 처음부터 재구현. ① Telerik Fluent 팔레트를 Dark로 전환(`App.xaml.cs`에 `FluentPalette.LoadPreset(FluentPalette.ColorVariation.Dark)`를 `new FluentTheme()` 앞에 추가; LoadPreset은 static·enum에 Dark 존재를 어셈블리 메타데이터로 확인) → RadDocking/RadMenu/RadGridView 등 Telerik 컨트롤 크롬 전부 다크. ② 비-Telerik 요소용 중앙 다크 브러시 사전 신설 `Themes/Brushes.xaml`(표면 Window/Header/Surface/Canvas·Border·Text/Muted/OnAccent·시맨틱 Info/Warn/Error) + `App.xaml`에 MergedDictionaries 병합. ③ 뷰 하드코딩 색상(XAML 10파일 ~63곳)을 용도별 `{DynamicResource}` 키로 치환 — 크롬(배경/글씨/테두리/콜아웃)만 다크로, 데이터-뷰 의미색(영역=녹·용접선=주황·끝점/오류=빨·3D 반투명 틴트)과 이미 어두운 3D 뷰포트(#1B2631)는 유지. 플레인 WPF 텍스트가 다크 배경에서 검정으로 남는 문제는 각 뷰 루트에 `TextElement.Foreground`(상속) 부여로 해결(implicit TextBlock 스타일 미사용—Telerik 내부 누수 회피). 흰 배경 도면/전개도 캔버스(`AppCanvasBrush`)·카드(`AppSurfaceBrush`) 다크화. `TankView.xaml.cs` 3D 코드 브러시는 어두운 뷰포트 기준이라 무변경. UI 12파일(App.xaml/.cs·Brushes.xaml 신규·MainWindow+뷰 9개), 백엔드/DB/스키마·VDA 5050 payload 무변경. UI 컴파일 0 error(실행 중 UI가 exe 잠금—copy만 실패, 재기동 후 반영). 겸사로 프로젝트 열기/저장 실패 메시지를 원인별로 구분(`ShellViewModel.DescribeProjectFailure`: 파일 파싱 InvalidDataException vs 서버/DB HttpRequestException)
