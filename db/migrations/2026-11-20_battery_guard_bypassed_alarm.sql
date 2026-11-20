-- 2026-11-20: 배터리 보호 임시 해제 알람 [VDA §6.4 batteryGuardBypassed]
-- HD_AMR 이 BMS 고장 임시 조치(PowerMode:BypassBatteryGuard=true)로 저전력 판정을 끈 동안 state.errors[] 에 batteryGuardBypassed(WARNING)를 싣는다.
-- 기동 시드(AlarmSpecSeed)가 넣으므로 수동 적용은 필요 없다(직접 접근 DB 용).
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('BATTERY_GUARD_BYPASSED', 'WARNING', '배터리 보호 해제 중 — BMS 잔량 무시',
    'AMR이 BMS 고장 임시 조치로 배터리 보호를 끈 상태(HD_AMR appsettings PowerMode:BypassBatteryGuard=true) — 배터리 잔량과 무관하게 정상 모드로 Order를 받고 저전력 안전정지를 하지 않는다. 실제 잔량을 모르므로 팩 전압을 따로 확인할 것. AMR에서 설정을 끄고 재시작하면 해제된다 [VDA §6.4 batteryGuardBypassed]')
ON CONFLICT (alarm_code) DO UPDATE
  SET severity = EXCLUDED.severity, title = EXCLUDED.title, description = EXCLUDED.description;
