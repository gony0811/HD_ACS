# HD_ACS 사용자 매뉴얼

> **대상 독자**: HD_ACS를 설치·실행·운영하려는 개발자/운영자.
> **문서 성격**: "어떻게 사용하는가" 중심. 코드 구조·향후 개발 방향은 `DEVELOPMENT_GUIDE.md` 참고.
> 최종 갱신: 2026-08-03 (커밋 `773441e` — seam 슬라이싱 API + WPF 시각화 기준)

---

## 1. HD_ACS가 무엇인가 (1분 요약)

HD_ACS는 **HD현대중공업 LNG 화물창 용접검사로봇의 관제 시스템(Mission Control)** 이다.
로봇을 직접 제어하지 않는다 — 유일한 통신 상대는 로봇 온보드의 **HD_AMR 통합 운영 S/W**이고,
인터페이스는 **VDA 5050 over MQTT** 하나뿐이다.

```
운영자(WPF UI / Web) ──REST+SignalR──▶ HD_ACS 서버 (:5199)
                                          │ VDA 5050 over MQTT (:1883)
                                          ▼
                       HD_AMR (로봇 온보드) — AMR 주행·코봇 검사·장비 제어 전담
```

역할 분담을 한 문장으로: **ACS는 "어느 지점에서 어떤 검사를 언제", HD_AMR은 "어떻게".**
검사 시퀀스·자세 제어·BASE 좌표 계산은 전부 로봇 쪽 책임이며 ACS 코드에 절대 넣지 않는다.

핵심 개념 흐름 (PHASE 2 도면 기반 워크플로우):

```
용접선(Seam) 등록 ─▶ 슬라이싱 ─▶ 스테이션+TASK 생성 ─▶ 시나리오 ─▶ Run 시작
  (도면 좌표)      (코봇 리치 단위)  (T_W_D로 맵 좌표 변환)              │
                                                                     ▼
                              VDA 5050 Order (노드=정차점, 액션=startWeldInspection)
```

**TASK 불변식** (모든 데이터·UI·통신의 기준): *TASK 1개 = 용접라인 1개의, 코봇 리치 안 1개 구간.*
따라서 TASK 1 = actionId 1 = 검사결과 1행 = 재시도 단위 1이다.

---

## 2. 솔루션 구성

`src/HD.Acs.sln` — .NET 8, 프로젝트 8개:

| 프로젝트 | 역할 |
|---|---|
| `HD.Acs.Core` | 도메인 — MissionStateMachine(Stateless), MapGraph(층 내 Dijkstra), DrawingTransform(T_W_D), SeamSlicer |
| `HD.Acs.Core.Tests` | xUnit 단위 테스트 (DrawingTransform, SeamSlicer) |
| `HD.Acs.Data` | EF Core + Npgsql — ref/run/hist/alarm/sys 스키마 엔티티, GraphLoader |
| `HD.Acs.Vda5050` | VDA 5050 v2.0 메시지, MQTT 마스터 클라이언트(MQTTnet), OrderBuilder |
| `HD.Acs.App` | **서버 본체** — ASP.NET Core, REST(:5199) + SignalR + VDA 브릿지, 단일 프로세스 [ADR-011] |
| `HD.Acs.Simulator` | 가상 HD_AMR — Order 수신→노드 순회→액션 실행/보고. 실장비 없이 E2E 검증용 |
| `HD.Acs.SimTest` | 시뮬레이터 검증 드라이버 (ACS·DB 없이 마스터 역할 수행, 시나리오 S1~S3) |
| `HD.Acs.UI` | WPF 운영 앱 (**Windows 전용**, net8.0-windows) — Telerik Fluent + HelixToolkit 3D |

MQTT 토픽 규약: `uagv/v2/{manufacturer}/{serialNumber}/{channel}` (channel = order / instantActions / state / connection / factsheet).

---

## 3. 사전 요구사항

