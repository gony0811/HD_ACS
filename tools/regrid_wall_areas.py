#!/usr/bin/env python3
"""면 하나의 검사 영역(AREA) 격자를 다시 나눈다 — 용접선(TASK)은 그대로 두고 소속 영역만 바꾼다.

  py tools/regrid_wall_areas.py --wall T --win 1080            # 미리보기 (DB 를 바꾸지 않음)
  py tools/regrid_wall_areas.py --wall T --win 1080 --apply    # 실제 적용 (트랜잭션 1개)

유지되는 것
  - taskId·좌표·seamType·가지(points) — ref.area_task 행을 지우지 않고 area_id·seq·이름만 고친다.
    검사 기록(run.order_action.task_id)·attempt 누적·SAIGE productId 가 그대로 이어진다.
  - 영역 ID — 새 칸은 그 칸 용접선이 가장 많이 속해 있던 옛 영역의 ID 를 물려받는다(옛 영역 하나당 한 칸).
    지난 run 의 work_item·결과 조회·시나리오 대상 연결이 계속 풀린다. 물려받을 ID 가 없는 칸만 새 ID.
바뀌는 것
  - 영역 사각형(창 안에서 용접선 범위 + 여유 30 mm)·이름(L{층}-R{행}C{열})·용접선 순번(라인 순)과 도구 형식 이름(H01·V03).
  - 정차: 새 칸은 가장 많이 겹치는 옛 영역의 정차를 "영역 중심 기준 같은 오프셋"으로 옮겨 받고 방향·리프트·이격·후진 상한은 그대로.
    옛 영역이 한 칸으로만 이어지면 사실상 같은 조건이지만, 여러 칸으로 갈라지면 초기값일 뿐이다 —
    적용 뒤 계획 ▸ 리치·간섭 ▸ 정차점 최적화로 다시 맞출 것.
  - 끝나지 않은(중단된) run 의 남은 정차는 이어하기 때 지금 영역으로 다시 계산된다(그 ID 의 새 칸 용접선만).

격자: 창 경계는 용접선 끝점 좌표에서만 고른다(가로 작업의 u 끝점·세로 작업의 v 끝점 + 면 가장자리).
      앞에서부터 폭이 --win 이하가 되게 묶는다(tools/build_hdacs_area_tasks.py 의 windows 와 같은 규칙) — 용접선이 창을 가로지르지 않는다.

DB 접속: 기본은 docker 컨테이너(dev-postgres)의 psql. 다른 DB 는 --psql "psql -h 127.0.0.1 -U postgres -d hdacs" 식으로.
적용 전 영역·작업 원본을 --backup 폴더(기본 db/dump)에 JSON 으로 남긴다(되돌리기 자료).
"""
import argparse, json, re, subprocess, sys, uuid, datetime, pathlib
from collections import Counter, defaultdict

EDGE = 1e-6
PAD = 0.03           # 영역 = 용접선 범위 + 여유 (m)
TOOL_NAME = re.compile(r'^[HVD]\d+$')   # 도구 형식 이름만 새 순번으로 바꾼다(운영자가 붙인 이름은 유지)


def psql(cmd, sql, tuples=True):
    args = cmd.split() + (['-At'] if tuples else []) + ['-v', 'ON_ERROR_STOP=1', '-f', '-']
    r = subprocess.run(args, input=sql.encode('utf-8'), capture_output=True)
    if r.returncode != 0:
        sys.exit('psql 실패:\n' + r.stderr.decode('utf-8', 'replace'))
    return r.stdout.decode('utf-8')


def windows(breaks, win):
    out, i = [], 0
    while i < len(breaks) - 1:
        lo, j = breaks[i], i + 1
        while j + 1 < len(breaks) and breaks[j + 1] - lo <= win + EDGE:
            j += 1
        out.append((lo, breaks[j]))
        i = j
    return out


def r4(x): return round(x, 4)


