-- HD_AMR 통신 단절 Fail-Safe 사양(rev.0) ACS 측 구현 — 2026-10-12
-- 기동 시드(AlarmSpecSeed)가 멱등 적용하므로 본 스크립트는 직접 접근 DB용 수동 패치다.
-- 유지보수: AlarmSpecSeed · db/schema.sql 과 세 곳 동기.

INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('COMM_LOSS_PAUSED', 'WARNING', '통신 단절 일시정지 — 재개 확인 필요',
    'Order 수행 중 통신 단절로 AMR이 로컬 일시정지(PAUSE/HOLD) — 실패 아님, 자동 재개 없음. 현장 확인 후 재개(stopPause) 또는 run 중단 [HD_AMR 통신단절 Fail-Safe §6]'),
  ('RESUME_REJECTED', 'WARNING', '재개 거부',
    'AMR이 재개 요청을 거부(비상정지·배터리·측위/mapId·운전 모드 중 불충족) — 사유 해소 후 다시 재개 [HD_AMR 통신단절 Fail-Safe §4.2]')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