- **.NET 8 SDK**
- **PostgreSQL 16** — 스키마는 `db/schema.sql` (ref/run/hist/alarm/sys, snake_case)
- **MQTT 브로커** — 로컬 개발은 Mosquitto가 간단. `docker/docker-compose.yml`은 RabbitMQ(MQTT 플러그인, 1883 포트)도 제공
- **(UI 빌드 시) Windows + Telerik NuGet 피드 자격증명** — Telerik UI for WPF 2025.3.813은 `nuget.telerik.com` 로그인 필요.
  루트 `nuget.config`는 nuget.org + Telerik 두 소스만 등록하며 **packageSourceMapping을 추가하지 말 것**
  (Telerik.Licensing은 nuget.org, MediaFoundation은 Telerik 피드로 나뉘어 있어 매핑이 복원을 깨뜨린 이력 있음)
- Mac/Linux에서는 UI 프로젝트를 제외하고 빌드한다:
  `dotnet build src/HD.Acs.App` 처럼 프로젝트 단위로 빌드하거나 sln에서 UI 제외

---

## 4. 설치 및 실행

### 4.1 인프라 기동

**방법 A — Docker Compose** (`docker/` 디렉터리, `.env`에 POSTGRES_USER/PASSWORD/DB, RABBITMQ_USER/PASSWORD 정의):

```bash
cd docker
docker compose up -d       # PostgreSQL :5432, RabbitMQ :5672/:15672(관리콘솔)/:1883(MQTT)
```

**방법 B — 로컬 설치**: PostgreSQL + Mosquitto (`brew install mosquitto` / `apt install mosquitto`).

**DB 스키마 적용** (최초 1회, DB명은 appsettings 기본값 기준 `hdacs`):

```bash
createdb hdacs
psql -d hdacs -f db/schema.sql
```

> `schema.sql`에는 `startWeldInspection` 액션 카탈로그 시드가 포함되어 있다.
> 선창 geometry 등록·프로젝트 불러오기 시 `level_z`의 층 수에 맞춰 `ref.map`을 자동 등록한다
> (`{tankId}-L{n}`, 예: `CT1-L2`). 기존 맵·버전·캘리브레이션은 보존하고 누락된 층만 추가한다.
> 서버 기동 시에도 기존 geometry의 누락 맵을 보정한다. `ref.node` / `ref.edge` 주행 그래프는 현장 데이터로 별도 입력한다.

### 4.2 서버(App) 실행

```bash
cd src
dotnet run --project HD.Acs.App        # http://localhost:5199
```

`HD.Acs.App/appsettings.json` 주요 설정:

| 키 | 기본값 | 의미 |
|---|---|---|
| `ConnectionStrings:Default` | localhost/hdacs/postgres | PostgreSQL 연결 |
| `Acs:Mqtt:Host/Port` | localhost:1883 | MQTT 브로커 |
| `Acs:Api:ListenPort` | 5199 | REST/SignalR 포트 |
| `Acs:Calibration:RmsWarnM` | 0.05 | T_W_D 등록 잔차 RMS 경고 임계(m) |
| `Acs:Slicer:CobotReachM` | 1.0 | 코봇 리치(m) — 슬라이싱 구간 길이 결정 |
| `Acs:Slicer:OverlapM` | 0.2 | 구간 겹침(m) |
| `Acs:Slicer:StandoffM` | 0.4 | 벽면→정차점 법선 오프셋(m) |
| `Acs:Slicer:MergeDistM` | 0.3 | 이 거리 내 정차점은 같은 스테이션(anchorGroup)으로 병합 |
| `Acs:Slicer:WorkingDistanceMm` | 400 | payload로 전달하는 워킹 디스턴스 |

### 4.3 시뮬레이터(가상 HD_AMR) 실행

```bash
cd src
dotnet run --project HD.Acs.Simulator -- localhost HHI AMR-01 CT1-L1
#                                        │        │   │      └ 초기 mapId(층)
#                                        │        │   └ serialNumber
#                                        │        └ manufacturer
#                                        └ MQTT 브로커 호스트
```

환경변수 옵션:

| 변수 | 용도 |
|---|---|
| `SIM_FAIL_ACTION_IDS` | 콤마 구분 actionId 목록 — 해당 액션을 FAILED로 보고 (재시도 정책 테스트) |
| `SIM_TRAVEL_MS` / `SIM_FULL_MS` / `SIM_SHARED_MS` | 주행 / 정렬 포함 검사(①~⑧) / 앵커 공유 검사(⑤~⑦) 소요시간(ms) |

