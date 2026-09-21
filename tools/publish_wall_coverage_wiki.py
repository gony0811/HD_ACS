"""Copy the user-requested WALL review artifacts into the local iCloud Obsidian vault.

Run only for an explicitly requested wiki update. Source artifacts are never
modified; historical wiki calculations are retained and marked superseded.
"""
from __future__ import annotations

import hashlib
import re
import shutil
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VAULT = Path.home() / "Library/Mobile Documents/iCloud~md~obsidian/Documents/HD현대중공업-용접검사 LLM WIKI"
ACS = VAULT / "10 HD-ACS"
ATTACH = next(p for p in ACS.iterdir() if unicodedata.normalize("NFC", p.name).startswith("91 첨부"))
FEATURE = next(p for p in ACS.iterdir() if unicodedata.normalize("NFC", p.name).startswith("03 기능"))
DEST = ATTACH / "2026-09-21 코봇 AREA 전체높이 검토"
TITLE = "HD_ACS - 2026-09-21 코봇 AREA 전체높이 검토"


def nfc(value: str) -> str:
    return unicodedata.normalize("NFC", value)


def page(title: str) -> Path:
    found = [p for p in ACS.rglob("*.md") if nfc(p.stem) == title]
    if len(found) != 1:
        raise RuntimeError(f"Expected exactly one wiki page: {title}; found {len(found)}")
    return found[0]


def add_current_note(title: str, note: str):
    path = page(title)
    body = path.read_text(encoding="utf-8")
    marker = "## 2026-09-21 현재 기준"
    block = marker + "\n\n" + note.strip() + "\n\n"
    if marker in body:
        body = re.sub(r"## 2026-09-21 현재 기준\n\n.*?(?=\n# |\n## |\Z)", "", body, count=1, flags=re.S)
    # Preserve the original dated calculation and place the pointer after H1.
    m = re.search(r"(?m)^# [^\n]+\n", body)
    if m is None:
        raise RuntimeError(f"No heading in {path}")
    body = body[:m.end()] + "\n" + block + body[m.end():]
    body = re.sub(r"(?m)^updated: \d{4}-\d{2}-\d{2}$", "updated: 2026-09-21", body, count=1)
    path.write_text(body, encoding="utf-8")


