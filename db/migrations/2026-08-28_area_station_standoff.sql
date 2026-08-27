-- 2026-08-28: standoff 정차점 도입 — 영역별 정차 이격 거리 [m].
-- 정차점 = 영역 중심 도면좌표 + 내부향 법선 수평성분 × standoff (B/T는 폴백).
-- NULL = 앱 설정 Acs:Area:StationStandoffM 기본값 사용. 비파괴·idempotent.
-- 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-08-28_area_station_standoff.sql

ALTER TABLE ref.inspection_area
  ADD COLUMN IF NOT EXISTS station_standoff_m double precision;
