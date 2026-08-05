-- ============================================================================
-- HD_ACS Database Schema  (PostgreSQL 15+)
-- 네이밍 규칙 [C안]: PostgreSQL 스키마(namespace)로 계층 구분 + snake_case
--   ref   = Reference(마스터: 그래프/시나리오/로봇/액션)
--   run   = Runtime(실행: 미션/Order 스냅샷/로봇 컨텍스트)
--   hist  = History(이력)      alarm = 알람      sys = 시스템(계정/감사)
-- 근거 문서: docs/GRAPH_DATA_MODEL.md, docs/ARCHITECTURE_DECISIONS.md
-- ============================================================================
CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- gen_random_uuid()

CREATE SCHEMA IF NOT EXISTS ref;
CREATE SCHEMA IF NOT EXISTS run;
CREATE SCHEMA IF NOT EXISTS hist;
CREATE SCHEMA IF NOT EXISTS alarm;
CREATE SCHEMA IF NOT EXISTS sys;
-- ═══════════════════════════ ① 정적 그래프 (ref) ═══════════════════════════

CREATE TABLE ref.map (
  map_id      text PRIMARY KEY,            -- VDA5050 nodePosition.mapId (예: 'CT1-L1')
  tank_id     text NOT NULL,               -- 화물창 ID [TANK_WALL_LAYOUT]
  level       int  NOT NULL,               -- 층: 1(바닥)~4 [4층 슬라이스]
  name        text NOT NULL,
  version     int  NOT NULL DEFAULT 1,
  is_active   boolean NOT NULL DEFAULT true,
  UNIQUE (tank_id, level, version)
);

CREATE TABLE ref.node (
  node_id           text PRIMARY KEY,      -- VDA5050 node.nodeId (예: 'CT1-L1-N012')
  map_id            text NOT NULL REFERENCES ref.map,
  name              text,
  x                 double precision NOT NULL,   -- 맵 좌표 [m]
  y                 double precision NOT NULL,
  theta             double precision,            -- 정차 방향 [rad], NULL=무관
  allowed_dev_xy    double precision,            -- VDA5050 allowedDeviationXY
  allowed_dev_theta double precision,
  node_type         text NOT NULL DEFAULT 'WAYPOINT',
    -- WAYPOINT | INSPECTION_STOP | ELEVATOR | CHARGING | PARKING
  metadata          jsonb
);
CREATE INDEX ix_node_map ON ref.node (map_id);

CREATE TABLE ref.edge (
  edge_id       text PRIMARY KEY,          -- VDA5050 edge.edgeId
  map_id        text NOT NULL REFERENCES ref.map,
  start_node_id text NOT NULL REFERENCES ref.node,
  end_node_id   text NOT NULL REFERENCES ref.node,
  bidirectional boolean NOT NULL DEFAULT true,
  edge_type     text NOT NULL DEFAULT 'TRAVEL',
    -- TRAVEL          : 경로 계산 포함
    -- MANUAL_TRANSFER : 층간 수동 이송 표시 — 경로계산/Order 생성 항상 제외 [Q9]
  max_speed     double precision,
  length        double precision,          -- 경로 비용 (NULL=유클리드 거리)
  metadata      jsonb
);
CREATE INDEX ix_edge_map ON ref.edge (map_id);

CREATE TABLE ref.zone (
  zone_id     text PRIMARY KEY,
  map_id      text NOT NULL REFERENCES ref.map,
  name        text NOT NULL,
  zone_type   text NOT NULL,   -- FLOOR | AREA | ELEVATOR_CELL | RESTRICTED
  geometry    jsonb            -- 폴리곤 (UI 표시/포함 판정)
);

CREATE TABLE ref.zone_member (          -- NA_R_LINK_ZONE 대응
  zone_id  text NOT NULL REFERENCES ref.zone,
  node_id  text NOT NULL REFERENCES ref.node,
  PRIMARY KEY (zone_id, node_id)
);

-- ─────────────── 도면→맵 캘리브레이션 (ref) [PHASE2 WP-1 · T_W_D] ───────────────
-- 층별 도면 좌표 → AMR SLAM 맵 좌표 강체변환. 맵버전과 바인딩되어 맵 재생성 시 자동 무효.
CREATE TABLE ref.map_calibration (
  map_id        text NOT NULL,               -- ref.map.map_id
  map_version   int  NOT NULL,               -- ref.map.version 과 일치할 때만 유효 [§2.5]
  tx            double precision NOT NULL,    -- 평행이동 X [m]
  ty            double precision NOT NULL,    -- 평행이동 Y [m]
  yaw_rad       double precision NOT NULL,    -- 회전 [rad], 맵 X축 기준 CCW
  rms_m         double precision NOT NULL,    -- 등록 잔차 RMS [m]
  point_count   int NOT NULL,                 -- 사용된 대응쌍 수
  registered_by text,
  registered_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (map_id, map_version)
);

