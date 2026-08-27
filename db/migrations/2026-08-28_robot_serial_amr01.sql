-- 2026-08-28: 로봇 serial_number 정렬 — DB(SN-01)가 시뮬레이터 기본값·MANUAL §4.3(AMR-01)과 달라
-- 무인자 시뮬레이터가 App 구독 토픽(uagv/v2/HHI/SN-01/…)에 닿지 못하던 문제 해소.
-- serial을 AMR-01로 통일(문서·코드·DB 3자 일치). 적용 후 App 재기동 필요(구독은 기동 시 1회).
-- 적용: docker cp 후 psql -U postgres -d hdacs -f /tmp/2026-08-28_robot_serial_amr01.sql

UPDATE ref.robot SET serial_number = 'AMR-01' WHERE robot_id = 'AMR-01' AND serial_number = 'SN-01';

-- 재현성: 빈 DB에도 로봇이 존재하도록 시드 (schema.sql과 동일)
INSERT INTO ref.robot (robot_id, name, manufacturer, serial_number) VALUES
  ('AMR-01', 'HD AMR #1', 'HHI', 'AMR-01')
ON CONFLICT (robot_id) DO UPDATE SET
  manufacturer = EXCLUDED.manufacturer, serial_number = EXCLUDED.serial_number;
