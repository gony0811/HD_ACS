-- 2026-08-28: 부분 검사 계획 — 시나리오 검사 대상 영역 연결 테이블.
-- 연결 0건 = 선창 전체 검사(하위호환). sort_order는 표시·Seq용(배차 순서는 greedy 최근접).
-- idempotent. 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-08-28_scenario_area.sql

CREATE TABLE IF NOT EXISTS ref.scenario_area (
  scenario_id uuid NOT NULL REFERENCES ref.scenario ON DELETE CASCADE,
  area_id     uuid NOT NULL REFERENCES ref.inspection_area (area_id) ON DELETE CASCADE,
  sort_order  int  NOT NULL DEFAULT 0,
  PRIMARY KEY (scenario_id, area_id)
);
