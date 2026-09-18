-- 면 CAD(DXF) 등록 테이블 — 면별 용접선·Corrugation 선분(면-로컬 mm)을 DB에 저장.
-- 앱은 자동 마이그레이션을 하지 않으므로 현장 이동식 서버(폐쇄망)에 수동 적용 필요.
-- schema.sql과 동일 정의 유지.
CREATE TABLE IF NOT EXISTS ref.face_cad (
  tank_id     text NOT NULL REFERENCES ref.tank_geometry(tank_id) ON DELETE CASCADE,
  wall_code   text NOT NULL,                 -- B/SL/PL/SM/PM/SU/PU/T/F/A
  source_file text,
  seg_count   int  NOT NULL DEFAULT 0,
  weld_count  int  NOT NULL DEFAULT 0,
  corr_count  int  NOT NULL DEFAULT 0,
  segments    jsonb NOT NULL DEFAULT '[]',   -- [{ax,ay,bx,by,kind}] 면-로컬 mm, kind=WeldLine|Corrugation
  updated_by  text,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tank_id, wall_code)
);
