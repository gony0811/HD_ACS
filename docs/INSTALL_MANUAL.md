# HD_ACS 설치 매뉴얼 — 단계별 절차

| 항목 | 내용 |
|---|---|
| 문서 버전 | 1.0 (2026-09-02) |
| 대상 | HD_ACS를 PC(개발/실험실/현장 서버)에 설치·기동하려는 담당자 |
| 소요 시간 | 인프라 포함 약 30~60분 (다운로드 제외) |
| 구성 요소 | HD.Acs.App(서버 :5199) · HD.Acs.UI.Desktop(Avalonia 운영 앱, Win/macOS/Linux) · PostgreSQL(:5432) · MQTT 브로커(:1883) · HD.Acs.Simulator(검증용 가상 로봇) |

> 이 PC(개발 PC)에는 3~4단계 인프라가 **이미 docker로 구동 중**(dev-postgres/dev-rabbitmq)이고 DB도 구성돼 있다.
> 이 PC 기준으로는 **5단계(빌드)부터** 진행하면 된다. 신규 PC는 0단계부터 순서대로.

---

## 0단계. 사전 요구사항

| 항목 | 요구 | 확인 방법 |
|---|---|---|
| OS | Windows 10/11 · macOS · Linux (UI 포함 전 구성요소가 크로스플랫폼) | — |
| .NET SDK | **8.0 이상** (9.x로 8.0 타깃 빌드 가능) | `dotnet --version` |
| Docker Desktop | 인프라를 docker로 쓸 경우 (권장) | `docker --version` |
| Git | 소스 확보용 | `git --version` |

⚠ **포트 확인**: 이 프로젝트의 서버 포트는 **5199**다(5100은 이 개발 PC에 상주하는 NAMUGA 계열 제품
`CS01_P.exe`와 충돌하여 회피). 설치 대상 PC에서 5199·5432·1883이 비어 있는지 확인:

```powershell
Get-NetTCPConnection -LocalPort 5199,5432,1883 -State Listen -ErrorAction SilentlyContinue
```

---

## 1단계. 소스 확보

```powershell
git clone https://github.com/gony0811/HD_ACS.git D:\Github\HD_ACS
cd D:\Github\HD_ACS
```

폐쇄망이면 개발 PC에서 리포 폴더 전체를 복사(빌드 산출물 `bin/`, `obj/` 제외 가능).

---

## 2단계. 인프라 기동 — PostgreSQL + MQTT 브로커

### 방법 A — Docker Compose (권장, 이 PC 방식)

```powershell
cd D:\Github\HD_ACS\docker
# .env 파일 확인/작성: POSTGRES_USER=postgres POSTGRES_PASSWORD=postgres POSTGRES_DB=hdacs
#                      RABBITMQ_USER=... RABBITMQ_PASSWORD=...
docker compose up -d
# dev-postgres(5432)·dev-rabbitmq(1883/5672/15672) 확인
docker ps
```

- PostgreSQL 볼륨이 **비어 있는 최초 기동**이면 `db/schema.sql`이 **자동 적용**된다(3단계 생략 가능).
- RabbitMQ는 MQTT 플러그인이 활성화돼 1883에서 MQTT를 서비스한다.

### 방법 B — 네이티브 설치 (현장 서버, ADR-011)

