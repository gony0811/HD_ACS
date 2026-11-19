-- 2026-11-19: HD_AMR 교시 위치 사본 — 리치 시뮬레이터 출발 자세(②)의 정본을 AMR 값으로.
-- 앱 기동 시 SchemaEnsure 가 같은 문장을 실행하므로 수동 적용은 필요 없다(직접 접근 DB 용).
CREATE TABLE IF NOT EXISTS ref.amr_teaching (
  source             text PRIMARY KEY,
  teaching_positions jsonb NOT NULL DEFAULT '[]',
  start_joints       jsonb NOT NULL DEFAULT '{}',
  content_hash       text NOT NULL DEFAULT '',
  received_at        timestamptz NOT NULL DEFAULT now(),
  changed_at         timestamptz NOT NULL DEFAULT now()
);