시뮬레이터는 `startWeldInspection` 파라미터를 스키마 기준으로 검증하고(위반 시 FAILED + `FAIL;reason=PARAM(...)`),
같은 `anchorGroupId`가 연속되고 사이에 주행이 없으면 정렬을 스킵한다(⑤~⑦만 수행).
FINISHED 시 `resultDescription="OK;anchor=FULL|SHARED;jobRef=..."`로 실행 방식을 보고한다.

### 4.4 자동 검증 (SimTest)

ACS 서버·DB 없이 시뮬레이터만 검증하는 원커맨드 스크립트:

```bash
cd src
./run_simtest.sh           # mosquitto 기동 → 시뮬레이터(실패주입+고속타이밍) → 3개 시나리오 검증
```

시나리오: S1 앵커 공유(FULL/FULL/SHARED 순서 검증) · S2 파라미터 위반 검출 · S3 실패 주입.
exit code 0 = 전체 통과. 실장비 HD_AMR 구현 시 이 시나리오를 그대로 대조 기준으로 재사용할 수 있다.

### 4.5 WPF UI 실행 (Windows)

```bash
cd src
dotnet run --project HD.Acs.UI
```

셸 구성(RadDocking): 좌측 **화물창 뷰**(3D + 전개도, TankView), 우측 운영 패널 탭 —
로봇 상태 / 미션 / 알람 / 수동 층 변경 / **캘리브레이션(기준점 캡처)** / **슬라이싱(seam→스테이션 시각화)**.
상단 툴바에 비상정지 버튼. UI는 REST+SignalR만 사용한다(API-First — 서버에 직접 접근하는 로직 없음).

### 4.6 크로스플랫폼 UI(HD.Acs.UI.Desktop, Avalonia) 실행 — Windows / macOS / Linux

WPF UI와 같은 화면(운영/계획/이력, 3D·전개도)을 Win/mac/Linux에서 제공하는 헤드. 서버(App)는 그대로 Windows 현장 서버에 두고 UI만 원격 PC/Mac에서 띄운다.

**개발 실행** (.NET 8 SDK만 필요, 어느 OS든 동일)
```bash
cd src
dotnet run --project HD.Acs.UI.Desktop
```

**서버 주소 지정** — 실행 파일 옆 `appsettings.json` 의 `Acs:BaseUrl`(기본 `http://localhost:5199`) 또는 환경변수:
```bash
Acs__BaseUrl=http://192.168.0.10:5199 dotnet run --project HD.Acs.UI.Desktop     # mac/Linux
$env:Acs__BaseUrl="http://192.168.0.10:5199"; dotnet run --project HD.Acs.UI.Desktop   # Windows PowerShell
```

**배포 산출물 만들기** (`tools/publish_desktop.sh`, 자체 포함 publish — 대상 PC에 .NET 설치 불필요)
```bash
tools/publish_desktop.sh osx-arm64   # Apple Silicon → artifacts/desktop/osx-arm64/HD_ACS.app (+ .zip)
tools/publish_desktop.sh osx-x64     # Intel Mac
tools/publish_desktop.sh win-x64     # Windows 폴더 (또는 tools\publish_desktop.ps1)
tools/publish_desktop.sh linux-x64
```
- Telerik 피드에 접근할 수 없는 PC(자격증명 없음)에서는 `HDACS_NUGET_SOURCE=https://api.nuget.org/v3/index.json` 을 붙인다 — Desktop 헤드는 공개 패키지만 쓴다.
- macOS 번들은 `Contents/MacOS/` 에 실행 파일·어셈블리·`appsettings.json` 을 두므로 **번들 안의 appsettings.json 을 편집해 서버 주소를 바꾼다**(또는 환경변수).
- 서명: 기본 ad-hoc(`codesign -s -`) — **같은 Mac에서 만든 번들은 그대로 실행**된다. 다른 Mac으로 배포하면 Gatekeeper가 차단하므로 ① Finder에서 우클릭 ▸ 열기(1회) 또는 `xattr -dr com.apple.quarantine HD_ACS.app`, ② 정식 배포는 `HDACS_SIGN_IDENTITY="Developer ID Application: …"` 로 서명 후 notarization(`xcrun notarytool submit`). 폐쇄망 현장은 ①로 충분.
- 아이콘(.icns)은 macOS에서 스크립트를 실행할 때만 생성된다(iconutil). Linux/Windows 호스트에서 만든 번들은 기본 아이콘.
- 파일 메뉴(새 프로젝트·열기·저장·다른 이름으로 저장)는 전 플랫폼에서 창 상단 앱바에 표시된다. macOS에서는 추가로 시스템 메뉴바(⌘N 새 프로젝트·⌘O 열기·⌘S 저장·⌘⇧S 다른 이름으로 저장)에도 같은 명령이 뜬다. 한글은 OS 시스템 폰트(Apple SD Gothic Neo / 맑은 고딕 / Noto CJK)로 폴백된다.
- 3D 뷰 조작: 좌드래그 회전 · 우드래그(또는 휠 클릭 드래그) 이동 · 휠 확대/축소 · 우상단 "맞춤". 트랙패드는 두 손가락 스크롤=줌.
- 3D 뷰의 로봇 마커(빨간 원) 중심에서 **3D 방향 화살표**(축+화살촉, 입체)가 뻗어 나온다 — AMR이 보고한 heading(VDA `agvPosition.theta`)을 층 캘리브레이션(T_W_D) yaw로 보정한 도면 방향. theta를 보고하지 않는 동안(부팅 직후 등)은 원만 보인다. 같은 값은 로봇 상태 카드 "방향(도면 x축 기준)"에 도 단위로 표시된다.

