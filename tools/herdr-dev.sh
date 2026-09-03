#!/usr/bin/env bash
set -euo pipefail

if [[ "${HERDR_ENV:-}" != "1" ]]; then
  cat <<'EOF'
이 명령은 Herdr 안에서 실행해야 합니다.

  1. herdr --session hd-acs
  2. ./tools/herdr-dev.sh
EOF
  exit 1
fi

for command in herdr jq dotnet docker codex; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "필수 명령을 찾을 수 없습니다: $command" >&2
    exit 1
  fi
done

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

create_tab() {
  local label="$1"
  local response
  local pane_id

  response="$(herdr tab create \
    --workspace "$workspace_id" \
    --cwd "$repo_dir" \
    --label "$label" \
    --no-focus)"
  pane_id="$(jq -er '.result.root_pane.pane_id' <<<"$response")"
  printf '%s\n' "$pane_id"
}

workspace_json="$(herdr workspace create \
  --cwd "$repo_dir" \
  --label "HD_ACS" \
  --no-focus)"
workspace_id="$(jq -er '.result.workspace.workspace_id' <<<"$workspace_json")"
infra_pane="$(jq -er '.result.root_pane.pane_id' <<<"$workspace_json")"
infra_tab="$(jq -er '.result.tab.tab_id' <<<"$workspace_json")"

herdr tab rename "$infra_tab" "Infra" >/dev/null
herdr pane rename "$infra_pane" "PostgreSQL + RabbitMQ" >/dev/null
herdr pane run "$infra_pane" "docker compose --env-file docker/.env -f docker/docker-compose.yml up" >/dev/null

app_pane="$(create_tab "App")"
herdr pane rename "$app_pane" "HD.Acs.App :5199" >/dev/null
herdr pane run "$app_pane" "dotnet run --project src/HD.Acs.App" >/dev/null

simulator_pane="$(create_tab "Simulator")"
herdr pane rename "$simulator_pane" "HD.Acs.Simulator" >/dev/null
herdr pane run "$simulator_pane" "dotnet run --project src/HD.Acs.Simulator -- localhost HHI AMR-01 CT1-L1" >/dev/null

ui_pane="$(create_tab "UI")"
herdr pane rename "$ui_pane" "HD.Acs.UI.Desktop" >/dev/null
herdr pane run "$ui_pane" "dotnet run --project src/HD.Acs.UI.Desktop" >/dev/null

codex_pane="$(create_tab "Codex")"
herdr pane rename "$codex_pane" "Codex" >/dev/null
codex_agent_name="hd_acs_${workspace_id//[^a-zA-Z0-9_-]/_}"
if ! herdr agent start "$codex_agent_name" --kind codex --pane "$codex_pane"; then
  echo "Codex 자동 시작에 실패했습니다. Codex 탭에서 'codex'를 직접 실행하세요." >&2
fi

herdr workspace focus "$workspace_id" >/dev/null

cat <<EOF
HD_ACS workspace를 시작했습니다: $workspace_id
탭: Infra / App / Simulator / UI / Codex
EOF
