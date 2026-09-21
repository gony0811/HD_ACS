"""Add review layers to WALL A DXF for AutoCAD DWG conversion.

This preserves the original DXF and writes a separate ASCII DXF. All overlay
coordinates are raw DXF millimetres from wall-a-level-areas.json.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "drawing/2D도면/WALL A.dxf"
PLAN = ROOT / "drawing/area-preview/wall-a-level-areas.json"
OUTPUT = ROOT / "drawing/area-preview/WALL_A_cobot_review.dxf"

LAYERS = {
    "ACS_AREA_L0": 3,       # green
    "ACS_AREA_L1": 4,       # cyan
    "ACS_AREA_L2": 2,       # yellow
    "ACS_AREA_L3": 6,       # magenta
    "ACS_LINE_H": 30,      # orange
    "ACS_LINE_V": 30,
    "ACS_CROSS3": 200,     # violet
    "ACS_CROSS4": 206,
    "ACS_LEVEL_BAND": 8,   # grey
    "ACS_VERIFY_L2_LOW": 1,# red
    "ACS_LABEL": 7,
}


def pair(code, value):
    return f"{code:>3}\n{value}\n"


class Writer:
    def __init__(self):
        self.next_handle = 0x10000
        self.entities = []

    def handle(self):
        value = f"{self.next_handle:X}"
        self.next_handle += 1
        return value

    def entity(self, typ, layer, subclass, fields):
        body = (pair(0,typ) + pair(5,self.handle()) + pair(330,"23")
                + pair(100,"AcDbEntity") + pair(8,layer)
                + pair(100,subclass) + "".join(pair(k,v) for k,v in fields))
        self.entities.append(body)

    def line(self, layer, x0,y0,x1,y1):
        self.entity("LINE",layer,"AcDbLine",[(10,x0),(20,y0),(30,0),(11,x1),(21,y1),(31,0)])

    def rect(self, layer, x0,y0,x1,y1):
        pts=((x0,y0),(x1,y0),(x1,y1),(x0,y1))
        fields=[(90,4),(70,1)]
        for x,y in pts: fields.extend(((10,x),(20,y)))
        self.entity("LWPOLYLINE",layer,"AcDbPolyline",fields)

    def circle(self, layer, x,y,radius=65):
        self.entity("CIRCLE",layer,"AcDbCircle",[(10,x),(20,y),(30,0),(40,radius)])

    def label(self, layer, x,y,value,height=78):
        self.entity("TEXT",layer,"AcDbText",[(10,x),(20,y),(30,0),(40,height),(1,value),(7,"Default"),(100,"AcDbText"),(73,0)])


def layer_record(writer, name, color):
    return (pair(0,"LAYER") + pair(5,writer.handle()) + pair(330,"1")
            + pair(100,"AcDbSymbolTableRecord") + pair(100,"AcDbLayerTableRecord")
            + pair(2,name) + pair(70,0) + pair(62,color) + pair(6,"Continuous")
            + pair(370,25) + pair(390,"A"))


def build():
    plan=json.loads(PLAN.read_text(encoding="utf-8"))
    writer=Writer()
    records="".join(layer_record(writer,name,color) for name,color in LAYERS.items())
    for level in plan["levels"]:
        n=level["level"]
        for area in level["areas"]:
            x0,y0,x1,y1=area["bbox_dxf_mm"]
            writer.rect(f"ACS_AREA_L{n}",x0,y0,x1,y1)
            writer.label("ACS_LABEL",x0+35,y1+45,area["id"],68)
            for item in area["work_items"]:
                a,b,c,d=item["bbox_dxf_mm"]
                if item["method"]=="LINE":
                    writer.line(f"ACS_LINE_{item['direction']}",a,b,c,d)
                else:
                    writer.circle(f"ACS_{item['method']}",a,b)
                    writer.label(f"ACS_{item['method']}",a+72,b+45,item["method"],55)

    floor_y=plan["assumptions"]["floor_y_dxf_mm"]
    level_z=(0,3630,6630,10530)
    for n,z in enumerate(level_z):
        y=floor_y+z
        writer.line("ACS_LEVEL_BAND",16733.786864409154,y,34011.386864409156,y)
        writer.line("ACS_LEVEL_BAND",16733.786864409154,y+1440,34011.386864409156,y+1440)
        writer.label("ACS_LEVEL_BAND",34500,y+500,f"LEVEL {n} REACH 0-1440",95)

    # The horizontal weld 28.8 mm above level 2 remains pending site access check.
    writer.line("ACS_VERIFY_L2_LOW",16897.587,16710.1,33847.587,16710.1)
    writer.label("ACS_VERIFY_L2_LOW",19000,16480,"L2 LOW WELD +28.8mm VERIFY CAMERA REACH",95)
    writer.label("ACS_LABEL",16000,24600,"ACS COBOT AREA REVIEW ONLY - NOT RELEASED FOR ROBOT",145)

    source=SOURCE.read_bytes().decode("cp949")
    layer_start=source.index("  0\nTABLE\n  2\nLAYER\n")
    layer_end=source.index("  0\nENDTAB\n",layer_start)
    table=source[layer_start:layer_end]
    match=re.search(r"(\n 70\n\s*)(\d+)(\n)",table)
    if match is None:
        raise ValueError("LAYER table count not found")
    table=table[:match.start(2)]+str(int(match.group(2))+len(LAYERS))+table[match.end(2):]
    source=source[:layer_start]+table+records+source[layer_end:]
    entity_start=source.index("  0\nSECTION\n  2\nENTITIES\n")
    entity_end=source.index("  0\nENDSEC\n",entity_start)
    source=source[:entity_end]+"".join(writer.entities)+source[entity_end:]
    source=re.sub(r"(\$HANDSEED\n  5\n)[0-9A-F]+",lambda m:m.group(1)+f"{writer.next_handle:X}",source,count=1)
    OUTPUT.write_bytes(source.encode("cp949"))
    print(f"{OUTPUT}: {len(LAYERS)} layers, {len(writer.entities)} overlay entities")


if __name__=="__main__":
    build()
