# HD_ACS PHASE 2 구현 사양서 — 도면 기반 TASK 생성·전송

> **대상**: HD_ACS 솔루션 (`src/HD.Acs.sln`) · **작성**: 2026-07-29 설계 확정분
> **용도**: Claude Code가 이 문서만으로 ACS측 구현을 진행할 수 있도록 작성된 사양.
> 기존 코드 관례(ADR 주석, 한국어 주석, robot-is-truth)를 그대로 따를 것.

---

## 0. 컨텍스트 — 이미 있는 것 / 만들 것

**이미 구현되어 있음 (수정 금지 원칙, 확장만):**

| 위치 | 내용 |
|---|---|
| `HD.Acs.Core/Domain/MissionStateMachine.cs` | Stateless 미션 상태머신 |
| `HD.Acs.Core/Graph/MapGraph.cs` | 층 내 Dijkstra (MANUAL_TRANSFER 제외) |
| `HD.Acs.Data/Entities/*` | ref/run/hist 스키마 엔티티, `AcsDbContext` |
| `HD.Acs.Vda5050/*` | v2.0 메시지, MQTT 마스터, `OrderBuilder`(노드=짝수/엣지=홀수 seq, 동일노드 액션 병합) |
| `HD.Acs.App/Services/MissionService.cs` | 시나리오→층별 미션 분해, 릴리즈 가드(mapId 일치), jobRef/position/params → actionParameters |
| `HD.Acs.App/Services/RobotStateService.cs` | state 수신, actionId 대조, RobotContext 갱신(ReportedX/Y/Theta/MapId) |
| `HD.Acs.Simulator/Program.cs` | 가상 AMR (Order 수신→노드 순회→액션 FINISHED) |

**이 사양서로 만들 것 (WP = Work Package, 의존 순서대로):**

- WP-1 T_W_D 좌표 변환 모듈 (도면→맵, 등록 API 포함)
- WP-2 WeldSeam 데이터 모델 + 슬라이싱/스테이션 생성기
- WP-3 액션 카탈로그 `startWeldInspection` + payload 빌드 확장
- WP-4 시뮬레이터 확장 (액션 파라미터 검증·앵커 공유 시뮬레이션)
- WP-5 (후순위) 기준점 캡처 UI · TASK 관리 3-Pane UI

---

## 1. 전역 불변식 (모든 WP 공통 — 위반하는 코드 작성 금지)

1. **TASK 1개 = 용접라인(seam) 1개의 코봇 리치 안 1개 구간.** TASK 1 = actionId 1 =
   inspection_result 1행 = 재시도 단위 1 = 단면 DXF·프로파일 1 = 진행방향 1.
2. **actionId는 ACS가 발급**(GUID)하고 DB에 보존한다. AMR이 생성하지 않는다.
3. **Cobot BASE 좌표·재시도 정책·DXF 원문은 전송하지 않는다.**
   BASE 변환은 AMR 온보드(실측 pose 기준), 정책은 ACS(policy jsonb), DXF는 사전 배포 후 ID 참조.
4. **좌표 단위**: VDA 5050 구간은 m/rad, theta는 맵 X축 기준 CCW. 도면 좌표는 mm일 수 있으므로
   T_W_D 적용 시점에 m로 통일한다. z는 2D 변환 대상이 아니며 그대로 통과(맵 평면=바닥 z=0).
5. **ACS는 SLAM 맵 원본(점유격자)을 보유·요구하지 않는다.** 맵 좌표계 접근은 T_W_D로만.

---

## 2. WP-1: T_W_D 좌표 변환 모듈

### 2.1 목적
3D 선창 도면 좌표(층별 2D 평면)를 AMR SLAM 맵 좌표로 변환하는 층별 강체변환
`T_W_D = (tx, ty, yaw)`를 등록·저장·적용한다.

### 2.2 스키마 (ref 스키마, `db/schema.sql` + `HD.Acs.Data/Entities/RefEntities.cs`)

