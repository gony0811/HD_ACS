-- 2026-08-21  VDA5050 액션 카탈로그 확장 — cobotHome / homeReturn
-- 사양: docs/VDA5050_ACTION_CATALOG.md (Phase 3 확장)
-- coarse 노드 액션(전부 NODE, HARD). 코봇 검사 서브스텝은 startWeldInspection 내부(HD_AMR 시퀀스)에서 실행.
-- '이동 전 코봇 안전자세' 인터록은 카탈로그 액션이 아니라 어댑터 암묵 강제(§2.1).

INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES
  ('cobotHome', 'NODE', 'HARD',
   '{ "type": "object", "properties": { "target": { "enum": ["HOME", "SAFE"] } } }',
   '코봇 안전(홈) 자세 복귀'),
  ('homeReturn', 'NODE', 'HARD',
   '{ "type": "object", "properties": { "dockId": { "type": "string" } } }',
   'AMR 도킹/홈 복귀')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
