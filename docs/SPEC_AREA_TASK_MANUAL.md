# SPEC — 선창 파라메트릭 벽면 정의 · 영역/검사 작업 수동 등록 (PHASE 2 개정 v3.1)

작성일: 2026-08-04 (v3) · 2026-08-05 (v3.1 — 층 자동 유도·면×층 도달 밴드 추가) · 상태: 확정, 구현 대상
v2 이하가 미구현 상태라면 본 문서만 구현한다. 완료 후 `CLAUDE.md` 변경 이력·저장소 구조 절을 갱신할 것.

## v3.1 변경 요약 (v3 기구현분에 대한 증분 작업 목록)

문제: 층(level)을 운영자가 자유 선택하면 "0층 + 천장 영역" 같은 불가능 조합이 등록될 수 있음
(로봇이 0층에 있는데 천장 작업을 명령하는 오류). 해결: **층을 입력이 아닌 유도값으로 전환.**

1. §2 `tank_geometry`에 선택 파라미터 `reach_z_min`/`reach_z_max` 추가 (층 도달 밴드 보정)
2. §4 영역 등록에서 level 입력 제거 — 서버가 영역 z범위로 층을 유도·저장 (§5-A 판정 규칙)
3. §5-A 면×층 도달 밴드 판정 신설: 밴드 밖·경계 걸침 영역은 400 거부
4. §8 `GET /api/tanks/{id}/walls?level=` 확장 — 층 필터 + 면별 도달 가능 v범위 반환
5. §9 UI: 층 선택은 "필터"로 역할 변경 — 면 목록 필터링 + 전개도 도달 밴드 하이라이트·입력 제한
6. 기존 릴리즈 게이트(로봇 보고 mapId 일치)는 최후 방어선으로 유지 (변경 없음)

## 0. 결정 요약

1. **자동 슬라이싱(WP-2 SeamSlicer) 보류(dormant)** — 코로게이션 위치·배치 기준을 도면에서 확정 불가.
   코드·테스트·`/api/seams*`·`generate-from-seams`는 보존, 워크플로우·UI에서 제외.
2. **선창 파라메트릭 정의(A안)** — 팔각 단면 치수 몇 개로 선창의 모든 면(벽면)을 자동 생성.
   면 간 모서리 정합이 기하적으로 보장되며, 다른 선창은 파라미터만 교체.
3. **영역·검사 작업은 벽면 로컬 2D(u,v)로 등록** — 운영자는 면 위 좌표만 입력. 시스템이 면 프레임으로
   선창 전역 3D로 변환 후 T_W_D 적용. 챔퍼·바닥·수직벽 동일 워크플로우.
4. **법선 입력·전송 없음 (v2 결정 유지)** — 툴 자세는 HD_AMR의 wall id 기반 티칭이 결정.
   payload에 `wallNormalW` 없음. ACS 책임 = 위치(`seamStartW/EndW`)와 정차 pose.
   - 전제 A: 양측 동일 wall_code 체계(TANK_WALL_LAYOUT) · 전제 B: 정차는 작업면을 바라봄 ·
     전제 C: 면 타입별 티칭 커버리지 존재. ※ 계약 변경으로 HD_AMR 통보·합의 필요(ACS 구현은 선행).
5. **T_W_D(WP-1)·payload 빌드(WP-3)·시뮬레이터(WP-4) 골격 유지** — wallNormalW 관련 부분만 수정.

## 1. 좌표계 규약

- **선창 전역 프레임(도면 좌표)**: 원점 = 선창 바닥면 중심, x = 선창 길이 방향, y = 폭 방향(+y 좌현),
  z = 상방, **선창 바닥 z = 0**. 단위 m.
  - 도면 기준 마킹이 중심이 아니면 선택 파라미터 `origin_offset (ox, oy)`로 보정.
  - T_W_D 캘리브레이션 기준점 입력과 반드시 동일한 프레임을 사용한다(원점 임의성은 T_W_D가 흡수).
