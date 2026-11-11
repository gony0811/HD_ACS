-- AMR 층 불일치 경고 알람 — AMR 보고 층(mapId)과 작업(Order) 층이 달라 작업 지시를 보류할 때 기록.
-- 유지보수: AlarmSpecSeed · db/schema.sql 과 세 곳 동기. 앱 기동 시드(AlarmSpecSeed)가 같은 행을 넣으므로
-- 앱을 새 버전으로 올리면 이 파일을 따로 적용하지 않아도 된다(직접 접근 DB용).

INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('FLOOR_MISMATCH', 'WARNING', 'AMR 층 불일치 — 작업 지시 보류',
    '미션 시작·이어하기·다음 층 릴리스 때 AMR 보고 층(mapId)에 보낼 작업이 없음(작업이 다른 층에만 남았거나 AMR이 층을 보고하지 않음) — Order를 보내지 않음. AMR을 작업 층으로 옮기고 수동 층 변경 → 다음 층 릴리스')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
