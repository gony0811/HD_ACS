-- 2026-11-17: moveToSeamStart(코봇 seam 시작점 이동 시험) param_schema 에 params.taskId 추가 [VDA §8.7].
-- 시험 Order 에도 본검사(startWeldInspection)와 같은 표기의 대상 용접선 taskId 를 싣는다(종전엔 jobRef "TEST-SEAM-{taskId}" 문자열에만 있었다).
-- 선택 필드(구 발행분 호환). 멱등(ON CONFLICT). 앱 기동 시 ActionCatalogSeed 가 같은 값을 적용하므로 직접 접근 DB용.

INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES ('moveToSeamStart', 'NODE', 'HARD',
'{
  "type": "object",
  "required": ["jobRef", "position"],
  "properties": {
    "jobRef": { "type": "string" },
    "params": {
      "type": "object",
      "properties": {
        "liftStrokeMm": { "type": "number", "minimum": 0, "maximum": 1000 },
        "taskId":       { "type": "string", "format": "uuid" }
      }
    },
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
    }
  }
}',
        '코봇툴 seam 시작점 이동 시험(촬영 없음) [VDA §8]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