---

## 5. 운영 워크플로우 (도면 기반 검사 한 사이클)

### 단계 0 — 준비물
층별 `ref.map` + 주행 그래프(ref.node/edge), 용접선의 **도면 좌표**(벽면 전개 기준),
단면 DXF ID·검사 프로파일 ID(HD_AMR에 사전 배포된 식별자 — 원문은 전송하지 않음).

### 단계 1 — T_W_D 캘리브레이션 (도면↔맵 좌표 정합, 층마다 1회)

ACS는 SLAM 맵 원본을 갖지 않는다. 필요한 것은 층별 2D 강체변환 `T_W_D = (tx, ty, yaw)`뿐이다.

1. AMR을 도면상 위치를 아는 랜드마크 근처로 이동·정차시킨다 (AMR은 state로 pose를 2초마다 보고 중).
2. UI **캘리브레이션 패널**(또는 API)에서 해당 지점의 **도면 좌표를 입력하고 "기준점 캡처"** —
   ACS가 로봇 보고 pose(x,y)를 자동으로 짝지어 저장한다. 로봇이 다른 층(mapId 불일치)이면 409로 거부.
3. 2~3점 반복 — **점 간 거리를 최대화**할 것(가까운 3점보다 먼 2점이 낫다. 층 대각 양끝 권장).
   3점째는 잔차 검증용이다.
4. **Solve** 실행 → tx/ty/yaw + 잔차 RMS 확인. RMS가 `RmsWarnM`(기본 5cm) 초과면 경고 — 오등록 의심.

> **맵버전 바인딩**: T_W_D는 `mapId + ref.map.version`에 묶여 저장된다. SLAM 맵을 재생성(version 증가)하면
> 기존 T_W_D는 자동 무효(404)가 되며 재등록해야 한다. 일상적인 재측위는 무관.

### 단계 2 — 용접선(Seam) 등록

`POST /api/seams`로 도면 좌표 기준 등록. LINE은 시작·끝 2점, POLYLINE은 폴리라인 점열 + 벽면 법선 + 단면 DXF ID + 프로파일 ID.

### 단계 3 — 시나리오 생성 + 슬라이싱

```
POST /api/scenarios                          { "name": "...", "tankId": "CT1" }
POST /api/scenarios/{id}/generate-from-seams { "seamIds": [...], "userId": "..." }
```

서버가 자동으로: seam을 코봇 리치 단위로 슬라이싱 → 구간 중점 + 법선×standoff에 **스테이션**(정차점) 산출 →
근접 정차점 병합(같은 `anchorGroupId`) → T_W_D로 맵 좌표 변환 → STATION 노드 생성 + 최근접 주행 노드와 TRAVEL 엣지 연결 →
InspectionPoint(스테이션) + InspectionTask(TASK) 생성. **유효 T_W_D가 없으면 명시적으로 거부된다(조용한 기본값 없음).**

