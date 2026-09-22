-- VDA5050_INTERFACE_SPEC 개정 1.4 (2026-09-21) — startWeldInspection params 에 taskId·attempt 추가.
--   taskId  : 용접선 1구간의 영구 식별자(GUID 문자열) — AMR이 CAPTURE_REQ에 실어 SAIGE productId가 된다.
--   attempt : taskId별 누적 시도 번호(1~255, ACS 발급 — 발행 시점).
-- 둘 다 선택 필드(required 미포함) — 구버전 AMR 호환 + attempt는 큐 전개 시 검증 payload에 없기 때문.
-- 앱 기동 시드(ActionCatalogSeed)가 동일 내용을 멱등 upsert 하므로 현장은 바이너리 배포만으로 반영된다.
-- ⚠️ schema.sql · ActionCatalogSeed.cs · 이 파일 3곳은 동일 내용 유지.

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
        "seamType":            { "enum": ["LINE", "CROSS3", "CROSS4", "CORNER2", "CORNER3"] },
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
        '단일 용접라인 구간 자동 검사 [WP-3, SPEC §8.5.1 seamType 5종 카탈로그 1:1]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
