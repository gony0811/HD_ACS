-- 2026-10-07: CROSS3/CROSS4 교차 가지 끝점 저장 — VDA5050_INTERFACE_SPEC §8.5.1 (N13 확정안).
-- ref.area_task 에 points jsonb 추가: [[u,v],...] 면-로컬(ACS) 미터, 중심 = (start_u, start_v).
--   CROSS3 = [줄기, 통과1, 통과2]  ·  CROSS4 = 가지 4개(무순)  ·  LINE/CORNER = NULL.
-- 발행 시 CrossGeometry.BuildAmrPoints 가 AMR 면-로컬 mm·규약 순서로 변환해 params.points 로 전송.
-- 추가형 컬럼(nullable) — 앱 기동 시 SchemaEnsure 가 동일 ADD COLUMN IF NOT EXISTS 를 멱등 적용하므로
-- 앱 바이너리 배포만으로도 반영된다. 직접-접근 DB 는 이 파일로.
-- 적용: psql -U postgres -d hdacs -f /tmp/2026-10-07_area_task_points.sql   (멱등)

ALTER TABLE ref.area_task ADD COLUMN IF NOT EXISTS points jsonb;