- **벽면 로컬 프레임 (u,v)**: 각 면은 `P0(원점 3D) + u·U + v·V` 평면 조각.
  U = 면의 가로축(길이 방향 면은 +x, 마구리 면은 선창 내부에서 봤을 때 왼→오른쪽),
  V = 면의 세로축(면 위 방향; 바닥·천장은 +y). 법선 n = 선창 내부(로봇 쪽)를 향한다.
- **층(level)**: 층 = 맵(mapId) 모델 유지. 층 경계는 §2의 `level_z`로 정의하며,
  전역 z ↔ 층-상대 z 변환의 유일한 기준이다.

## 2. 선창 파라메트릭 정의 — ref.tank_geometry (신설)

팔각 단면(좌우 대칭) × 길이 L 프리즘. 각도는 수평면 기준.

| 파라미터 | 의미 |
|---|---|
| `length_l` | 선창 길이 L |
| `w_floor` | 바닥 폭 |
| `theta_low`, `h_low` | 하부 챔퍼 경사각·높이 (폭 w_low = h_low/tanθ_low 유도) |
| `h_wall` | 수직벽 높이 |
| `theta_up`, `h_up` | 상부 챔퍼 경사각·높이 (상·하부 각도 상이 가능) |
| `level_z` | 층 경계 z 목록 jsonb, 예 `[0, 3.2, 6.4, 9.6]` (각 층 바닥의 전역 z) |
| `reach_z_min`, `reach_z_max` | (선택, v3.1) 플랫폼 기준 코봇 작업 가능 상대 높이. 미지정 시 층 밴드 = 층 경계 그대로 |
| `origin_offset` | (선택) 도면 마킹 원점 보정 (ox, oy) |
| `check_h_total`, `check_beam`, `check_w_ceil` | (선택) 검증용 도면 치수 |

- 유도값: 전폭 `B = w_floor + 2·h_low/tanθ_low`, 천장폭 `W_ceil = B − 2·h_up/tanθ_up`,
  전체 높이 `H = h_low + h_wall + h_up`.
- **등록 검증**: 검증용 치수가 입력되면 유도값과 대조, 허용오차(기본 5mm) 초과 시 400 + 상세 사유.
  `W_ceil > 0`, 각도 (0°, 90°), `level_z` 오름차순·최상단 < H 도 검증.
- 등록/수정 시 §3의 면 자동 재생성. 영역이 이미 존재하는 선창의 치수 변경은 409
  (영역 전체 삭제 후 재정의 강제 — 조용한 좌표 이동 금지).

```sql
CREATE TABLE ref.tank_geometry (
  tank_id     text PRIMARY KEY,
  length_l    double precision NOT NULL,
  w_floor     double precision NOT NULL,
  theta_low   double precision NOT NULL,   -- [rad] 저장 (API는 deg 입력 허용, 서버 변환)
  h_low       double precision NOT NULL,
  h_wall      double precision NOT NULL,
  theta_up    double precision NOT NULL,
  h_up        double precision NOT NULL,
  level_z     jsonb NOT NULL,
  origin_ox   double precision NOT NULL DEFAULT 0,
  origin_oy   double precision NOT NULL DEFAULT 0,
  created_by  text,
  created_at  timestamptz NOT NULL DEFAULT now()
);
```

## 3. 면 자동 생성 — ref.wall (신설)

파라미터 등록 시 **10면**을 자동 생성한다 (통짜 면 1개 = 행 1개, 층 슬라이스는 하지 않음 —
층 소속은 영역이 가진다). wall_code는 TANK_WALL_LAYOUT의 기존 naming rule이 있으면 그것을
우선 적용하고, 없으면 아래 잠정 코드를 사용한다.

