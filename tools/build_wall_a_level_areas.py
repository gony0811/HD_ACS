"""Draw review-only WALL A inspection AREAs for the four platform levels.

Uses the existing DXF-derived 720 mm task candidates. Coordinates stay in raw
DXF millimetres; robot station poses and actual reach remain unverified.
"""
from __future__ import annotations

import csv
import json
from pathlib import Path
from analyze_dxf_regions import entities, first, points

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "drawing/area-preview/area-task-coordinates.json"
OUT = ROOT / "drawing/area-preview"

WIDTH = 17277.6
HEIGHT = 12957.6
LOW = 3735.3
UP = 2655.3
MAX_AREA = 1440.0
LEVEL_Z = (0.0, 3630.0, 6630.0, 10530.0)
CX, CY = 25372.586864409153, 16530.1
FLOOR_Y = CY - HEIGHT / 2
EPS = 0.04


def sheet_edges():
    """Raw membrane-sheet straight edges, including diagonal and short lines."""
    path = ROOT / "drawing/2D도면/WALL A.dxf"
    result = []
    for e in entities(path):
        if first(e, 8) != "KC-2B Membrane Sheet UM" or e["type"] not in ("LINE", "LWPOLYLINE"):
            continue
        pts = points(e)
        edges = list(zip(pts, pts[1:]))
        if e["type"] == "LWPOLYLINE" and int(first(e,70,"0")) & 1 and pts[-1] != pts[0]:
            edges.append((pts[-1],pts[0]))
        result.extend(edges)
    return result


def wall_bounds(y):
    z = y - FLOOR_Y
    half = (WIDTH - 2 * LOW) / 2 + min(max(z, 0), LOW)
    if z > HEIGHT - UP:
        half -= z - (HEIGHT - UP)
    return CX - half, CX + half


def fits(box, lo, hi):
    x0, y0, x1, y1 = box
    if x1 - x0 > MAX_AREA + EPS or y1 - y0 > MAX_AREA + EPS:
        return False
    if y0 < lo - EPS or y1 > hi + EPS:
        return False
    return all(x0 >= wall_bounds(y)[0] - EPS and x1 <= wall_bounds(y)[1] + EPS for y in (y0, y1))


def union(a, b):
    return min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3])


def cross_points(seams):
    """Candidate T and four-way intersections of long axis-aligned weld seams."""
    hlines = [s for s in seams if s["axis"] == "H" and s["hi"]-s["lo"] >= 720]
    vlines = [s for s in seams if s["axis"] == "V" and s["hi"]-s["lo"] >= 720]
    found = {}
    for h in hlines:
        for v in vlines:
            x,y = v["fixed"],h["fixed"]
            if not (h["lo"]-EPS <= x <= h["hi"]+EPS and v["lo"]-EPS <= y <= v["hi"]+EPS):
                continue
            arms = sum((x > h["lo"]+EPS, x < h["hi"]-EPS,
                        y > v["lo"]+EPS, y < v["hi"]-EPS))
            if arms >= 3:
                key = (round(x,2),round(y,2))
                found[key] = max(arms,found.get(key,0))
    return [(x,y,kind) for (x,y),kind in sorted(found.items())]


def plan(tasks, lo, hi):
    """Greedy coverage of task bounding boxes, respecting the octagonal wall."""
    remaining = set(range(len(tasks)))
    groups = []
    while remaining:
        best = None
        for seed in sorted(remaining):
            box = tasks[seed]["box"]
            if not fits(box, lo, hi):
                continue
            owned = {seed}
            while True:
                options = []
                for i in remaining - owned:
                    candidate = union(box, tasks[i]["box"])
                    if fits(candidate, lo, hi):
                        potential = sum(fits(union(candidate, tasks[j]["box"]), lo, hi) for j in remaining - owned)
                        options.append((potential, -(candidate[2] - candidate[0]) * (candidate[3] - candidate[1]), -i, candidate, i))
                if not options:
                    break
                _, _, _, box, added = max(options)
                owned.add(added)
            key = (len(owned), -(box[2] - box[0]) * (box[3] - box[1]), -seed)
            if best is None or key > best[0]:
                best = (key, box, owned)
        if best is None:
            raise ValueError(f"{len(remaining)} tasks cannot fit inside WALL A contour")
        _, box, owned = best
        groups.append({"box": box, "tasks": sorted(owned)})
        remaining -= owned
    groups.sort(key=lambda g: (g["box"][0], g["box"][1]))
    return groups


