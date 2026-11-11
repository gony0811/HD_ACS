-- 2026-11-12: 수동 비드 입력 대기 알람 [VDA §6.4 manualBeadInputRequired]
-- HD_AMR이 수동 비드 입력 팝업을 띄운 동안 state.errors[]에 manualBeadInputRequired(WARNING)를 싣는다.
-- ACS는 등장 시 이 알람을 기록하고, 사라지면 cleared_at을 남긴다(실패 집계·배차와 무관).
-- 앱 기동 시드(AlarmSpecSeed)가 같은 행을 넣으므로 현장 서버는 수동 적용이 필요 없다 — 직접 접근 DB용.
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('MANUAL_BEAD_INPUT_REQUIRED', 'WARNING', '수동 비드 입력 대기',
    'AMR이 비드 자동 검출 대신 작업자 입력을 기다림(팝업 표시 중, 타워램프 녹색 점멸) — 실패 아님. AMR 화면에서 비드선을 지정하면 검사가 이어지고 알람이 해제된다 [VDA §6.4 manualBeadInputRequired]')
ON CONFLICT (alarm_code) DO UPDATE SET
  severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
