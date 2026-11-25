-- 2026-11-25: AMR 주행 경고 알람 [VDA §6.4 amrDriveWarning]
-- HD_AMR 이 주행 중 주행부 경고(W 코드)를 받으면 주행을 끝내지 않고 state.errors[] 에 amrDriveWarning(WARNING)을 싣는다.
-- 기동 시드(AlarmSpecSeed)가 넣으므로 수동 적용은 필요 없다(직접 접근 DB 용).
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('AMR_DRIVE_WARNING', 'WARNING', 'AMR 주행 경고 — 주행 계속',
    'AMR 주행부가 주행 중 경고(W 코드, 예 W07_001)를 보고 — 실패 아님. AMR 은 주행을 끝내지 않고 도착을 기다린다. 경고 뒤 30초 동안 위치·방향이 그대로면 AMR 이 주행 실패로 종결한다. 같은 경고가 반복되면 AMR 주행부 담당에 코드 의미를 문의할 것 [VDA §6.4 amrDriveWarning]')
ON CONFLICT (alarm_code) DO UPDATE
  SET severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
