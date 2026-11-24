-- 가스검사: 구역(영역)별 검사 횟수 → 지령 params.round (1부터, NULL = 용접검사)
-- 앱 기동 시 SchemaEnsure 가 같은 구문을 실행하므로 수동 적용은 필요 없다.
ALTER TABLE run.work_item ADD COLUMN IF NOT EXISTS round int;
