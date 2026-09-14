-- 2026-09-14: VDA5050_INTERFACE_SPEC §8.5.1 반영 — startWeldInspection param_schema의
-- seamType enum 을 ["LINE","POLYLINE"] → ["LINE","CROSS","CORNER"] 로 확장([협의 N13] 선반영).
-- 계획 UI(③ 검사 작업 등록)에서 운영자가 용접라인 형태(LINE/CROSS/CORNER)를 지정할 수 있게 되었고,
-- 그 값이 Order 발행 직전 param_schema 검증(WeldInspectionPayload)을 통과해야 배차가 가능하다.
-- POLYLINE 은 등록 게이트(POST /api/areas/{id}/tasks)에서 거부되므로 enum 에서 제거해 정합화.
-- CROSS/CORNER 는 HD_AMR 레시피 미확정 — 계획 데이터로 저장·전달만(실제 검사 미동작).
-- idempotent (ON CONFLICT DO UPDATE 재시드).
-- 적용: docker cp 후 컨테이너 안에서
--   psql -U postgres -d hdacs -f /tmp/2026-09-14_seamtype_cross_corner.sql
-- (또는 호스트 psql로 -h localhost -p 5432)

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
        "seamType":            { "enum": ["LINE", "CROSS", "CORNER"] },
        "points":              { "type": "array" },
        "sectionDxfId":        { "type": "string" },
        "inspectionProfileId": { "type": "string" },
        "standoffMm":          { "type": "number" },
        "workingDistanceMm":   { "type": "number" },
        "anchorGroupId":       { "type": "string" },
        "seqInGroup":          { "type": "integer", "minimum": 1 }
      }
    }
  }
}',
        '단일 용접라인 구간 자동 검사 [WP-3, SPEC §8.5.1 seamType LINE·CROSS·CORNER]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
