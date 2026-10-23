#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 저장된 T_W_D(map_calibration)의 회전만 보정 — [도면 좌표 입력 프레임 오류 복구]
#
#   증상 → 원인 대조표 (실제 로봇 위치/방향 대비 화면 표시)
#   ┌──────────────────────────────────────────────┬──────────────┐
#   │ 화면 표시                                     │ yaw 오차      │
#   ├──────────────────────────────────────────────┼──────────────┤
#   │ 사분면·방향 모두 정반대                        │ 180°         │
#   │  (예: 실제 선수/좌현·선수 방향 → 선미/우현·선미) │  --deg 180   │
#   │ 좌·우로 90° 돌아감 (선수/좌현 → 선미/좌현,      │ ±90°         │
#   │  방향도 90° 회전)                              │  --deg ±90   │
#   └──────────────────────────────────────────────┴──────────────┘
#   ※ 180° 오차는 탱크가 중심 대칭이라 **마커가 탱크 안에 그대로 남는다** —
#     "벽 밖으로 나가지 않았으니 맞다"고 판단하면 놓친다. 방향(뱃머리)까지 함께 봐야 한다.
#
#   근거: 표시 좌표 = R(−yaw)·(map − t), 표시 방향 = theta − yaw
#         [src/HD.Acs.Core/Geometry/DrawingTransform.cs]
#         yaw 가 Δ 만큼 어긋나면 표시 좌표·방향이 도면 원점 기준으로 −Δ 회전한다.
#         → 표시를 +Δ 되돌리려면  yaw_new = yaw_old − Δ,  t 는 그대로.
#
#   ┌ 앱(지오메트리) 도면 프레임 [src/HD.Acs.Core/Planning/TankGeometry.cs] ┐
#   │   +X = 선수(F),  −X = 선미(A),  +Y = 좌현(P*),  −Y = 우현(S*)        │
#   │   방향: 0°=선수, +90°=좌현, ±180°=선미, −90°=우현                    │
#   │   부호 판정 기준은 탱크 중심 (origin_ox, origin_oy) — 기본 (0,0)      │
#   └──────────────────────────────────────────────────────────────────┘
#
#   이 도구는 **회전만** 고친다(평행이동 t·RMS 불변). 위치가 따로 벗어났거나
#   기준점 측정 자체가 틀렸으면 tools/recalibrate_map.sh 로 재측정·재산출해야 한다.
#
# 사용법:
#   export PGHOST=localhost PGPORT=5432 PGDATABASE=hdacs PGUSER=hdacs PGPASSWORD=***
#   ./rotate_map_calibration.sh CT1-L1                      # 미리보기(기본 180°)
#   ./rotate_map_calibration.sh --apply CT1-L1              # 실제 적용
#   ./rotate_map_calibration.sh --apply CT1-L1 CT1-L2 CT1-L3 CT1-L4   # 전 층
#   ./rotate_map_calibration.sh --deg -90 --apply CT1-L4    # 90° 오차 보정
#
# 환경변수: BASE_URL (기본 http://localhost:5199) · PG* (psql 표준)
# 필요 도구: psql, curl, awk
# ---------------------------------------------------------------------------
set -euo pipefail

DEG=180
APPLY=0
MAPS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --deg)   DEG="${2:?--deg 에 각도(도) 필요}"; shift 2 ;;
    --apply) APPLY=1; shift ;;
    -h|--help) sed -n '2,45p' "$0"; exit 0 ;;
    -*) echo "알 수 없는 옵션: $1" >&2; exit 2 ;;
    *)  MAPS+=("$1"); shift ;;
  esac
done
BASE_URL="${BASE_URL:-http://localhost:5199}"

