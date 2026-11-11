# DB 이전 — 개발 PC의 HD_ACS DB를 덤프해 현장 서버 PC에 복원 (개발 PC에서 실행, PowerShell)
#   tools\migrate_db.ps1                       # 덤프만: db\dump\hdacs_<시각>.dump 생성 + 복원 검증
#   tools\migrate_db.ps1 -Restore              # 가장 최근 덤프를 서버에 복원 (미리보기 — 서버를 바꾸지 않음)
#   tools\migrate_db.ps1 -Restore -Apply       # 실제 복원 (서버 hdacs DB 전체 교체)
#   tools\migrate_db.ps1 -Restore -Apply -Dump db\dump\hdacs_20261111_2332.dump
#
# 덤프: docker 컨테이너(dev-postgres)의 pg_dump 사용 — 개발 PC에 PostgreSQL 클라이언트가 없어도 된다.
#       custom 포맷(-Fc)·소유자/권한 제외. 만든 뒤 임시 DB에 복원해 주요 테이블 건수를 대조한다.
# 복원: 서버(SSH 키 접속, 서비스 HDACS-App·postgresql-x64-16, deploy_server.ps1 과 같은 전제)에서
#       ① HDACS-App 정지(서버 UI 종료) ② 서버의 현재 DB를 C:\HDACS\backup\db\hdacs_<시각>.dump 로 백업
#       ③ hdacs DB 삭제·재생성 ④ pg_restore ⑤ 진행 중이던 run 을 ABORTED 로 정리 ⑥ HDACS-App 시작.
#
# 주의: 복원은 서버 DB를 **통째로 교체**한다 — 서버에서만 넣은 데이터(현장에서 캡처한 맵 캘리브레이션 기준점,
#       정차 덮어쓰기, 검사 이력)는 덤프에 없으면 사라진다. ② 의 백업으로 되돌릴 수 있다:
#         pg_restore -h 127.0.0.1 -U postgres -d hdacs --clean --if-exists C:\HDACS\backup\db\<파일>
#       App 버전: 덤프보다 새 App 은 기동 때 추가 컬럼을 스스로 넣는다(SchemaEnsure). 더 옛 App 이면 먼저 deploy_server.ps1 로 배포.
param(
    [switch]$Restore,
    [switch]$Apply,
    [string]$Dump,
    [string]$Server = "user@10.10.100.4",
    [string]$Container = "dev-postgres",
    [string]$Database = "hdacs",
    [string]$PgPassword = "postgres",
    [switch]$KeepActiveRuns      # 복원 뒤 RUNNING·WAITING run 을 그대로 둔다(기본은 ABORTED 로 정리)
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dumpDir = Join-Path $root "db\dump"
$ssh = @("-o", "BatchMode=yes", "-o", "ConnectTimeout=10")

$counts = "select 'area='||(select count(*) from ref.inspection_area)||' task='||(select count(*) from ref.area_task)" +
          "||' calibration='||(select count(*) from ref.map_calibration)||' run='||(select count(*) from run.scenario_run)" +
          "||' action='||(select count(*) from run.order_action)"

function Dk([string[]]$a) {
    $out = & docker.exe @a
    if ($LASTEXITCODE -ne 0) { throw "docker $($a -join ' ') 실패" }
    $out
}

if (-not $Restore) {
    # ── 덤프 ──
    New-Item -ItemType Directory -Force $dumpDir | Out-Null
    $file = Join-Path $dumpDir ("hdacs_{0}.dump" -f (Get-Date -Format yyyyMMdd_HHmm))
    Write-Host "==> 덤프: $Container/$Database → $file"
    Dk @("exec", $Container, "pg_dump", "-U", "postgres", "-d", $Database, "-Fc", "--no-owner", "--no-privileges", "-f", "/tmp/hdacs.dump") | Out-Null
    Dk @("cp", "${Container}:/tmp/hdacs.dump", $file) | Out-Null

    Write-Host "==> 복원 검증 (임시 DB hdacs_verify)"
    Dk @("exec", $Container, "psql", "-U", "postgres", "-q", "-c", "drop database if exists hdacs_verify", "-c", "create database hdacs_verify") | Out-Null
    Dk @("exec", $Container, "pg_restore", "-U", "postgres", "-d", "hdacs_verify", "--no-owner", "--exit-on-error", "/tmp/hdacs.dump") | Out-Null
    $src = Dk @("exec", $Container, "psql", "-U", "postgres", "-d", $Database, "-Atc", $counts)
    $dst = Dk @("exec", $Container, "psql", "-U", "postgres", "-d", "hdacs_verify", "-Atc", $counts)
    Dk @("exec", $Container, "psql", "-U", "postgres", "-q", "-c", "drop database hdacs_verify") | Out-Null
    Dk @("exec", $Container, "rm", "/tmp/hdacs.dump") | Out-Null
    Write-Host "   원본 : $src"
    Write-Host "   복원 : $dst"
    if ($src -ne $dst) { throw "덤프 검증 실패 — 건수가 다릅니다." }
    Write-Host ("완료: {0} ({1:N0} KB). 서버 복원 미리보기: tools\migrate_db.ps1 -Restore" -f $file, ((Get-Item $file).Length / 1KB))
    return
}

# ── 서버 복원 ──
if (-not $Dump) {
    $latest = Get-ChildItem $dumpDir -Filter "hdacs_*.dump" -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw "덤프 파일이 없습니다 — 먼저 tools\migrate_db.ps1 로 덤프를 만드세요." }
    $Dump = $latest.FullName
} elseif (-not [IO.Path]::IsPathRooted($Dump)) { $Dump = Join-Path $root $Dump }
if (-not (Test-Path $Dump)) { throw "덤프 파일 없음: $Dump" }
$name = Split-Path $Dump -Leaf
$host_ = $Server.Split("@")[-1]
$mode = if ($Apply) { "apply" } else { "preview" }
$keep = if ($KeepActiveRuns) { '$true' } else { '$false' }
Write-Host "==> 복원 ($mode): $name → $Server / $Database"

