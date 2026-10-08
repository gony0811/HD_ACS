-- 2026-10-02: 자동 재시도 폐지 — 정차 검사 실패 시 INSPECTION_FAILED 알람(용접선별 실패 사유 포함).
-- 앱 기동 시드(AlarmSpecSeed)도 같은 행을 넣으므로 현장은 앱 배포만으로 충분 — 직접 접근 DB용.
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('INSPECTION_FAILED', 'WARNING', '검사 실패',
   '정차 검사(용접선) 실패 — 자동 재시도하지 않음. detail.items에 용접선별 실패 사유, 재실행 여부는 작업자가 결정')
ON CONFLICT (alarm_code) DO NOTHING;
