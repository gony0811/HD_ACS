-- 2026-08-28: alarm.spec 시드 누락 수정 — INSPECTION_SKIPPED 미등재로
-- 디스패처 SKIPPED 알람 INSERT가 FK(alarm_alarm_code_fkey) 위반으로 실패하던 문제 (풀 E2E에서 발견).
-- idempotent (ON CONFLICT DO UPDATE).
-- 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-08-28_alarm_spec_seed.sql

INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('INSPECTION_SKIPPED', 'WARNING', '검사 스킵', '재시도 상한 초과로 검사 작업이 스킵됨 (디스패처 실패 정책)')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
