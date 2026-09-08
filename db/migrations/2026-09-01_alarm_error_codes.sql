-- 2026-09-01: errors[] 유형별 소비 구현에 따른 알람 코드 시드 추가 [VDA 사양서 §4.5.2/§6.4].
-- idempotent. 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-09-01_alarm_error_codes.sql

INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('ORDER_REJECTED',    'WARNING', 'Order 거부', 'AMR이 Order 검증 실패로 폐기(orderValidationError) — 실패 정책 적용됨 [§4.5.2]'),
  ('LOCALIZATION_LOST', 'WARNING', '측위 상실', '맵 일치율 저하·재측위 실패 — 재시도 무의미, 재측위/수동 개입 필요 [§6.4]'),
  ('EQUIPMENT_ERROR',   'WARNING', '장비 이상', '코봇/카메라 등 온보드 장비 이상 보고 [§6.4]'),
  ('BATTERY_LOW',       'WARNING', '배터리 부족', 'AMR 배터리 부족 보고 [§6.4]'),
  ('EMERGENCY_STOP',    'WARNING', '비상정지 중', 'AMR측 기능 정지(emergencyStopActive) 보고 — 활성 run 자동 중단됨 [§6.4]')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