def expanded(box, lo, hi):
    """Add drawing margin without changing coverage or crossing wall/level limits."""
    x0, y0, x1, y1 = box
    choices = []
    # Horizontal tasks may already use all 1440 mm in x; they still need a
    # nonzero AREA height. Expand the two axes independently.
    for px in (0, 5, 20, 40, 80):
        for py in (0, 5, 20, 40, 80):
            candidate = (x0 - px, max(lo, y0 - py), x1 + px, min(hi, y1 + py))
            if candidate[2] - candidate[0] > EPS and candidate[3] - candidate[1] > EPS and fits(candidate, lo, hi):
                choices.append(candidate)
    if not choices:
        raise ValueError(f"Cannot give AREA a positive width and height: {box}")
    return max(choices, key=lambda c: ((c[2]-c[0]) * (c[3]-c[1]), c[2]-c[0], c[3]-c[1]))


def svg(rows, tasks_by_level, top_lines, sheet_lines, show_sheet_lines=False):
    colors = ("#097d72", "#2467ae", "#a65b0b", "#8b4aa3")
    scale, left, top, panel_h = 0.091, 125, 110, 284
    parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1840" height="1300" viewBox="0 0 1840 1300">',
             '<rect width="1840" height="1300" fill="#f7f9fc"/>',
             '<text x="70" y="52" font-family="sans-serif" font-size="30" font-weight="bold" fill="#172538">WALL A · 층별 코봇 AREA 배치 (검토용)</text>',
             '<text x="70" y="80" font-family="sans-serif" font-size="17" fill="#46566c">각 층 바닥부터 1440 mm · 최하단 수평 용접선 제외 · DXF 좌표 mm · AREA 최대 1440 × 1440</text>']
    for level, areas in enumerate(rows):
        lo = FLOOR_Y + LEVEL_Z[level]
        hi = lo + MAX_AREA
        ybase = top + level * panel_h
        color = colors[level]
        def X(x): return left + (x - (CX - WIDTH / 2)) * scale
        def Y(y): return ybase + 186 - (y - lo) * scale
        # Level band and true interior-wall contour at both band ends.
        lx0, rx0 = wall_bounds(lo)
        lx1, rx1 = wall_bounds(hi)
        poly = f"{X(lx0):.1f},{Y(lo):.1f} {X(rx0):.1f},{Y(lo):.1f} {X(rx1):.1f},{Y(hi):.1f} {X(lx1):.1f},{Y(hi):.1f}"
        parts += [f'<rect x="70" y="{ybase-28}" width="1700" height="247" rx="15" fill="white" stroke="#d9e1ea"/>',
                  f'<polygon points="{poly}" fill="#eef3f7" stroke="#879aad" stroke-width="2"/>',
                  f'<text x="91" y="{ybase+5}" font-family="sans-serif" font-size="22" font-weight="bold" fill="{color}">{level}단</text>',
                  f'<text x="91" y="{ybase+31}" font-family="sans-serif" font-size="14" fill="#46566c">z {LEVEL_Z[level]:.0f}–{LEVEL_Z[level]+MAX_AREA:.0f} · AREA {len(areas)}개 · 작업 {len(tasks_by_level[level])}개</text>']
        parts.append(f'<defs><clipPath id="wall-level-{level}"><polygon points="{poly}"/></clipPath></defs>')
        display = "inline" if show_sheet_lines else "none"
        parts.append(f'<g class="sheet-boundaries" clip-path="url(#wall-level-{level})" style="display:{display}" stroke="#525e6b" stroke-width="1.5" stroke-opacity=".7">')
        for a,b in sheet_lines:
            if max(a[1],b[1])<lo or min(a[1],b[1])>hi:
                continue
            parts.append(f'<line x1="{X(a[0]):.1f}" y1="{Y(a[1]):.1f}" x2="{X(b[0]):.1f}" y2="{Y(b[1]):.1f}"/>')
        parts.append('</g>')
        parts.append(f'<g clip-path="url(#wall-level-{level})" stroke="#71869b" stroke-width="1.3" stroke-dasharray="5 4">')
        vertical = [s for s in top_lines if s["axis"] == "V"]
        horizontal = [s for s in top_lines if s["axis"] == "H"]
        for s in vertical:
            a,b=max(lo,s["lo"]),min(hi,s["hi"])
            if b>a:
                parts.append(f'<line x1="{X(s["fixed"]):.1f}" y1="{Y(a):.1f}" x2="{X(s["fixed"]):.1f}" y2="{Y(b):.1f}"/>')
        for s in horizontal:
            if lo<=s["fixed"]<=hi:
                parts.append(f'<line x1="{X(s["lo"]):.1f}" y1="{Y(s["fixed"]):.1f}" x2="{X(s["hi"]):.1f}" y2="{Y(s["fixed"]):.1f}"/>')
        parts.append('</g>')
        parts.append(f'<g clip-path="url(#wall-level-{level})" fill="#536d86">')
        for h in horizontal:
            y=h["fixed"]
            if not lo<=y<=hi:
                continue
            for v in vertical:
                x=v["fixed"]
                if h["lo"]-EPS<=x<=h["hi"]+EPS and v["lo"]-EPS<=y<=v["hi"]+EPS:
                    parts.append(f'<circle cx="{X(x):.1f}" cy="{Y(y):.1f}" r="2.4"/>')
        parts.append('</g>')
        for t in tasks_by_level[level]:
            if t["method"] != "LINE":
                continue
            x0,y0,x1,y1=t["box"]
            parts.append(f'<line x1="{X(x0):.1f}" y1="{Y(y0):.1f}" x2="{X(x1):.1f}" y2="{Y(y1):.1f}" stroke="#d07036" stroke-width="4" stroke-linecap="round"/>')
        for i,a in enumerate(areas,1):
            x0,y0,x1,y1=a["draw_box"]
            parts.append(f'<rect x="{X(x0):.1f}" y="{Y(y1):.1f}" width="{(x1-x0)*scale:.1f}" height="{(y1-y0)*scale:.1f}" fill="{color}" fill-opacity=".20" stroke="{color}" stroke-width="2"><title>{level}-A{i:02d}: {len(a["tasks"])} tasks, DXF x {x0:.1f}–{x1:.1f}, y {y0:.1f}–{y1:.1f}</title></rect>')
            parts.append(f'<text x="{X((x0+x1)/2):.1f}" y="{Y((y0+y1)/2)+5:.1f}" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#172538">{i}</text>')
        for t in tasks_by_level[level]:
            if not t["id"].startswith("A-CROSS"):
                continue
            x,y=t["box"][0],t["box"][1]
            px,py=X(x),Y(y)
            parts.append(f'<polygon points="{px:.1f},{py-8:.1f} {px+8:.1f},{py:.1f} {px:.1f},{py+8:.1f} {px-8:.1f},{py:.1f}" fill="#7425a8" stroke="white" stroke-width="1.5"><title>{t["id"]}</title></polygon>')
            parts.append(f'<text x="{px:.1f}" y="{py+3:.1f}" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="bold" fill="white">{t["method"][-1]}</text>')
        parts.append(f'<text x="{left}" y="{ybase+210}" font-family="sans-serif" font-size="13" fill="#46566c">좌 → 우: DXF x {CX-WIDTH/2:.1f}–{CX+WIDTH/2:.1f} mm | 세로: 해당 층 바닥에서 0–1440 mm</text>')
    parts.append('<text x="70" y="1270" font-family="sans-serif" font-size="15" fill="#46566c">회색 점선·점: Top  ·  짙은 실선(선택): 막 시트 경계 후보  ·  주황: LINE  ·  보라 마름모: CROSS3/4</text></svg>')
    return "\n".join(parts)


