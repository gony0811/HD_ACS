-- 가스 측정 결과 저장 + 세이지 전송 상태. 앱 기동 시 SchemaEnsure 가 같은 구문을 실행하므로 수동 적용은 필요 없다.
CREATE TABLE IF NOT EXISTS hist.gas_result (
  result_id     uuid PRIMARY KEY,
  action_id     uuid NOT NULL,
  run_id        uuid NOT NULL,
  task_id       uuid,
  tank_id       text,
  robot_id      text NOT NULL,
  round         int,
  result        text NOT NULL,
  point_count   int  NOT NULL DEFAULT 0,
  body          jsonb NOT NULL,
  send_state    text NOT NULL DEFAULT 'PENDING',
  send_attempts int  NOT NULL DEFAULT 0,
  last_error    text,
  created_at    timestamptz NOT NULL DEFAULT now(),
  sent_at       timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gas_result_action ON hist.gas_result (action_id);
CREATE INDEX IF NOT EXISTS ix_gas_result_send ON hist.gas_result (send_state, created_at);
CREATE INDEX IF NOT EXISTS ix_gas_result_run ON hist.gas_result (run_id);
