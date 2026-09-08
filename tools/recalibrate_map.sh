#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 층별 맵 재캘리브레이션(T_W_D 재산출) — [SLAM↔도면 좌표 변환 오류 복구, 방법 A]
#
#   증상: 도면 뷰에서 로봇이 엉뚱한 위치/방향(예: 선수/좌현인데 선미·좌현으로)에
#         표시됨. 원인은 저장된 T_W_D(map_calibration)의 회전값이 앱 도면 프레임이
#         아니라 CAD 프레임 기준으로 잡혀 ~90° 어긋난 것.
#
#   ┌ 앱(지오메트리) 도면 프레임 규약 [src/HD.Acs.Core/Planning/TankGeometry.cs] ┐
#   │   +X = 선수(Bow, 마구리 F),  -X = 선미(Stern, 마구리 A)                   │
#   │   +Y = 좌현(Port, P* 벽),    -Y = 우현(Starboard, S* 벽)                 │
#   └────────────────────────────────────────────────────────────────────────┘
#   캘리브레이션의 도면 좌표는 반드시 이 앱 프레임으로 입력해야 한다.
#   (CAD 도면이 선수=+Y 로 그려져 있으면 −90° 돌려서 넣어야 함 — 아래 PAIRS 참고)
#
# 사용법:
#   1) 환경변수로 DB 접속 정보 지정 (폐쇄망 서버에서 실행)
#        export PGHOST=localhost PGPORT=5432 PGDATABASE=hdacs PGUSER=hdacs PGPASSWORD=***
#   2) (기본) 미리보기 — 아무것도 바꾸지 않고 현재값·투입할 대응쌍·예상결과만 출력
#        ./recalibrate_map.sh CT1-L4
#   3) 실제 적용 — 기존 기준점 교체 후 solve 로 T_W_D 재계산·저장
#        ./recalibrate_map.sh CT1-L4 --apply
#
# 환경변수:
#   BASE_URL   HD_ACS App 주소 (기본 http://localhost:5199) — solve 는 REST 로 호출
#   PG*        psql 표준 접속 변수 (기준점 대응쌍은 DB에 직접 insert)
#
# 필요 도구: psql, curl, awk
# ---------------------------------------------------------------------------
set -euo pipefail

MAP_ID="${1:-}"
MODE="${2:-}"
BASE_URL="${BASE_URL:-http://localhost:5199}"

if [[ -z "$MAP_ID" ]]; then
  echo "사용법: $0 <mapId> [--apply]   (예: $0 CT1-L4 --apply)" >&2
  exit 2
fi

# ---------------------------------------------------------------------------
# 측정된 기준점 대응쌍 (surveyed pairs)
#   drawing_x_m / drawing_y_m : 앱(지오메트리) 도면 프레임 [m]
#   map_x / map_y             : 현장 측정 SLAM 좌표 [m]
#
# 아래 값은 2026-09 현장 측정(선미/좌현·선수/좌현·중앙 3점)을 앱 프레임으로 변환한 것.
#   CAD 원본(선미/좌현)=(1.7,2.6), (선수/좌현)=(1.54,15.06), (중앙)=(7.5,18.2)
#   탱크 중심 CAD 오프셋 = (8.64, 15.48)  ← CAD→앱 변환 기준(현장에서 반드시 확인)
#   변환식:  G_x = (C_y − 15.48),  G_y = −(C_x − 8.64)     [R(−90°)]
# 다른 층/다른 현장에서는 이 블록만 교체하면 된다.
# ---------------------------------------------------------------------------
# 형식: "drawing_x drawing_y  map_x map_y  # 라벨"
PAIRS=(
  "-12.88  6.94   4.969  3.747   # 선미/좌현"
  "-0.42   7.10   4.875 16.108   # 선수/좌현"
  " 2.72   1.14  10.75  19.455   # 중앙"
)

echo "== 맵 재캘리브레이션 : ${MAP_ID} =="
echo "   App   : ${BASE_URL}"
echo "   DB    : ${PGHOST:-?}:${PGPORT:-5432}/${PGDATABASE:-?}"
echo

# 1) 현재 유효 맵 버전
VER="$(psql -At -c "SELECT version FROM ref.map WHERE map_id='${MAP_ID}';")"
if [[ -z "$VER" ]]; then
  echo "!! ref.map 에 '${MAP_ID}' 가 없습니다. mapId 를 확인하세요." >&2
  exit 1