if [[ ${#MAPS[@]} -eq 0 ]]; then
  echo "사용법: $0 [--deg 180] [--apply] <mapId> [mapId...]" >&2
  exit 2
fi

# 회전 행렬 — 90° 배수는 부동소수 먼지 없이 정확한 정수로 고정
read -r ROT_COS ROT_SIN <<<"$(awk -v d="$DEG" 'BEGIN{
  m = d % 360; if (m < 0) m += 360;
  if (m==0)        print 1, 0;
  else if (m==90)  print 0, 1;
  else if (m==180) print -1, 0;
  else if (m==270) print 0, -1;
  else { r = d * atan2(0,-1) / 180; printf "%.15f %.15f\n", cos(r), sin(r); }
}')"

norm_deg() { awk -v a="$1" 'BEGIN{ while (a <= -180) a += 360; while (a > 180) a -= 360; printf "%.3f", a }'; }

# 도면 좌표·방향 → 사람이 읽는 라벨 (부호 판정은 탱크 중심 기준)
label_pose() {   # $1=dx $2=dy $3=head_deg(또는 빈 문자열) $4=ox $5=oy
  awk -v x="$1" -v y="$2" -v h="$3" -v ox="$4" -v oy="$5" 'BEGIN{
    e = 0.2;
    if (x > ox + e) fx = "선수"; else if (x < ox - e) fx = "선미"; else fx = "중앙";
    if (y > oy + e) fy = "좌현"; else if (y < oy - e) fy = "우현"; else fy = "중앙";
    printf "%s/%s (%.2f, %.2f)", fx, fy, x, y;
    if (h == "") { printf ", 방향 미보고"; exit }
    a = h + 0; while (a <= -180) a += 360; while (a > 180) a -= 360;
    if (a > -45 && a <= 45)        d = "선수";
    else if (a > 45 && a <= 135)   d = "좌현";
    else if (a > -135 && a <= -45) d = "우현";
    else                           d = "선미";
    printf ", 방향 %s(%.1f°)", d, a;
  }'
}

echo "== T_W_D 회전 보정 =="
echo "   회전량 : ${DEG}° (표시 좌표·방향을 이만큼 되돌림, 평행이동 t 는 불변)"
echo "   모드   : $([[ $APPLY -eq 1 ]] && echo '실제 적용(--apply)' || echo '미리보기')"
echo "   App    : ${BASE_URL}"
echo "   DB     : ${PGHOST:-?}:${PGPORT:-5432}/${PGDATABASE:-?}"

FAILED=0
for MAP_ID in "${MAPS[@]}"; do
  echo
  echo "────────────────────────────────────────────────────────────"
  echo "[${MAP_ID}]"

  VER="$(psql -At -c "SELECT version FROM ref.map WHERE map_id='${MAP_ID}';")"
  if [[ -z "$VER" ]]; then
    echo "  !! ref.map 에 없는 mapId — 건너뜀" >&2; FAILED=1; continue
  fi

  read -r TX TY YAW RMS NPT <<<"$(psql -At -F' ' -c \
    "SELECT tx, ty, yaw_rad, rms_m, point_count
       FROM ref.map_calibration WHERE map_id='${MAP_ID}' AND map_version=${VER};")" || true
  if [[ -z "${YAW:-}" ]]; then
    echo "  !! 맵버전 ${VER} 에 유효한 캘리브레이션이 없습니다 — 먼저 캘리브레이션을 등록하세요." >&2
    FAILED=1; continue
  fi

  YAW_NEW="$(awk -v y="$YAW" -v d="$DEG" 'BEGIN{
    pi = atan2(0,-1); r = y - d*pi/180;
    while (r <= -pi) r += 2*pi; while (r > pi) r -= 2*pi; printf "%.6f", r }')"
  YAW_DEG="$(awk -v y="$YAW" 'BEGIN{printf "%.3f", y*180/atan2(0,-1)}')"
  YAW_NEW_DEG="$(awk -v y="$YAW_NEW" 'BEGIN{printf "%.3f", y*180/atan2(0,-1)}')"

  PTS="$(psql -At -c "SELECT count(*) FROM ref.map_calibration_point
                       WHERE map_id='${MAP_ID}' AND map_version=${VER};")"

  echo "  맵버전 ${VER} · 기준점 ${PTS}점 · RMS ${RMS} m"
  echo "  yaw  : ${YAW} rad (${YAW_DEG}°)  →  ${YAW_NEW} rad (${YAW_NEW_DEG}°)"
  echo "  t    : (${TX}, ${TY})  → 변경 없음"

  # 검증: 이 맵을 보고 중인 로봇의 현재 위치를 before/after 로 환산
  read -r OX OY <<<"$(psql -At -F' ' -c \
    "SELECT g.origin_ox, g.origin_oy FROM ref.map m
       JOIN ref.tank_geometry g ON g.tank_id = m.tank_id WHERE m.map_id='${MAP_ID}';")"
  OX="${OX:-0}"; OY="${OY:-0}"

  while IFS=' ' read -r RID MX MY MTH; do
    [[ -z "${RID:-}" ]] && continue
    for TAG in before after; do
      Y_USE="$YAW"; [[ "$TAG" == after ]] && Y_USE="$YAW_NEW"
      read -r DX DY HD <<<"$(awk -v mx="$MX" -v my="$MY" -v th="$MTH" -v tx="$TX" -v ty="$TY" -v y="$Y_USE" 'BEGIN{
        c = cos(y); s = sin(y); ux = mx - tx; uy = my - ty;
        printf "%.4f %.4f ", c*ux + s*uy, -s*ux + c*uy;
        if (th == "") { print ""; } else { print (th - y) * 180 / atan2(0,-1); }
      }')"
      printf '  로봇 %-8s %-6s → %s\n' "$RID" "$TAG" "$(label_pose "$DX" "$DY" "${HD:-}" "$OX" "$OY")"
    done
  done < <(psql -At -F' ' -c \
      "SELECT robot_id, reported_x, reported_y, coalesce(reported_theta::text,'')
         FROM run.robot_context
        WHERE reported_map_id='${MAP_ID}' AND reported_x IS NOT NULL;")

  if [[ $APPLY -eq 0 ]]; then
    echo "  >> 미리보기입니다. after 가 실제 로봇 위치·방향과 맞으면: $0 --deg ${DEG} --apply ${MAP_ID}"
    continue
  fi

  # ---------- 실제 적용 ----------
  STAMP="$(date +%Y%m%d_%H%M%S)"
  BK="${TMPDIR:-/tmp}/mapcal_${MAP_ID}_v${VER}_${STAMP}"
  psql -c "\copy (SELECT * FROM ref.map_calibration       WHERE map_id='${MAP_ID}' AND map_version=${VER}) TO STDOUT" > "${BK}_cal.tsv"
  psql -c "\copy (SELECT * FROM ref.map_calibration_point WHERE map_id='${MAP_ID}' AND map_version=${VER}) TO STDOUT" > "${BK}_pts.tsv"
  echo "  백업: ${BK}_cal.tsv, ${BK}_pts.tsv"
  echo "  되돌리기: UPDATE ref.map_calibration SET yaw_rad=${YAW} WHERE map_id='${MAP_ID}' AND map_version=${VER};"
  echo "            (기준점도 함께 회전되므로 --deg $(awk -v d="$DEG" 'BEGIN{print -d}') 로 재실행하거나 백업 TSV 로 복구)"

  if [[ "${PTS:-0}" -ge 2 ]]; then
    # 기준점의 도면 좌표를 같은 각도로 회전 → solve 가 보정된 yaw 를 재현(이후 재계산에도 보존)
    # SET 의 우변은 모두 갱신 전 행 값으로 평가되므로 x,y 동시 치환이 안전하다.
    psql -v ON_ERROR_STOP=1 -q -c "
      UPDATE ref.map_calibration_point
         SET drawing_x_m = ${ROT_COS}*drawing_x_m - ${ROT_SIN}*drawing_y_m,
             drawing_y_m = ${ROT_SIN}*drawing_x_m + ${ROT_COS}*drawing_y_m
       WHERE map_id='${MAP_ID}' AND map_version=${VER};"
    echo "  기준점 ${PTS}점 도면좌표 ${DEG}° 회전 완료 → solve 재계산"
    RESP="$(curl -fsS -X POST "${BASE_URL}/api/maps/${MAP_ID}/calibration/solve")" \
      || { echo "  !! solve 호출 실패 — App(${BASE_URL}) 기동 여부 확인. 기준점은 회전된 상태입니다." >&2; FAILED=1; continue; }
    echo "  solve: ${RESP}"
  else
    echo "  기준점이 ${PTS}점(2점 미만) — solve 불가라 캘리브레이션 yaw 를 직접 갱신합니다."
    psql -v ON_ERROR_STOP=1 -q -c "
      UPDATE ref.map_calibration SET yaw_rad=${YAW_NEW}, registered_by='rotate_map_calibration.sh'
       WHERE map_id='${MAP_ID}' AND map_version=${VER};"
    echo "  ⚠ 기준점이 없어 이후 누군가 solve 를 돌리면 보정이 되돌아갑니다 — 기준점 재캡처를 권장합니다."
  fi

  read -r TX2 TY2 YAW2 <<<"$(psql -At -F' ' -c \
    "SELECT tx, ty, yaw_rad FROM ref.map_calibration WHERE map_id='${MAP_ID}' AND map_version=${VER};")"
  YAW2_DEG="$(awk -v y="$YAW2" 'BEGIN{printf "%.3f", y*180/atan2(0,-1)}')"
  DIFF="$(awk -v a="$YAW2" -v b="$YAW_NEW" 'BEGIN{d=a-b; if(d<0)d=-d; printf "%.6f", d}')"
  echo "  결과: yaw ${YAW2} rad (${YAW2_DEG}°) · t (${TX2}, ${TY2})"
  if awk -v d="$DIFF" 'BEGIN{exit !(d > 0.001)}'; then
    echo "  !! 예상 yaw(${YAW_NEW})와 ${DIFF} rad 차이 — 기준점이 수동 편집된 적이 있는지 확인하세요." >&2
    FAILED=1
  else
    echo "  ✓ 예상값과 일치"
  fi
done

echo
echo "────────────────────────────────────────────────────────────"
if [[ $APPLY -eq 1 ]]; then
  echo "적용 완료. UI 는 캘리브레이션을 10초 캐시하므로 잠시 후(또는 로봇 재선택 후)"
  echo "운영 탭 3D 뷰에서 마커 위치·뱃머리 방향이 실제와 맞는지 반드시 눈으로 확인하세요."
  echo "180° 오차는 마커가 탱크 안에 그대로 남으므로 '방향'까지 같이 봐야 판별됩니다."
fi
exit $FAILED
