# 참조 프로젝트: NAMUGA_ACS 분석 및 재사용 전략

> 출처: https://github.com/gony0811/NAMUGA_ACS (2026-07-15 분석 기준)
> NAMUGA_ACS는 사내에서 이미 운영 중인 .NET 8 기반 AMR/반송 관제(ACS) 플랫폼이다.
> HD_ACS는 이 플랫폼의 아키텍처·코드 자산을 기반으로 개발한다.

## 1. NAMUGA_ACS 아키텍처 요약

### 솔루션 구조 (계층형)
```
ACS.Core            인터페이스, 도메인 모델, 베이스 클래스 (최하위)
  ↑
ACS.Communication   프로토콜 구현 — Socket(TCP/XML), MQTT, RabbitMQ(Msb),
                    HTTP(uHttpSharp), SECS, XBee (멀티 프로토콜)
ACS.Manager         공통 비즈니스 로직 — Message/Resource/Path/Alarm/
                    Transfer/Material/History/Application Manager
ACS.Service         프로세스 유형별 서비스 계층
  ↑
ACS.App             진입점 — Executor 패턴 (설정 로드 → Autofac 컨테이너 빌드
                    → DB 초기화 → 스케줄러/서비스 기동), REST API 호스트 (:5100)
ACS.Elsa            Elsa 3.5 워크플로우 엔진 통합 (Autofac↔Elsa DI Bridge)
ACS.Elsa.Studio(+Client)  워크플로우 디자이너 웹 UI (Blazor WASM, :5200)
ACS.UI              Avalonia 11 + CommunityToolkit.Mvvm 데스크톱 관제 UI
ACS.AMR.Simulator   AMR 시뮬레이터 (관제 개발/테스트용)
ACS.Host.Test       Host(MES) 연동 테스트
```

### 핵심 패턴
- **Executor 패턴**: 기동 시퀀스의 단일 진입점
- **모듈러 DI (Autofac 9.1)**: 프로세스 타입(`Acs:Process:Type`)·사이트(`Acs:Site:Name`) 설정으로 동일 코드베이스에서 다른 런타임 구성 — 멀티 사이트/멀티 프로세스 재사용의 핵심
- **Elsa 워크플로우 오케스트레이션**: 메시지 단위 워크플로우(Trans/Host/Ei/Control 카테고리)로 비즈니스 흐름을 코드+JSON으로 정의, Studio에서 시각 편집
- **문서화 관행**: 루트 CLAUDE.md + 프로젝트별 `*.claude.md` + `docs/memory.md`(작업/결정 기록) + `docs/todo.md`

### 기술 스택
| 영역 | 기술 |
|---|---|
| 런타임 | .NET 8 |
| DI | Autofac 9.1 |
| DB | PostgreSQL + EF Core (Elsa 상태도 PostgreSQL) |
| 로깅 | Serilog |
| 스케줄링 | Quartz 3.13 |
| 워크플로우 | Elsa 3.5 + Elsa Studio(Blazor WASM) |
| 데스크톱 UI | Avalonia 11 + CommunityToolkit.Mvvm (MapCanvas 2D 맵, 1초 폴링, 역할 기반 로그인) |
| AMR 통신 | MQTT 커스텀 메시지 (AmrCommand/Status/Reply, RailVehicle* 계열) |
| Host(MES) 통신 | TCP/IP 바이너리 프레임 XML (connect-per-message) |

## 2. HD_ACS 관점의 GAP 분석

