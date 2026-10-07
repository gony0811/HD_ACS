#!/usr/bin/env python3
"""F-A0001 영역에 '정차 정면 수평 용접선' 1개만 남기고 정차 이격을 실측값으로 맞춘다 (운영 도구).

현장 조건(2026-10-07 요청):
  - 용접선 시작점 = AMR 정차 위치의 정중앙(정면) → 시작 u = 영역 중심 u
    (정차점 = 영역 중심 + 내부향 법선 × 이격 이므로, 정차 정면의 벽 위 점은 영역 중심 u 이다)
  - 높이 = AMR이 선 층 바닥에서 1150 mm → v = (층 발판 z + 1.15 − 면 origin.z) / vAxis.z
  - 수평 용접선 길이 720 mm → 끝 u = 시작 u ± 0.72 (기본 +u = 벽을 바라볼 때 오른쪽 [F면 uAxis=−y=우현])
  - AMR 중심 ↔ 벽 실제 거리 1350 mm → 영역 station_standoff_m = 1.35
  - F-A0001의 다른 용접선은 모두 삭제

영역 이름·코너·수동 정차(x,y,theta)는 그대로 유지하고 이격만 바꾼다(PUT /api/areas/{id} — areaId 유지).
새 용접선은 새 taskId로 등록한다(위치가 다른 용접선이므로 기존 taskId·촬영 이력을 이어 쓰지 않음).

사용법 (HD_ACS 서버가 떠 있는 PC에서):
  python3 tools/define_f_a0001_weld.py              # 미리보기 — 아무것도 바꾸지 않음
  python3 tools/define_f_a0001_weld.py --apply      # 실제 적용
옵션: --base http://localhost:5199  --tank CT1  --area F-A0001  --wall F
      --height-mm 1150  --length-mm 720  --standoff-mm 1350  --dir right|left
"""
import argparse
import json
import sys
import urllib.error
import urllib.parse
import urllib.request