def sha(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    src = ROOT / "drawing/area-preview/full-coverage"
    originals = [p for p in src.iterdir() if p.suffix.lower() in (".dwg", ".dxf", ".json", ".md")]
    older = ROOT / "drawing/area-preview"
    originals += [older / name for name in (
        "WALL_A_cobot_review.dwg", "WALL_A_cobot_review.dxf",
        "wall-a-level-areas.svg", "wall-a-level-areas-with-sheet-lines.svg",
        "wall-a-level-areas-toggle.html", "wall-a-level-reach-overview.svg",
        "wall-a-level-areas.csv", "wall-a-level-areas.json")]
    originals += [ROOT / "tools" / name for name in (
        "build_wall_full_coverage.py", "build_wall_a_autocad_dxf.py",
        "build_wall_a_level_areas.py")]
    originals += [ROOT / "docs/WALL_A_COBOT_GEOMETRY.md"]
    missing = [str(p) for p in originals if not p.is_file()]
    if missing:
        raise FileNotFoundError(missing)
    DEST.mkdir(parents=True, exist_ok=True)
    for path in originals:
        target = DEST / path.name
        if target.exists() and sha(target) == sha(path):
            continue
        shutil.copy2(path, target)
        if sha(target) != sha(path):
            raise IOError(f"Copy mismatch: {path}")

    rows = [
        ("A",218,104,448),("B",284,154,472),("F",218,104,448),
        ("PL",None,132,332),("PM",219,110,370),("PU",None,88,272),
        ("SL",None,132,332),("SM",228,110,354),("SU",None,88,272),
        ("T",355,192,616)]
    table = "\n".join(f"| {wall} | {old if old is not None else '미산출'} | {new} | {frags} |" for wall,old,new,frags in rows)
    doc = f"""---
project: HD_ACS
type: result
status: review-only
updated: 2026-09-21
tags:
  - hd-acs
  - llm-wiki
  - cobot
---

# 2026-09-21 코봇 AREA 전체높이 검토

## 현재 확인된 기준

- 사용자 제공 선창 치수: L 30960, W 17277.6, H 12957.6 mm. 하부 챔퍼 3735.3 mm/45°, 상부 챔퍼 2655.3 mm/45°.
- 층 바닥 z: 0, 3630, 6630, 10530 mm. 천장 z: 12957.6 mm.
- **1440 × 1440 mm는 AREA 한 개의 최대 크기다. 코봇 카메라 최대 도달 높이로 확정되지 않았다.** 이전의 각 층 바닥+1440 mm 검사 밴드는 폐기한 가정이다. 1440 mm 위의 용접선 후보도 도면에 포함했다.
- AREA 내부의 도면상 작업 후보는 수평·수직 `LINE`, 갈래 수로 찾은 `CROSS3`·`CROSS4`다. CROSS의 실제 판 수는 미확인이다.
- A/F 최하단 수평 용접선은 앞선 사용자 결정에 따라 제외하고, 그 선에서 위로 이어지는 수직선은 포함했다. 2단 최하단 선의 카메라 접근성과 하부 수직선 시작점은 현장 확인 대상이다.
- CAD 도면층 `ACS_AREA_L0`~`L3`은 층 **후보**를 색상으로 표시한다. 정차 높이별 실제 접근성과 경사면 도면 좌표→실거리 변환은 확정 전이다. WIKI의 [[HD_ACS - L4 측면벽 접근 금지영역]]도 별도 제약으로 남아 있다.

## 이전 영역 분할과 비교

| 항목 | 2026-09-07 기존 기록 | 2026-09-21 이번 검토 |
|---|---|---|
| AREA 크기 | 800 × 1600 mm 기준 창 | 각 변 최대 1440 mm. 경사면은 세로 도면 격자 1000 mm로 보수 표시 |
| 작업 정의 | top 360 mm 피치, top–top–top 직선 720 mm | 전체 막 시트 직선 용접선 후보를 AREA 경계마다 나눈 `LINE` 조각 + `CROSS3/4` 후보 |
| 포함 면 | A·B·F·PM·SM·T 6개 | 10개 면 전부. 경사면 4개는 좌표 변환 미확정의 도면상 후보 |
| 수직 범위 | 기존 알고리즘은 면의 도면상 전체 후보; 이후 A 검토도에는 층 바닥+1440 mm 가정 적용 | 그 높이 제한을 제거해 전체 도면 범위 표시 |
| 산출물 | 좌표 JSON·HTML, 독립 미리보기 | 10개 AutoCAD DWG/DXF, AREA↔작업 JSON, 이전 A 도면 기록 |

| 면 | 기존 AREA 수 | 새 AREA 후보 수 | 새 도면상 작업 조각 수 |
|---|---:|---:|---:|
{table}
| **합계** | **1,522 (6면)** | **1,214 (10면)** | **3,916** |

**수량은 직접 증감 비교하면 안 된다.** 새 도면은 격자 기반 AREA 후보이고, 작업 조각에는 경계에서 나뉜 LINE과 CROSS 후보가 섞여 있다. 기존 720 mm 고유 Task와 같은 단위가 아니다. 새 AREA 배치도 개수의 최적해가 아니다.

## 첨부와 검증

- 최신 DWG: [[2026-09-21 코봇 AREA 전체높이 검토/WALL_A_cobot_full_coverage.dwg|A]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_B_cobot_full_coverage.dwg|B]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_F_cobot_full_coverage.dwg|F]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_T_cobot_full_coverage.dwg|T]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_SL_cobot_full_coverage.dwg|SL]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_SM_cobot_full_coverage.dwg|SM]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_SU_cobot_full_coverage.dwg|SU]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_PL_cobot_full_coverage.dwg|PL]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_PM_cobot_full_coverage.dwg|PM]], [[2026-09-21 코봇 AREA 전체높이 검토/WALL_PU_cobot_full_coverage.dwg|PU]].
- 각 DWG의 재생성용 DXF, [[2026-09-21 코봇 AREA 전체높이 검토/full-coverage-summary.json|AREA·작업 매핑]], [[2026-09-21 코봇 AREA 전체높이 검토/README.md|표시 기준]], 이전 WALL A 밴드 도면·시각화와 생성 스크립트를 같은 첨부 폴더에 보존했다.
- 10개 DXF를 AutoCAD for Mac 2027에서 열어 DWG 2018 형식으로 저장했다. DWG 10개의 파일 형식과 모든 AREA의 도면상 폭·높이 상한을 확인했다. 실장비 카메라 도달, AMR 정차 자세, 실제 판 경계는 검증하지 않았다.

관련: [[HD_ACS - 결정 001 검사영역 분할]] · [[HD_ACS - Area와 Task]] · [[HD_ACS - DXF 영역 분할]] · [[HD_ACS - 좌표 산출 결과]]
"""
    (FEATURE / f"{TITLE}.md").write_text(doc, encoding="utf-8")
    pointer = f"현재 검토 기준은 [[{TITLE}]]에 기록했다. 1440 × 1440 mm는 AREA의 최대 크기이며 코봇 도달 높이가 아니다. 아래의 800 × 1600 mm 및 720 mm 작업 수량은 2026-09-07 당시 산출 이력이다.\n"
    for name in ("HD_ACS - DXF 영역 분할", "HD_ACS - Area와 Task", "HD_ACS - 좌표 산출 결과", "HD_ACS - 결정 001 검사영역 분할"):
        add_current_note(name, pointer)
    home = page("HD_ACS - 홈")
    text = home.read_text(encoding="utf-8")
    link = f"[[{TITLE}]]"
    text = re.sub(r"(?m)^## 2026-09-21 코봇 AREA 검토\n\n.*?\n(?=\n|# )", "", text, count=1, flags=re.S)
    m = re.search(r"(?m)^# [^\n]+\n", text)
    if m is None:
        raise RuntimeError(f"No heading in {home}")
    text = text[:m.end()] + "\n## 2026-09-21 코봇 AREA 검토\n\n" + link + ": 1440 mm AREA 상한, 전체 높이, 10개 WALL DWG와 이전 분할 비교.\n" + text[m.end():]
    home.write_text(re.sub(r"(?m)^updated: \d{4}-\d{2}-\d{2}$", "updated: 2026-09-21", text, count=1), encoding="utf-8")
    print(f"copied {len(originals)} files to {DEST}")
    print(f"updated wiki page {FEATURE / (TITLE + '.md')}")


if __name__ == "__main__":
    main()