| wall_code | 면 | P0 | U | V | n(내부향) |
|---|---|---|---|---|---|
| `FL` | 바닥 | (−L/2, −W_f/2, 0) | +x | +y | (0,0,+1) |
| `BC-S` | 하부 챔퍼 우현(−y) | (−L/2, −W_f/2, 0) | +x | (0,−cosθ_l,+sinθ_l) | (0,+sinθ_l,+cosθ_l) |
| `BC-P` | 하부 챔퍼 좌현(+y) | (−L/2, +W_f/2, 0) | +x | (0,+cosθ_l,+sinθ_l) | (0,−sinθ_l,+cosθ_l) |
| `SW-S` | 수직벽 우현 | (−L/2, −B/2, h_low) | +x | +z | (0,+1,0) |
| `SW-P` | 수직벽 좌현 | (−L/2, +B/2, h_low) | +x | +z | (0,−1,0) |
| `TC-S` | 상부 챔퍼 우현 | (−L/2, −B/2, h_low+h_wall) | +x | (0,+cosθ_u,+sinθ_u) | (0,+sinθ_u,−cosθ_u) |
| `TC-P` | 상부 챔퍼 좌현 | (−L/2, +B/2, h_low+h_wall) | +x | (0,−cosθ_u,+sinθ_u) | (0,−sinθ_u,−cosθ_u) |
| `CL` | 천장 | (−L/2, −W_ceil/2, H) | +x | +y | (0,0,−1) |
| `FW` | 선수 마구리 (x=+L/2) | 팔각 프로파일 | −y | +z | (−1,0,0) |
| `AW` | 선미 마구리 (x=−L/2) | 팔각 프로파일 | +y | +z | (+1,0,0) |

- 마구리 2면은 평면·수직으로 가정(팔각 윤곽 전체가 한 평면). 이 가정이 실선과 다르면 후속 개정.
- 저장 컬럼: `tank_id, wall_code(PK 복합), origin jsonb[x,y,z], u_axis jsonb, v_axis jsonb,
  normal jsonb, u_len, v_len(면 크기), generated boolean, description`.
  구현은 반드시 **단위 벡터 + U·V 직교 + n = U×V** 를 생성 후 검증(단위 테스트 대상).
- 정차각 `facing_yaw = atan2(−n.y, −n.x)` (n 수평 성분 기준, AMR이 면을 바라보는 도면 yaw).
  `FL`/`CL`은 수평 성분이 없어 facing_yaw 없음(§5 예외).

## 4. 영역·검사 작업 — 벽면 로컬 (u,v) 등록

```sql
CREATE TABLE ref.inspection_area (
  area_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tank_id       text NOT NULL,
  wall_code     text NOT NULL,             -- (tank_id, wall_code) → ref.wall FK
  level         int  NOT NULL,             -- AMR 주행 층 (mapId 결정) — v3.1: 입력 아님, 서버가 §5-A로 유도·저장
  name          text NOT NULL,             -- 'A01' — tank/wall 내 유일
  u_min         double precision NOT NULL, -- 면 로컬 좌표 (m)
  v_min         double precision NOT NULL,
  u_max         double precision NOT NULL,
  v_max         double precision NOT NULL,
  station_x     double precision,          -- 정차 수동 오버라이드 (전역 x,y + theta)
  station_y     double precision,
  station_theta double precision,
  sort_order    int  NOT NULL DEFAULT 0,
  created_by    text,
  created_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tank_id, wall_code, name)
);

CREATE TABLE ref.area_task (
  task_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  area_id        uuid NOT NULL REFERENCES ref.inspection_area(area_id) ON DELETE CASCADE,
  seq            int  NOT NULL,            -- 영역 내 실행 순서 → seqInGroup
  name           text,
  seam_type      text NOT NULL DEFAULT 'LINE',
  start_u        double precision NOT NULL,  -- 용접선 시작/끝 (면 로컬, m)
  start_v        double precision NOT NULL,
  end_u          double precision NOT NULL,
  end_v          double precision NOT NULL,
  section_dxf_id text NOT NULL,
  profile_id     text NOT NULL,
  created_by     text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (area_id, seq)
);
```

