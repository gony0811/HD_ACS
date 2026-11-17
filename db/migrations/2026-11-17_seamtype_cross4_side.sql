-- 2026-11-17: seamType 에 CROSS4_L · CROSS4_R 추가 [VDA §8.2, 1.22].
-- 면을 정면으로 마주 봤을 때 十자의 수직 용접선이 작업(가로 한 칸)의 왼쪽에 있으면 CROSS4_L, 오른쪽에 있으면 CROSS4_R.
-- 작업 시작/끝 기준이 아니다. 접미 없는 CROSS4 는 그대로 유효(AMR 이 면별 기본값으로 레시피를 고른다).
-- 앱 기동 시 ActionCatalogSeed 가 같은 내용을 적용하므로 직접 접근 DB용. 멱등.
-- 기존 CROSS4 작업의 L/R 은 바꾸지 않는다 — POST /api/tanks/{tankId}/cross4-sides?apply=true 로 판정·적용.

UPDATE ref.action_catalog
   SET param_schema = jsonb_set(param_schema, '{properties,params,properties,seamType,enum}',
       '["LINE","CROSS3_R0","CROSS3_R90","CROSS3_R180","CROSS3_R270","CROSS4","CROSS4_L","CROSS4_R","CORNER2","CORNER3"]'::jsonb)
 WHERE action_type = 'startWeldInspection'
   AND param_schema #> '{properties,params,properties,seamType}' IS NOT NULL;
