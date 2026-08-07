-- 검사 영역을 임의 4점 사각형(quad)으로 확장.
-- ref.inspection_area 에 corners(jsonb 4점) 추가. u/v_min·max 는 서버가 코너에서 유도하는 bbox로 유지.
-- 기존(AABB) 행은 min/max 사각형 4코너로 backfill. 앱은 자동 마이그레이션 안 하므로 수동 적용.

ALTER TABLE ref.inspection_area ADD COLUMN IF NOT EXISTS corners jsonb;

-- 기존 행 backfill: bbox 사각형 4코너(좌하→우하→우상→좌상)
UPDATE ref.inspection_area
   SET corners = jsonb_build_array(
         jsonb_build_array(u_min, v_min),
         jsonb_build_array(u_max, v_min),
         jsonb_build_array(u_max, v_max),
         jsonb_build_array(u_min, v_max))
 WHERE corners IS NULL;

ALTER TABLE ref.inspection_area ALTER COLUMN corners SET NOT NULL;

COMMENT ON COLUMN ref.inspection_area.corners IS '임의 4점 사각형 [[u1,v1]…[u4,v4]] (면 로컬 m). u/v_min·max=서버 유도 bbox';
