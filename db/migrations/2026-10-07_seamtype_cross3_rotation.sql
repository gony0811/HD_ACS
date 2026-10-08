-- 2026-10-07: VDA5050_INTERFACE_SPEC 개정(§8.2/§8.5.1, [N13 연장]) 반영 —
-- startWeldInspection param_schema 의 seamType enum 을 CROSS3 회전 4종으로 확장.
--   이전: ["LINE","CROSS3","CROSS4","CORNER2","CORNER3"]  (5종)
--   이후: ["LINE","CROSS3_R0","CROSS3_R90","CROSS3_R180","CROSS3_R270","CROSS4","CORNER2","CORNER3"]  (8종)
-- 배경: AMR 측이 CROSS3(T자 3갈래)를 면 자세별 5종에서 **회전별 4종**(0/90/180/270, 면 자세 무관)으로 재설계.
--   AMR 파서는 legacy bare "CROSS3"→"CROSS3_R0"(+ "CROSS"→"CROSS4", "CORNER"→"CORNER3")도 수용하나,
--   ACS 는 canonical 값으로 발행 전환해야 한다(§10 N13). 저장 정규화는 AreaTaskRules.Normalize.
--
-- 이 파일은 2가지를 한다:
--   (1) 기존 데이터 정규화 — ref.area_task 의 legacy seam_type 을 canonical 로 1회 치환.
--       (안 하면 과거 CROSS3/CROSS/CORNER 작업이 run 시작 때 새 enum 위반으로 막힌다.)
--   (2) action_catalog param_schema 재시드(계약) — 직접-접근 DB 용. 앱 기동 시 ActionCatalogSeed 가
--       동일 내용을 멱등 적용하므로, 앱 바이너리 배포만으로도 (2)는 반영된다. (1)은 데이터라 이 SQL 필요.
--
-- 적용: docker cp 후 컨테이너 안에서
--   psql -U postgres -d hdacs -f /tmp/2026-10-07_seamtype_cross3_rotation.sql
-- idempotent — 반복 실행해도 안전(이미 canonical 이면 UPDATE 0건, param_schema 는 재시드).

-- (1) 기존 area_task 데이터 정규화 (대소문자 무시)
UPDATE ref.area_task SET seam_type = 'CROSS3_R0' WHERE upper(seam_type) = 'CROSS3';
UPDATE ref.area_task SET seam_type = 'CROSS4'    WHERE upper(seam_type) = 'CROSS';
UPDATE ref.area_task SET seam_type = 'CORNER3'   WHERE upper(seam_type) = 'CORNER';
-- 대소문자만 다른 canonical 값도 대문자로 정렬(소문자 저장분 방지)
UPDATE ref.area_task SET seam_type = upper(seam_type)
 WHERE seam_type <> upper(seam_type)
   AND upper(seam_type) IN ('LINE','CROSS3_R0','CROSS3_R90','CROSS3_R180','CROSS3_R270','CROSS4','CORNER2','CORNER3');

-- (2) param_schema 재시드 (계약 — ActionCatalogSeed 와 동일 내용: taskId·attempt 포함)
INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES ('startWeldInspection', 'NODE', 'HARD',
'{
  "type": "object",
  "required": ["jobRef", "position", "params"],
  "properties": {
    "jobRef": { "type": "string" },
    "position": {
      "type": "object",
      "required": ["seamStartW", "seamEndW", "drawingPos"],
      "properties": {
        "seamStartW":  { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "seamEndW":    { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "drawingPos": {
          "type": "object",
          "required": ["tank", "level", "wall_code", "u", "v", "x", "y", "z"],
          "properties": {
            "tank": { "type": "string" }, "level": { "type": "integer" },
            "wall_code": { "type": "string" },
            "u": { "type": "number" }, "v": { "type": "number" },
            "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" }
          }
        }
      }
    },
    "params": {
      "type": "object",
      "required": ["seamType", "sectionDxfId", "inspectionProfileId", "standoffMm", "anchorGroupId", "seqInGroup"],
      "properties": {
        "seamType":            { "enum": ["LINE", "CROSS3_R0", "CROSS3_R90", "CROSS3_R180", "CROSS3_R270", "CROSS4", "CORNER2", "CORNER3"] },
        "points":              { "type": "array" },
        "sectionDxfId":        { "type": "string" },
        "inspectionProfileId": { "type": "string" },
        "standoffMm":          { "type": "number" },
        "workingDistanceMm":   { "type": "number" },
        "anchorGroupId":       { "type": "string" },
        "seqInGroup":          { "type": "integer", "minimum": 1 },
        "taskId":              { "type": "string", "format": "uuid" },
        "attempt":             { "type": "integer", "minimum": 1, "maximum": 255 }
      }
    }
  }
}',
        '단일 용접라인 구간 자동 검사 [WP-3, SPEC §8.2/§8.5.1 seamType CROSS3 회전 4종]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