- PostgreSQL 16 설치 → 서비스 자동 시작
- **Mosquitto** 설치(https://mosquitto.org) → `mosquitto.conf`에 `listener 1883` + `allow_anonymous true`(폐쇄망 전제)
  → OS 서비스 등록

⚠ **로봇(실 AMR)이 접속하는 환경**이면 브로커가 localhost가 아닌 **PC의 실제 IP로 수신**하도록 하고
방화벽에서 1883 인바운드를 허용할 것.

---

## 3단계. DB 스키마 적용 (방법 A 최초 기동이면 생략)

```powershell
# docker인 경우 (DB가 없을 때만 CREATE — 이미 있으면 "already exists" 오류는 무해)
docker exec dev-postgres psql -U postgres -c "CREATE DATABASE hdacs;"
docker cp D:\Github\HD_ACS\db\schema.sql dev-postgres:/tmp/schema.sql
docker exec dev-postgres psql -U postgres -d hdacs -f /tmp/schema.sql

# 네이티브인 경우
createdb -U postgres hdacs
psql -U postgres -d hdacs -f D:\Github\HD_ACS\db\schema.sql
```

`schema.sql`은 **시드 포함 통합 최신본**이다: 30테이블+2뷰, `startWeldInspection` 액션 카탈로그(param_schema
u,v 포함), 알람 코드 6종, 로봇 `AMR-01`(manufacturer=HHI, serial=AMR-01).

> **기존 DB를 업그레이드**하는 경우에만 `db/migrations/`의 파일을 날짜순으로 적용한다(전부 idempotent).
> 신규 설치는 schema.sql 하나로 끝.

확인:

```powershell
docker exec dev-postgres psql -U postgres -d hdacs -c "SELECT robot_id, manufacturer, serial_number FROM ref.robot;"
# → AMR-01 | HHI | AMR-01
```

---

## 4단계. 빌드

### 4.1 솔루션 전체 (어느 OS에서나 동일)

전 프로젝트가 `net8.0`이라 솔루션 하나로 빌드·테스트한다. 상용 피드 자격증명은 필요 없다
(Telerik UI for WPF에 의존하던 WPF 헤드는 은퇴 — 2026-09-21).

```bash
cd src
dotnet build HD.Acs.sln
dotnet test  HD.Acs.sln
```

개별 프로젝트만 빌드하려면:

```bash
dotnet build HD.Acs.App/HD.Acs.App.csproj
dotnet build HD.Acs.Simulator/HD.Acs.Simulator.csproj
dotnet test  HD.Acs.Core.Tests/HD.Acs.Core.Tests.csproj
```

### 4.2 운영 UI (Windows·macOS·Linux 공통)

```bash
dotnet build HD.Acs.UI.Desktop/HD.Acs.UI.Desktop.csproj
dotnet run   --project HD.Acs.UI.Desktop
```

배포 산출물(.app 번들 등)은 `tools/publish_desktop.sh <rid>` — 자세한 절차는 `MANUAL.md` §4.5.
루트 `nuget.config`는 nuget.org 하나만 등록한다. 폐쇄망이면 개발 PC의 전역 패키지 캐시
(`%USERPROFILE%\.nuget\packages` / `~/.nuget/packages`)를 복사해 오프라인 복원.

---

## 5단계. 설정 확인 — `src/HD.Acs.App/appsettings.json`

| 키 | 기본값 | 변경이 필요한 경우 |
|---|---|---|
| `ConnectionStrings:Default` | Host=localhost;Port=5432;Database=hdacs;Username=postgres;Password=postgres | DB가 다른 호스트/계정일 때 |
| `Acs:Mqtt:Host` / `Port` | localhost / 1883 | 브로커가 다른 호스트일 때 |
| `Acs:Api:ListenHost` | 0.0.0.0 | 저장소 기본값은 **다른 PC의 UI 접속 허용**(방화벽 5199 인바운드 허용 필요). 로컬 UI만 쓰면 `localhost`로 (코드 폴백값도 localhost) |
| `Acs:Api:ListenPort` | **5199** | 유지 권장 (5100 금지 — CS01_P 충돌) |
| `Acs:Area:StationStandoffM` | 0.8 | 정차 이격 기본값 — 로봇 치수 확정(N10) 시 조정 |
| `Acs:Dispatch:MaxRetries` | 2 | 실패 재시도 상한 |
| `Acs:Dispatch:AllowedDevXy` / `Theta` | 0.08 / 0.07 | 도착 판정 허용 오차 |

UI 쪽: `src/HD.Acs.UI.Desktop/appsettings.json`의 `Acs:BaseUrl`이 `http://localhost:5199`인지 확인
(환경변수 `Acs__BaseUrl`로도 덮어쓸 수 있다).
**서버가 다른 PC**(예: Mac에서 App 실행)면 ① 서버 측 `ListenHost`를 `0.0.0.0`으로 ② UI 측 `BaseUrl`을
`http://<서버IP>:5199`로 바꾼다(REST·SignalR 공용 — 이 값 하나면 됨). 수정 후 UI 재빌드(또는
`bin\Debug\net8.0\appsettings.json` 직접 수정) 필요.

---

## 6단계. 서버 기동·확인

```powershell
cd D:\Github\HD_ACS\src
# 또는 bin\Debug\net8.0\HD.Acs.App.exe 직접 실행(어느 폴더에서든 가능)
dotnet run --project HD.Acs.App
```

기동 로그에서 확인할 것: `Now listening on: http://localhost:5199`, `VDA 5050 마스터 기동 — 로봇 1대 구독`.

```powershell
# 동작 확인
Invoke-RestMethod http://localhost:5199/api/robots
# (Mac/Linux) curl http://localhost:5199/api/robots
# → AMR-01 응답이면 서버·DB 정상
```

---

## 7단계. UI 기동

```powershell
dotnet run --project HD.Acs.UI
```

- 상단 연결 칩이 **연결됨**(SignalR)인지 확인. 운영/계획/이력 모드 탭 표시 확인.
- 계획 데이터가 없으면 **파일 ▸ 새 프로젝트**로 선창 파라미터 등록부터(운영 워크플로우는 `MANUAL.md` 참조).

---

## 8단계. 시뮬레이터로 설치 검증 (실로봇 불필요)

```powershell
cd D:\Github\HD_ACS\src
# 무인자 = localhost / HHI / AMR-01 / CT1-L1
dotnet run --project HD.Acs.Simulator
```

- 콘솔에 `[SIM] AMR-01 ONLINE` + UI 로봇 상태가 ONLINE으로 바뀌면 **MQTT 사슬 정상**.
- (계획 데이터가 있다면) 운영 탭에서 시나리오 선택 → **미션 시작** → 작업 현황·지도 상태색 변화 →
  run COMPLETED까지 확인하면 전체 설치 검증 완료.
- 자동 검증: `cd src && ./run_simtest.sh` (bash 환경 — 시뮬레이터 계약 3시나리오 PASS 확인).

---

## 9단계. (선택) OS 서비스 등록 — 현장 상시 운영 [ADR-011]

> 전용 배포 스크립트는 아직 없다(후속 과제 Q4). 아래는 수동 절차.

```powershell
# 1) 자체 포함 게시
dotnet publish src\HD.Acs.App -c Release -o C:\HDACS\app

# 2) Windows 서비스 등록 (관리자 PowerShell)
sc.exe create HDACS-App binPath= "C:\HDACS\app\HD.Acs.App.exe" start= auto
sc.exe start HDACS-App
```

- App은 content root가 exe 위치로 고정돼 있어 서비스(cwd=System32)에서도 appsettings를 정상 로드한다.
- Mosquitto·PostgreSQL도 각자 서비스 자동 시작으로 두고, 기동 순서 의존은 없음(App이 브로커 연결을 5초 간격 재시도).

---

## 10단계. 문제 해결 (실제 발생 이력 기반)

| 증상 | 원인 / 조치 |
|---|---|
| API 호출이 404·엉뚱한 응답 | **다른 프로그램이 그 포트 점유** — 이 PC의 5100은 CS01_P. `Get-NetTCPConnection -LocalPort <포트>`로 점유 프로세스 확인. HD_ACS는 5199 사용 |
| 기동 시 `ConnectionString ... not initialized` | 구버전 빌드에서 exe를 타 폴더에서 실행한 경우 — 최신 소스로 재빌드(content root 고정 반영됨) |
| 기동 시 `address already in use` | 이전 App 인스턴스 잔존 — `Get-Process HD.Acs.App \| Stop-Process` 후 재기동 |
| 시뮬레이터/로봇이 ONLINE 안 됨 | **serial 3자 불일치** — `ref.robot.serial_number` = 로봇(시뮬레이터) serial = MQTT 토픽 요소가 모두 일치해야 함. 기본 AMR-01 |
| 등록/조회가 500 | **DB 스키마 구버전** — 3단계 최신 schema.sql(또는 migrations 순서 적용) 재확인 |
| UI 복원(NuGet) 실패 | 공개 피드(nuget.org) 접근 불가 — 폐쇄망이면 패키지 캐시 복사(4.2 참고). Telerik 피드 의존은 제거되었다 |
| UI가 구버전 동작(포트 5100 호출 등) | UI **재빌드 없이** 구 exe 실행 — `dotnet build` 후 재실행 |
| run 시작이 409 | 같은 로봇의 진행 중 run 존재 — 운영 화면 "이어하기" 또는 "중단" 후 시작 |

---

## 부록. 구성 요소·포트 요약

```
┌─────────────┐  REST+SignalR   ┌──────────────┐  VDA5050/MQTT  ┌────────────┐
│ UI.Desktop  │ ──── :5199 ───▶ │  HD.Acs.App  │ ─── :1883 ───▶ │ 브로커      │◀── 로봇/시뮬레이터
│ (Avalonia)  │                 │  (서버)       │                │ (RabbitMQ/  │
└─────────────┘                 └──────┬───────┘                │  Mosquitto) │
                                       │ :5432                  └────────────┘
                                ┌──────▼───────┐
                                │  PostgreSQL  │  DB=hdacs (ref/run/hist/alarm/sys)
                                └──────────────┘
```

관련 문서: 운영 방법 `MANUAL.md` · 운영 사양 `ACS_OPERATION_SPEC.pdf` · 로봇 계약 `VDA5050_INTERFACE_SPEC.pdf` · 실험실 평가 `LAB_TEST_GUIDE.md`