def overview_svg():
    colors = ("#097d72", "#2467ae", "#a65b0b", "#8b4aa3")
    scale, left, bottom = 0.038, 110, 650
    def X(x): return left + (x - (CX - WIDTH / 2)) * scale
    def Y(y): return bottom - (y - FLOOR_Y) * scale
    vertices = [(CX-(WIDTH-2*LOW)/2,FLOOR_Y), (CX+(WIDTH-2*LOW)/2,FLOOR_Y),
                (CX+WIDTH/2,FLOOR_Y+LOW), (CX+WIDTH/2,FLOOR_Y+HEIGHT-UP),
                (CX+(WIDTH-2*UP)/2,FLOOR_Y+HEIGHT), (CX-(WIDTH-2*UP)/2,FLOOR_Y+HEIGHT),
                (CX-WIDTH/2,FLOOR_Y+HEIGHT-UP), (CX-WIDTH/2,FLOOR_Y+LOW)]
    poly=" ".join(f"{X(x):.1f},{Y(y):.1f}" for x,y in vertices)
    parts=['<svg xmlns="http://www.w3.org/2000/svg" width="920" height="740" viewBox="0 0 920 740">',
           '<rect width="920" height="740" fill="#f7f9fc"/>',
           '<text x="55" y="55" font-family="sans-serif" font-size="26" font-weight="bold" fill="#172538">WALL A · 층별 도달 밴드</text>',
           '<text x="55" y="85" font-family="sans-serif" font-size="15" fill="#46566c">색상 = 각 층 바닥부터 1440 mm · 회색 = 이 가정으로 검사할 수 없는 높이</text>',
           f'<polygon points="{poly}" fill="#e5e9ee" stroke="#72869a" stroke-width="3"/>']
    for level,z in enumerate(LEVEL_Z):
        lo,hi=FLOOR_Y+z,FLOOR_Y+z+MAX_AREA
        l0,r0=wall_bounds(lo);l1,r1=wall_bounds(hi)
        band=" ".join(f"{X(x):.1f},{Y(y):.1f}" for x,y in ((l0,lo),(r0,lo),(r1,hi),(l1,hi)))
        parts.append(f'<polygon points="{band}" fill="{colors[level]}" fill-opacity=".7" stroke="white" stroke-width="2"/>')
        parts.append(f'<text x="{X(CX):.1f}" y="{Y((lo+hi)/2)+5:.1f}" text-anchor="middle" font-family="sans-serif" font-size="17" font-weight="bold" fill="white">{level}단  z={z:.0f}–{z+MAX_AREA:.0f}</text>')
    parts.append('<text x="55" y="706" font-family="sans-serif" font-size="15" fill="#46566c">로봇 승강 범위·높이별 정차 가능성 확인 전의 기하 검토도</text></svg>')
    return "\n".join(parts)