```sql
-- 층별 도면→맵 캘리브레이션 (mapId + 맵버전 바인딩 [필수 규칙])
CREATE TABLE ref.map_calibration (
    map_id        text NOT NULL,
    map_version   int  NOT NULL,            -- ref.map.version 과 일치할 때만 유효
    tx            double precision NOT NULL, -- m
    ty            double precision NOT NULL, -- m
    yaw_rad       double precision NOT NULL, -- rad, CCW
    rms_m         double precision NOT NULL, -- 등록 잔차 RMS
    point_count   int NOT NULL,
    registered_by text,
    registered_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (map_id, map_version)
);

-- 캡처된 기준점 대응쌍 (감사·재계산용 보존)
CREATE TABLE ref.map_calibration_point (
    id            uuid PRIMARY KEY,
    map_id        text NOT NULL,
    map_version   int  NOT NULL,
    drawing_x_m   double precision NOT NULL, -- 도면 좌표 (m로 정규화하여 저장)
    drawing_y_m   double precision NOT NULL,
    map_x         double precision NOT NULL, -- 캡처 시점 RobotContext.ReportedX
    map_y         double precision NOT NULL,
    captured_at   timestamptz NOT NULL DEFAULT now(),
    captured_by   text
);
```

엔티티: `MapCalibrationEntity`, `MapCalibrationPointEntity` — 기존 RefEntities.cs 스타일로 추가,
`AcsDbContext`에 DbSet + ref 스키마 매핑.

### 2.3 계산 서비스 `HD.Acs.Core/Geometry/DrawingTransform.cs` (신규)

```csharp
/// <summary>층별 도면→맵 2D 강체변환 [PHASE2 §T_W_D]. 스케일 없음(도면·맵 모두 m).</summary>
public sealed record DrawingTransform(double Tx, double Ty, double YawRad)
{
    public (double X, double Y) DrawingToMap(double dx, double dy);   // R·d + t
    public (double X, double Y) MapToDrawing(double mx, double my);   // R⁻¹·(m − t)
    public double DrawingYawToMap(double drawingYaw);                 // yaw 합성

    /// <summary>대응쌍 최소자승. 2쌍 미만이면 예외. 반환에 잔차 포함.</summary>
    public static (DrawingTransform T, double RmsM, double MaxResidualM)
        Solve(IReadOnlyList<((double X, double Y) Drawing, (double X, double Y) Map)> pairs);
}
```

**Solve 알고리즘 (2D 강체, 스케일 없음 — 반드시 이 방식):**
1. 도면측·맵측 각각 중심(centroid) 계산: `c_d`, `c_m`
2. 중심화 좌표 `d_i = p_d − c_d`, `m_i = p_m − c_m`
3. `yaw = atan2( Σ(d_i.x·m_i.y − d_i.y·m_i.x), Σ(d_i.x·m_i.x + d_i.y·m_i.y) )`
4. `t = c_m − R(yaw)·c_d`
5. 잔차: 각 쌍의 `‖R·p_d + t − p_m‖` → RMS·최대값 반환

단위 테스트 필수: 기지(旣知) 변환으로 생성한 합성 대응쌍 → Solve 결과가 원 변환 복원
(yaw 1e-9 rad, t 1e-9 m 이내), 노이즈 부여 시 RMS가 노이즈 수준과 일치.

### 2.4 API (`HD.Acs.App/Program.cs` 확장)

```
POST /api/maps/{mapId}/calibration/points     — 기준점 캡처
  body: { drawingX, drawingY, unit: "mm"|"m", userId }
  동작: RobotContext(로봇 보고 최신값)에서 ReportedX/Y를 읽어 대응쌍 저장.
       로봇 ReportedMapId ≠ mapId 이면 409. theta는 사용하지 않는다.
GET  /api/maps/{mapId}/calibration/points     — 현재 맵버전의 대응쌍 목록
DELETE /api/maps/{mapId}/calibration/points/{id}
POST /api/maps/{mapId}/calibration/solve      — 최소자승 계산·저장
  응답: { tx, ty, yawRad, rmsM, maxResidualM, pointCount }
  rmsM > 임계값(appsettings: Acs:Calibration:RmsWarnM, 기본 0.05) 이면 저장하되 warning 필드 반환.
GET  /api/maps/{mapId}/calibration            — 현재 유효 T_W_D (없으면 404)
```

캡처는 `AuditLogEntity`에 기록(Action="CALIBRATION_CAPTURE").

### 2.5 유효성 규칙
- T_W_D 조회 시 `map_calibration.map_version == ref.map.version`이 아니면 **무효 취급(404)**.
  맵 재생성(version 증가) 시 자동으로 무효가 되는 구조 — 별도 삭제 로직 불필요.
- WP-2/WP-3에서 도면→맵 변환이 필요한데 유효 T_W_D가 없으면 **명시적 실패**
  (시나리오 릴리즈 거부 + 사유 로그). 조용한 기본값 사용 금지.

