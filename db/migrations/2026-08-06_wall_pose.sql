-- ============================================================================
-- Migration: ref.wall 벽면 pose 컬럼 추가 [벽면-로컬 좌표 모델 Phase 1]
-- 날짜: 2026-08-06
-- 내용: 벽면-로컬 2D(u,v) → 도면 3D 변환용 pose(원점+u/v축)를 nullable로 추가.
--       비파괴(ADD COLUMN IF NOT EXISTS) — 기존 ref.wall 데이터 유지.
--       pose 없는 벽면(레지스트리-only)은 계속 유효하며, 벽면-로컬 흐름(Phase 2+)에서만 pose 필요.
--
-- 적용: psql -h localhost -U postgres -d hdacs -f db/migrations/2026-08-06_wall_pose.sql
--       (Docker: docker cp 후 docker exec ... psql -f)
-- ============================================================================
BEGIN;

ALTER TABLE ref.wall ADD COLUMN IF NOT EXISTS origin jsonb;   -- [x,y,z] 벽면-로컬 (0,0)의 도면 좌표
ALTER TABLE ref.wall ADD COLUMN IF NOT EXISTS u_axis jsonb;   -- [x,y,z] +u축 단위벡터(도면 좌표계)
ALTER TABLE ref.wall ADD COLUMN IF NOT EXISTS v_axis jsonb;   -- [x,y,z] +v축 단위벡터(U와 직교)

COMMIT;
