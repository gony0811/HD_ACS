-- 리페어 run — 용접선(TASK)을 지정해 그것만 검사하는 run.
-- task_ids: 검사 대상 taskId json 배열. NULL = 일반 run(시나리오 영역 전체).
-- 앱 기동 시 SchemaEnsure 가 같은 구문을 실행하므로 수동 적용은 선택 사항이다.
ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS task_ids jsonb;
