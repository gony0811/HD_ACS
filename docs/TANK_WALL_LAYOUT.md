# 화물창 전개도 구성 및 벽면 Naming Rule

> 출처: HD현대중공업 제공 전개도 (LOCATION OF TANK WALL, NON-SCALE)
> 이 명명 체계는 ① 전개도 UI 렌더링 [ADR-005], ② 검사 지점 주소 체계,
> ③ 검사 S/W와의 위치 키 규약 [Q2] 의 공통 기준이 된다.

## 1. 전개도 구성

LNG 멤브레인 화물창은 팔각형 단면 구조이며, 전개도는 **후방 격벽(A)을 중심에 두고
8개 벽면을 방사형으로 펼친 뒤, PM 바깥에 전방 격벽(F)을 배치**하는 방식이다.

```
                    ┌───┐
          SU        │ T │        PU
            ＼      │   │      ／
             ＼     └───┘     ／
              ＼   ┌─────┐  ／
      ┌────────┐ ／       ＼ ┌────────┐
      │   SM   │(    A     )│   PM   │──( F )
      └────────┘ ＼        ／└────────┘
              ／   └─────┘  ＼
             ／     ┌───┐     ＼
            ／      │ B │       ＼
          SL        │   │        PL
                    └───┘
```
(A, F는 팔각형 / 각 벽면은 A의 8개 변에서 바깥으로 전개)

## 2. 벽면 코드 Naming Rule

| 코드 | 명칭 | 위치 |
|---|---|---|
| **A** | Aft Bulkhead (후방 격벽) | 전개도 중심, 팔각형 |
| **F** | Fore Bulkhead (전방 격벽) | PM 바깥에 전개, 팔각형 |
| **T** | Top | 천장 (상면) |
| **B** | Bottom | 바닥 (하면) — 요철 멤브레인 주행면 |
| **SM** | Starboard Middle | 우현 수직 벽면 |
| **SU** | Starboard Upper (chamfer) | 우현 상부 경사면 |
| **SL** | Starboard Lower (chamfer) | 우현 하부 경사면 |
| **PM** | Port Middle | 좌현 수직 벽면 |
| **PU** | Port Upper (chamfer) | 좌현 상부 경사면 |
| **PL** | Port Lower (chamfer) | 좌현 하부 경사면 |

- 접두 규칙: `S` = Starboard(우현), `P` = Port(좌현) / 접미 규칙: `U` = Upper chamfer, `M` = Middle(수직면), `L` = Lower chamfer
- ⚠️ 확인 필요: 원본 전개도에서 SM 벽면이 해칭(빗금) 표시되어 있음 — 의미(기준면? 예시 표기?) 확인 필요

## 3. HD_ACS 데이터 모델 연계

### 검사 지점 주소 체계 (제안)
검사 지점(InspectionPoint)과 촬영 위치는 벽면 코드를 포함한 계층 주소를 갖는다:

```
{tank_id} - {wall_code} - {local 위치}
예: CT1-B-... (1번 화물창 Bottom의 특정 위치)
```

- `local 위치`의 표현(그리드 행/열, 용접선 ID, 벽면 로컬 좌표 등)은 검사 S/W와의 키 규약 협의[Q2]에서 확정한다.
- AMR이 주행하는 면은 B(Bottom)이며, 벽면(SM/PM 등)의 검사 지점은 "B 위의 AMR 정차 위치 + 협동로봇이 도달하는 대상 벽면/좌표"의 조합으로 표현된다 — 대상 벽면 좌표는 촬영 명령의 위치 파라미터[ADR-004]에 포함.

### 전개도 UI [ADR-005]
- WPF 전개도 뷰는 본 문서의 배치(중앙 A + 방사형 8면 + F)를 표준 레이아웃으로 렌더링한다.
- 검사 진행률·결과를 벽면 코드 단위로 집계 표시할 수 있어야 한다 (벽면별 완료율 등).
- 3D 뷰와 전개도는 동일한 벽면 코드·좌표 체계를 공유한다 (뷰 간 위치 상호 하이라이트).

## 4. 층(Level) 구조 — 4층 슬라이스
- 화물창은 건조 단계에서 **바닥부터 4개 층으로 슬라이스**되어 검사가 수행된다 (1개 통이 아님)
- AMR은 **엘리베이터로 층간 이동**한다 (그래프 모델링: GRAPH_DATA_MODEL.md 8절)
- 전개도 UI는 층 선택기(L1~L4)를 제공하고, 벽면 검사 위치·진행률은 층별로 분할 집계한다
- 검사 지점 주소 체계에 층 요소 추가 검토: `{tank_id}-{level}-{wall_code}-{local 위치}` (예: CT1-L2-SM-...)