- 등록 검증: 영역 `max > min` + 면 범위`[0, u_len]×[0, v_len]` 내(400) ·
  작업 시작/끝점 영역 경계 내(400) · `(tank, wall_code)` 존재(404) ·
  **층 유도 성공(§5-A — 실패 시 400 + 사유)**. 요청에 level 필드가 있어도 무시한다(응답에 유도된 level 반환).
- **좌표 변환(공용 유틸, Core에 순수 함수로)**: `To3D(wall, u, v) = P0 + u·U + v·V`.
  단위 테스트 필수(수직벽·챔퍼·마구리 왕복 검증).

## 5-A. 층 자동 유도 — 면×층 도달 밴드 (v3.1 신설)

층은 운영자 입력이 아니라 **영역 위치에서 유도**한다. "0층 + 천장" 같은 불가능 조합을 원천 차단한다.

- **층 도달 밴드**: 층 ℓ의 밴드 `B(ℓ) = [z_ℓ + reach_z_min, min(z_{ℓ+1}, z_ℓ + reach_z_max))`.
  `reach_z_*` 미지정 시 `B(ℓ) = [z_ℓ, z_{ℓ+1})`. 최상층의 상한은 전체 높이 H(천장 포함, 폐구간).
- **영역 z범위**: `z(v) = P0.z + v·V.z` 이용, `[min, max] = z(v_min), z(v_max) 정렬`.
  (바닥·천장은 V.z=0이라 z 상수 — 각각 z=0, z=H.)
- **판정**: 영역 z범위가 허용오차 ε(기본 5mm) 내에서 **정확히 하나의 밴드에 완전히 포함**되면
  그 층으로 유도·저장. 아니면 400:
  - 어떤 밴드에도 안 들어감 → `"도달 불가 높이"` + 면·z범위·인접 밴드 정보
  - 층 경계에 걸침 → `"층 경계에 걸침 — 영역을 층별로 분할 필요"` (아래층 팔 길이로 위층 구간
    도달 불가·위층 비계 바닥이 물리적으로 차단하므로 걸침 허용 없음)
- **면×층 교차(파생 데이터)**: 각 면에 대해 `B(ℓ)`와 면 z범위의 교집합을 v구간으로 환산한
  `reachable v-band` 목록을 서버가 기하로 계산 — §8 walls 조회와 §9 UI 필터·클리핑의 근거.
  예) 바닥 FL → 0층만, 천장 CL → 최상층만, 수직벽 SW → 여러 층에 걸쳐 층별 v구간 분할.
- 기존 **릴리즈 게이트**(한 미션=한 층, 로봇 보고 mapId 일치 시에만 릴리즈)는 불량 데이터가
  존재하더라도 실행을 막는 최후 방어선으로 그대로 유지된다.

## 5. 정차점 산출 규칙

영역 중심 `(uc, vc) = ((u_min+u_max)/2, (v_min+v_max)/2)` 기준:

1. `C = To3D(wall, uc, vc)` (면 위 3D 점)
2. `n_h` = 면 법선의 수평 성분 정규화 (n.x, n.y)/‖·‖
3. **정차 위치(도면) = (C.x, C.y) + n_h × standoff** — 면에서 선창 내부로 standoff만큼 물러난 바닥 점
4. **정차 방향 = facing_yaw** (= −n_h 방향, 면을 바라봄)
5. 수동 오버라이드(`station_x/y/theta`)가 있으면 그것을 사용
6. **예외 — `FL`(바닥)·`CL`(천장) 영역은 n_h가 없으므로 `station_x/y/theta` 수동 지정 필수**
   (미지정 시 생성 단계에서 명시적 실패 목록에 포함)
