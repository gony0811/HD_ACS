-- 2026-11-27: 십자(CROSS4) 유형을 좌/우 2종 → T1~T4 4종으로 [VDA §8.2, 1.23].
-- 기존 CROSS4_L = CROSS4_T1, CROSS4_R = CROSS4_T2 로 1:1 흡수. T3·T4 는 신규 형상. 접미 없는 CROSS4 는 그대로 유효(AMR 이 면별 기본값으로 T1/T2).
-- 앱 기동 시 ActionCatalogSeed 가 카탈로그 enum 을 같은 내용으로 적용하므로 직접 접근 DB용. 전부 멱등(재실행해도 변화 없음).

-- (1) 카탈로그 param_schema enum
UPDATE ref.action_catalog
   SET param_schema = jsonb_set(param_schema, '{properties,params,properties,seamType,enum}',
       '["LINE","CROSS3_R0","CROSS3_R90","CROSS3_R180","CROSS3_R270","CROSS4","CROSS4_T1","CROSS4_T2","CROSS4_T3","CROSS4_T4","CORNER2","CORNER3"]'::jsonb)
 WHERE action_type = 'startWeldInspection'
   AND param_schema #> '{properties,params,properties,seamType}' IS NOT NULL;

-- (2) 저장된 seamType (구역 작업 규칙·용접선·프로필)
UPDATE ref.area_task SET seam_type = 'CROSS4_T1' WHERE upper(seam_type) = 'CROSS4_L';
UPDATE ref.area_task SET seam_type = 'CROSS4_T2' WHERE upper(seam_type) = 'CROSS4_R';
UPDATE ref.weld_seam SET seam_type = 'CROSS4_T1' WHERE upper(seam_type) = 'CROSS4_L';
UPDATE ref.weld_seam SET seam_type = 'CROSS4_T2' WHERE upper(seam_type) = 'CROSS4_R';
UPDATE ref.inspection_profile SET seam_type = 'CROSS4_T1' WHERE upper(seam_type) = 'CROSS4_L';
UPDATE ref.inspection_profile SET seam_type = 'CROSS4_T2' WHERE upper(seam_type) = 'CROSS4_R';

-- (3) 프로필 지정표 — PK (source, seam_type, wall_code, direction) 라 이미 T1/T2 지정이 있으면 구 행은 옮기지 않고 지운다(신 지정 우선).
UPDATE ref.inspection_profile_assignment a SET seam_type = 'CROSS4_T1'
 WHERE upper(a.seam_type) = 'CROSS4_L'
   AND NOT EXISTS (SELECT 1 FROM ref.inspection_profile_assignment b
                    WHERE b.source = a.source AND b.seam_type = 'CROSS4_T1' AND b.wall_code = a.wall_code AND b.direction = a.direction);
UPDATE ref.inspection_profile_assignment a SET seam_type = 'CROSS4_T2'
 WHERE upper(a.seam_type) = 'CROSS4_R'
   AND NOT EXISTS (SELECT 1 FROM ref.inspection_profile_assignment b
                    WHERE b.source = a.source AND b.seam_type = 'CROSS4_T2' AND b.wall_code = a.wall_code AND b.direction = a.direction);
DELETE FROM ref.inspection_profile_assignment WHERE upper(seam_type) IN ('CROSS4_L', 'CROSS4_R');
