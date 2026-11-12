-- 용접선(검사 작업) 임시 제외 — enabled = false 면 큐 전개·재개·재검사에서 싣지 않는다.
-- 앱 기동 시 SchemaEnsure 가 같은 문을 실행하므로 수동 적용은 필요 없다(직접 접근 DB용).
ALTER TABLE ref.area_task ADD COLUMN IF NOT EXISTS enabled boolean NOT NULL DEFAULT true;
