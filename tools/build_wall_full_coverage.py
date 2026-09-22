"""Build full-height, review-only weld AREA overlays for all ten WALL DXFs.

1440 mm limits each AREA's dimensions. It is not used as a camera reach limit.
The membrane-sheet long straight segments are weld candidates; site verification
is still needed for access, panel junction type, and inclined-face scaling.
"""
from __future__ import annotations

import json
import math
import re
from collections import defaultdict
from pathlib import Path

from build_wall_a_autocad_dxf import Writer, layer_record
from build_wall_a_level_areas import cross_points

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "drawing/area-preview/area-task-coordinates.json"
SOURCES = ROOT / "drawing/2D도면"
OUT = ROOT / "drawing/area-preview/full-coverage"
SIZE = 1440.0
LEVELS = (0.0, 3630.0, 6630.0, 10530.0)
LAYER_COLORS = {**{f"ACS_AREA_L{i}": c for i, c in enumerate((3, 4, 2, 6))},
                "ACS_LINE_H": 30, "ACS_LINE_V": 30,
                "ACS_CROSS3": 200, "ACS_CROSS4": 206,
                "ACS_LEVEL": 8, "ACS_VERIFY": 1, "ACS_LABEL": 7}


def approximate_z(wall: str, y: float, ymin: float, ymax: float) -> float:
    """Review-only level colour, not a calibrated wall-to-world transform."""
    t = max(0.0, min(1.0, (y-ymin)/(ymax-ymin)))
    if wall in ("A", "F"):
        return (y - (ymin + ymax)/2) + 12957.6/2
    if wall in ("SM", "PM"):
        return 3735.3 + t * 6567.0
    if wall in ("SL", "PL"):
        return t * 3735.3
    if wall in ("SU", "PU"):
        return 10302.3 + t * 2655.3
    return 0.0 if wall == "B" else 12957.6


def level_of(z: float) -> int:
    return min(range(4), key=lambda i: abs(LEVELS[i] - z))


def clipped_segment(s, x0, y0, x1, y1):
    a, b = s["lo"], s["hi"]
    if s["axis"] == "H":
        lo, hi = max(a, x0), min(b, x1)
        return (lo, s["fixed"], hi, s["fixed"]) if hi-lo > .01 and y0-.01 <= s["fixed"] <= y1+.01 else None
    lo, hi = max(a, y0), min(b, y1)
    return (s["fixed"], lo, s["fixed"], hi) if hi-lo > .01 and x0-.01 <= s["fixed"] <= x1+.01 else None


def inject(source: Path, output: Path, writer: Writer):
    raw = source.read_bytes().decode("cp949")
    start = raw.index("  0\nTABLE\n  2\nLAYER\n")
    end = raw.index("  0\nENDTAB\n", start)
    table = raw[start:end]
    m = re.search(r"(\n 70\n\s*)(\d+)(\n)", table)
    if m is None:
        raise ValueError(f"Missing LAYER count: {source}")
    table = table[:m.start(2)] + str(int(m.group(2))+len(LAYER_COLORS)) + table[m.end(2):]
    raw = raw[:start] + table + "".join(layer_record(writer,k,v) for k,v in LAYER_COLORS.items()) + raw[end:]
    start = raw.index("  0\nSECTION\n  2\nENTITIES\n")
    end = raw.index("  0\nENDSEC\n", start)
    raw = raw[:end] + "".join(writer.entities) + raw[end:]
    raw = re.sub(r"(\$HANDSEED\n  5\n)[0-9A-F]+", lambda m:m.group(1)+f"{writer.next_handle:X}", raw, count=1)
    output.write_bytes(raw.encode("cp949"))


