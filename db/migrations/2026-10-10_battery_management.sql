-- HD_AMR 배터리 관리 사양 ACS 측 구현 — 2026-10-10
-- 기동 시드(AlarmSpecSeed · ActionCatalogSeed)가 멱등 적용하므로 본 스크립트는 직접 접근 DB용 수동 패치다.

-- ① 알람 코드 — 2행 추가(기존 BATTERY_LOW는 그대로 유지)
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('BATTERY_CRITICAL', 'CRITICAL', '배터리 위험',
    'AMR 배터리 임계(≤10%) — 그 자리 안전정지 보고 [HD_AMR 배터리관리 §2]'),
  ('ORDER_REJECTED_BATTERY_LOW', 'WARNING', '배터리 저전력 — Order 거부',
    'AMR이 저전력 상태에서 작업 Order를 거부(WARNING, 실패 아님) — 교체 장소로 보내거나 교체 완료 후 재시도 [HD_AMR 배터리관리 §8]'),
  ('BATTERY_SWAP_DISPATCHED', 'INFO', '배터리 교체 이동 발행',
    '운영자가 배터리 교체 장소로 이동 Order를 수동 발행 — 활성 run이 있으면 함께 중단됨, 교체 완료 후 이어하기로 재배차')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;

-- ② batterySwapMove 액션 — ActionCatalogSeed canonical과 동일 내용 (유지보수: 세 곳 동기)
INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description) VALUES
  ('batterySwapMove', 'NODE', 'HARD',
    '{
      "type": "object",
      "required": ["targetNodeId", "mapId"],
      "properties": {
        "targetNodeId": { "type": "string" },
        "mapId":        { "type": "string" },
        "reason":       { "enum": ["LOW", "CRITICAL", "MANUAL"] }
      }
    }'::jsonb,
    '현재 층 배터리 교체 장소로 이동(핫스왑 전제) [HD_AMR 배터리관리 §6]')
ON CONFLICT (action_type) DO UPDATE SET
  scope = EXCLUDED.scope, blocking_type = EXCLUDED.blocking_type,
  param_schema = EXCLUDED.param_schema, description = EXCLUDED.description;

-- ③ ref.node.node_type 열거값에 BATTERY_SWAP 추가 — 컬럼이 text라 스키마 변경 없음, 데이터만.
-- 등록은 서비스 레이어(BatterySwapService)가 담당.
