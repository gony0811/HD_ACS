-- 2026-11-12: 코봇 반경 장애물 알람 [VDA §6.4 cobotZoneObstacle]
-- HD_AMR이 주행 중 코봇 반경 안의 장애물을 감지해 선 동안 state.errors[]에 cobotZoneObstacle(WARNING)을 싣는다.
-- 앱 기동 시 AlarmSpecSeed 가 같은 내용을 넣으므로 수동 적용은 직접 접근 DB 용이다.
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('COBOT_ZONE_OBSTACLE', 'WARNING', '코봇 반경 장애물 — AMR 주행 정지',
    'AMR이 주행 중 라이다로 코봇 반경 안의 장애물을 감지해 그 자리에 정지(타워램프 황색·부저) — 실패 아님. 장애물이 사라지면 AMR이 같은 목표로 다시 출발하고 알람이 해제된다. 주행 제한 시간까지 안 사라지면 정차가 실패로 보고된다 [VDA §6.4 cobotZoneObstacle, HD_AMR 파라미터 cobotGuard.enabled]')
ON CONFLICT (alarm_code) DO UPDATE
  SET severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