---

## 3. WP-2: WeldSeam 모델 + 슬라이싱/스테이션 생성기

### 3.1 스키마 (ref 스키마)

```sql
-- 용접선 (사람이 등록하는 유일한 입력 — 도면 좌표로 저장)
CREATE TABLE ref.weld_seam (
    seam_id         uuid PRIMARY KEY,
    tank_id         text NOT NULL,
    level           int  NOT NULL,           -- 층 (ref.map 매핑: tank+level → map_id)
    wall_code       text NOT NULL,           -- 'W03'
    seam_type       text NOT NULL DEFAULT 'LINE',  -- LINE | POLYLINE
    path_drawing    jsonb NOT NULL,          -- [[x,y,z],...] m, 도면 좌표. LINE이면 2점
    normal_drawing  jsonb NOT NULL,          -- [nx,ny,nz] 벽면 법선 (도면 좌표계)
    section_dxf_id  text NOT NULL,           -- 단면 DXF 참조 (원문 미저장)
    profile_id      text NOT NULL,           -- 검사 프로파일
    created_by      text,
    created_at      timestamptz NOT NULL DEFAULT now()
);
```

### 3.2 생성기 `HD.Acs.Core/Planning/SeamSlicer.cs` (신규)

```csharp
public sealed record SlicerConfig(
    double CobotReachM,        // appsettings Acs:Slicer:CobotReachM
    double OverlapM,           // 구간 겹침
    double StandoffM,          // 벽면→정차점 법선 오프셋 (코봇 워킹디스턴스 포함)
    double StationThetaOffset  // 정차 방향 = 벽면 법선 반대 방향 기준 보정
);

public sealed record SlicedTask(
    int SeqInGroup, string AnchorGroupId,
    (double X, double Y, double Z) SeamStartDrawing, (double X, double Y, double Z) SeamEndDrawing);

public sealed record SlicedStation(
    string AnchorGroupId,                       // '{tank}-L{level}-{wall}-ST{nn}'
    (double X, double Y, double Theta) StationDrawing,  // 도면 좌표 정차 pose
    IReadOnlyList<SlicedTask> Tasks);

public static class SeamSlicer
{
    /// <summary>seam 집합 → 스테이션(영역)·TASK 자동 산출 [불변식 1 준수].
    /// 구간수 = ceil(L / (reach − overlap)). 스테이션 = 구간 중점 + 법선×standoff.
    /// 같은 정차점(거리 임계 내)에 오는 구간은 같은 AnchorGroup으로 병합.</summary>
    public static IReadOnlyList<SlicedStation> Slice(
        IReadOnlyList<WeldSeamEntity> seams, SlicerConfig cfg);
}
```

규칙:
- **LINE**: 시작·끝 2점을 길이 L로 보고 `n = ceil(L / (reach − overlap))` 등분할.
- **POLYLINE**: 호길이 기준 동일 분할, 구간별 접선으로 진행방향 갱신.
- 스테이션 병합: 서로 다른 seam의 구간이라도 정차점 거리 < `MergeDistM`(기본 0.3m)이면
  같은 스테이션(=같은 anchorGroupId)에 배정, seqInGroup은 배정 순서.
- 교차 seam은 이 단계에서 특별 취급하지 않는다(등록 단계에서 별도 seam으로 분리되어 들어옴).

### 3.3 시나리오 반영 서비스 (`HD.Acs.App/Services/SeamPlanningService.cs` 신규)

`POST /api/scenarios/{scenarioId}/generate-from-seams` :
1. 대상 seam 목록 로드 → `SeamSlicer.Slice`
2. **T_W_D 적용**해 스테이션 도면 pose → 맵 pose 변환 (유효 T_W_D 없으면 400 + 사유)
3. 스테이션마다 `ref.node` 생성/재사용(NodeType='STATION', allowedDev 기본 0.08m/0.07rad)
   — 기존 주행 그래프와의 연결 엣지는 최근접 노드로 자동 생성(EdgeType='TRAVEL')
4. `InspectionPointEntity`(스테이션=Point) + `InspectionTaskEntity`(TASK) 생성.
   Task.Position(jsonb)에 도면 좌표 원본, Task.Params(jsonb)에 §4.2 params 원형 저장.
5. 생성 결과 요약 반환: { stations, tasks, skipped(사유 포함) } — **잘린 항목 무언 삭제 금지.**

---

## 4. WP-3: 액션 카탈로그 + payload 빌드

