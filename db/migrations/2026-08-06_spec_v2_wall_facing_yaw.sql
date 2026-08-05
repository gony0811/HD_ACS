-- ============================================================================
-- Migration: 벽면(ref.wall) 재정의 — 정차각 자동화 (facing_yaw/normal 제거)
-- 날짜: 2026-08-06
-- 대상: 구버전 ref.wall(normal_drawing 또는 facing_yaw)로 생성된 DB
-- 증상: POST /api/walls 벽면 등록 시 500 (컬럼 불일치 / NOT NULL 위반)
--
-- 내용: 벽면 3테이블(wall/inspection_area/area_task)을 현재 db/schema.sql 정의로 재생성한다.
--       ref.wall은 정차각을 저장하지 않는다 — 정차각은 영역·작업 seam 기하에서 자동 산출된다.
--       구 데이터는 신 구조와 호환 불가하므로 폐기된다(그 외 테이블·데이터는 보존).
--       겸사로 startWeldInspection param_schema를 최신화(wallNormalW 요구 제거)한다.
--
-- 적용: psql -h localhost -U postgres -d hdacs -f db/migrations/2026-08-06_spec_v2_wall_facing_yaw.sql
--       (대안: 데이터 전량 폐기가 편하면 DROP SCHEMA ref,run,hist,alarm,sys CASCADE 후 db/schema.sql 재적용)
-- ============================================================================
BEGIN;

-- 1) 벽면 3테이블 재생성 (FK 역순 삭제 → 정순 생성)
DROP TABLE IF EXISTS ref.area_task CASCADE;
DROP TABLE IF EXISTS ref.inspection_area CASCADE;
DROP TABLE IF EXISTS ref.wall CASCADE;

CREATE TABLE ref.wall (
  tank_id     text NOT NULL,
  level       int  NOT NULL,
  wall_code   text NOT NULL,                        -- 통제 어휘 [TANK_WALL_LAYOUT §2] 예: 'SM','PM','A'
  description text,
  PRIMARY KEY (tank_id, level, wall_code)
);

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
  station_theta  double precision,                  -- NULL=디폴트: ref.wall.facing_yaw
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

-- 2) startWeldInspection param_schema 최신화 (position.required 에서 wallNormalW 제거)
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

COMMIT;
