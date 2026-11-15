-- 측면 정차 층+벽면 설정 [VDA §4.4.3 / N25]. 행이 없으면 정면(FRONT).
-- 앱 기동 시 SchemaEnsure 가 같은 구문으로 만든다 — 수동 적용은 선택.
CREATE TABLE IF NOT EXISTS ref.wall_station_mode (
  tank_id     text NOT NULL,
  level       int  NOT NULL,
  wall_code   text NOT NULL,
  mode        text NOT NULL DEFAULT 'FRONT' CHECK (mode IN ('FRONT','SIDE')),
  default_dir text NOT NULL DEFAULT 'U_PLUS' CHECK (default_dir IN ('U_PLUS','U_MINUS')),
  standoff_m  double precision,
  updated_by  text,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tank_id, level, wall_code)
);
