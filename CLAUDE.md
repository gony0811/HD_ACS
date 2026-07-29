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
│   ├── DB_SCHEMA.md           # DB 스키마 카탈로그 (24테이블+2뷰, ERD)
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
