-- SPEC v3.1 §2/§5-A — 층 자동 유도(면×층 도달 밴드) 지원.
-- tank_geometry 에 코봇 도달 밴드 보정용 선택 컬럼 reach_z_min/max 추가(비파괴 ADD COLUMN).
-- inspection_area.level 은 유지하되 의미를 "서버 유도값"으로 재정의(스키마 변경 없음, 주석만 갱신).
-- 앱은 자동 마이그레이션을 하지 않으므로 수동 적용한다.

ALTER TABLE ref.tank_geometry
  ADD COLUMN IF NOT EXISTS reach_z_min double precision,   -- (선택) 플랫폼 기준 코봇 도달 하한 상대높이
  ADD COLUMN IF NOT EXISTS reach_z_max double precision;   -- (선택) 상한. 미지정 시 밴드 = 층 경계 그대로

COMMENT ON COLUMN ref.inspection_area.level IS
  'AMR 주행 층(mapId 결정) — v3.1: 입력 아님, 서버가 §5-A 영역 z범위→도달 밴드로 유도·저장';
