-- ---------------------------------------------------------------------------
-- 맵 T_W_D 회전 보정 — 도면 뷰 로봇이 반시계 90° 틀어져 표시되던 문제 수정
--   근본원인: 저장된 캘리브레이션 yaw가 앱 프레임(선수=+X,좌현=+Y)이 아닌
--            CAD 프레임 기준(yaw≈0)이라 렌더가 90° 회전.
--   현장 3점(선미/좌현·선수/좌현·중앙, CAD↔SLAM) 재산출값(RMS 8.9cm):
--            Tx=11.9127  Ty=16.6471  YawRad=+1.57418(+90.19°)
--   검증: 로봇 SLAM(5.318,19.066,θ1.564) → 도면 선수/좌현·화살표 선수(0°).
--
-- 실행: 현장 서버(폐쇄망)에서
--   psql "<앱 appsettings.json 의 ConnectionString>" -v map_id=CT1-L4 -f 2026-09-08_fix_twd_yaw.sql
--   (map_id 는 로봇이 보고 중인 층 맵으로 교체. 모르면 아래 참고 쿼리로 확인)
--
-- ※ 적용 후에도 반시계로 남으면(앱 렌더 축 반전) yaw_rad 만 -1.56741 로 교체.
--   Tx/Ty/RMS 는 3점이 확정하므로 그대로. 정답은 +1.57418 또는 -1.56741 중 하나.
-- ---------------------------------------------------------------------------

-- (참고) 로봇이 현재 보고 중인 mapId 확인:
--   SELECT reported_map_id, reported_x, reported_y, reported_theta, reported_at
--     FROM ref.robot_context ORDER BY reported_at DESC LIMIT 5;

\if :{?map_id}
\else
\set map_id 'CT1-L4'
\endif

\echo '대상 map_id =' :'map_id'

INSERT INTO ref.map_calibration
    (map_id, map_version, tx, ty, yaw_rad, rms_m, point_count, registered_by, registered_at)
SELECT m.map_id, m.version,
       11.9127, 16.6471, 1.57418, 0.0887, 3, 'twd-yaw-fix-2026-09-08', now()
FROM ref.map m
WHERE m.map_id = :'map_id'
ON CONFLICT (map_id, map_version) DO UPDATE
SET tx = EXCLUDED.tx,
    ty = EXCLUDED.ty,
    yaw_rad = EXCLUDED.yaw_rad,
    rms_m = EXCLUDED.rms_m,
    point_count = EXCLUDED.point_count,
    registered_by = EXCLUDED.registered_by,
    registered_at = now();

-- 결과 확인
SELECT map_id, map_version, tx, ty, yaw_rad, degrees(yaw_rad) AS yaw_deg, rms_m, registered_at
FROM ref.map_calibration
WHERE map_id = :'map_id';