### 4.1 `ref.action_catalog` 등록 (시드 데이터)

```sql
INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES ('startWeldInspection', 'NODE', 'HARD', '<아래 JSON Schema>', '단일 용접라인 구간 자동 검사');
```

param_schema (JSON Schema draft-07, 문자열로 저장):

```json
{
  "type": "object",
  "required": ["jobRef", "position", "params"],
  "properties": {
    "jobRef":  { "type": "string" },
    "position": {
      "type": "object",
      "required": ["seamStartW", "seamEndW", "drawingPos"],
      "properties": {
        "seamStartW":  { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "seamEndW":    { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "drawingPos":  { "type": "object",
          "required": ["tank", "level", "wall_code", "x", "y", "z"],
          "properties": {
            "tank": { "type": "string" }, "level": { "type": "integer" },
            "wall_code": { "type": "string" },
            "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" } } }
      }
    },
    "params": {
      "type": "object",
      "required": ["seamType", "sectionDxfId", "inspectionProfileId", "standoffMm",
                   "anchorGroupId", "seqInGroup"],
      "properties": {
        "seamType":            { "enum": ["LINE", "POLYLINE"] },
        "points":              { "type": "array" },
        "sectionDxfId":        { "type": "string" },
        "inspectionProfileId": { "type": "string" },
        "standoffMm":          { "type": "number" },
        "workingDistanceMm":   { "type": "number" },
        "anchorGroupId":       { "type": "string" },
        "seqInGroup":          { "type": "integer", "minimum": 1 }
      }
    }
  }
}
```

### 4.2 `MissionService.ReleaseMissionAsync` 확장

기존 jobRef/position/params 조립부를 다음과 같이 확정한다:
- `position.seamStartW/EndW`: Task 저장된 도면 좌표에 **릴리즈 시점의 유효 T_W_D 적용**
  (x,y 변환·z 통과, m 단위). 유효 T_W_D 없으면 릴리즈 실패 처리(§2.5).
- ~~`position.wallNormalW`~~ **제거됨 [SPEC v2]** — 툴 자세는 HD_AMR이 `wall_code` 키 티칭으로 결정. ACS는 위치·정차각만 책임.
- `params`: Task.Params 원형 + anchorGroupId/seqInGroup (WP-2가 저장한 값).
- 발행 직전 param_schema로 **JSON Schema 검증** — 실패 시 릴리즈 중단·알람 로그.
  (검증 라이브러리: `JsonSchema.Net` 권장, 없으면 필수 필드 수동 검증으로 대체)
- payload 예시(전체)는 부록 A 참조 — 시뮬레이터 테스트 픽스처로도 사용.

### 4.3 OrderBuilder — 수정 없음 확인
동일 노드 다중 액션 병합은 이미 구현되어 있으므로 anchorGroup TASK N개가
한 OrderNode에 액션 N개로 실리는지 **테스트로만 확인**한다(수정 금지).

---

## 5. WP-4: 시뮬레이터 확장 (`HD.Acs.Simulator/Program.cs`)

1. `startWeldInspection` 액션 수신 시 actionParameters를 §4.1 스키마 기준으로 검증,
   위반 항목을 콘솔에 출력(테스트 실패 근거).
2. **앵커 공유 시뮬레이션**: 직전 실행 액션과 `anchorGroupId`가 같고 그 사이 주행이 없었으면
   "정렬 스킵(⑤~⑦만)" 로그 + 실행 시간 단축(예: 3s→1s). 다르면 "정렬 포함(①~⑧)".
3. 실패 주입: 환경변수 `SIM_FAIL_ACTION_IDS`(콤마 구분)에 포함된 actionId는 FAILED 보고
   → ACS 재시도 정책 경로 테스트용.

---

## 6. WP-5 (후순위): UI

- **기준점 캡처 화면**: 대응쌍 테이블(도면좌표 입력 + "현재 위치 캡처" 버튼) → solve 호출 →
  tx/ty/yaw/RMS 표시, RMS 경고 시 배지. WP-1 API만 사용.
- **TASK 관리 3-Pane**(트리/벽면 전개도/상세): 표현 규칙 —
  영역(anchorGroup)=반투명 박스 컨테이너, TASK=선분 오버레이(상태색+방향화살표+seqInGroup 배지),
  박스 클릭=그룹 선택/선분 클릭=TASK 선택, 영역 상태=자식 집계,
  그룹 첫 TASK "정렬 포함"·이후 "정렬 공유" 배지. (HTML 목업 `acs_task_ui_mockup.html` 참조)