def main():
    data = json.loads(SOURCE.read_text(encoding="utf-8"))
    wall = next(w for w in data["walls"] if w["wall"] == "A")
    crosses = cross_points(wall["seams"])
    ox, oy = wall["origin_dxf"]
    rows, tasks_by_level = [], []
    csv_rows = []
    for level,z in enumerate(LEVEL_Z):
        lo,hi = FLOOR_Y + z, FLOOR_Y + z + MAX_AREA
        selected=[]
        for t in wall["tasks"]:
            ax,ay = t["start"][0]+ox,t["start"][1]+oy
            bx,by = t["end"][0]+ox,t["end"][1]+oy
            box=(min(ax,bx),min(ay,by),max(ax,bx),max(ay,by))
            if box[1] < lo-EPS or box[3] > hi+EPS:
                continue
            if t["direction"] == "H" and abs(ay-10215.1)<EPS:
                continue  # User excluded the lowest weld line.
            selected.append({"id":t["id"],"method":"LINE","direction":t["direction"],"box":box})
        if level == 0:
            # The 720 mm top-to-top task list starts at y=10410.126. Preserve
            # the real vertical weld from the lowest horizontal seam up to
            # that first top instead of silently discarding its lower 195 mm.
            first_top = min(s["fixed"] for s in wall["top_lines"] if s["axis"] == "H")
            for s in wall["seams"]:
                if s["axis"] != "V" or abs(s["lo"]-10215.1)>EPS or s["hi"]-s["lo"]<1000:
                    continue
                selected.append({"id":f'A-VLOW-{round(s["fixed"]):05d}',"method":"LINE","direction":"V",
                                 "box":(s["fixed"],s["lo"],s["fixed"],first_top)})
        for x,y,kind in crosses:
            if lo-EPS<=y<=hi+EPS and not (level==0 and abs(y-10215.1)<EPS):
                selected.append({"id":f"A-CROSS{kind}-{round(x):05d}-{round(y):05d}",
                                 "method":f"CROSS{kind}","direction":None,"box":(x,y,x,y)})
        groups=plan(selected,lo,hi)
        for i,g in enumerate(groups,1):
            g["id"]=f"A-L{level}-A{i:02d}"
            g["draw_box"]=expanded(g["box"],lo,hi)
            x0,y0,x1,y1=g["draw_box"]
            items=[selected[j] for j in g["tasks"]]
            csv_rows.append((g["id"],level,round(x0,3),round(y0,3),round(x1,3),round(y1,3),
                             sum(t["method"]=="LINE" and t["direction"]=="H" for t in items),
                             sum(t["method"]=="LINE" and t["direction"]=="V" for t in items),
                             sum(t["method"]=="CROSS3" for t in items),
                             sum(t["method"]=="CROSS4" for t in items),
                             ";".join(t["id"] for t in items)))
        rows.append(groups)
        tasks_by_level.append(selected)
        print(f"{level}단: {len(selected)} tasks, {len(groups)} AREAs, band DXF y {lo:.1f}–{hi:.1f}")
    OUT.mkdir(exist_ok=True)
    raw_sheet_edges = sheet_edges()
    base_svg = svg(rows,tasks_by_level,wall["top_lines"],raw_sheet_edges)
    (OUT/"wall-a-level-areas.svg").write_text(base_svg,encoding="utf-8")
    (OUT/"wall-a-level-areas-with-sheet-lines.svg").write_text(
        svg(rows,tasks_by_level,wall["top_lines"],raw_sheet_edges,show_sheet_lines=True),encoding="utf-8")
    viewer = '''<!doctype html><html lang="ko"><meta charset="utf-8"><title>WALL A 판 경계선 비교</title>
<style>body{font-family:system-ui,sans-serif;margin:0;background:#e9eef4;color:#172538}header{position:sticky;top:0;background:white;padding:14px 24px;box-shadow:0 2px 8px #b8c5d0;z-index:2}label{margin-right:24px;font-size:16px}main{max-width:1840px;margin:16px auto;background:white}svg{width:100%;height:auto}small{color:#526273}</style>
<header><strong>WALL A 층별 AREA</strong>　<label><input id="sheet" type="checkbox"> 원본 막 시트 선 보기 (판 경계 후보)</label><small>짙은 실선은 DXF Membrane Sheet 선입니다. 판 수 확정 전의 비교용입니다.</small></header>
<main>__SVG__</main><script>document.querySelector('#sheet').addEventListener('change',e=>{document.querySelectorAll('.sheet-boundaries').forEach(g=>g.style.display=e.target.checked?'inline':'none')});</script></html>'''
    (OUT/"wall-a-level-areas-toggle.html").write_text(viewer.replace("__SVG__",base_svg),encoding="utf-8")
    (OUT/"wall-a-level-reach-overview.svg").write_text(overview_svg(),encoding="utf-8")
    with (OUT/"wall-a-level-areas.csv").open("w",newline="",encoding="utf-8") as f:
        writer=csv.writer(f)
        writer.writerow(("area_id","level","x_min_dxf_mm","y_min_dxf_mm","x_max_dxf_mm","y_max_dxf_mm",
                         "line_h_count","line_v_count","cross3_count","cross4_count","task_ids"))
        writer.writerows(csv_rows)
    (OUT/"wall-a-level-areas.json").write_text(json.dumps({"status":"review_only",
        "work_methods":{"LINE":"straight weld; direction H or V","CROSS3":"three plates / three weld branches","CROSS4":"four plates / four weld branches"},
        "assumptions":{"floor_y_dxf_mm":FLOOR_Y,"max_area_mm":MAX_AREA,"reach_from_level_floor_mm":[0,1440],
                       "excluded_lowest_weld_y_dxf_mm":10215.1,
                       "cross_classification":"candidate based on joined DXF weld branches; plate count not independently verified",
                       "placement":"Greedy task coverage within wall contour; global optimum and robot station reach not verified."},
        "levels":[{"level":i,"tasks":len(tasks_by_level[i]),"areas":[{"id":g["id"],"bbox_dxf_mm":g["draw_box"],
            "work_items":[{"id":tasks_by_level[i][j]["id"],"method":tasks_by_level[i][j]["method"],
                           "direction":tasks_by_level[i][j]["direction"],"bbox_dxf_mm":tasks_by_level[i][j]["box"]} for j in g["tasks"]],
            "task_ids":[tasks_by_level[i][j]["id"] for j in g["tasks"]]} for g in rows[i]]} for i in range(4)]},
        ensure_ascii=False,indent=2),encoding="utf-8")


if __name__ == "__main__":
    main()