fi
echo "[1] 현재 맵 버전 : ${VER}"

# 2) 현재 저장된 T_W_D (있으면)
echo "[2] 현재 저장된 캘리브레이션(before):"
psql -c "SELECT tx, ty, yaw_rad, degrees(yaw_rad) AS yaw_deg, rms_m, point_count, registered_at
         FROM ref.map_calibration WHERE map_id='${MAP_ID}' AND map_version=${VER};" || true

# 3) 투입할 대응쌍 표시
echo "[3] 투입할 기준점 대응쌍 (앱 프레임 도면 ↔ SLAM):"
printf '      %-8s %-8s | %-8s %-8s | %s\n' draw_x draw_y map_x map_y 라벨
for p in "${PAIRS[@]}"; do
  read -r dx dy mx my _hash label <<<"$p"
  printf '      %-8s %-8s | %-8s %-8s | %s\n' "$dx" "$dy" "$mx" "$my" "${label}"
done
echo
echo "    예상 결과: Yaw ≈ 1.574 rad (+90.2°),  Tx ≈ 11.91,  Ty ≈ 16.65,  RMS ≈ 0.089 m"
echo "    (검증) 로봇 SLAM(5.318,19.066,θ1.564) → 도면 ≈ 선수(+X)/좌현(+Y), 뱃머리(0°) 방향"
echo

if [[ "$MODE" != "--apply" ]]; then
  echo ">> 미리보기 모드입니다. 실제 적용하려면:  $0 ${MAP_ID} --apply"
  exit 0
fi

# --------- 실제 적용 ---------
echo "[4] 기존 캘리브레이션 백업:"
psql -c "\copy (SELECT * FROM ref.map_calibration       WHERE map_id='${MAP_ID}' AND map_version=${VER}) TO STDOUT" \
  > "/tmp/mapcal_backup_${MAP_ID}_v${VER}.tsv" || true
psql -c "\copy (SELECT * FROM ref.map_calibration_point WHERE map_id='${MAP_ID}' AND map_version=${VER}) TO STDOUT" \
  > "/tmp/mapcalpts_backup_${MAP_ID}_v${VER}.tsv" || true
echo "    → /tmp/mapcal_backup_${MAP_ID}_v${VER}.tsv, /tmp/mapcalpts_backup_${MAP_ID}_v${VER}.tsv"

echo "[5] 기존 기준점 삭제 후 측정 대응쌍 insert (map_version=${VER}):"
{
  echo "BEGIN;"
  echo "DELETE FROM ref.map_calibration_point WHERE map_id='${MAP_ID}' AND map_version=${VER};"
  for p in "${PAIRS[@]}"; do
    read -r dx dy mx my _hash label <<<"$p"
    echo "INSERT INTO ref.map_calibration_point
            (map_id, map_version, drawing_x_m, drawing_y_m, map_x, map_y, captured_by)
          VALUES ('${MAP_ID}', ${VER}, ${dx}, ${dy}, ${mx}, ${my}, 'recalibrate_map.sh');"
  done
  echo "COMMIT;"
} | psql -v ON_ERROR_STOP=1 -q
echo "    → 3점 insert 완료"

echo "[6] solve 호출(REST) — T_W_D 재계산·저장:"
RESP="$(curl -fsS -X POST "${BASE_URL}/api/maps/${MAP_ID}/calibration/solve")"
echo "    응답: ${RESP}"
echo

echo "[7] 저장 결과 확인(after):"
psql -c "SELECT tx, ty, yaw_rad, degrees(yaw_rad) AS yaw_deg, rms_m, point_count, registered_at
         FROM ref.map_calibration WHERE map_id='${MAP_ID}' AND map_version=${VER};"

echo
echo "완료. UI 는 캘리브레이션을 최대 10초 캐시하므로, 운영 화면에서 로봇을 재선택하거나"
echo "잠시 후 위치/방향이 정상(선수/좌현·뱃머리)으로 갱신되는지 확인하세요."
echo "yaw_deg 가 +90° 근처가 아니거나 rms_m 가 0.1m 를 크게 넘으면 PAIRS(특히 CAD→앱 변환)를"
echo "다시 점검해야 합니다."
