-- F-A0001: 정차 정면 수평 용접선 1개만 남기고 정차 이격을 실측 1.35 m로 — DB 직접 반영판
-- (REST판: tools/define_f_a0001_weld.py. 이 SQL은 서버 검증·감사로그를 거치지 않으므로 값 검사를 아래에서 직접 한다)
--   시작 u = 영역 중심 u (정차점 = 영역 중심 + 법선×이격 → 정차 정면)
--   v      = 그 층 발판 z + 1.15 m (면-전체 v, 수직면 기준 (z - origin.z)/vAxis.z)
--   끝 u   = 시작 u + 0.72 (벽을 바라볼 때 오른쪽 = +u). 왼쪽이면 아래 dir 을 -1 로.
-- 실행: psql -h localhost -U postgres -d hdacs -f tools/define_f_a0001_weld.sql   (또는 DataGrip에서 통째로 실행)
DO $$
DECLARE
  a    ref.inspection_area;
  dir  int := 1;                -- +1 = 오른쪽(+u), -1 = 왼쪽(-u)
  uc double precision; sv double precision; eu double precision;
  fz double precision; oz double precision; vz double precision;
  sec text; prof text; n int;
BEGIN
  SELECT * INTO STRICT a FROM ref.inspection_area
   WHERE tank_id = 'CT1' AND wall_code = 'F' AND name = 'F-A0001';
  SELECT avg((p->>0)::float8) INTO uc FROM jsonb_array_elements(a.corners) p;   -- AreaGeometry.Centroid
  SELECT (level_z->>(a.level - 1))::float8 INTO fz FROM ref.tank_geometry WHERE tank_id = a.tank_id;
  SELECT (origin->>2)::float8, (v_axis->>2)::float8 INTO oz, vz
    FROM ref.wall WHERE tank_id = a.tank_id AND wall_code = a.wall_code;
  uc := round(uc::numeric, 6);
  sv := round(((fz + 1.15 - oz) / vz)::numeric, 6);
  eu := round((uc + dir * 0.72)::numeric, 6);
  IF least(uc, eu) < a.u_min - 1e-9 OR greatest(uc, eu) > a.u_max + 1e-9 OR sv < a.v_min OR sv > a.v_max THEN
    RAISE EXCEPTION '용접선 (%,%)→(%,%)이 영역 u[%,%] v[%,%] 밖 — 중단', uc, sv, eu, sv, a.u_min, a.u_max, a.v_min, a.v_max;
  END IF;

  SELECT section_dxf_id, profile_id INTO sec, prof FROM ref.area_task WHERE area_id = a.area_id ORDER BY seq LIMIT 1;
  UPDATE ref.inspection_area SET station_standoff_m = 1.35 WHERE area_id = a.area_id;
  DELETE FROM ref.area_task WHERE area_id = a.area_id;
  GET DIAGNOSTICS n = ROW_COUNT;
  INSERT INTO ref.area_task (area_id, seq, name, seam_type, start_u, start_v, end_u, end_v, section_dxf_id, profile_id, created_by)
  VALUES (a.area_id, 1, 'H01', 'LINE', uc, sv, eu, sv,
          coalesce(nullif(sec, ''), 'WALL F'), coalesce(nullif(prof, ''), 'PROF-1'), 'manual-sql');
  INSERT INTO sys.audit_log (user_id, action, target, detail)
  VALUES ('manual-sql', 'AREA_TASK_REDEFINE', a.area_id::text,
          jsonb_build_object('area', a.name, 'standoffM', 1.35, 'deleted', n,
                             'task', jsonb_build_array(uc, sv, eu, sv)));
  RAISE NOTICE 'F-A0001 (L%): 이격 1.35 m, 기존 용접선 %개 삭제, H01 (%, %) → (%, %)', a.level, n, uc, sv, eu, sv;
  IF a.station_x IS NOT NULL THEN
    RAISE NOTICE '주의: 수동 정차 좌표(station_x/y)가 있어 이격 1.35 m가 정차점에 반영되지 않습니다.';
  END IF;
END $$;

SELECT a.name, a.level, a.station_standoff_m, t.seq, t.name AS task, t.seam_type, t.start_u, t.start_v, t.end_u, t.end_v
  FROM ref.inspection_area a JOIN ref.area_task t USING (area_id)
 WHERE a.tank_id = 'CT1' AND a.wall_code = 'F' AND a.name = 'F-A0001';