def call(base, method, path, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        sys.exit(f"[실패] {method} {path} → HTTP {e.code}: {e.read().decode('utf-8', 'replace')}")
    except urllib.error.URLError as e:
        sys.exit(f"[실패] 서버 연결 불가 ({base}): {e.reason}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://localhost:5199")
    ap.add_argument("--tank", default="CT1")
    ap.add_argument("--wall", default="F")
    ap.add_argument("--area", default="F-A0001")
    ap.add_argument("--height-mm", type=float, default=1150)
    ap.add_argument("--length-mm", type=float, default=720)
    ap.add_argument("--standoff-mm", type=float, default=1350)
    ap.add_argument("--dir", choices=["right", "left"], default="right",
                    help="벽을 바라볼 때 용접선이 뻗는 방향 (right=+u, left=−u)")
    ap.add_argument("--name", default="H01")
    ap.add_argument("--apply", action="store_true")
    a = ap.parse_args()
    q = urllib.parse.quote

    areas = call(a.base, "GET", f"/api/internal/areas?tankId={q(a.tank)}&wallCode={q(a.wall)}")
    hit = [x for x in areas if x["name"] == a.area]
    if len(hit) != 1:
        sys.exit(f"[실패] 면 {a.wall}에서 영역 '{a.area}'를 {len(hit)}개 찾음. 있는 영역: {[x['name'] for x in areas]}")
    area = hit[0]
    area_id, level, corners = area["areaId"], area["level"], area["corners"]

    geom = call(a.base, "GET", f"/api/internal/tanks/{q(a.tank)}/geometry")
    walls = call(a.base, "GET", f"/api/internal/tanks/{q(a.tank)}/walls")
    wall = next(w for w in walls if w["wallCode"] == a.wall)
    floor_z = geom["levelZ"][level - 1]
    oz, vz = wall["origin"][2], wall["vAxis"][2]
    if abs(vz) < 1e-6:
        sys.exit(f"[실패] 면 {a.wall}의 v축이 수평이라 '바닥에서 높이'로 v를 정할 수 없습니다.")

    uc = sum(p[0] for p in corners) / len(corners)          # AreaGeometry.Centroid 와 같은 식(정차점 기준)
    sv = (floor_z + a.height_mm / 1000.0 - oz) / vz           # 면-전체 v (API 경계 좌표)
    eu = uc + (a.length_mm / 1000.0) * (1 if a.dir == "right" else -1)
    r6 = lambda x: round(x, 6)
    su, sv, eu = r6(uc), r6(sv), r6(eu)

    tasks = call(a.base, "GET", f"/api/internal/areas/{area_id}/tasks")
    us = [p[0] for p in corners]; vs = [p[1] for p in corners]
    print(f"영역 {a.area} ({area_id}) — 면 {a.wall} L{level}, u[{min(us)}, {max(us)}] v[{min(vs)}, {max(vs)}]")
    print(f"  정차 이격: {area.get('stationStandoffM')} → {a.standoff_mm / 1000.0} m"
          f"   (수동 정차 x/y/θ = {area.get('stationX')}/{area.get('stationY')}/{area.get('stationTheta')} 유지)")
    if area.get("stationX") is not None:
        print("  ⚠ 수동 정차 좌표가 지정돼 있어 이격값이 정차점에 반영되지 않습니다 — 계획 화면에서 정차 수동 지정을 해제하세요.")
    print(f"  층 L{level} 바닥 z={floor_z} m → 용접선 v = {sv} m (바닥+{a.height_mm:.0f} mm)")
    print(f"  새 용접선 {a.name}: LINE ({su}, {sv}) → ({eu}, {sv})  길이 {abs(eu - su) * 1000:.0f} mm, 방향 {a.dir}")
    print(f"  삭제할 기존 용접선 {len(tasks)}개: " + ", ".join(f"#{t['seq']} {t.get('name') or ''}" for t in tasks))
    if not (min(us) - 1e-9 <= min(su, eu) and max(su, eu) <= max(us) + 1e-9 and min(vs) <= sv <= max(vs)):
        sys.exit("[중단] 새 용접선이 영역 밖입니다 — 영역 크기/위치를 먼저 확인하세요(영역 최대 1.44 m).")
    if not a.apply:
        print("\n미리보기만 했습니다. 적용하려면 --apply 를 붙여 다시 실행하세요.")
        return

    # 1) 이격만 교체 (이름·코너·수동 정차 유지)
    call(a.base, "PUT", f"/api/areas/{area_id}", {
        "name": area["name"], "corners": corners,
        "stationX": area.get("stationX"), "stationY": area.get("stationY"),
        "stationTheta": area.get("stationTheta"), "stationStandoffM": a.standoff_mm / 1000.0,
        "userId": "define_f_a0001_weld"})
    # 2) 기존 용접선 삭제
    for t in tasks:
        call(a.base, "DELETE", f"/api/area-tasks/{t['taskId']}")
    # 3) 새 용접선 등록 (seamType·단면·프로파일은 기존 첫 작업 값을 승계, 없으면 기본값)
    ref = tasks[0] if tasks else {}
    res = call(a.base, "POST", f"/api/areas/{area_id}/tasks", {
        "seq": 1, "name": a.name, "seamType": "LINE",
        "startU": su, "startV": sv, "endU": eu, "endV": sv,
        "sectionDxfId": ref.get("sectionDxfId") or f"WALL {a.wall}",
        "profileId": ref.get("profileId") or "PROF-1", "userId": "define_f_a0001_weld"})
    print(f"\n적용 완료 — 새 taskId {res['taskId']}. 화면은 계획 ▸ 영역·작업에서 [새로고침]하세요.")
    after = call(a.base, "GET", f"/api/internal/areas/{area_id}/tasks")
    for t in after:
        print(f"  #{t['seq']} {t.get('name')} {t['seamType']} ({t['startU']}, {t['startV']}) → ({t['endU']}, {t['endV']})")


if __name__ == "__main__":
    main()