def main():
    ap = argparse.ArgumentParser(description='면 하나의 AREA 격자 재분할 (taskId·검사 기록 유지)')
    ap.add_argument('--tank', default='CT1')
    ap.add_argument('--wall', required=True, help='면 코드 (예: T)')
    ap.add_argument('--win', type=float, required=True, help='AREA 최대 크기 mm (가로·세로 같은 값, 예: 1080)')
    ap.add_argument('--win-v', type=float, help='세로(v) 최대 크기 mm (생략 = --win)')
    ap.add_argument('--psql', default='docker exec -i dev-postgres psql -U postgres -d hdacs')
    ap.add_argument('--backup', default=str(pathlib.Path(__file__).resolve().parent.parent / 'db' / 'dump'))
    ap.add_argument('--apply', action='store_true', help='실제 적용 (없으면 미리보기)')
    ap.add_argument('--force', action='store_true', help='진행 중 run 이 이 면을 검사하고 있어도 적용')
    a = ap.parse_args()
    tank, wall = a.tank, a.wall.upper()
    wu, wv = a.win / 1000.0, (a.win_v or a.win) / 1000.0
    lit = lambda s: "'" + s.replace("'", "''") + "'"

    data = json.loads(psql(a.psql, f"""
select json_build_object(
  'wall', (select row_to_json(w) from (select origin, u_axis, v_axis, u_len, v_len from ref.wall
                                       where tank_id = {lit(tank)} and wall_code = {lit(wall)}) w),
  'areas', (select coalesce(json_agg(row_to_json(x)), '[]') from (select * from ref.inspection_area
                                       where tank_id = {lit(tank)} and wall_code = {lit(wall)} order by name) x),
  'tasks', (select coalesce(json_agg(row_to_json(x)), '[]') from (select t.* from ref.area_task t
              join ref.inspection_area a using (area_id)
              where a.tank_id = {lit(tank)} and a.wall_code = {lit(wall)} order by t.task_id) x),
  'active', (select count(*) from run.work_item w join run.scenario_run r using (run_id)
              join ref.inspection_area a using (area_id)
              where a.tank_id = {lit(tank)} and a.wall_code = {lit(wall)}
                and r.state in ('RUNNING', 'WAITING_FLOOR_TRANSFER') and w.status in ('PENDING', 'DISPATCHED')));
"""))
    w, old_areas, tasks = data['wall'], data['areas'], data['tasks']
    if w is None: sys.exit(f'면 {tank}/{wall} 없음')
    if not tasks: sys.exit(f'면 {tank}/{wall} 에 용접선이 없습니다.')
    ulen, vlen = w['u_len'], w['v_len']
    O, U, V = w['origin'], w['u_axis'], w['v_axis']
    to_xy = lambda u, v: (O[0] + u * U[0] + v * V[0], O[1] + u * U[1] + v * V[1])
    old_by_id = {x['area_id']: x for x in old_areas}

    def pts(t):   # 용접선이 차지하는 면 좌표 점 — 시작·끝 + 교차 가지
        p = [(t['start_u'], t['start_v']), (t['end_u'], t['end_v'])]
        for q in (t.get('points') or []):
            p.append((q[0], q[1]))
        return p

    levels = sorted({old_by_id[t['area_id']]['level'] for t in tasks})
    new_areas, problems = [], []
    for level in levels:
        ts = [t for t in tasks if old_by_id[t['area_id']]['level'] == level]
        # 층 안의 v 범위 — 옛 영역이 덮던 범위를 넘지 않게(층 경계 유지). 한 층만 쓰는 면(바닥·천장)은 면 전체.
        la = [x for x in old_areas if x['level'] == level]
        vlo = 0.0 if len(levels) == 1 else min(x['v_min'] for x in la)
        vhi = vlen if len(levels) == 1 else max(x['v_max'] for x in la)
        bu = {0.0, ulen}
        bv = {vlo, vhi}
        for t in ts:
            if abs(t['start_u'] - t['end_u']) > EDGE: bu.update((r4(t['start_u']), r4(t['end_u'])))
            if abs(t['start_v'] - t['end_v']) > EDGE: bv.update((r4(t['start_v']), r4(t['end_v'])))
        uw, vw = windows(sorted(bu), wu), windows(sorted(bv), wv)
        cells = defaultdict(list)
        for t in ts:
            us, vs = [p[0] for p in pts(t)], [p[1] for p in pts(t)]
            ci = next((i for i, (lo, hi) in enumerate(uw) if lo - EDGE <= min(us) and max(us) <= hi + EDGE), None)
            cj = next((j for j, (lo, hi) in enumerate(vw) if lo - EDGE <= min(vs) and max(vs) <= hi + EDGE), None)
            if ci is None or cj is None:
                problems.append(f"용접선 {t['task_id']} ({min(us):.3f}~{max(us):.3f}, {min(vs):.3f}~{max(vs):.3f}) 이 창 하나에 들어가지 않음")
                continue
            cells[(cj, ci)].append(t)
        for (cj, ci), ct in sorted(cells.items()):
            (ua, ub), (va, vb) = uw[ci], vw[cj]
            allp = [p for t in ct for p in pts(t)]
            umin = r4(max(ua, 0.0, min(p[0] for p in allp) - PAD)); umax = r4(min(ub, ulen, max(p[0] for p in allp) + PAD))
            vmin = r4(max(va, 0.0, min(p[1] for p in allp) - PAD)); vmax = r4(min(vb, vlen, max(p[1] for p in allp) + PAD))

            def line_key(t):   # 라인 순: 가로 = v 라인 → u, 세로 = u 라인 → v, 사선은 맨 뒤 (AreaRules.OrderByLine 과 같은 규칙)
                du, dv = abs(t['start_u'] - t['end_u']), abs(t['start_v'] - t['end_v'])
                if dv <= EDGE: return (0, r4(t['start_v']), min(t['start_u'], t['end_u']))
                if du <= EDGE: return (1, r4(t['start_u']), min(t['start_v'], t['end_v']))
                return (2, min(t['start_v'], t['end_v']), min(t['start_u'], t['end_u']))
            ct.sort(key=line_key)
            new_areas.append(dict(level=level, name=f"L{level}-R{cj + 1:02d}C{ci + 1:02d}", tasks=ct,
                                  u_min=umin, u_max=umax, v_min=vmin, v_max=vmax,
                                  votes=Counter(t['area_id'] for t in ct)))

    if problems:
        print('\n'.join(problems[:20])); sys.exit(f'중단: 창에 들어가지 않는 용접선 {len(problems)}개')

    # 영역 ID 물려주기 — 겹치는 용접선이 많은 (새 칸, 옛 영역) 쌍부터, 옛 영역 하나당 한 칸
    pairs = sorted(((n, i, oid) for i, na in enumerate(new_areas) for oid, n in na['votes'].items()),
                   key=lambda x: (-x[0], x[1], x[2]))
    taken, ns = set(), uuid.UUID('6f1d3c1e-6b43-4f0e-9d51-0c1f5a7e2b10')
    for n, i, oid in pairs:
        if 'area_id' not in new_areas[i] and oid not in taken:
            new_areas[i]['area_id'] = oid; taken.add(oid)
    for na in new_areas:
        na['reused'] = 'area_id' in na
        na.setdefault('area_id', str(uuid.uuid5(ns, f"{tank}/{wall}/{a.win}/{na['name']}/{na['u_min']}/{na['v_min']}")))
        # 정차 물려받기 — 가장 많이 겹치는 옛 영역의 "영역 중심 기준 오프셋"·방향·리프트·이격
        src = old_by_id[na['votes'].most_common(1)[0][0]]
        na['src'] = src
        cu, cv = (na['u_min'] + na['u_max']) / 2, (na['v_min'] + na['v_max']) / 2
        ncx, ncy = to_xy(cu, cv)
        oc = src['corners']
        ocx, ocy = to_xy(sum(p[0] for p in oc) / len(oc), sum(p[1] for p in oc) / len(oc))
        na['station_x'] = None if src['station_x'] is None else round(ncx + src['station_x'] - ocx, 4)
        na['station_y'] = None if src['station_y'] is None else round(ncy + src['station_y'] - ocy, 4)
        na['shift'] = ((ncx - ocx) ** 2 + (ncy - ocy) ** 2) ** 0.5
    dropped = [x for x in old_areas if x['area_id'] not in taken]

    # ── 요약 ──
    n_old, n_new = len(old_areas), len(new_areas)
    spans_u = [na['u_max'] - na['u_min'] for na in new_areas]; spans_v = [na['v_max'] - na['v_min'] for na in new_areas]
    per = [len(na['tasks']) for na in new_areas]
    moved = sum(1 for na in new_areas for t in na['tasks'] if t['area_id'] != na['area_id'])
    split = Counter(len({i for i, na in enumerate(new_areas) if oid in na['votes']}) for oid in old_by_id)
    print(f"면 {tank}/{wall}: 창 {wu * 1000:.0f} × {wv * 1000:.0f} mm")
    print(f"  영역 {n_old} → {n_new}개  (ID 유지 {len(taken)} · 새 ID {n_new - len(taken)} · 없어지는 옛 영역 {len(dropped)})")
    print(f"  용접선 {len(tasks)}개 그대로 (taskId 유지) · 소속 영역이 바뀌는 용접선 {moved}개")
    print(f"  영역 크기 u {min(spans_u):.3f}~{max(spans_u):.3f} m · v {min(spans_v):.3f}~{max(spans_v):.3f} m · 영역당 용접선 {min(per)}~{max(per)}개 (평균 {sum(per) / n_new:.1f})")
    print(f"  옛 영역이 갈라지는 칸 수: " + ', '.join(f"{k}칸 {v}개" for k, v in sorted(split.items())))
    print(f"  정차 물려받기: 영역 중심 이동 평균 {sum(na['shift'] for na in new_areas) / n_new:.2f} m · 최대 {max(na['shift'] for na in new_areas):.2f} m")
    if data['active']:
        print(f"  ⚠ 진행 중 run 이 이 면의 정차 {data['active']}곳을 아직 검사하지 않았습니다.")
        if a.apply and not a.force: sys.exit('중단: 진행 중 run 을 끝내거나 중단한 뒤 적용하세요(-force 로 무시).')
    bad = [na['name'] for na in new_areas if na['u_max'] - na['u_min'] > wu + 1e-6 or na['v_max'] - na['v_min'] > wv + 1e-6
           or na['u_max'] <= na['u_min'] or na['v_max'] <= na['v_min']]
    if bad: sys.exit(f'중단: 크기 규칙 위반 영역 {len(bad)}개 — {bad[:5]}')
    if sum(per) != len(tasks) or len({t['task_id'] for na in new_areas for t in na['tasks']}) != len(tasks):
        sys.exit('중단: 용접선 수가 맞지 않습니다.')

    # ── SQL ──
    num = lambda x: 'null' if x is None else repr(float(x))
    sql = ['begin;',
           # 이름·순번 유일 제약을 피하려고 먼저 임시값으로
           f"update ref.inspection_area set name = '~regrid~' || area_id where tank_id = {lit(tank)} and wall_code = {lit(wall)};",
           f"update ref.area_task t set seq = -t.seq - 100000 from ref.inspection_area a where a.area_id = t.area_id and a.tank_id = {lit(tank)} and a.wall_code = {lit(wall)};"]
    for na in new_areas:
        s = na['src']
        corners = json.dumps([[na['u_min'], na['v_max']], [na['u_max'], na['v_max']], [na['u_max'], na['v_min']], [na['u_min'], na['v_min']]])
        vals = dict(name=lit(na['name']), level=str(na['level']), corners=lit(corners) + '::jsonb',
                    u_min=num(na['u_min']), v_min=num(na['v_min']), u_max=num(na['u_max']), v_max=num(na['v_max']),
                    station_x=num(na['station_x']), station_y=num(na['station_y']), station_theta=num(s['station_theta']),
                    station_standoff_m=num(s['station_standoff_m']), station_max_backward_mm=num(s['station_max_backward_mm']),
                    station_lift_mm=num(s['station_lift_mm']), sort_order=str(s['sort_order']))
        if na['reused']:
            sql.append(f"update ref.inspection_area set {', '.join(f'{k} = {v}' for k, v in vals.items())} where area_id = '{na['area_id']}';")
        else:
            cols = ['area_id', 'tank_id', 'wall_code', 'created_by'] + list(vals)
            row = [f"'{na['area_id']}'", lit(tank), lit(wall), "'regrid'"] + list(vals.values())
            sql.append(f"insert into ref.inspection_area ({', '.join(cols)}) values ({', '.join(row)});")
    for na in new_areas:
        for k, t in enumerate(na['tasks'], 1):
            name = t['name']
            if name and TOOL_NAME.match(name): name = f"{name[0]}{k:02d}"
            sql.append(f"update ref.area_task set area_id = '{na['area_id']}', seq = {k}, name = {'null' if name is None else lit(name)} where task_id = '{t['task_id']}';")
    if dropped:
        ids = ', '.join(f"'{x['area_id']}'" for x in dropped)
        # 용접선을 모두 옮긴 뒤라 CASCADE 로 지워지는 작업은 없어야 한다 — 남아 있으면 중단
        sql.append(f"do $$ begin if exists (select 1 from ref.area_task where area_id in ({ids})) then raise exception '옮기지 못한 용접선이 남아 있습니다'; end if; end $$;")
        sql.append(f"delete from ref.inspection_area where area_id in ({ids});")
    detail = f"{tank}/{wall} 창 {wu * 1000:.0f}x{wv * 1000:.0f}mm 영역 {n_old}→{n_new} 용접선 {len(tasks)} (taskId 유지)"
    sql.append(f"insert into sys.audit_log (user_id, action, target) values ('regrid', 'AREA_REGRID', {lit(detail)});")
    sql.append(f"do $$ begin if (select count(*) from ref.area_task t join ref.inspection_area a using (area_id) where a.tank_id = {lit(tank)} and a.wall_code = {lit(wall)}) <> {len(tasks)} then raise exception '용접선 수가 달라졌습니다'; end if; end $$;")
    sql.append('commit;')

    if not a.apply:
        print('미리보기 — DB 는 바뀌지 않았습니다. 적용은 --apply.')
        return
    out = pathlib.Path(a.backup); out.mkdir(parents=True, exist_ok=True)
    stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
    bak = out / f"regrid_{tank}_{wall}_{stamp}.json"
    bak.write_text(json.dumps(dict(tank=tank, wall=wall, areas=old_areas, tasks=tasks), ensure_ascii=False), encoding='utf-8')
    (out / f"regrid_{tank}_{wall}_{stamp}.sql").write_text('\n'.join(sql), encoding='utf-8')
    print(f"백업: {bak}")
    psql(a.psql, '\n'.join(sql), tuples=False)
    print('적용 완료.')


if __name__ == '__main__':
    main()
