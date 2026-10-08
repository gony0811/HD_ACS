"""KC-2B 도면(DXF 10장) → 층별 AREA / TASK 분할 → 등록용 프로젝트 파일(.hdacs) 생성.

기준 = 위키 "결정 001 검사영역 분할"(2026-09-27 개정):
  - AREA 최대 1440 x 1440 mm(코봇 최대 검사 범위), 층마다 새로 배치, 한 층 범위 안에만 둔다.
  - TASK 1개 = 인접한 두 top(CENTER 선) 사이 용접선 1칸. 가로·세로 모두.
  - 교차점: CROSS3(T자)·2갈래 교차점에 닿는 TASK는 제거, CROSS4(十자) 교차점은 가로 TASK 1개만
    남겨 seamType=CROSS4 (가로 TASK가 없으면 세로 1개).
  - 층 경계를 가로지르는 TASK는 어느 AREA에도 넣을 수 없어 제외하고 보고서에 남긴다.

좌표 변환 (DXF mm → ACS 면-로컬 u,v m):
  - 면 원점 = 도면 Steel Wall 외곽선 모서리 (TankGeometry.GenerateWalls 규약과 일치).
  - 10개 면 도면은 모두 선창 내부에서 본 방향(이웃 면 라벨로 확인). SL·SM·SU는 도면 좌우를,
    T는 상하를 뒤집어 ACS u(선미→선수)·v(우현→좌현)에 맞춘다.
  - 경사면 도면은 실제 경사 길이로 그려져 있어 환산하지 않는다(SL 외곽 5625.2 = 3977.6·√2).
    경사 방향 top 피치 254.6mm는 실제 값이다.

ID는 좌표에서 결정적으로 생성(uuid5) — 같은 도면·기준으로 다시 돌리면 같은 areaId/taskId가 나온다.

사용:
  python3 tools/build_hdacs_area_tasks.py OUT.hdacs [--report OUT.json] [--preview OUT.png]
  (--preview 는 matplotlib 필요)
"""
from __future__ import annotations

import argparse
import gzip
import json
import math
import uuid
from collections import Counter, defaultdict

from analyze_dxf_regions import ROOT, entities, first, points
from build_dxf_area_preview import segment_records, merge_segments

# ── 선창 형상 (m) — 도면 Steel Wall 외곽선 실측, WALL A 팔각·B/T/SM/SL/SU 치수 ──
TANK_ID = 'CT1'
GEOMETRY = dict(lengthL=31.785, wFloor=10.1497, thetaLowDeg=45, hLow=3.9776, hWall=6.9097,
                thetaUpDeg=45, hUp=2.8976, levelZ=[0, 3.63, 6.63, 10.53],   # 층 발판 높이
                originOx=0, originOy=0, reachZMin=None, reachZMax=None)   # 높이 도달 제한 없음(리프트)

WIN = 1440.0            # AREA 최대 크기 (mm)
PAD = 30.0              # AREA = 작업 범위 + 여유(창 안으로 클리핑)
PITCH_OK = (360.0, 254.6)
PITCH_TOL = 0.2
JT = 0.5                # 교차점 판정 허용오차 (mm)
EDGE = 1e-6
FORMAT_VERSION = 3      # ProjectService.FormatVersion
NS = uuid.UUID('6f1c1d0e-4b7a-4c1e-9d2b-3a5e7c9f0b12')
WALL_ORDER = ['B', 'SL', 'PL', 'SM', 'PM', 'SU', 'PU', 'T', 'F', 'A']

g = GEOMETRY
hLow, hWall, hUp = g['hLow'], g['hWall'], g['hUp']
H = hLow + hWall + hUp
SL45 = math.sin(math.radians(g['thetaLowDeg']))
SU45 = math.sin(math.radians(g['thetaUpDeg']))
LZ = g['levelZ']
BANDS = [(LZ[i], LZ[i + 1] if i + 1 < len(LZ) else H) for i in range(len(LZ))]   # 도달 제한 없음 = [z_l, z_{l+1}]

# 면별 도면 반전 여부와 z(v) = z0 + v·kz
WALLS = {
    'B':  dict(mu=False, mv=False, z0=0.0,          kz=0.0),
    'T':  dict(mu=False, mv=True,  z0=H,            kz=0.0),
    'SL': dict(mu=True,  mv=False, z0=0.0,          kz=SL45),
    'PL': dict(mu=False, mv=False, z0=0.0,          kz=SL45),
    'SM': dict(mu=True,  mv=False, z0=hLow,         kz=1.0),
    'PM': dict(mu=False, mv=False, z0=hLow,         kz=1.0),
    'SU': dict(mu=True,  mv=False, z0=hLow + hWall, kz=SU45),
    'PU': dict(mu=False, mv=False, z0=hLow + hWall, kz=SU45),
    'A':  dict(mu=False, mv=False, z0=0.0,          kz=1.0),
    'F':  dict(mu=False, mv=False, z0=0.0,          kz=1.0),
}


