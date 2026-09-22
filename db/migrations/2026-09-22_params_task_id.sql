-- 2026-09-22: VDA5050_INTERFACE_SPEC §8.1/§8.2 반영 — startWeldInspection params 에
-- **taskId(계획 TASK 불변 키)** 추가. ACS 선반영(HD_AMR 회신 전, [협의 N14]).
--
-- 배경: 종전 계약의 식별자는 actionId(배차마다 재발급 = 실행 인스턴스)와
-- jobRef(= JOB-{tank}-L{level}-{wall}-{영역명}-{seq} — 영역 이름/순번 변경 시 값이 바뀜)뿐이라
-- 계획 TASK(ref.area_task.task_id)를 시간이 지나도 동일하게 가리킬 키가 AMR/검사 S/W 쪽에 없었다.
-- ACS 내부는 run.order_action.task_id 로 역추적되지만 그 값은 발행되지 않았다.
--
-- taskId 는 **required 아님(선택 필드)** — 기존 AMR 파서는 무시하면 되고, 계약 위반이 아니다.
-- 값 = ref.area_task.task_id (uuid 문자열). ACS는 이번 변경부터 항상 채워 발행한다.
-- 소비(검사 결과 대조 키 채택)는 N14 확정 후 HD_AMR/검사 S/W 2차 연동.
--
-- ⚠️ 이 param_schema 는 db/schema.sql · src/HD.Acs.App/Services/ActionCatalogSeed.cs 와 동일 내용이어야 한다.
--    앱 기동 시 ActionCatalogSeed 가 같은 값을 멱등 upsert 하므로, 앱 배포만으로도 반영된다
--    (이 파일은 DB에 직접 접근하는 배포/점검용).
-- idempotent (ON CONFLICT DO UPDATE 재시드).
-- 적용: docker cp 후 컨테이너 안에서
--   psql -U postgres -d hdacs -f /tmp/2026-09-22_params_task_id.sql
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
        "taskId":              { "type": "string" },
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
        '단일 용접라인 구간 자동 검사 [WP-3]')
ON CONFLICT (action_type) DO UPDATE
  SET param_schema = EXCLUDED.param_schema,
      scope        = EXCLUDED.scope,
      blocking_type= EXCLUDED.blocking_type;

-- 확인: params.taskId 속성이 들어갔는지
-- SELECT param_schema->'properties'->'params'->'properties'->'taskId'
--   FROM ref.action_catalog WHERE action_type = 'startWeldInspection';