UI **슬라이싱 패널**에서 시나리오별 스테이션/TASK를 도면·맵 좌표로 시각화해 확인할 수 있다
(`GET /api/scenarios/{id}/stations`).

### 단계 4 — Run 시작·릴리즈

```
POST /api/runs                       { "scenarioId": "...", "robotId": "AMR-01" }
```

시나리오는 **층 단위 미션 시퀀스**로 분해된다(한 미션 = 한 층). 릴리즈 가드: 미션의 층 == 로봇이 보고하는 층(mapId)일 때만
Order 발행 [Q9]. Order는 두절 내성을 위해 전체 Base 선릴리즈된다 [ADR-002] — 통신이 끊겨도 로봇은 계속 실행한다.

**층 전환(수동 절차)**: 한 층 완료 → `WAITING_FLOOR_TRANSFER` → 작업자가 엘리베이터로 로봇 이송 →
AMR에서 새 층의 initPose 실행 → UI **수동 층 변경** 패널(또는 `POST /api/robots/{robotId}/zone`)로 목표 층 지정 →
`POST /api/runs/{runId}/release-next`로 다음 층 미션 릴리즈.

**수동 이동(이동 테스트)**: TankView에서 층(L1~L4)을 선택하면 그 층 주행 평면에 **1m 바닥 그리드**가 표시된다.
상단 "수동 이동(그리드 클릭)" 체크 후 그리드를 클릭하면 그 지점(도면 좌표→T_W_D 변환)으로 **액션 없는 단일 노드 Order**가
발행되어 로봇이 이동한다(보라 마커=목표점). 진행 중 run이 있거나 로봇이 다른 층이면 409로 거부(오조작 방지).
해당 맵 버전의 유효 T_W_D가 없으면 잘못된 좌표 발행을 막기 위해 400으로 거부한다.
API: `POST /api/robots/{id}/goto { level, xDrawing, yDrawing }`.

로봇 상태 패널과 선창 3D 마커의 위치는 AMR이 보고한 SLAM 좌표를 해당 층의 `T_W_D⁻¹`로 변환한
**도면 좌표**로 표시한다. 유효한 캘리브레이션이 없으면 SLAM 원시 좌표로 폴백하지 않고
`캘리브레이션 없음`으로 표시한다.

**부분 검사 계획**: 계획 ▸ 시나리오 탭에서 시나리오를 선택하면 우측 "검사 대상 영역" 패널에 선창 전체 영역이
체크박스 목록으로 뜬다 — 검사할 영역만 체크 후 "대상 저장". **체크 0개 = 선창 전체 검사**(정기 전수검사).
run 시작 시 그 시나리오에 담긴 영역만 큐로 전개된다(예: "L2 좌현벽 보수 후 재검" 시나리오에 PL 영역 8개만 담기).
전개는 시작 시점 스냅샷 — run 도중 시나리오를 바꿔도 진행 중 run에는 영향 없다.

**중단·이어하기**: 미션 컨트롤 바의 **"중단"**은 후속 배차만 멈춘다(진행 중 정차는 완주·결과 기록, 즉시 정지는 ■비상정지).
**"이어하기"**는 로봇의 가장 최근 미완료 run을 재개 — **완료·스킵된 영역은 건너뛰고** 남은 작업만 재배차한다
(중단 시점에 배차만 됐던 정차는 재검사). 같은 로봇에 진행 중 run이 있으면 새 "미션 시작"은 409로 거부된다.
완료 이력은 run 단위 — **새 run은 항상 선창 전체 재검사**(정기검사 사이클)이며, 영구 이력은 `hist.inspection_result`.

### 단계 5 — 모니터링·예외 대응

- 실시간 현황은 SignalR `/hubs/monitoring` → UI 로봇상태/미션 패널에 푸시.
- **작업 현황(실행 큐)**: 운영 탭 좌측 "작업 현황" 탭에서 정차 단위 항목(순번·영역·층·상태·재시도)을 실시간 확인
  (`WorkItemProgress` 푸시 — 배차/완료/재큐잉/스킵 시점). 같은 상태가 TankView 3D·전개도의 **영역 색**으로도 표시:
  대기=회색 · 배차중=파랑 · 완료=녹색 · 스킵/실패=빨강 (run이 없으면 계획 보기 기본 녹색). 층 진행 레일의 미니 바는
  그 층 실행 큐의 종결 비율(완료+스킵/전체).
