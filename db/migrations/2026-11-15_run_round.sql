-- 가스검사 회차(선창 전체 검사 1|2) — run 단위 값. 가스검사 선창의 지령 params.round 로 나간다.
-- 앱 기동 시 SchemaEnsure(컬럼)·ActionCatalogSeed(param_schema)가 같은 내용을 넣으므로 수동 적용은 필요 없다.
ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS round int;

UPDATE ref.action_catalog
   SET param_schema = jsonb_set(param_schema, '{properties,params,properties,round}', '{"enum":[1,2]}'::jsonb)
 WHERE action_type = 'startWeldInspection';
