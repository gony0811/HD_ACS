#!/usr/bin/env python3
"""Build the checked-in CT1 project with one production F-wall inspection area.

The source is the review definition SM-A0001 in the Obsidian LLM WIKI attachment
(`drawing/area-preview/area-task-coordinates.json`).  Among the vertical-wall
definitions F/A/PM/SM, SM has the largest unique task total (408).  SM-A0001 is
the first deterministic SM candidate with the maximum per-area assignment
(three tasks: one horizontal and two vertical).

SM-A0001 is translated +3.0 m on the F-wall u axis so that the complete 1.44 m
square and every task lie inside the lower edge of the CT1 octagonal bulkhead.
No scaling or rotation is applied.
"""
from __future__ import annotations

import gzip
import json
import uuid
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "docs" / "CT1.hdacs"
MAGIC = b"HDACSPRJ"
FORMAT_VERSION = 3
ID_NAMESPACE = uuid.UUID("6f1c1d0e-4b7a-4c1e-9d2b-3a5e7c9f0b12")


def stable_id(kind: str, source_id: str) -> str:
    return str(uuid.uuid5(ID_NAMESPACE, f"CT1/F/{kind}/{source_id}"))


def build_document() -> dict:
    u_offset = 3.0
    corners = [[3.0, 0.0], [4.44, 0.0], [4.44, 1.44], [3.0, 1.44]]
    source_tasks = [
        ("SM-T00001", "H", 0.195, 0.0, 0.915, 0.0),
        ("SM-T00169", "V", 0.0, 0.12902, 0.0, 0.84902),
        ("SM-T00177", "V", 0.735, 0.12902, 0.735, 0.84902),
    ]
    tasks = [
        {
            "seq": seq,
            "name": f"{direction}{seq:02d}",
            "seamType": "LINE",
            "startU": round(start_u + u_offset, 5),
            "startV": start_v,
            "endU": round(end_u + u_offset, 5),
            "endV": end_v,
            "sectionDxfId": "WALL SM",
            "profileId": "PROF-1",
            "sourceId": stable_id("task", source_id),
        }
        for seq, (source_id, direction, start_u, start_v, end_u, end_v)
        in enumerate(source_tasks, 1)
    ]
    return {
        "version": FORMAT_VERSION,
        "tankId": "CT1",
        "geometry": {
            "lengthL": 30,
            "wFloor": 10,
            "thetaLowDeg": 45,
            "hLow": 3,
            "hWall": 8,
            "thetaUpDeg": 45,
            "hUp": 2,
            "levelZ": [0, 3.2, 6.4, 9.6],
            "originOx": 0,
            "originOy": 0,
            "reachZMin": 0,
            "reachZMax": 3.6,
        },
        "areas": [
            {
                "wallCode": "F",
                "level": 1,
                "name": "F-SM-A0001",
                "uMin": 3.0,
                "vMin": 0.0,
                "uMax": 4.44,
                "vMax": 1.44,
                "stationX": None,
                "stationY": None,
                "stationTheta": None,
                "tasks": tasks,
                "corners": corners,
                "stationStandoffM": None,
                "sourceId": stable_id("area", "SM-A0001"),
            }
        ],
        "calibrations": [],
        "scenarios": [],
    }


def validate(document: dict) -> None:
    area, = document["areas"]
    assert area["wallCode"] == "F"
    assert len(area["tasks"]) == 3
    assert area["uMax"] - area["uMin"] <= 1.44 + 1e-9
    assert area["vMax"] - area["vMin"] <= 1.44 + 1e-9
    # F-wall lower boundary for this geometry: u is in [3-v, 13+v].
    for u, v in area["corners"]:
        assert 3.0 - v <= u <= 13.0 + v
    for task in area["tasks"]:
        for u, v in ((task["startU"], task["startV"]), (task["endU"], task["endV"])):
            assert area["uMin"] <= u <= area["uMax"]
            assert area["vMin"] <= v <= area["vMax"]


def main() -> None:
    document = build_document()
    validate(document)
    payload = json.dumps(document, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    OUTPUT.write_bytes(MAGIC + bytes([FORMAT_VERSION]) + gzip.compress(payload, mtime=0))
    print(f"{OUTPUT}: F AREA 1, TASK 3 (source SM-A0001)")


if __name__ == "__main__":
    main()