def pitch_ok(d):
    return any(abs(d - p) <= PITCH_TOL for p in PITCH_OK)


def touches(t, x, y):
    (ax, ay), (bx, by) = t['dxf']
    return min(ax, bx) - JT <= x <= max(ax, bx) + JT and min(ay, by) - JT <= y <= max(ay, by) + JT


def load_wall(path, stats):
    es = entities(path)
    w = path.stem.split()[-1]
    cfg = WALLS[w]
    steel = [e for e in es if first(e, 8) == 'KC-2B Steel Wall' and e['type'] in ('LINE', 'LWPOLYLINE')]
    outline = [q for e in steel if not first(e, 6).startswith('CENTER') for q in points(e)]
    sx0, sx1 = min(q[0] for q in outline), max(q[0] for q in outline)
    sy0, sy1 = min(q[1] for q in outline), max(q[1] for q in outline)
    layers = {first(e, 8) for e in es if 'Membrane Sheet' in first(e, 8)}
    seams = merge_segments(segment_records(es, layers))
    tops = merge_segments(segment_records([e for e in steel if first(e, 6).startswith('CENTER')], {'KC-2B Steel Wall'}))

    def U(x): return round(((sx1 - x) if cfg['mu'] else (x - sx0)) / 1000.0, 4)
    def V(y): return round(((sy1 - y) if cfg['mv'] else (y - sy0)) / 1000.0, 4)

    # 1) TASK = seam 위에서 인접한 두 top 사이 1칸
    tasks = []
    for s in seams:
        cross = sorted({t['fixed'] for t in tops if t['axis'] != s['axis']
                        and t['lo'] - .03 <= s['fixed'] <= t['hi'] + .03 and s['lo'] - .03 <= t['fixed'] <= s['hi'] + .03})
        for a, b in zip(cross, cross[1:]):
            if not pitch_ok(b - a):
                continue
            if s['axis'] == 'H':
                p1, p2 = (U(a), V(s['fixed'])), (U(b), V(s['fixed']))
                dxf = ((a, s['fixed']), (b, s['fixed']))
            else:
                p1, p2 = (U(s['fixed']), V(a)), (U(s['fixed']), V(b))
                dxf = ((s['fixed'], a), (s['fixed'], b))
            p1, p2 = sorted([p1, p2])
            tasks.append(dict(dir=s['axis'], p1=p1, p2=p2, len_mm=round(b - a, 1), dxf=dxf))

    # 2) 교차점 갈래 수 분류
    Hs = [s for s in seams if s['axis'] == 'H']
    Vs = [s for s in seams if s['axis'] == 'V']
    junc = []
    for h in Hs:
        for v in Vs:
            x, y = v['fixed'], h['fixed']
            if h['lo'] - JT <= x <= h['hi'] + JT and v['lo'] - JT <= y <= v['hi'] + JT:
                br = (x - h['lo'] > JT) + (h['hi'] - x > JT) + (y - v['lo'] > JT) + (v['hi'] - y > JT)
                junc.append((x, y, br))
    st = stats[w]
    st.update({f'교차점 {k}갈래': n for k, n in Counter(br for _, _, br in junc).items()})

    # 3) CROSS3·2갈래 교차점에 닿는 TASK 제거, CROSS4 교차점에만 닿는 TASK = CROSS4 후보
    kept = []
    for t in tasks:
        hit = {br for x, y, br in junc if touches(t, x, y)}
        if hit and hit != {4}:
            st['제거: CROSS3·2갈래 교차점'] += 1
            continue
        t['seamType'] = 'CROSS4' if hit == {4} else 'LINE'
        kept.append(t)

    # 4) CROSS4 교차점당 가로 TASK 1개만 유지(없으면 세로 1개)
    keep_ids, drop_ids = set(), set()
    for x, y, br in junc:
        if br != 4:
            continue
        on = [t for t in kept if t['seamType'] == 'CROSS4' and touches(t, x, y)]
        if not on:
            st['CROSS4 교차점: 작업 없음'] += 1
            continue
        hs = sorted([t for t in on if t['dir'] == 'H'], key=lambda t: t['p1'])
        pick = hs[0] if hs else sorted(on, key=lambda t: t['p1'])[0]
        if not hs:
            st['CROSS4 교차점: 가로 없어 세로 유지'] += 1
        keep_ids.add(id(pick))
        drop_ids.update(id(t) for t in on if t is not pick)
    tasks = [t for t in kept if not (id(t) in drop_ids and id(t) not in keep_ids)]
    st['제거: CROSS4 중복(세로 등)'] = len(drop_ids - keep_ids)

    tu = sorted({U(t['fixed']) for t in tops if t['axis'] == 'V'})
    tv = sorted({V(t['fixed']) for t in tops if t['axis'] == 'H'})
    return w, dict(cfg=cfg, tasks=tasks, tu=tu, tv=tv,
                   ulen=round((sx1 - sx0) / 1000.0, 4), vlen=round((sy1 - sy0) / 1000.0, 4))