- **용접라인 드릴다운**: 작업 현황에서 영역 행을 선택하면 아래로 그 영역의 **용접라인(액션) 목록**이 펼쳐진다 —
  순번·이름·상태 배지(PLANNED/WAITING=회 · RUNNING=파랑 · FINISHED=녹 · FAILED=빨)·실행 상세(`anchor=FULL/SHARED` 등).
  실시간은 `TaskActionProgress` 푸시(액션 상태 변화 단건), 초기 로드는 `GET /api/runs/{id}/task-actions`.
  TankView의 **용접선 선분**도 같은 상태색으로 칠해진다(run 없으면 계획 기본 주황).
- 상태의 진실은 항상 로봇(robot-is-truth): ACS는 state 보고의 lastNodeId/actionStates를 actionId로 대조해 DB를 갱신.
- 통신 두절 시 connection Last Will로 OFFLINE 표시 — 로봇은 릴리즈된 Order를 계속 실행, 복귀 시 state 기준 재동기화.
- **비상정지**: UI 툴바 또는 `POST /api/robots/{robotId}/emergency-stop` (instantAction 발행 + 감사로그).
  ⚠️ 이는 기능적 정지이며 안전 규격 정지가 아니다 — 인명 안전은 로봇 측 하드웨어 E-Stop 체계가 담당 [ADR-007].

---

## 6. REST API 레퍼런스 (:5199)

### 로봇
| 메서드/경로 | 설명 |
|---|---|
| `GET /api/robots` | 로봇 목록 |
| `GET /api/robots/{robotId}/context` | 로봇 컨텍스트 — 보고 pose(ReportedX/Y/Theta), 층(mapId), 온라인 여부 |
| `POST /api/robots/{robotId}/zone` | 수동 층 지정 — `{ mapId, userId }`; AMR 보고 mapId 검증 게이트 설정 |
| `POST /api/robots/{robotId}/emergency-stop` | 비상정지 — `{ userId }` (활성 run 자동 중단) |
| `POST /api/robots/{robotId}/goto` | 수동 이동(이동 테스트) — `{ level, xDrawing, yDrawing }` 도면 좌표→T_W_D→액션 없는 단일 노드 Order. 진행 run/타층 409 |

### 캘리브레이션 (T_W_D)
| 메서드/경로 | 설명 |
|---|---|
| `POST /api/maps/{mapId}/calibration/points` | 기준점 캡처 — `{ drawingX, drawingY, unit: "mm"\|"m", userId }`. 로봇 보고 층 ≠ mapId면 409 |
| `GET /api/maps/{mapId}/calibration/points` | 현재 맵버전의 대응쌍 목록 |
| `DELETE /api/maps/{mapId}/calibration/points/{id}` | 대응쌍 삭제 |
| `POST /api/maps/{mapId}/calibration/solve` | 최소자승 계산·저장 → `{ tx, ty, yawRad, rmsM, maxResidualM, pointCount, warning? }` |
| `GET /api/maps/{mapId}/calibration` | 현재 유효 T_W_D (맵버전 불일치 시 404) |

### 용접선·시나리오
| 메서드/경로 | 설명 |
|---|---|
| `POST /api/seams` | seam 등록 — `{ tankId, level, wallCode, seamType?, pathDrawing: [[x,y,z],...], normalDrawing: [nx,ny,nz], sectionDxfId?, profileId?, userId? }` |
| `GET /api/seams?tankId=&level=` | seam 목록 |
| `DELETE /api/seams/{seamId}` | seam 삭제 |
| `GET /api/scenarios` | 시나리오 목록 |
| `POST /api/scenarios` | 시나리오 생성 — `{ name, tankId }` |
| `POST /api/scenarios/{scenarioId}/generate-from-seams` | 슬라이싱 실행 — `{ seamIds?, userId? }` → `{ stations, tasks, skipped }` |
| `GET /api/scenarios/{scenarioId}/stations` | 생성된 스테이션/TASK 조회 (도면·맵 좌표, 전개도 렌더용) |

