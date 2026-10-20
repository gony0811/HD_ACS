-- HD_AMR 장치 통신 HEAVY 알람 사양(rev.0) ACS 측 구현 — 2026-10-13
-- 기동 시드(AlarmSpecSeed)가 멱등 적용하므로 본 스크립트는 직접 접근 DB용 수동 패치다.
-- 유지보수: AlarmSpecSeed · db/schema.sql 과 세 곳 동기.
-- 사양의 HEAVY 등급은 ACS 등급 체계(INFO|WARNING|CRITICAL)에 없어 CRITICAL로 매핑(detail.grade="HEAVY").

INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('DEVICE_LINK_LOST', 'CRITICAL', '장치 통신 두절(HEAVY) — 현장 RESET 필요',
    '장치 6종(비전·Depth·레이저·I/O·코봇·AMR) 중 통신 두절 — AMR이 HEAVY 래치·임무 중단. errors[]에서 사라질 때(현장 RESET)까지 배차 보류, 해제 시 자동 재개 [HD_AMR 장치통신 HEAVY알람 §5]'),
  ('ORDER_REJECTED_DEVICE_FAULT', 'WARNING', '장치 알람 — Order 거부',
    'AMR이 장치 HEAVY 알람 중 Order 거부(WARNING, 기존 orderId 유지) — 실행 없음, ACS는 정차를 PENDING 복귀 후 RESET 뒤 재배차 [HD_AMR 장치통신 HEAVY알람 §4.2]')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