- 과도기: 슬라이싱 자동화 전 TASK 수동 입력 폼으로 E2E 우선 확보 가능.

---

## 7. 수용 기준 (Definition of Done)

**단위 테스트** (xUnit, 신규 `HD.Acs.Core.Tests` 프로젝트):
- [ ] DrawingTransform.Solve: 합성 대응쌍 왕복 복원, 3점+노이즈 RMS 검증, 2점 미만 예외
- [ ] SeamSlicer: L=3.2m, reach=1.0m, overlap=0.2m → 4구간 / 근접 seam 2개 → 스테이션 병합
      + anchorGroupId 공유 + seqInGroup 부여 / POLYLINE 호길이 분할
- [ ] payload 빌더: 부록 A 예시와 필드 단위 일치(golden test), 스키마 검증 통과
- [ ] T_W_D 무효(맵버전 불일치) 시 릴리즈 거부

**E2E (시뮬레이터, 수동 또는 스크립트)**:
- [ ] 기준점 3점 캡처 API → solve → T_W_D 저장 (시뮬레이터 agvPosition 이용)
- [ ] seam 2개 등록 → generate-from-seams → 스테이션·TASK 생성 확인
- [ ] Run 시작 → Order 발행 → 시뮬레이터가 anchorGroup 2-TASK 노드에서
      "정렬 포함 → 정렬 스킵" 순서로 실행 → 전 액션 FINISHED → 미션 Completed
- [ ] SIM_FAIL_ACTION_IDS로 1개 FAILED → inspection_result FAILED 1행 기록

**금지 사항 재확인**: BASE 좌표 계산·전송 없음 / 측정 포인트를 DB 정의하지 않음
(프로파일 파라미터로만) / SLAM 맵 파일 접근 코드 없음.

---

## 부록 A. payload 전체 예시 (golden fixture)

```json
{
  "actionType": "startWeldInspection",
  "actionId": "8f3c19aa-0000-4000-8000-0000000000e2",
  "blockingType": "HARD",
  "actionParameters": [
    { "key": "jobRef", "value": "JOB-CT1-L2-W03-S07-2" },
    { "key": "position", "value": {
        "seamStartW": [12.510, 5.980, 1.420],
        "seamEndW":   [13.310, 5.980, 1.420],
        "drawingPos": { "tank": "CT1", "level": 2, "wall_code": "W03",
                        "x": 3.120, "y": 0.0, "z": 1.420 } } },
    { "key": "params", "value": {
        "seamType": "LINE",
        "sectionDxfId": "DXF-CORR-T12",
        "inspectionProfileId": "INSPECT-STD-01",
        "standoffMm": 400,
        "workingDistanceMm": 400,
        "anchorGroupId": "CT1-L2-W03-ST04",
        "seqInGroup": 2 } }
  ]
}
```

노드측(nodePosition): `{ x: 12.482, y: 5.117, theta: 1.571, mapId: "CT1-L2",
allowedDeviationXY: 0.08, allowedDeviationTheta: 0.07 }`

> 참고: `ActionParameter.Value`는 object 직렬화가 기본. 로봇측 파서가 문자열만 수용하면
> position/params를 JSON 문자열로 직렬화하는 폴백 스위치(appsettings
> `Acs:Vda:StringifyActionParams`)를 두고 시뮬레이터로 선검증한다.

## 부록 B. 관련 결정 이력 (요약)

- TASK 정의 불변식·앵커 공유(C안 채택, seams 배열 B안 기각), ⑥ 다중 라인 구분 =
  seam 기하 피드포워드 + 이격 게이트 — 2026-07-29 확정.
- Fat vs Thin = 하이브리드(payload 자기완결 + jobRef 역추적 전용).
- T_W_D: SLAM 맵 원본 불필요, 기준점 2~3점(3점째=잔차 검증) AMR 정차 캡처,
  점 간 거리 최대화, 맵버전 바인딩, 정밀 대안=코봇 TCP 터치.
- AMR측 대응(참고): VDA 5050 로봇측 구현 — order 구독, state 2초 발행(agvPosition.mapId 포함),
  connection Last Will, startWeldInspection 핸들러 + 앵커 캐시(무효화: 주행 발생/보정 실패/
  그룹 변경/재시도 Order), 단위 변환(m/rad↔mm/deg)은 온보드 한 곳으로 통일.
