#!/usr/bin/env bash
# WP-4 시뮬레이터 E2E 검증 [SPEC_PHASE2_ACS.md §5, §7]
# 브로커 기동 → 시뮬레이터(실패 주입 설정) 기동 → SimTest 드라이버로 3개 시나리오 검증.
# 사용: ./run_simtest.sh [brokerHost]   (기본 localhost, 사전 요구: mosquitto, dotnet 8)
set -euo pipefail
cd "$(dirname "$0")"
BROKER=${1:-localhost}
FAIL_ID="00000000-0000-4000-8000-00000000fa11"   # SimTest S3와 공유하는 상수

# 1. 브로커 — 이미 떠 있으면 재사용
if ! (exec 3<>/dev/tcp/localhost/1883) 2>/dev/null; then
  echo "[RUN] mosquitto 기동"
  mosquitto -d 2>/dev/null || { echo "mosquitto 설치 필요 (brew/apt install mosquitto)"; exit 1; }
  sleep 1
fi

# 2. 빌드 (UI 프로젝트는 Windows 전용 — 개별 빌드)
dotnet build HD.Acs.Simulator -c Release -v q
dotnet build HD.Acs.SimTest -c Release -v q

# 3. 시뮬레이터 기동 (실패 주입 + 고속 타이밍)
SIM_FAIL_ACTION_IDS=$FAIL_ID SIM_TRAVEL_MS=200 SIM_FULL_MS=400 SIM_SHARED_MS=100 \
  dotnet run --project HD.Acs.Simulator -c Release --no-build -- "$BROKER" HHI AMR-01 CT1-L2 &
SIM_PID=$!
trap 'kill $SIM_PID 2>/dev/null || true' EXIT
sleep 2

# 4. 드라이버 실행 (exit code = 검증 결과)
dotnet run --project HD.Acs.SimTest -c Release --no-build -- "$BROKER" HHI AMR-01
