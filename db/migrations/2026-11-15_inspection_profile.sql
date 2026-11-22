-- HD_AMR 검사 프로필(순회 경유점) 사본 — AMR 이 저장할 때 올려 보낸다(PUT /api/inspection-profiles/sync).
-- 앱 기동 시 SchemaEnsure 가 같은 구문을 실행하므로 수동 적용은 필요 없다(직접 접근 DB 용).
CREATE TABLE IF NOT EXISTS ref.inspection_profile (
  source         text NOT NULL,
  amr_profile_id int  NOT NULL,
  name           text NOT NULL,
  seam_type      text NOT NULL DEFAULT 'LINE',
  pose_absolute  boolean NOT NULL DEFAULT false,
  waypoints      jsonb NOT NULL DEFAULT '[]',
  point_count    int  NOT NULL DEFAULT 0,
  version        int  NOT NULL DEFAULT 1,
  content_hash   text NOT NULL DEFAULT '',
  amr_updated_at timestamptz,
  received_at    timestamptz NOT NULL DEFAULT now(),
  changed_at     timestamptz NOT NULL DEFAULT now(),
  deleted        boolean NOT NULL DEFAULT false,
  PRIMARY KEY (source, amr_profile_id)
);
CREATE INDEX IF NOT EXISTS ix_inspection_profile_name ON ref.inspection_profile (name);
-- 유형·면·진행 방향(H 가로 / V 세로)별 프로필 지정표 — AMR 이 통째로 올려 보낸다.
CREATE TABLE IF NOT EXISTS ref.inspection_profile_assignment (
  source         text NOT NULL,
  seam_type      text NOT NULL,
  wall_code      text NOT NULL,
  direction      text NOT NULL,
  amr_profile_id int  NOT NULL,
  PRIMARY KEY (source, seam_type, wall_code, direction)
);
