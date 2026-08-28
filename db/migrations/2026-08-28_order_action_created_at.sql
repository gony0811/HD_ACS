-- 2026-08-28: run.order_action.created_at 추가 — 용접라인(액션) 현황 드릴다운에서
-- 재시도 시 동일 task_id 액션들의 최신 판별용. 비파괴·idempotent.
-- 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-08-28_order_action_created_at.sql

ALTER TABLE run.order_action
  ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