### 실행
| 메서드/경로 | 설명 |
|---|---|
| `GET /api/scenarios/{id}/areas` | 시나리오 검사 대상 영역 목록 [부분 검사 계획] |
| `PUT /api/scenarios/{id}/areas` | 대상 영역 전체 교체 — `{ areaIds: [...] }`. 빈 배열=선창 전체 검사. 타 선창/미존재 영역 400 |
| `POST /api/runs` | Run 시작 — `{ scenarioId, robotId }`. **시나리오 연결 영역만 전개(미연결=선창 전체)**, 층별 미션 분해 + 첫 미션 릴리즈 시도. 동일 로봇 활성 run 존재 시 409 |
| `GET /api/runs/{runId}` | Run/미션 상태 조회 |
| `POST /api/runs/{runId}/abort` | Run 중단 — 후속 배차 중지(진행 중 정차는 완주·기록). 즉시 정지는 비상정지 |
| `POST /api/runs/{runId}/resume` | Run 재개 — DONE/SKIPPED 보존, DISPATCHED→PENDING 리셋 후 잔여만 재배차. COMPLETED는 400 |
| `GET /api/runs/resumable?robotId=` | 로봇의 가장 최근 재개 가능 run(미종결 작업 보유) — 없으면 404 |
| `GET /api/runs/{runId}/work-items` | 실행 큐(정차 단위) 상태 조회 |
| `GET /api/runs/{runId}/task-actions` | 용접라인(액션) 단위 상태 조회 |
| `POST /api/runs/{runId}/release-next` | 층 전환 후 다음 층 미션 릴리즈 |

### 실시간
| 경로 | 설명 |
|---|---|
| `/hubs/monitoring` (SignalR) | 로봇 상태·미션 진행률·알람 푸시 |

---

## 7. 트러블슈팅

| 증상 | 원인/조치 |
|---|---|
| 기준점 캡처가 409 | 로봇이 보고하는 mapId ≠ 대상 mapId — 로봇 층 확인 또는 수동 층 변경 먼저 |
| `GET .../calibration`이 404 | T_W_D 미등록이거나 **맵버전 불일치**(맵 재생성됨) — 재캘리브레이션 |
| generate-from-seams 실패 | 유효 T_W_D 없음(의도된 명시적 실패) — 단계 1 먼저 수행 |
| Order가 릴리즈되지 않음 | 릴리즈 가드 — 로봇 보고 층과 미션 층 불일치. 시뮬레이터 4번째 인자(mapId)를 미션 층과 맞출 것 |
| Mac/Linux 빌드 실패 | HD.Acs.UI는 net8.0-windows — 프로젝트 단위 빌드로 제외 |
| Telerik 패키지 복원 실패 | nuget.telerik.com 자격증명 필요. `nuget.config`에 packageSourceMapping 넣지 말 것 |
| solve 결과 RMS 경고 | 기준점 오입력 의심 — 점 목록 확인, 점 간 거리를 벌려 재캡처 |

---

## 8. 더 읽을 문서

| 문서 | 내용 |
|---|---|
| `CLAUDE.md` | 프로젝트 총람 + Claude Code 작업 원칙 + 변경 이력 (최우선 참조) |
| `docs/DEVELOPMENT_GUIDE.md` | 구현 현황·설계 불변식·향후 로드맵 (이 매뉴얼의 자매 문서) |
| `docs/ARCHITECTURE.md` / `ARCHITECTURE_DECISIONS.md` | 아키텍처와 결정 기록(ADR-001~011), 미결 항목(Q1~Q9) |
| `docs/INSPECTION_SCENARIO.md` | 시나리오 모델·미션 상태머신·실패 정책 |
| `docs/GRAPH_DATA_MODEL.md` | 그래프/DB 4계층 설계, 층=맵 모델, Order 빌더 규칙 |
| `docs/DB_SCHEMA.md` + `db/schema.sql` | DB 스키마 카탈로그(ERD)와 통합 DDL |
| `src/SPEC_PHASE2_ACS.md` | PHASE 2 구현 사양서 (WP-1~5, 수용 기준, payload golden fixture) |
| `docs/TANK_WALL_LAYOUT.md` | 화물창 전개도·벽면 naming rule (위치 주소 체계) |
