-- 선창(프로젝트)의 검사 종류 — WELD(용접검사, 기본) | GAS(가스검사).
-- 기존 선창은 모두 WELD. 앱 기동 시 SchemaEnsure 가 같은 구문을 실행하므로 수동 적용은 필요 없다.
ALTER TABLE ref.tank_geometry ADD COLUMN IF NOT EXISTS inspection_kind text NOT NULL DEFAULT 'WELD';
