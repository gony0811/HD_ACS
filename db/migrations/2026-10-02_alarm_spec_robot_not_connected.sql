-- 2026-10-02: AMR 미연결 상태의 미션 시작·이어하기 차단 알람.
-- 앱 기동 시드(AlarmSpecSeed)도 같은 행을 넣으므로 현장은 앱 배포만으로 충분 — 직접 접근 DB용.
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('ROBOT_NOT_CONNECTED', 'WARNING', 'AMR 미연결',
   'AMR 미연결(connection ≠ ONLINE 또는 state 수신 끊김) 상태에서 미션 시작·이어하기 요청 — 명령을 보내지 않고 차단됨. HD_AMR 실행·MQTT 연결 확인')
ON CONFLICT (alarm_code) DO NOTHING;