7. 검증: 정차점의 소속 층은 `area.level`과 일치해야 하며(level_z로 판단은 z=층 바닥 고정이므로
   자동 성립), 해당 층 활성 맵 + 유효 T_W_D 필수 — 하나라도 없으면 전체 생성 실패(400 + reasons)

standoff 설정: `Acs:Area:StandoffMm`(기본 400) · `Acs:Area:WorkingDistanceMm`(기본 = Standoff).

## 6. payload 계약 (v2 확정안 유지 — wallNormalW 없음)

```jsonc
{
  "jobRef": "JOB-CT1-SW-P-A01-1",
  "position": {
    "seamStartW": [x,y,z],       // To3D(작업 시작 u,v) → T_W_D 적용 (x,y 변환·z 통과)
    "seamEndW":   [x,y,z],
    "drawingPos": { "tank", "level", "wall_code", "u", "v", "x", "y", "z" }  // wall_code = 티칭 조회 키
  },
  "params": { "seamType", "sectionDxfId", "inspectionProfileId",
              "standoffMm", "workingDistanceMm", "anchorGroupId", "seqInGroup" }
}
```

수정 대상: `Core/Planning/WeldInspectionPayload.cs`(WallNormal 제거·drawingPos에 u,v 추가),
`db/schema.sql` param_schema 시드(required에서 wallNormalW 제거),
`App/Services/MissionService.cs`(Position 파싱), `Simulator/Program.cs` 검증기(필수 키 목록),
`SimTest/Program.cs` 골든·S2 시나리오, `Core.Tests/WeldInspectionPayloadTests.cs`.

## 7. 생성 규칙 (generate-from-areas → AreaPlanningService 신설)

1. 시나리오 tank의 영역 로드(areaIds 필터 가능), 정렬 level → wall_code → sort_order → name.
   작업 0개 영역은 skipped.
2. 사전 검증(전부 통과 못 하면 아무것도 만들지 않음): 층별 유효 T_W_D · FL/CL 수동 정차 지정 ·
   면 존재. 실패 시 400 + reasons 목록.
3. 영역 → STATION 노드 get-or-create: `nodeId = {tank}-L{level}-{wall_code}-{name}`,
   §5 정차 pose에 T_W_D 적용(`DrawingToMap`/`DrawingYawToMap`), 허용편차 XY 0.08 / θ 0.07.
   같은 맵 최근접 비-STATION 노드와 양방향 TRAVEL 엣지 get-or-create.
4. 영역 → `InspectionPoint`, 작업 → `InspectionTask`(Position/Params §6 형태,
   `JobRef = JOB-{nodeId}-{seq}`, `ActionType = startWeldInspection`).
   Position에는 도면 전역 좌표(`seamStartDrawing/seamEndDrawing` [x,y,z])와 u,v를 함께 저장
   (릴리즈 시 WP-3 빌더가 전역 좌표에 T_W_D 적용 — 기존 흐름 유지).
5. 재실행 안전: 기존 InspectionPoint 삭제 후 재생성. 감사 로그 `AREA_GENERATE`.

## 8. REST API

| Method | 경로 | 기능 |
|---|---|---|
| POST | `/api/tanks/{tankId}/geometry` | 선창 파라미터 등록/수정 (검증·면 재생성. 영역 존재 시 409) |
| GET | `/api/tanks/{tankId}/geometry` | 파라미터 + 유도값(B, W_ceil, H) 조회 |
| GET | `/api/tanks/{tankId}/walls?level=` | 면 목록 (프레임·크기·facing_yaw). v3.1: `level` 지정 시 그 층 밴드와 교차하는 면만 + 면별 도달 가능 v구간(reachableVBand) 반환. 미지정 시 전체 면 + 층별 v구간 목록 |
| POST | `/api/areas` | 영역 등록 (u,v — 면 범위·경계 검증. v3.1: level 입력 무시, §5-A로 유도) |
| GET | `/api/areas?tankId=&wallCode=&level=` | 영역 목록 (+taskCount) |
| DELETE | `/api/areas/{areaId}` | 영역 삭제 (작업 CASCADE) |
| POST | `/api/areas/{areaId}/tasks` | 검사 작업 등록 (u,v — 경계 검증, seq 자동 부여 가능) |
| GET | `/api/areas/{areaId}/tasks` | 영역 내 작업 목록 |
| DELETE | `/api/area-tasks/{taskId}` | 검사 작업 삭제 |
| POST | `/api/scenarios/{id}/generate-from-areas` | 영역/작업 → 스테이션/TASK 생성 |

