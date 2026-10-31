-- 2026-10-31: 정차 온보드 보정 후진 상한의 영역별 덮어쓰기 [VDA5050_INTERFACE_SPEC §4.4.2, N23].
-- 정차점 뒤 공간이 좁은 영역(맞은편 구조물·층 경계 등)만 줄인다 — AMR 뒤 범퍼는 원래 정차점 중심 뒤로 365 + 후진량까지 간다.
-- NULL = 앱 설정 Acs:Dispatch:StationAdjust:MaxBackwardMm(기본 400) 사용, 0 = 그 영역은 후진 보정 금지. 비파괴·idempotent.
-- 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-10-31_area_station_max_backward.sql

ALTER TABLE ref.inspection_area
  ADD COLUMN IF NOT EXISTS station_max_backward_mm double precision;
