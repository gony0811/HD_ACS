-- T_W_D affine 확장 — 선형부 2×2 (맵 = A·도면 + t). 네 값 NULL = 강체 R(yaw_rad) (종전 동작).
-- SLAM 맵이 실제보다 늘거나 줄어든 층(강체로는 잔차가 남는 층)을 흡수한다.
-- 앱 기동 시 SchemaEnsure 가 같은 구문을 실행하므로 수동 적용은 필요 없다(직접 접근 DB용).
ALTER TABLE ref.map_calibration ADD COLUMN IF NOT EXISTS a11 double precision;
ALTER TABLE ref.map_calibration ADD COLUMN IF NOT EXISTS a12 double precision;
ALTER TABLE ref.map_calibration ADD COLUMN IF NOT EXISTS a21 double precision;
ALTER TABLE ref.map_calibration ADD COLUMN IF NOT EXISTS a22 double precision;
