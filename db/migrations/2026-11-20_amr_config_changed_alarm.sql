-- AMR factsheet 설정 변경 알람 [VDA §7.3] — 기동 시드(AlarmSpecSeed)가 넣으므로 수동 적용은 직접 DB 접근용
INSERT INTO alarm.spec (alarm_code, severity, title, description) VALUES
  ('AMR_CONFIG_CHANGED', 'WARNING', 'AMR 설정 변경 — 티칭·리프트·장착 보정', 'AMR이 factsheet로 보고한 설정(면별 출발 자세 티칭·리프트 운영·장착 보정 T_A_B·코봇 값)이 바뀜 — ACS가 리치 시뮬레이션·배차 설정에 반영함. 옛 설정으로 고른 영역 정차·리프트는 정차점 최적화로 다시 확인할 것 [VDA §7.3]')
ON CONFLICT (alarm_code) DO NOTHING;
