-- ═══════════════════════════════════════════════════════════════════════════
-- HD_ACS 전체 스키마 재적용 스크립트 (파괴적 — 기존 데이터 전부 삭제)
--
-- 목적: 스키마 드리프트(예: `42P01 relation "ref.robot" does not exist`) 복구.
-- 이 앱은 자동 마이그레이션을 하지 않으므로 DB를 최신 정의로 재구성한다.
--
-- 구성: db/schema.sql 은 이미 대부분의 마이그레이션(corners·tank_geometry·
--       reach_z 등)이 통합된 최신본이다. schema.sql 에 유일하게 빠진 것은
--       run.work_item(2026-08-10) 뿐이므로 그 마이그레이션만 추가로 적용한다.
--       (그 외 db/migrations/*.sql 는 이미 schema.sql 에 반영되어 재적용 시 충돌)
--
-- 사용법 (반드시 db/ 디렉터리에서 실행 — \i 상대경로):
--   cd db
--   psql "Host=...;Database=hdacs;Username=postgres;Password=..." -f apply_full.sql
--   또는:  psql -h localhost -U postgres -d hdacs -f apply_full.sql
--
-- 경고: 아래 DROP SCHEMA 는 ref/run/hist/alarm/sys 의 모든 데이터를 삭제한다.
-- ═══════════════════════════════════════════════════════════════════════════

\set ON_ERROR_STOP on

BEGIN;
DROP SCHEMA IF EXISTS ref, run, hist, alarm, sys CASCADE;
COMMIT;

\echo '>> schema.sql 적용'
\i schema.sql

\echo '>> migrations/2026-08-10_work_item.sql 적용'
\i migrations/2026-08-10_work_item.sql

\echo '>> 재적용 완료. 핵심 릴레이션 확인:'
SELECT to_regclass('ref.robot')          AS ref_robot,
       to_regclass('run.work_item')      AS run_work_item,
       to_regclass('ref.tank_geometry')  AS ref_tank_geometry;
