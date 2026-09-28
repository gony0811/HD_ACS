-- SAIGE 연동 1단계 [SAIGE 연동 사양서 v2.6 §9.5] — 로봇 상태 전송 실패 알람 코드.
-- 앱 기동 시드(AlarmSpecSeed)가 누락분을 자동 insert 하므로 현장은 바이너리 배포만으로 반영된다.
-- 이 파일은 DB 직접 접근 환경용(schema.sql 시드와 동일 내용 유지).
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('SAIGE_UNREACHABLE', 'WARNING', 'SAIGE 전송 불가', '로봇 상태(robot-health-check) 전송이 임계 횟수 이상 연속 실패 — SAIGE 기동/네트워크 확인 [SAIGE §9.5]'),
  ('SAIGE_BAD_REQUEST', 'WARNING', 'SAIGE 형식 오류', 'SAIGE가 로봇 상태 페이로드를 형식 오류(40001)로 거부 — 재시도 없이 폐기됨, 규격 불일치 확인 [SAIGE §9.5]')
ON CONFLICT (alarm_code) DO NOTHING;
