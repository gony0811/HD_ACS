-- 2026-08-27  startWeldInspection 액션 카탈로그 계약 간소화 [VDA5050_INTERFACE §6]
-- 배경: AMR로 전달하는 검사 액션 파라미터를 4+1개 flat 필드로 재정의.
--   (구) jobRef + position{seamStartW,seamEndW,drawingPos} + params{seamType,sectionDxfId,inspectionProfileId,
--        standoffMm,anchorGroupId,seqInGroup}  → 앵커 그룹/프로필 계약 은퇴.
--   (신) wallId(면 코드) · seamStart/seamEnd(맵 좌표 [x,y,z] m) · orientation(H|V) · patternType(디폴트 LINEAR).
--        툴 자세·법선은 AMR이 면 티칭으로 결정(ACS는 위치·방향·도면타입만 전달).
-- 적용: psql -U postgres -d hdacs -f db/migrations/2026-08-27_startweld_action_schema.sql

INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description)
VALUES ('startWeldInspection', 'NODE', 'HARD',
'{
  "type": "object",
  "required": ["wallId", "seamStart", "seamEnd", "orientation", "patternType"],
  "properties": {
    "wallId":      { "type": "string" },
    "seamStart":   { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
    "seamEnd":     { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
    "orientation": { "enum": ["H", "V"] },
    "patternType": { "enum": ["LINEAR"] }
  }
}',
        '단일 용접라인 검사 [VDA5050_INTERFACE §6]')
ON CONFLICT (action_type) DO UPDATE SET
  param_schema  = EXCLUDED.param_schema,
  scope         = EXCLUDED.scope,
  blocking_type = EXCLUDED.blocking_type,
  description   = EXCLUDED.description;
