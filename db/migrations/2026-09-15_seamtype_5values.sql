-- 2026-09-15: VDA5050_INTERFACE_SPEC §8.5.1 반영 — startWeldInspection param_schema 의
-- seamType enum 을 ["LINE","CROSS","CORNER"] → ["LINE","CROSS3","CROSS4","CORNER2","CORNER3"] 로 확장.
-- 목적: seamType 을 검사 타입 카탈로그와 **1:1(5종)** 로 세분화([협의 N13]).
--   LINE   = 직선 용접선 구간
--   CROSS3 = 3갈래 교차(T/Y자 — 용접선 3개 교차)
--   CROSS4 = 4갈래 교차(十자 — 용접선 4개 교차)
--   CORNER2= 2면 코너(이면각 모서리 — 두 면 접합)
--   CORNER3= 3면 코너(삼면 접합)
-- 계획 UI(③ 검사 작업 등록) seamType 드롭다운도 이 5값으로 확장되며, 그 값이 Order 발행 직전
-- param_schema 검증(WeldInspectionPayload)을 통과해야 배차가 가능하다.
-- CROSS*/CORNER* 는 HD_AMR 레시피 미확정 — 계획 데이터로 저장·전달만(실제 검사 미동작).
-- POLYLINE 은 등록 게이트(POST /api/areas/{id}/tasks)에서 계속 거부.
-- idempotent (ON CONFLICT DO UPDATE 재시드).
-- 적용: docker cp 후 컨테이너 안에서
--   psql -U postgres -d hdacs -f /tmp/2026-09-15_seamtype_5values.sql
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
        "seamType":            { "enum": ["LINE", "CROSS3", "CROSS4", "CORNER2", "CORNER3"] },
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
        '단일 용접라인 구간 자동 검사 [WP-3, SPEC §8.5.1 seamType 5종 카탈로그 1:1]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
