-- ============================================================================
-- Migration: SPEC v3 §4 — 영역·검사 작업 (벽면-로컬 u,v) 재도입
-- 날짜: 2026-08-07
-- 내용: §2/§3에서 철거했던 inspection_area·area_task를 v3 (u,v) 스키마로 생성.
--       ref.wall(§3)이 선행 존재해야 함(FK). 비파괴(기존 다른 테이블 무관).
--
-- 적용(Docker): docker cp db/migrations/2026-08-07_spec_v3_areas.sql dev-postgres:/tmp/areas.sql
--               docker exec -e PGPASSWORD=postgres dev-postgres psql -U postgres -d hdacs -v ON_ERROR_STOP=1 -f /tmp/areas.sql
-- ============================================================================
BEGIN;

CREATE TABLE IF NOT EXISTS ref.inspection_area (
  area_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tank_id       text NOT NULL,
  wall_code     text NOT NULL,
  level         int  NOT NULL,
  name          text NOT NULL,
  u_min         double precision NOT NULL,
  v_min         double precision NOT NULL,
  u_max         double precision NOT NULL,
  v_max         double precision NOT NULL,
  station_x     double precision,
  station_y     double precision,
  station_theta double precision,
  sort_order    int  NOT NULL DEFAULT 0,
  created_by    text,
  created_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tank_id, wall_code, name),
  CHECK (u_min < u_max AND v_min < v_max),
  FOREIGN KEY (tank_id, wall_code) REFERENCES ref.wall (tank_id, wall_code) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_inspection_area_wall ON ref.inspection_area (tank_id, wall_code);

CREATE TABLE IF NOT EXISTS ref.area_task (
  task_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  area_id        uuid NOT NULL REFERENCES ref.inspection_area(area_id) ON DELETE CASCADE,
  seq            int  NOT NULL,
  name           text,
  seam_type      text NOT NULL DEFAULT 'LINE',
  start_u        double precision NOT NULL,
  start_v        double precision NOT NULL,
  end_u          double precision NOT NULL,
  end_v          double precision NOT NULL,
  section_dxf_id text NOT NULL DEFAULT '',
  profile_id     text NOT NULL DEFAULT '',
  created_by     text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (area_id, seq)
);
CREATE INDEX IF NOT EXISTS ix_area_task_area ON ref.area_task (area_id);

COMMIT;
