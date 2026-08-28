-- 2026-08-28  운영자 맵 주석(벽·이동 불가 구역) [2D 평면도]
-- 배경: 2D 평면도 우클릭으로 벽(WALL, 선분 2점)·이동 불가 구역(NOGO, 다각형)을 등록.
--   좌표는 도면 프레임 [[x,y],…]. NOGO는 "여기로 이동"(goto) 게이트에 사용 — 대상 지점이 구역 내면 이동 거부.
-- 적용: psql -U postgres -d hdacs -f db/migrations/2026-08-28_map_annotation.sql

CREATE TABLE IF NOT EXISTS ref.map_annotation (
  annotation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tank_id       text NOT NULL,
  level         int  NOT NULL,
  kind          text NOT NULL,                 -- WALL | NOGO
  name          text NOT NULL,
  points        jsonb NOT NULL,                -- [[x,y],…] 도면 좌표 (WALL=2점, NOGO≥3점)
  created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_map_annotation_tank_level ON ref.map_annotation (tank_id, level);
