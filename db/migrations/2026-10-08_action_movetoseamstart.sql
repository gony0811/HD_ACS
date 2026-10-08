-- 2026-10-08: 신규 커스텀 액션 moveToSeamStart 등재 — VDA5050_INTERFACE_SPEC §8.
-- 코봇툴을 용접선 시작점(seamStartW, 맵 좌표)까지만 이동하는 reach 시험 액션(촬영 없음).
-- 발행 경로: POST /api/robots/{id}/test/seam-start { taskId } → InspectionDispatcher.MoveToSeamStartAsync.
-- 앱 기동 시 ActionCatalogSeed.EnsureAsync 가 동일 행을 멱등 upsert → 앱 바이너리 배포만으로도 반영.
-- 이 파일은 직접-접근 DB 용(멱등). ⚠️ param_schema 는 ActionCatalogSeed.MoveToSeamStartParamSchema 와 동일해야 함.
-- 적용: psql -U postgres -d hdacs -f /tmp/2026-10-08_action_movetoseamstart.sql

INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES ('moveToSeamStart', 'NODE', 'HARD',
'{
  "type": "object",
  "required": ["jobRef", "position"],
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
    }
  }
}',
        '코봇툴 seam 시작점 이동 시험(촬영 없음) [VDA §8]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