def build_wall(w):
    code = w["wall"]
    xmin, ymin = w["origin_dxf"]
    xmax, ymax = xmin+w["size_drawing"][0], ymin+w["size_drawing"][1]
    # Keep short membrane-sheet runs visible as candidates; some are folds or
    # drawing details and must be confirmed against the physical weld list.
    seams = [s for s in w["seams"] if s["hi"]-s["lo"] > .01]
    if code in ("A", "F"):
        seams = [s for s in seams if not (s["axis"] == "H" and abs(s["fixed"]-ymin)<.05)]
    crosses = cross_points(seams)
    if code in ("A", "F"):
        crosses = [c for c in crosses if abs(c[1]-ymin) >= .05]
    writer = Writer()
    areas = []
    work_count = 0
    # The four chamfer DXFs have an unresolved drawing-to-surface transform.
    # A conservative 1000 mm drawing tile stays within 1440 mm at 45 degrees.
    ysize = 1000.0 if code in ("SL","PL","SU","PU") else SIZE
    for ix in range(math.ceil((xmax-xmin)/SIZE)):
        x0, x1 = xmin+ix*SIZE, min(xmax,xmin+(ix+1)*SIZE)
        for iy in range(math.ceil((ymax-ymin)/ysize)):
            y0, y1 = ymin+iy*ysize, min(ymax,ymin+(iy+1)*ysize)
            lines = [p for s in seams if (p:=clipped_segment(s,x0,y0,x1,y1))]
            nodes = [(x,y,n) for x,y,n in crosses if x0-.01 <= x <= x1+.01 and y0-.01 <= y <= y1+.01]
            if not lines and not nodes:
                continue
            z = approximate_z(code,(y0+y1)/2,ymin,ymax)
            level = level_of(z)
            area_id = f"{code}-L{level}-A{len(areas)+1:03d}"
            writer.rect(f"ACS_AREA_L{level}",x0,y0,x1,y1)
            writer.label("ACS_LABEL",x0+35,y0+90,area_id,75)
            for a,b,c,d in lines:
                direction = "H" if abs(b-d)<.01 else "V"
                writer.line(f"ACS_LINE_{direction}",a,b,c,d)
            for x,y,n in nodes:
                writer.circle(f"ACS_CROSS{n}",x,y,55)
                writer.label(f"ACS_CROSS{n}",x+65,y+35,f"CROSS{n}",50)
            work_count += len(lines)+len(nodes)
            areas.append({"id":area_id,"level_candidate":level,"bbox_dxf_mm":[x0,y0,x1,y1],
                          "line_count":len(lines),"cross3_count":sum(n==3 for _,_,n in nodes),
                          "cross4_count":sum(n==4 for _,_,n in nodes),
                          "work_items":[{"method":"LINE","direction":"H" if abs(b-d)<.01 else "V",
                                         "bbox_dxf_mm":[a,b,c,d]} for a,b,c,d in lines] +
                                       [{"method":f"CROSS{n}","point_dxf_mm":[x,y]} for x,y,n in nodes]})
    writer.label("ACS_VERIFY",xmin,ymax+250,
                 "REVIEW ONLY - 1440 AREA SIZE IS NOT CAMERA REACH",130)
    if code in ("SL","PL","SU","PU"):
        writer.label("ACS_VERIFY",xmin,ymax+450,
                     "INCLINED FACE: SURFACE SCALE AND LEVEL ACCESS VERIFY",100)
    else:
        writer.label("ACS_VERIFY",xmin,ymax+450,
                     "LEVEL COLORS ARE CANDIDATES: VERIFY CAMERA ACCESS ON SITE",100)
    output = OUT / f"WALL_{code}_cobot_full_coverage.dxf"
    inject(SOURCES/f"WALL {code}.dxf",output,writer)
    return {"wall":code,"status":"review_only","source":f"WALL {code}.dxf",
            "dxf":output.name,"areas":areas,"seam_candidates":len(seams),
            "cross_candidates":len(crosses),"work_fragments":work_count,
            "level_mapping":"approximate visual grouping; site verification required",
            "notes":["1440 mm limits AREA dimensions only, not camera reach.",
                     "All substantial straight membrane-sheet seam runs are covered across full drawing extent.",
                     "Boundary crossings can duplicate a small weld fragment in neighbouring AREAs.",
                     "Short membrane-sheet segments are visible as weld candidates; fold/detail lines need field classification."]}


def main():
    OUT.mkdir(parents=True,exist_ok=True)
    data = json.loads(DATA.read_text(encoding="utf-8"))
    result = [build_wall(w) for w in data["walls"]]
    (OUT/"full-coverage-summary.json").write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
    for r in result:
        print(r["wall"],len(r["areas"]),"AREAs",r["work_fragments"],"work fragments",r["cross_candidates"],"crosses")


if __name__ == "__main__":
    main()