| 항목 | NAMUGA_ACS | HD_ACS 요구 | 판단 |
|---|---|---|---|
| AMR 프로토콜 | MQTT 커스텀 메시지 | **VDA 5050** [ADR-001] | 어댑터 신규 개발, MQTT 인프라(`MqttInterfaceManager`)는 재사용 |
| 실행 모델 | 관제 주도 반송 디스패치 | 온보드 실행 + 두절 내성 [ADR-002] | Base 선릴리즈/재동기화 로직 신규 |
| 도메인 | 컨테이너/자재 반송 (Transfer) | 용접 검사 시나리오 (Inspection) | Transfer→Inspection 도메인 모델 교체, Resource/Alarm/History는 개념 재사용 |
| UI | Avalonia 11, 2D MapCanvas | 3D 뷰 + 전개도 [ADR-005] | ⚠️ 결정 필요 — 아래 3절 |
| 실시간 전파 | 1초 REST 폴링 | SignalR 푸시 (다중 사용자) [ADR-003] | 푸시 계층 신규 (개선 포인트) |
| DB | PostgreSQL + EF Core | 미결(Q3)이었음 | **PostgreSQL로 확정 제안** — 팀 자산 일치 |
| 시뮬레이터 | ACS.AMR.Simulator | 필요 (VDA 5050) | 패턴 재사용, VDA 5050 시뮬레이터로 재작성 |
| 워크플로우 | Elsa 3.5 | 미션 오케스트레이션 필요 | Elsa 재사용 유력 — 아래 3절 |

## 3. 주요 결정 포인트 (참조 분석으로 새로 발생)

### (1) UI 프레임워크: WPF vs Avalonia — ⬜ 재검토 필요
- ADR-005는 WPF로 결정했으나, 팀의 기존 관제 UI 자산(Avalonia 11 + MVVM + MapCanvas + 로그인/권한)은 Avalonia에 있다.
- 긴장점: Avalonia는 성숙한 3D 렌더링 생태계가 없다 (WPF는 HelixToolkit 등 존재). 반면 Avalonia를 버리면 MapCanvas/권한/서비스 계층 UI 자산 재사용률이 낮아진다.
- 선택지:
  - A안. **WPF 신규** — 3D/전개도 최우선. UI 자산은 포기하되 ViewModel/Service 패턴은 이식
  - B안. **Avalonia 유지 + 3D 우회** — 전개도(2D)는 MapCanvas 확장, 3D는 별도 뷰어(외부 창) 또는 임베디드 렌더러
- 결정 기준: 3D 뷰의 실제 요구 수준 (실시간 로봇 포즈 표시? 단순 형상 확인?)

### (2) 미션 오케스트레이터: Elsa 채택 여부 — ⬜
- NAMUGA_ACS는 메시지 단위 Elsa 워크플로우로 오케스트레이션하며 Studio로 시각 편집 가능 — 현장 엔지니어 유지보수[ADR-006]에 유리
- HD_ACS 검사 미션(선릴리즈 + 두절 내성)은 장주기·상태보존형 흐름이라 Elsa의 지속 실행(persistence) 모델과 부합
- 대안: 경량 상태머신 직접 구현 (Stateless 라이브러리 등) — 의존성은 줄지만 시각 편집 포기

### (3) Host(HD현대 상위 시스템) 연동 — ⬜
- NAMUGA_ACS의 Host 연동(TCP/XML, MOVECMD/JOBREPORT)은 MES 표준 패턴. HD현대중공업 측 상위 시스템(생산/품질) 연동 요구가 나오면 이 계층을 재사용할 수 있다. 현재는 범위 미정.

## 4. 재사용 전략 (제안)

```
그대로 재사용        ACS.Core 계층 구조, Executor+Autofac 모듈러 DI,
                   Serilog/Quartz 인프라, MqttInterfaceManager,
                   Alarm/History/Resource Manager 골격, 로그인/권한 모델
개조 재사용         Elsa 워크플로우 (Trans→Inspection 워크플로우로 재정의),
                   AMR Simulator (VDA 5050 메시지로 교체),
                   REST API 호스트 (SignalR 허브 추가)
신규 개발           VDA 5050 마스터 어댑터 (order/state/connection/instantActions,
                   Base 선릴리즈, robot-is-truth 재동기화),
                   검사 시나리오 도메인 (Scenario/Point/Task),
                   3D/전개도 UI
도입 보류/제외      SECS, XBee, RabbitMQ(Msb), Host TCP/XML (요구 발생 시)
```

## 5. HD_ACS에 승계할 관행

- 프로젝트별 `*.claude.md` 문서 + 루트 CLAUDE.md 체계
- `docs/memory.md` 작업/결정 기록, `docs/todo.md` 일정 관리
- 코드 주석·운영 문서 한국어 기준
- publish/deploy 스크립트 (ps1) 기반 배포 — 폐쇄망 배포[ADR-003]와 정합
