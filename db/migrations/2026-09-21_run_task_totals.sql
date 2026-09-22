-- SAIGE 연동 2단계 [SAIGE 연동 사양서 v2.6 §6.1/§6.4] — 진행률 분모를 run 시작 시점에 고정.
-- total_tasks: 큐에 전개된 TASK 총수(분모). NULL = 이 컬럼 도입 전 run → 앱이 work_item.actions 에서 폴백 산출.
-- excluded_tasks: 유효 T_W_D 부재 등으로 검사 큐에서 제외된 TASK 수(분모 미포함, 별도 보고).
-- 앱 기동 시 SchemaEnsure 가 동일 구문(IF NOT EXISTS)을 멱등 실행하므로 현장은 바이너리 배포만으로 반영된다.
ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS total_tasks    int;
ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS excluded_tasks int NOT NULL DEFAULT 0;