## 5. 미결 확인 사항
- SM 해칭 표기의 의미
- ~~벽면 로컬 좌표계의 원점/축 방향 정의~~ → **해소(§6): 입력은 벽면-로컬이 아니라 층 도면(drawing) 프레임 기준이며, T_W_D로 맵(월드)으로 변환한다.** 벽면별 로컬 좌표계는 별도 도입하지 않음. 정차각은 seam 기하에서 자동 산출(§6.3), `wall_code`는 `ref.wall` 키로 통제(레지스트리·티칭 키). 툴 자세용 법선은 ACS에 없음(HD_AMR 티칭). CAD 도면 좌표 축 규약(선체 기준 등) 자체의 확정은 여전히 열려 있음
- 다중 화물창 선박의 tank_id 표기 규칙 (CT1/CT2... 또는 No.1/No.2...)
- 층 표기 규칙 (L1~L4 vs 다른 사내 표기) 및 층 경계 높이 정의
- 엘리베이터 설치 위치(층별 탑승 지점)와 제어 인터페이스 [Q9]

## 6. 벽면·영역 입력 규약 [정차각 자동화]

계층: **Floor(층) → Wall(벽면) → Area(영역)**. 벽면(`ref.wall (tank_id, level, wall_code)`)은 **레지스트리 + HD_AMR 티칭 키**일 뿐 정차각을 저장하지 않는다. **정차각은 영역·작업 seam 기하에서 자동 산출**된다. 영역 1개 = STATION 1개 = anchorGroup 1개 (`ref.inspection_area`) [DB_SCHEMA](DB_SCHEMA.md).

> **ACS는 법선을 다루지 않는다.** 툴(코봇) 접근 자세는 HD_AMR이 `wall_code` 키 티칭 데이터로 결정한다. ACS는 **위치(seamStartW/EndW)와 정차각만** 책임진다. payload에 `wallNormalW`는 없다.

### 6.1 공통 — 좌표계·단위
- `min/max`(영역, m)와 seam 점(작업, m) 모두 **해당 층 도면(drawing) 좌표계** 기준이다. (벽면-로컬 좌표 아님 — §5)
- 도면 프레임은 **맵 캘리브레이션(T_W_D) 기준점 프레임**이다. 릴리즈 시 `DrawingTransform.DrawingToMap`(위치)·`DrawingYawToMap`(각도)이 맵(월드) 좌표로 변환한다.
  - 소스: `HD.Acs.Core/Geometry/DrawingTransform.cs`·`AreaGeometry.cs`, `HD.Acs.App/Services/AreaPlanningService.cs`

### 6.2 min(x,y) / max(x,y) — 영역 사각형 (축정렬 AABB)
- 도면 평면상의 **축정렬 직사각형**. `min` = 좌하단, `max` = 우상단. 제약 **`minX < maxX`·`minY < maxY`**(DB `CHECK` + API 400).
- 용도: ① **디폴트 정차 위치** = 사각형 중앙(오버라이드 없으면)  ② 모든 작업 seam 시작/끝점이 사각형 **내부**여야 함(위반 시 API 400, `AreaGeometry.InBounds`)  ③ payload `position.areaBounds`.

### 6.3 정차각 — seam 기하에서 자동 산출 (수동 입력 없음)
- **원리:** seam(용접선) 점은 벽 표면 위에 있으므로, **정차 위치에서 영역 내 seam 점들의 중심으로 향하는 방향**이 곧 "벽을 바라봄"이다.
  `theta = atan2(seamCentroid_y − stationY, seamCentroid_x − stationX)` (도면 기준, 이후 `DrawingYawToMap`). (`AreaGeometry.FacingYawToward`)
- **정차 위치**: `station_x/y`(오버라이드) ?? 영역 중앙(`AreaGeometry.AreaCenter`).
- **정차각**: `station_theta`(영역 수동 오버라이드) **??** 위 자동 산출값. **degenerate**(정차 위치 ≈ seam 중심 → 방향 불명)면 **생성 시 명시적 실패**(reasons 목록으로 400, T_W_D 부재와 동일. 조용한 기본값 금지).
- **배치 규율(중요):** 자동 산출이 성립하려면 영역을 **"정차 지점(바닥) ↔ seam(벽)"을 모두 포함**하도록 그려, seam이 사각형 **중앙이 아니라 벽쪽 가장자리**에 오게 한다(그래야 중앙→seam이 벽 방향). 불가하면 `station_theta`(또는 `station_x/y`)로 수동 지정.
- **정차와 무관한 것:** 벽면 코드·티칭은 코봇 접근각용(HD_AMR 책임). ACS 정차각은 위 기하만으로 정해진다.

### 6.4 한 줄 요약
벽면은 코드·설명만 등록(레지스트리·티칭 키)한다. 영역은 도면 좌표(m)의 `min/max` 축정렬 사각형과 작업 seam만 입력하며 — **정차각은 "정차 위치 → seam 중심" 방향으로 자동 산출**된다(seam을 벽쪽에 두도록 영역을 그릴 것). 방향 수동 지정이 필요하면 `station_theta`.
