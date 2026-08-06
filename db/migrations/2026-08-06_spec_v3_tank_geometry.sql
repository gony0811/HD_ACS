-- ============================================================================
-- Migration: SPEC v3 §2/§3 — 선창 파라메트릭 정의 + 면 자동생성
-- 날짜: 2026-08-06
-- 내용: ref.tank_geometry 신설, ref.wall을 v3(자동생성 프레임)로 재정의.
--       v2 영역/작업(inspection_area, area_task)은 철거(§4에서 (u,v) 모델로 재생성).
--       구 데이터 폐기(면은 파라미터에서 재생성됨). 그 외 테이블·데이터는 보존.
--
-- 적용(Docker): docker cp db/migrations/2026-08-06_spec_v3_tank_geometry.sql dev-postgres:/tmp/v3.sql
--               docker exec -e PGPASSWORD=postgres dev-postgres psql -U postgres -d hdacs -v ON_ERROR_STOP=1 -f /tmp/v3.sql
-- ============================================================================
BEGIN;

DROP TABLE IF EXISTS ref.area_task CASCADE;
DROP TABLE IF EXISTS ref.inspection_area CASCADE;
DROP TABLE IF EXISTS ref.wall CASCADE;

CREATE TABLE ref.tank_geometry (
  tank_id     text PRIMARY KEY,
  length_l    double precision NOT NULL,
  w_floor     double precision NOT NULL,
  theta_low   double precision NOT NULL,   -- [rad]
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

CREATE TABLE ref.wall (
  tank_id     text NOT NULL REFERENCES ref.tank_geometry(tank_id) ON DELETE CASCADE,
  wall_code   text NOT NULL,               -- B/SL/PL/SM/PM/SU/PU/T/F/A
  origin      jsonb NOT NULL,
  u_axis      jsonb NOT NULL,
  v_axis      jsonb NOT NULL,
  normal      jsonb NOT NULL,
  u_len       double precision NOT NULL,
  v_len       double precision NOT NULL,
  facing_yaw  double precision,            -- B/T는 NULL
  generated   boolean NOT NULL DEFAULT true,
  description text,
  PRIMARY KEY (tank_id, wall_code)
);

COMMIT;
