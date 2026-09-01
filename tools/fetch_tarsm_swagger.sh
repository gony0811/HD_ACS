#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# TARS-M v3 REST API 스펙(swagger.json / openapi.json) 자동 탐색·수집
#
# 사용법:  ./fetch_tarsm_swagger.sh <robot-ip> [out-dir]
# 예:      ./fetch_tarsm_swagger.sh 192.168.0.50 ../docs
#
# 로봇과 같은 망에 있는 PC에서 실행할 것(폐쇄망). 실패 시 아래 수동 절차 참고:
#   브라우저로 http://<robot-ip>/api/v3 접속 → F12 개발자도구 → Network 탭 →
#   새로고침 → 응답이 JSON인 요청(보통 swagger.json / api-docs / openapi.json)의
#   URL 이 정답. 그 URL 을 이 스크립트 CANDIDATES 에 추가하거나 직접 저장.
# ---------------------------------------------------------------------------
set -uo pipefail

HOST="${1:-}"
OUT_DIR="${2:-.}"
[ -z "$HOST" ] && { echo "usage: $0 <robot-ip> [out-dir]"; exit 1; }

BASE="http://${HOST}"

# 프레임워크별 관례 경로 (앞쪽일수록 가능성 높음)
CANDIDATES=(
  "/api/v3/swagger.json"              # 일반 Swagger UI
  "/api/v3/openapi.json"              # FastAPI 계열
  "/api/v3/swagger/v1/swagger.json"   # ASP.NET Swashbuckle
  "/api/v3/api-docs"                  # springfox
  "/api/v3/v3/api-docs"               # springdoc (prefix 중첩)
  "/api/v3/docs/swagger.json"
  "/api/v3/swagger.yaml"
  "/v3/api-docs"                      # springdoc 기본
  "/swagger.json"
  "/openapi.json"
  "/swagger/v1/swagger.json"
  "/api-docs"
)

echo "== 1) 관례 경로 탐색 =========================================="
FOUND=""
for p in "${CANDIDATES[@]}"; do
  url="${BASE}${p}"
  code=$(curl -s -o /tmp/_spec.$$ -w '%{http_code}' --max-time 5 "$url" || echo 000)
  size=$(wc -c < /tmp/_spec.$$ 2>/dev/null || echo 0)
  if [ "$code" = "200" ] && grep -qE '"(openapi|swagger)"' /tmp/_spec.$$ 2>/dev/null; then
    echo "  [FOUND] $url  (${code}, ${size} bytes)"
    FOUND="$url"
    break
  else
    echo "  [ ---- ] $url  (${code})"
  fi
done

if [ -n "$FOUND" ]; then
  ts=$(date +%Y%m%d)
  dest="${OUT_DIR}/ADENT_TARSM_V3_swagger_${ts}.json"
  cp /tmp/_spec.$$ "$dest"
  rm -f /tmp/_spec.$$
  echo
  echo "== 저장 완료: $dest"
  command -v python3 >/dev/null && python3 - "$dest" <<'PY'
import json,sys
d=json.load(open(sys.argv[1]))
print("  spec :", d.get("openapi") or d.get("swagger"))
info=d.get("info",{}); print("  title:", info.get("title"), info.get("version"))
paths=d.get("paths",{}); print("  paths:", len(paths))
for p,ops in sorted(paths.items()):
    ms=",".join(m.upper() for m in ops if m in ("get","post","put","delete","patch"))
    print(f"    {ms:<12} {p}")
PY
  exit 0
fi

rm -f /tmp/_spec.$$
echo
echo "== 2) 관례 경로 실패 → Swagger UI 페이지에서 스펙 URL 추출 시도 =="
html=$(curl -s --max-time 8 "${BASE}/api/v3" || true)
if [ -z "$html" ]; then
  echo "  ${BASE}/api/v3 응답 없음 — 로봇 전원/네트워크/IP 확인"
  exit 2
fi
echo "$html" | grep -oE '(url|configUrl|spec[Uu]rl)[^,;]{0,200}' | head -20
echo
echo "  위 출력에 스펙 파일 경로가 보이면 그 경로를 직접 받으십시오:"
echo "    curl -s ${BASE}<경로> -o ${OUT_DIR}/ADENT_TARSM_V3_swagger.json"
echo
echo "  보이지 않으면(스펙 URL을 JS가 동적으로 넣는 경우) 브라우저 수동 절차:"
echo "    ① http://${HOST}/api/v3 접속 → F12 → Network 탭 → 새로고침"
echo "    ② Type 이 xhr/fetch 이고 응답이 JSON 인 요청을 찾음(보통 이름에 swagger/api-docs/openapi 포함)"
echo "    ③ 그 요청 우클릭 → Copy → Copy link address"
echo "    ④ 또는 Console 탭에서 아래 실행 후 붙여넣기 저장:"
echo "       copy(JSON.stringify(window.ui.specSelectors.specJson().toJS(), null, 2))"
exit 3