$remote = @"
`$ErrorActionPreference = 'Stop'
`$bin = 'C:\Program Files\PostgreSQL\16\bin'
`$env:PGPASSWORD = '$PgPassword'
`$pg = @('-h', '127.0.0.1', '-U', 'postgres')
`$counts = "$counts"
function Run([string]`$exe, [string[]]`$a) { & (Join-Path `$bin `$exe) @a; if (`$LASTEXITCODE -ne 0) { throw "`$exe 실패 (exit `$LASTEXITCODE)" } }

`$exists = (Run 'psql.exe' (`$pg + @('-d', 'postgres', '-Atc', "select count(*) from pg_database where datname = '$Database'"))) -eq '1'
if (`$exists) { "서버 현재 : " + (Run 'psql.exe' (`$pg + @('-d', '$Database', '-Atc', `$counts))) } else { "서버 현재 : DB 없음" }
"덤프 내용 : " + ((Run 'pg_restore.exe' @('-l', 'C:\HDACS\staging\$name')) | Select-String 'TABLE DATA').Count + "개 테이블"
if ('$mode' -ne 'apply') { "미리보기 — 서버는 바뀌지 않았습니다. 실제 복원은 -Apply."; return }

Get-Process HD.Acs.UI.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service HDACS-App; (Get-Service HDACS-App).WaitForStatus('Stopped', '00:00:30')
try {
  if (`$exists) {
    New-Item -ItemType Directory -Force C:\HDACS\backup\db | Out-Null
    `$bak = "C:\HDACS\backup\db\hdacs_`$(Get-Date -Format yyyyMMdd_HHmmss).dump"
    Run 'pg_dump.exe' (`$pg + @('-d', '$Database', '-Fc', '-f', `$bak)) | Out-Null
    "서버 백업 : `$bak"
    Run 'psql.exe' (`$pg + @('-d', 'postgres', '-q', '-c', "select pg_terminate_backend(pid) from pg_stat_activity where datname = '$Database' and pid <> pg_backend_pid()")) | Out-Null
    Run 'psql.exe' (`$pg + @('-d', 'postgres', '-q', '-c', 'drop database $Database')) | Out-Null
  }
  Run 'psql.exe' (`$pg + @('-d', 'postgres', '-q', '-c', 'create database $Database')) | Out-Null
  Run 'pg_restore.exe' (`$pg + @('-d', '$Database', '--no-owner', '--exit-on-error', 'C:\HDACS\staging\$name')) | Out-Null
  if (-not $keep) {
    # 개발 PC에서 진행 중이던 run 은 서버 로봇과 무관하다 — 남겨 두면 새 미션 시작이 409 로 막힌다
    `$n = Run 'psql.exe' (`$pg + @('-d', '$Database', '-Atc', "with u as (update run.scenario_run set state = 'ABORTED', ended_at = now() where state in ('RUNNING', 'WAITING_FLOOR_TRANSFER') returning 1) select count(*) from u"))
    "진행 중 run 정리 : `$n 건 → ABORTED"
  }
  "복원 후   : " + (Run 'psql.exe' (`$pg + @('-d', '$Database', '-Atc', `$counts)))
} finally { Start-Service HDACS-App }
"@

& ssh @ssh $Server "if (-not (Test-Path C:\HDACS\staging)) { New-Item -ItemType Directory C:\HDACS\staging | Out-Null }"
if ($LASTEXITCODE -ne 0) { throw "서버 접속 실패: $Server" }
$script = Join-Path $dumpDir "restore_remote.ps1"
[IO.File]::WriteAllText($script, $remote, (New-Object Text.UTF8Encoding $true))   # BOM UTF-8 — PS 5.1 한글
& scp -q @ssh $Dump $script "${Server}:C:/HDACS/staging/"
if ($LASTEXITCODE -ne 0) { throw "scp 실패" }
& ssh @ssh $Server "powershell -NoProfile -ExecutionPolicy Bypass -File C:\HDACS\staging\restore_remote.ps1"
if ($LASTEXITCODE -ne 0) { throw "원격 복원 실패 — 서버 HDACS-App 상태와 C:\HDACS\backup\db 를 확인하세요." }

if ($Apply) {
    Write-Host "==> App 기동 확인"
    $ok = $false
    for ($i = 0; $i -lt 12 -and -not $ok; $i++) {
        Start-Sleep 5
        try { Invoke-RestMethod "http://${host_}:5199/api/robots" -TimeoutSec 5 | Out-Null; $ok = $true } catch { }
    }
    if (-not $ok) { throw "App 응답 없음 — 서버 C:\HDACS\logs\app-*.log 확인. DB 되돌리기는 C:\HDACS\backup\db 의 최신 파일." }
    Write-Host "완료: $name 복원됨."
}
