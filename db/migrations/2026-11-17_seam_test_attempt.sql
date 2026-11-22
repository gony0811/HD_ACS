-- 2026-11-17: 코봇 이동 시험(moveToSeamStart)에도 taskId 별 누적 attempt 발급 [VDA §8.7, 1.21].
-- 작업자가 시험 자세에서 검사 프로필·시퀀스 화면으로 수동 촬영할 때 AMR 이 이 taskId·attempt 를 비전 CAPTURE_REQ 에 싣는다.
-- 시험 발행 기록(run.seam_test)을 남겨 다음 시도(본검사·시험 공통)가 그보다 큰 attempt 를 받게 한다 —
-- 본검사가 나중에 다시 촬영하면 그 결과가 세이지에서 최신이 된다.
-- 앱 기동 시 SchemaEnsure(테이블)·ActionCatalogSeed(param_schema)가 같은 내용을 적용하므로 직접 접근 DB용. 멱등.

CREATE TABLE IF NOT EXISTS run.seam_test (
  test_id    uuid PRIMARY KEY,
  task_id    uuid NOT NULL,
  robot_id   text NOT NULL,
  order_id   text NOT NULL,
  attempt    int  NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_seam_test_task ON run.seam_test (task_id);

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
        "taskId":       { "type": "string", "format": "uuid" },
        "attempt":      { "type": "integer", "minimum": 1, "maximum": 255 }
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
