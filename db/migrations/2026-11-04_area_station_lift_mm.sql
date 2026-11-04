-- 2026-11-04: 영역별 정차 리프트 높이(station_lift_mm) 컬럼 추가 및 startWeldInspection param_schema(liftStrokeMm) 반영.
-- 정차점 최적화 적용 시 찾은 공통 리프트 높이(0~1000 mm)를 저장하고, 배차 시 params.liftStrokeMm(NULL이면 기본 0)으로 AMR에 전달.
-- 멱등(IF NOT EXISTS / ON CONFLICT). 앱 기동 시 SchemaEnsure·ActionCatalogSeed 가 동일 적용을 수행한다.

ALTER TABLE ref.inspection_area
  ADD COLUMN IF NOT EXISTS station_lift_mm double precision;