CREATE TABLE ref.map_calibration_point (     -- 캡처된 기준점 대응쌍 (감사·재계산용 보존)
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  map_id        text NOT NULL,
  map_version   int  NOT NULL,
  drawing_x_m   double precision NOT NULL,    -- 도면 좌표 (m로 정규화하여 저장)
  drawing_y_m   double precision NOT NULL,
  map_x         double precision NOT NULL,    -- 캡처 시점 robot_context.reported_x
  map_y         double precision NOT NULL,
  captured_at   timestamptz NOT NULL DEFAULT now(),
  captured_by   text
);
CREATE INDEX ix_map_calib_point ON ref.map_calibration_point (map_id, map_version);

-- ═══════════════════════ ② 액션 카탈로그 (ref) [Q1] ═══════════════════════

CREATE TABLE ref.action_catalog (
  action_type   text PRIMARY KEY,   -- 예: 'startInspectionJob','capture','initPosition','emergencyStop'
  scope         text NOT NULL,      -- NODE | EDGE | INSTANT
  blocking_type text NOT NULL,      -- NONE | SOFT | HARD  (VDA5050 blockingType)
  param_schema  jsonb,              -- 파라미터 JSON Schema (Order 생성 시 검증)
  description   text
);

-- 액션 카탈로그 시드 [PHASE2] — inspection_task.action_type FK + 발행 전 payload 검증(WP-3 §4.1).
-- startWeldInspection: 단일 용접라인 구간 자동 검사. param_schema = SPEC §4.1 JSON Schema(draft-07).
INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES ('startWeldInspection', 'NODE', 'HARD',
'{
  "type": "object",
  "required": ["jobRef", "position", "params"],
  "properties": {
    "jobRef": { "type": "string" },
    "position": {
      "type": "object",
      "required": ["seamStartW", "seamEndW", "drawingPos"],
      "properties": {
        "seamStartW":  { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "seamEndW":    { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "drawingPos": {
          "type": "object",
          "required": ["tank", "level", "wall_code", "x", "y", "z"],
          "properties": {
            "tank": { "type": "string" }, "level": { "type": "integer" },
            "wall_code": { "type": "string" },
            "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" }
          }
        }
      }
    },
    "params": {
      "type": "object",
      "required": ["seamType", "sectionDxfId", "inspectionProfileId", "standoffMm", "anchorGroupId", "seqInGroup"],
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
}',
        '단일 용접라인 구간 자동 검사 [WP-3]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;

-- ═══════════════════════════ ③ 시나리오 (ref) ═══════════════════════════

CREATE TABLE ref.scenario (
  scenario_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text NOT NULL,
  version     int  NOT NULL,
  tank_id     text NOT NULL,
  policy      jsonb NOT NULL,      -- 재시도/스킵 정책 외부화 [ADR-010]
  status      text NOT NULL DEFAULT 'DRAFT',   -- DRAFT | RELEASED | RETIRED
  UNIQUE (name, version)
);

CREATE TABLE ref.inspection_point (
  point_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  scenario_id uuid NOT NULL REFERENCES ref.scenario ON DELETE CASCADE,
  seq         int  NOT NULL,                       -- 검사 순서 (층 전환 포함 전체 순서)
  node_id     text NOT NULL REFERENCES ref.node,-- AMR 정차 노드 → 층은 node.map_id로 결정
  UNIQUE (scenario_id, seq)
);

CREATE TABLE ref.inspection_task (
  task_id     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  point_id    uuid NOT NULL REFERENCES ref.inspection_point ON DELETE CASCADE,
  seq         int  NOT NULL,
  action_type text NOT NULL REFERENCES ref.action_catalog,
  job_ref     text,      -- HD_AMR에 정의된 검사 작업 식별자 [ADR-001]
  position    jsonb,     -- 촬영 위치 [ADR-004]: {tank,level,wall_code,x,y,z} [TANK_WALL_LAYOUT]
  params      jsonb,     -- opaque — HD_AMR이 해석
  UNIQUE (point_id, seq)
);

-- ─────────────── 용접선 (ref) [PHASE2 WP-2] ───────────────
-- 사람이 등록하는 유일한 입력. 도면 좌표로 저장하고 SeamSlicer가 스테이션/TASK로 전개한다.
CREATE TABLE ref.weld_seam (
  seam_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tank_id        text NOT NULL,
  level          int  NOT NULL,                     -- 층 (tank+level → ref.map.map_id)
  wall_code      text NOT NULL,                     -- 'W03' [TANK_WALL_LAYOUT]
  seam_type      text NOT NULL DEFAULT 'LINE',      -- LINE | POLYLINE
  path_drawing   jsonb NOT NULL,                    -- [[x,y,z],...] m, 도면 좌표. LINE이면 2점
  normal_drawing jsonb NOT NULL,                    -- [nx,ny,nz] 벽면 법선 (도면 좌표계)
  section_dxf_id text NOT NULL,                      -- 단면 DXF 참조 (원문 미저장 [WP-1 §1.3])
  profile_id     text NOT NULL,                      -- 검사 프로파일 참조
  created_by     text,
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_weld_seam_map ON ref.weld_seam (tank_id, level);

-- ─────────────── 벽면(Wall) LAYER (ref) [정차각 자동화 2026-08-06] ───────────────
-- 벽면 레지스트리 + HD_AMR 티칭 조회 키(wall_code). 영역 FK·"미등록 벽면" 게이트의 상위 LAYER.
-- 정차각은 저장하지 않는다 — 영역·작업 seam 기하에서 자동 산출(정차 위치 → seam 중심 방향) [TANK_WALL_LAYOUT §6].
CREATE TABLE ref.wall (
  tank_id     text NOT NULL,
  level       int  NOT NULL,
  wall_code   text NOT NULL,                        -- 통제 어휘 [TANK_WALL_LAYOUT §2] 예: 'SM','PM','A'
  description text,
  PRIMARY KEY (tank_id, level, wall_code)
);

-- ─────────────── 영역(Area) LAYER + 수동 검사 작업 (ref) [PHASE2 개정 2026-08-04] ───────────────
-- 자동 슬라이싱(weld_seam/SeamSlicer) 대체. 운영자가 영역과 그 안의 검사 작업을 수동 정의한다.
-- 영역 1개 = STATION 노드 1개 = anchorGroup 1개, 작업 1개 = TASK 1개 (불변식 유지).
-- 정차각은 저장하지 않고 정차 위치→작업 seam 중심 방향으로 자동 산출 [정차각 자동화].
CREATE TABLE ref.inspection_area (
  area_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tank_id        text NOT NULL,
  level          int  NOT NULL,
  wall_code      text NOT NULL,                     -- 상위 LAYER [TANK_WALL_LAYOUT], 정차각 상속원
  name           text NOT NULL,                     -- 예: 'A01' (tank-level-wall 내 유일)
  min_x          double precision NOT NULL,         -- 도면 좌표(m) 사각 영역
  min_y          double precision NOT NULL,
  max_x          double precision NOT NULL,
  max_y          double precision NOT NULL,
  station_x      double precision,                  -- 정차 pose 수동 오버라이드 (NULL=디폴트: 영역 중앙)
  station_y      double precision,
  station_theta  double precision,                  -- NULL=디폴트: 정차→seam 중심 방향 자동 산출
  sort_order     int NOT NULL DEFAULT 0,            -- 방문 순서 (level→wall→sort_order→name)
  created_by     text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tank_id, level, wall_code, name),
  CHECK (min_x < max_x AND min_y < max_y),
  FOREIGN KEY (tank_id, level, wall_code) REFERENCES ref.wall (tank_id, level, wall_code)
);
CREATE INDEX ix_inspection_area_map ON ref.inspection_area (tank_id, level, wall_code);

CREATE TABLE ref.area_task (
  task_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  area_id        uuid NOT NULL REFERENCES ref.inspection_area ON DELETE CASCADE,
  seq            int  NOT NULL,                     -- 영역 내 실행 순서(1..N → seqInGroup)
  name           text,                              -- 작업 이름(선택)
  seam_type      text NOT NULL DEFAULT 'LINE',      -- LINE | POLYLINE
  start_drawing  jsonb NOT NULL,                    -- [x,y,z] 도면 좌표 (챔퍼는 z 포함 3D)
  end_drawing    jsonb NOT NULL,                    -- [x,y,z]
  section_dxf_id text NOT NULL DEFAULT '',
  profile_id     text NOT NULL DEFAULT '',
  created_by     text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (area_id, seq)
);
CREATE INDEX ix_area_task_area ON ref.area_task (area_id);

-- ═══════════════════════════ 로봇 마스터 (ref) ═══════════════════════════

CREATE TABLE ref.robot (
  robot_id      text PRIMARY KEY,   -- fleet-ready 키 [ADR-003]
  name          text NOT NULL,
  manufacturer  text NOT NULL,      -- VDA5050 MQTT 토픽 요소
  serial_number text NOT NULL,      -- VDA5050 MQTT 토픽 요소
  vda_version   text NOT NULL DEFAULT '2.0',
  is_active     boolean NOT NULL DEFAULT true,
  UNIQUE (manufacturer, serial_number)
);

-- ═══════════════════════════ ④ 런타임 (run) ═══════════════════════════

-- 시나리오 실행 단위: 층 단위 미션들의 시퀀스 [GRAPH_DATA_MODEL 8.4]
CREATE TABLE run.scenario_run (
  run_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  scenario_id  uuid NOT NULL,
  scenario_ver int  NOT NULL,
  robot_id     text NOT NULL REFERENCES ref.robot,
  state        text NOT NULL,   -- RUNNING | WAITING_FLOOR_TRANSFER | COMPLETED | ABORTED
  started_at   timestamptz,
  ended_at     timestamptz
);

-- 층 단위 미션 (한 미션의 모든 노드는 같은 map_id)
CREATE TABLE run.mission (
  mission_id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id          uuid NOT NULL REFERENCES run.scenario_run,
  seq             int  NOT NULL,             -- run 내 층 순서
  map_id          text NOT NULL REFERENCES ref.map,
  robot_id        text NOT NULL,
  order_id        text NOT NULL,             -- VDA5050 orderId
  order_update_id int  NOT NULL DEFAULT 0,   -- 재시도/스킵 시 증가
  state           text NOT NULL,             -- Stateless 상태머신 [ADR-010]
    -- CREATED|RELEASED|RUNNING|DISCONNECTED|PAUSED|COMPLETED|ABORTED
  started_at      timestamptz,
  ended_at        timestamptz,
  UNIQUE (run_id, seq)
);
CREATE INDEX ix_mission_robot ON run.mission (robot_id, state);

-- Order 스냅샷: 맵/시나리오 수정과 무관하게 당시 값 보존
CREATE TABLE run.order_node (
  mission_id  uuid NOT NULL REFERENCES run.mission ON DELETE CASCADE,
  sequence_id int  NOT NULL,                 -- VDA5050: 노드=짝수 (0,2,4…)
  node_id     text NOT NULL,
  x           double precision NOT NULL,
  y           double precision NOT NULL,
  theta       double precision,
  released    boolean NOT NULL DEFAULT true, -- Base 선릴리즈 [ADR-002]
  status      text NOT NULL DEFAULT 'PENDING', -- PENDING | PASSED (state.lastNodeId 반영)
  PRIMARY KEY (mission_id, sequence_id)
);

CREATE TABLE run.order_edge (
  mission_id    uuid NOT NULL REFERENCES run.mission ON DELETE CASCADE,
  sequence_id   int  NOT NULL,               -- VDA5050: 엣지=홀수 (1,3,5…)
  edge_id       text NOT NULL,
  start_node_id text NOT NULL,
  end_node_id   text NOT NULL,
  released      boolean NOT NULL DEFAULT true,
  PRIMARY KEY (mission_id, sequence_id)
);

CREATE TABLE run.order_action (
  action_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),  -- state.actionStates 대조 키
  mission_id       uuid NOT NULL REFERENCES run.mission ON DELETE CASCADE,
  node_sequence_id int  NOT NULL,
  task_id          uuid,               -- 원본 HD_R_INSPECTION_TASK 참조 (이력 대조용)
  action_type      text NOT NULL,
  blocking_type    text NOT NULL,
  params           jsonb,
  status           text NOT NULL DEFAULT 'WAITING',
    -- WAITING | INITIALIZING | RUNNING | FINISHED | FAILED  (VDA5050 actionStatus)
  result           jsonb,              -- 성공/실패 응답 [ADR-004]
  attempts         int NOT NULL DEFAULT 0
);
CREATE INDEX ix_orderaction_mission ON run.order_action (mission_id, node_sequence_id);

-- 로봇 컨텍스트: 수동 지정값 vs 로봇 보고값 분리 보관 [Q9, GRAPH_DATA_MODEL 8.4]
CREATE TABLE run.robot_context (
  robot_id          text PRIMARY KEY REFERENCES ref.robot,
  manual_map_id     text,              -- 작업자가 지정한 현재 층 (수동 존 변경)
  manual_updated_by text,
  manual_updated_at timestamptz,
  reported_map_id   text,              -- HD_AMR state 최신 보고 (agvPosition.mapId)
  reported_x        double precision,
  reported_y        double precision,
  reported_theta    double precision,
  battery_pct       double precision,
  connection_state  text,              -- ONLINE | OFFLINE | CONNECTIONBROKEN
  reported_at       timestamptz
);

-- ═══════════════════════════ ⑤ 이력 (hist) ═══════════════════════════

-- 상태머신 전이 이벤트 [ADR-010]
CREATE TABLE hist.transition_log (
  id          bigserial PRIMARY KEY,
  mission_id  uuid NOT NULL,
  from_state  text NOT NULL,
  to_state    text NOT NULL,
  trigger     text NOT NULL,   -- StateReceived | ActionFailed | ConnectionLost | OperatorPause …
  payload     jsonb,
  occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_translog_mission ON hist.transition_log (mission_id, occurred_at);

-- 검사 수행 이력: 검사 S/W와의 대조 키(위치+시각) 보존 [ADR-004, Q2]
CREATE TABLE hist.inspection_result (
  result_id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id      uuid NOT NULL,
  mission_id  uuid NOT NULL,
  point_id    uuid,
  task_id     uuid,
  robot_id    text NOT NULL,
  node_id     text NOT NULL,
  action_type text NOT NULL,
  position    jsonb NOT NULL,   -- {tank,level,wall_code,x,y,z} — 검사 S/W 대조 키
  status      text NOT NULL,    -- SUCCESS | FAILED | SKIPPED
  attempts    int  NOT NULL,
  occurred_at timestamptz NOT NULL   -- 대조 키 (타임스탬프 규약 Q2)
);
CREATE INDEX ix_inspresult_time ON hist.inspection_result (occurred_at);
CREATE INDEX ix_inspresult_run  ON hist.inspection_result (run_id);

-- ═══════════════════════════ ⑥ 알람 (alarm) ═══════════════════════════

CREATE TABLE alarm.spec (           -- NA_A_ALARMSPEC 승계
  alarm_code  text PRIMARY KEY,
  severity    text NOT NULL,    -- INFO | WARNING | CRITICAL
  title       text NOT NULL,
  description text
);

CREATE TABLE alarm.alarm (
  alarm_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  alarm_code  text NOT NULL REFERENCES alarm.spec,
  robot_id    text,
  mission_id  uuid,
  detail      jsonb,
  raised_at   timestamptz NOT NULL DEFAULT now(),
  cleared_at  timestamptz,       -- NULL = 활성 알람
  cleared_by  text
);
CREATE INDEX ix_alarm_active ON alarm.alarm (raised_at) WHERE cleared_at IS NULL;

-- ═══════════════════════════ ⑦ 시스템 (sys) ═══════════════════════════

CREATE TABLE sys.app_user (                 -- NA_X_USER 승계 (user는 PG 예약어 → app_user)
  user_id       text PRIMARY KEY,
  password_hash text NOT NULL,
  role          text NOT NULL,   -- ADMIN | OPERATOR | VIEWER
  is_active     boolean NOT NULL DEFAULT true,
  must_change_password boolean NOT NULL DEFAULT true
);

CREATE TABLE sys.audit_log (            -- 수동 존 변경 등 감사 기록 [Q9]
  id          bigserial PRIMARY KEY,
  user_id     text NOT NULL,
  action      text NOT NULL,      -- MANUAL_ZONE_CHANGE | MISSION_ABORT | EMERGENCY_STOP …
  target      text,               -- robot_id / mission_id 등
  detail      jsonb,
  occurred_at timestamptz NOT NULL DEFAULT now()
);

-- ═══════════════════════════ 조회 뷰 — NAMUGA *_VW 패턴 승계 ═══════════

-- 미션 진행률 (UI/SignalR 집계용)
CREATE VIEW run.mission_progress_vw AS
SELECT m.mission_id, m.run_id, m.map_id, m.state,
       count(oa.action_id)                                   AS total_actions,
       count(oa.action_id) FILTER (WHERE oa.status='FINISHED') AS finished_actions,
       count(oa.action_id) FILTER (WHERE oa.status='FAILED')   AS failed_actions
FROM run.mission m
LEFT JOIN run.order_action oa ON oa.mission_id = m.mission_id
GROUP BY m.mission_id;

-- 노드 평탄화 (전개도/3D UI 조회용: 노드+층+존)
CREATE VIEW ref.node_vw AS
SELECT n.node_id, n.node_type, n.x, n.y, n.theta,
       mp.map_id, mp.tank_id, mp.level,
       z.zone_id, z.zone_type
FROM ref.node n
JOIN ref.map mp ON mp.map_id = n.map_id
LEFT JOIN ref.zone_member zm ON zm.node_id = n.node_id
LEFT JOIN ref.zone z ON z.zone_id = zm.zone_id;