def windows(breaks, win_m):
    """정렬된 경계점(top 좌표·층 경계)을 span<=win 이 되도록 앞에서부터 묶는다."""
    out, i = [], 0
    while i < len(breaks) - 1:
        lo, j = breaks[i], i + 1
        while j + 1 < len(breaks) and breaks[j + 1] - lo <= win_m + EDGE:
            j += 1
        out.append((lo, breaks[j]))
        i = j
    return out


def z_range(cfg, v1, v2):
    a, b = cfg['z0'] + v1 * cfg['kz'], cfg['z0'] + v2 * cfg['kz']
    return min(a, b), max(a, b)


def build():
    stats = defaultdict(Counter)
    walls = dict(load_wall(p, stats) for p in sorted((ROOT / 'drawing' / '2D도면').glob('*.dxf')))
    areas, excluded = [], []
    W = WIN / 1000.0
    for w in WALL_ORDER:
        d, cfg = walls[w], walls[w]['cfg']
        by_level = defaultdict(list)
        for t in d['tasks']:
            z1, z2 = z_range(cfg, t['p1'][1], t['p2'][1])
            hits = [i for i, (lo, hi) in enumerate(BANDS) if z1 >= lo - 1e-9 and z2 <= hi + 1e-9]
            if hits:                       # 경계선 위에 정확히 놓인 수평 작업은 아래층
                by_level[hits[0]].append(t)
            else:
                excluded.append(dict(wall=w, dir=t['dir'], seamType=t['seamType'], start=t['p1'], end=t['p2'],
                                     z=[round(z1, 3), round(z2, 3)], reason='층 경계를 가로지름'))
        for li, ts in sorted(by_level.items()):
            lo, hi = BANDS[li]
            if cfg['kz'] == 0:
                vlo, vhi = 0.0, d['vlen']
            else:
                vlo, vhi = sorted(((lo - cfg['z0']) / cfg['kz'], (hi - cfg['z0']) / cfg['kz']))
                vlo, vhi = max(0.0, round(vlo, 4)), min(d['vlen'], round(vhi, 4))
            uw = windows(sorted({0.0, d['ulen'], *d['tu']}), W)
            vw = windows(sorted({vlo, vhi, *[x for x in d['tv'] if vlo < x < vhi]}), W)
            cells = defaultdict(list)
            for t in ts:
                (u1, v1), (u2, v2) = t['p1'], t['p2']
                ci = next(i for i, (a, b) in enumerate(uw) if a - EDGE <= min(u1, u2) and max(u1, u2) <= b + EDGE)
                cj = next(j for j, (a, b) in enumerate(vw) if a - EDGE <= min(v1, v2) and max(v1, v2) <= b + EDGE)
                cells[(cj, ci)].append(t)
            for (cj, ci), ct in sorted(cells.items()):
                (ua, ub), (va, vb) = uw[ci], vw[cj]
                umin = round(max(ua, min(min(t['p1'][0], t['p2'][0]) for t in ct) - PAD / 1000), 4)
                umax = round(min(ub, max(max(t['p1'][0], t['p2'][0]) for t in ct) + PAD / 1000), 4)
                vmin = round(max(va, min(min(t['p1'][1], t['p2'][1]) for t in ct) - PAD / 1000), 4)
                vmax = round(min(vb, max(max(t['p1'][1], t['p2'][1]) for t in ct) + PAD / 1000), 4)
                ct.sort(key=lambda t: (t['dir'], t['p1'][1], t['p1'][0]))
                name = f"L{li + 1}-R{cj + 1:02d}C{ci + 1:02d}"
                areas.append(dict(
                    wallCode=w, level=li + 1, name=name,
                    uMin=umin, vMin=vmin, uMax=umax, vMax=vmax,
                    stationX=None, stationY=None, stationTheta=None,
                    tasks=[dict(seq=k + 1, name=f"{t['dir']}{k + 1:02d}", seamType=t['seamType'],
                                startU=t['p1'][0], startV=t['p1'][1], endU=t['p2'][0], endV=t['p2'][1],
                                sectionDxfId=f"WALL {w}", profileId='PROF-1',   # profileId 임시값
                                taskId=str(uuid.uuid5(NS, f"{TANK_ID}/{w}/{t['p1']}/{t['p2']}")))
                           for k, t in enumerate(ct)],
                    corners=[[umin, vmax], [umax, vmax], [umax, vmin], [umin, vmin]],
                    stationStandoffM=None,
                    areaId=str(uuid.uuid5(NS, f"{TANK_ID}/{w}/{name}"))))
    return areas, excluded, stats


