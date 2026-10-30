-- 영역 안 작업 순번(seq)·이름을 "라인 순"으로 다시 매긴다 (2026-10-30).
--
-- 배경: tools/build_hdacs_area_tasks.py 가 영역 안 작업을 (방향, v, u) 로 정렬해 순번을 매겨,
--   세로 작업이 높이 우선이 되어 이웃한 두 세로 라인을 칸마다 번갈아 갔다
--   (예: 천장 T L4-R01C05 — u=5.94 라인이 3·5·7, u=6.30 라인이 4·6·8).
-- 규칙(도구와 동일): 가로(start_v=end_v) 먼저, 라인(v) → 진행(u) 순 / 그다음 세로, 라인(u) → 진행(v) 순.
--   어느 축에도 평행하지 않은 작업은 맨 뒤에 기존 순번대로 둔다.
-- 이름이 도구 형식(H01·V03 …)인 작업만 새 순번으로 이름을 바꾸고, 운영자가 붙인 이름은 그대로 둔다.
-- 바뀌는 것은 ref.area_task 의 seq·name 뿐 — task_id·좌표·정차·캘리브레이션·시나리오는 그대로다.
-- 진행 중 run 은 시작 시 work_item 에 스냅샷되므로 영향이 없고, 다음 run 부터 새 순서로 발행된다.
--
-- 사용: psql -v tank=CT1 -f tools/renumber_area_tasks_line_order.sql   (tank 미지정 시 전 선창)
-- 반영 후 앱에서 파일 ▸ 저장으로 .hdacs 에도 남길 것(파일을 다시 열면 그 파일의 순번으로 돌아간다).

\if :{?tank}
\else
  \set tank ''
\endif

BEGIN;

CREATE TEMP TABLE renum ON COMMIT DROP AS
SELECT t.task_id, t.seq AS old_seq, t.name AS old_name,
       row_number() OVER (
         PARTITION BY t.area_id
         ORDER BY
           CASE WHEN t.start_v = t.end_v THEN 0 WHEN t.start_u = t.end_u THEN 1 ELSE 2 END,
           CASE WHEN t.start_v = t.end_v THEN least(t.start_v, t.end_v)
                WHEN t.start_u = t.end_u THEN least(t.start_u, t.end_u) END,
           CASE WHEN t.start_v = t.end_v THEN least(t.start_u, t.end_u)
                WHEN t.start_u = t.end_u THEN least(t.start_v, t.end_v) END,
           t.seq) AS new_seq,
       CASE WHEN t.start_v = t.end_v THEN 'H' WHEN t.start_u = t.end_u THEN 'V' END AS dir
FROM ref.area_task t
JOIN ref.inspection_area a ON a.area_id = t.area_id
WHERE :'tank' = '' OR a.tank_id = :'tank';

SELECT count(*) AS 대상_작업, count(*) FILTER (WHERE old_seq <> new_seq) AS 순번_변경 FROM renum;

-- (area_id, seq) 유일 제약을 피하려고 음수로 한 번 옮긴 뒤 새 값으로 쓴다.
UPDATE ref.area_task t SET seq = -r.new_seq
FROM renum r WHERE r.task_id = t.task_id AND r.old_seq <> r.new_seq;

UPDATE ref.area_task t
SET seq  = r.new_seq,
    name = CASE WHEN r.old_name ~ '^[HV][0-9]{2,}$' AND r.dir IS NOT NULL
                THEN r.dir || lpad(r.new_seq::text, 2, '0') ELSE r.old_name END
FROM renum r WHERE r.task_id = t.task_id AND r.old_seq <> r.new_seq;

COMMIT;
