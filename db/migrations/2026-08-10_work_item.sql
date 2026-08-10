-- 검사 작업 큐 [greedy 최근접 배차] — run 스코프. 시작 시 선창 영역에서 전개.
-- 한 항목 = 영역 1개(정차 1곳) + 그 영역 작업의 액션 payload. 좌표는 맵 프레임(로봇 보고와 동일).
CREATE TABLE IF NOT EXISTS run.work_item (
  work_item_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id       uuid NOT NULL REFERENCES run.scenario_run ON DELETE CASCADE,
  area_id      uuid NOT NULL,                 -- 원본 ref.inspection_area (이력 대조용, FK 미설정)
  map_id       text NOT NULL,                 -- 층 (예: CT1-L2) — 큐 필터 키
  x            double precision NOT NULL,      -- 맵 프레임 정차 x
  y            double precision NOT NULL,      -- 맵 프레임 정차 y
  theta        double precision,               -- 맵 프레임 정차각
  seq          int NOT NULL DEFAULT 0,         -- 안정 정렬/동률 tiebreak
  status       text NOT NULL DEFAULT 'PENDING',-- PENDING | DISPATCHED | DONE | FAILED | SKIPPED
  attempts     int NOT NULL DEFAULT 0,
  actions      jsonb,                          -- 이 정차의 액션 payload 배열(발행 시 사용)
  order_id     text,                           -- 현재/최근 배차 orderId (state 대조)
  updated_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_work_item_run_map_status ON run.work_item (run_id, map_id, status);

-- greedy 배차 시 order_action ↔ work_item 연결 (정차 완료/실패 집계 키)
ALTER TABLE run.order_action ADD COLUMN IF NOT EXISTS work_item_id uuid;