# ── 서버 등록 규칙 재현 검증 (POST /api/areas · /api/areas/{id}/tasks, ProjectService) ──
def validate(areas):
    s = SL45
    L, Wf = g['lengthL'], g['wFloor']
    B = Wf + 2 * hLow
    Wc = B - 2 * hUp
    face = {'B': (L, Wf, 0, 0), 'SL': (L, hLow / s, 0, SL45), 'PL': (L, hLow / s, 0, SL45),
            'SM': (L, hWall, hLow, 1), 'PM': (L, hWall, hLow, 1),
            'SU': (L, hUp / SU45, hLow + hWall, SU45), 'PU': (L, hUp / SU45, hLow + hWall, SU45),
            'T': (L, Wc, H, 0), 'F': (B, H, 0, 1), 'A': (B, H, 0, 1)}

    def half(v):
        if v <= hLow: return Wf / 2 + v / hLow * (B / 2 - Wf / 2)
        if v <= hLow + hWall: return B / 2
        return B / 2 - (v - hLow - hWall) / hUp * (B / 2 - Wc / 2)

    def on_seg(px, py, ax, ay, bx, by, eps=1e-9):   # AreaGeometry.OnSegment
        cr = (bx - ax) * (py - ay) - (by - ay) * (px - ax)
        if abs(cr) > eps * max(1, abs(bx - ax) + abs(by - ay)): return False
        return min(ax, bx) - eps <= px <= max(ax, bx) + eps and min(ay, by) - eps <= py <= max(ay, by) + eps

    def pip(x, y, poly):                            # AreaGeometry.PointInPolygon
        ins, j = False, len(poly) - 1
        for i in range(len(poly)):
            (xi, yi), (xj, yj) = poly[i], poly[j]
            if on_seg(x, y, xi, yi, xj, yj): return True
            if ((yi > y) != (yj > y)) and x < (xj - xi) * (y - yi) / (yj - yi) + xi: ins = not ins
            j = i
        return ins

    err = Counter()
    names, ids = Counter(), Counter()
    for a in areas:
        ul, vl, z0, kz = face[a['wallCode']]
        c = a['corners']
        us, vs = [p[0] for p in c], [p[1] for p in c]
        if any(not (0 <= p[0] <= ul and 0 <= p[1] <= vl) for p in c): err['코너가 면 범위 밖'] += 1
        if max(us) - min(us) < 1e-6 or max(vs) - min(vs) < 1e-6: err['영역 퇴화'] += 1
        if max(us) - min(us) > 1.44 + 1e-9 or max(vs) - min(vs) > 1.44 + 1e-9: err['1.44m 초과'] += 1
        za, zb = sorted((z0 + min(vs) * kz, z0 + max(vs) * kz))
        hits = [i + 1 for i, (lo, hi) in enumerate(BANDS) if za >= lo - 0.005 and zb <= hi + 0.005]
        if hits != [a['level']]: err['층 유도 불일치'] += 1
        if a['wallCode'] in ('A', 'F') and any(abs(p[0] - B / 2) > half(p[1]) + 1e-6 for p in c): err['격벽 팔각 밖'] += 1
        names[(a['wallCode'], a['name'])] += 1
        ids[a['areaId']] += 1
        if len({t['seq'] for t in a['tasks']}) != len(a['tasks']): err['seq 중복'] += 1
        for t in a['tasks']:
            ids[t['taskId']] += 1
            if not (pip(t['startU'], t['startV'], c) and pip(t['endU'], t['endV'], c)): err['작업이 영역 밖'] += 1
    err['영역 이름 중복'] = sum(n > 1 for n in names.values())
    err['ID 중복'] = sum(n > 1 for n in ids.values())
    return {k: v for k, v in err.items() if v}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('out', help='출력 .hdacs 경로')
    ap.add_argument('--report', help='분류·제외 보고서 JSON 경로')
    ap.add_argument('--preview', help='전개도 미리보기 PNG 경로 (matplotlib 필요)')
    args = ap.parse_args()

    areas, excluded, stats = build()
    errors = validate(areas)
    if errors:
        raise SystemExit(f'검증 실패: {errors}')

    doc = dict(version=FORMAT_VERSION, tankId=TANK_ID, geometry=GEOMETRY, areas=areas)
    with open(args.out, 'wb') as f:
        f.write(b'HDACSPRJ' + bytes([FORMAT_VERSION]))
        f.write(gzip.compress(json.dumps(doc, ensure_ascii=False, separators=(',', ':')).encode('utf-8')))

    types = Counter(t['seamType'] for a in areas for t in a['tasks'])
    per = Counter((a['wallCode'], a['level']) for a in areas)
    print(f"AREA {len(areas)} · TASK {sum(types.values())} {dict(types)} · 층 경계 제외 {len(excluded)}")
    for w in WALL_ORDER:
        print(f"  {w:2}", ' '.join(f"L{l}={per[(w, l)]}" for l in range(1, len(BANDS) + 1) if per[(w, l)]), dict(stats[w]))
    if args.report:
        json.dump(dict(geometry=GEOMETRY, bands=BANDS, types=dict(types),
                       per_wall_level={f'{w}-L{l}': n for (w, l), n in sorted(per.items())},
                       classification={w: dict(c) for w, c in stats.items()}, excluded=excluded),
                  open(args.report, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    if args.preview:
        render_preview(areas, excluded, args.preview)


def render_preview(areas, excluded, path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.patches import Polygon, Patch
    B = g['wFloor'] + 2 * hLow
    Wc = B - 2 * hUp
    L = g['lengthL']
    size = {'B': (L, g['wFloor']), 'SL': (L, hLow / SL45), 'PL': (L, hLow / SL45), 'SM': (L, hWall), 'PM': (L, hWall),
            'SU': (L, hUp / SU45), 'PU': (L, hUp / SU45), 'T': (L, Wc), 'F': (B, H), 'A': (B, H)}

    def octagon():
        vs = [0, hLow, hLow + hWall, H]
        half = [g['wFloor'] / 2, B / 2, B / 2, Wc / 2]
        return [(B / 2 - hw, v) for hw, v in zip(half, vs)] + [(B / 2 + hw, v) for hw, v in reversed(list(zip(half, vs)))]

    col = {1: '#2a9d8f', 2: '#e9c46a', 3: '#f4a261', 4: '#6c8ebf'}
    fig, axs = plt.subplots(5, 2, figsize=(18, 26))
    for ax, w in zip(axs.flat, ['T', 'PU', 'SU', 'PM', 'SM', 'PL', 'SL', 'B', 'A', 'F']):
        ul, vl = size[w]
        cfg = WALLS[w]
        ax.add_patch(Polygon(octagon() if w in ('A', 'F') else [(0, 0), (ul, 0), (ul, vl), (0, vl)],
                             closed=True, fc='#f4f4f4', ec='k', lw=1))
        if cfg['kz']:
            for z in LZ[1:]:
                v = (z - cfg['z0']) / cfg['kz']
                if 0 < v < vl:
                    ax.axhline(v, color='r', ls='--', lw=1)
        mine = [a for a in areas if a['wallCode'] == w]
        for a in mine:
            ax.add_patch(Polygon(a['corners'], closed=True, fc=col[a['level']], alpha=.35, ec=col[a['level']], lw=.6))
            for t in a['tasks']:
                c4 = t['seamType'] == 'CROSS4'
                ax.plot([t['startU'], t['endU']], [t['startV'], t['endV']], color='#d6008f' if c4 else '#333', lw=1.4 if c4 else .4)
        for e in excluded:
            if e['wall'] == w:
                ax.plot([e['start'][0], e['end'][0]], [e['start'][1], e['end'][1]], color='r', lw=2)
        ax.set_title(f"{w}  (u 0-{ul:.3f} m, v 0-{vl:.3f} m)  AREA {len(mine)} / TASK {sum(len(a['tasks']) for a in mine)}")
        ax.set_xlim(-0.3, ul * 1.06)
        ax.set_ylim(-0.3, vl + 0.3)
        ax.set_aspect('equal')
        ax.tick_params(labelsize=7)
    fig.legend(handles=[Patch(color=col[l], alpha=.5, label=f'L{l}') for l in col]
               + [Patch(color='#d6008f', label='CROSS4 task'), Patch(color='r', label='excluded (crosses level boundary)')],
               loc='upper center', ncol=6)
    plt.tight_layout(rect=(0, 0, 1, .985))
    plt.savefig(path, dpi=110)


if __name__ == '__main__':
    main()