기존 `/api/seams*` · `generate-from-seams`는 dormant 주석 표기만 하고 유지.

## 9. UI (HD.Acs.UI — SlicingView 대체)

`SlicingView/SlicingViewModel` → `AreaPlanningView/AreaPlanningViewModel`
(App.xaml.cs DI · ShellViewModel · MainWindow.xaml 탭 헤더 "영역/검사 작업" 갱신):

- **선창 파라미터 섹션**: 치수 입력 + 등록. 등록 후 단면 팔각형 미리보기(파라미터로 그린 단면 도형)와
  면 목록 표시 — 치수 오입력을 눈으로 검증.
- **영역/작업 섹션 (v3.1 개정)**: 층 선택은 **필터** 역할 — 층 선택 시 면 콤보를
  `walls?level=` 결과(그 층에서 도달 가능한 면)로 제한하고(0층 선택 시 천장 CL 미표시),
  면 캔버스에는 해당 층의 **도달 가능 v구간을 하이라이트**하고 영역 입력을 그 구간 안으로 제한.
  등록 폼: 영역(이름/u·v 경계/정차 수동 지정 체크 — **level 입력 없음**, 등록 응답의 유도 층을 표시),
  작업(시작·끝 u,v/DXF/프로파일). FL·CL 면 선택 시 정차 수동 입력을 필수로 활성화.
  서버 400(도달 불가·경계 걸침)은 상태 메시지로 노출.
- 서버 400/409 메시지는 상태 메시지로 노출. `IAcsApiClient`/`Dtos.cs`에 신규 계약 추가.

## 10. 수용 기준 (DoD)

> v3.1 층 유도(§2 reach_z·§5-A·§8 walls 필터·§9 UI 층 필터) 구현분 체크(2026-08-07). §6·§7(payload·generate) 등 v3 잔여는 별도.

- [x] 전체 솔루션 빌드 0 error (실행 중 App/UI exe 잠금 회피 위해 별도 출력 디렉터리로 검증)
- [x] 단위 테스트: 면 생성(10면 프레임 직교·단위·모서리 정합) 등 기존 통과 유지
- [x] 층 유도 테스트(v3.1): FL→L1만·CL→최상층만·수직벽 v구간별 층 판정·경계 걸침 400·
      도달 불가 400·reach_z 지정 시 밴드 축소·ε 경계 케이스 (`LevelBandsTests` 11건, 전체 44건 통과)
- [ ] SimTest 3종 PASS (wallNormalW 제거 반영판) — 본 v3.1 범위 밖(무변경)
- [ ] E2E(시뮬레이터): geometry 등록 → 면 자동 생성 → 영역/작업 등록 → 생성 → Run — PostgreSQL/시뮬레이터 수동
- [x] `docs/MANUAL.md` 운영 워크플로우 갱신(층 필터·층 유도), `CLAUDE.md` 변경 이력 추가

## 11. 후속 (본 구현 범위 아님)

- 도면 언더레이 클릭 등록(면 로컬 캔버스에 도면 이미지 오버레이) → DXF 용접선 레이어 파싱 검토
- CSV 일괄 등록(영역·작업), 돔·개구부 등 검사 제외 영역 표시
- 마구리 면 비평면(실선 형상) 대응, 비대칭 단면 지원
- HD_AMR 측: wall_code별 티칭 커버리지 확인 · wallNormalW 제거 합의 (전제 A~C)
