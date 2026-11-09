# HD_ACS DB 덤프(.sql)를 운영(현장) 서버 PostgreSQL에 통째로 적용 — 운영 서버에서 실행 (Windows PowerShell 5.1 / 7)
#
#   powershell -ExecutionPolicy Bypass -File apply_db_dump.ps1 -DumpFile C:\HDACS\hdacs_dump_20261109_1522.sql
#   powershell -ExecutionPolicy Bypass -File apply_db_dump.ps1 -DumpFile ... -WhatIf     # 확인만(아무것도 바꾸지 않음)
#
# 하는 일:
#   1) HDACS-App 서비스 정지(DB 연결 끊기)
#   2) 기존 DB 백업 — pg_dump -Fc 로 C:\HDACS\backup\db\<DB>_<시각>.dump
#   3) 기존 DB 를 <DB>_backup_<시각> 으로 이름 변경(지우지 않음 — 되돌리기용)
#   4) 같은 인코딩·로케일로 빈 <DB> 생성 → 덤프를 한 트랜잭션으로 적용(오류 하나라도 나면 전부 취소)
#   5) 실패하면 새 DB 를 지우고 이름을 되돌려 원상 복구, 성공하면 주요 테이블 행 수 출력
#   6) HDACS-App 서비스 시작
#
# 되돌리기(적용 후 문제 시): 서비스 정지 → DROP DATABASE <DB> → ALTER DATABASE <DB>_backup_<시각> RENAME TO <DB> → 서비스 시작.
# 이름 바꾼 옛 DB 는 자동으로 지우지 않는다 — 운영 확인 후 직접 DROP DATABASE 하세요.
#
# 덤프 만드는 법(개발 PC, 개발 DB 컨테이너):
#   docker exec -e PGPASSWORD=postgres dev-postgres pg_dump -h 127.0.0.1 -U postgres -d hdacs --no-owner --no-privileges --encoding=UTF8 > hdacs_dump.sql
#   ※ pg_dump 16.10+ 는 \restrict / \unrestrict 줄을 넣는다 — psql 16.10 미만에서는 모르는 명령이라 적용이 멈춘다.
#     이 스크립트는 두 줄이 있으면 빼고 적용한다(복원 중 보호용일 뿐 데이터와 무관).
param(
    [Parameter(Mandatory = $true)] [string]$DumpFile,
    [string]$Db = "hdacs",
    [string]$PgHost = "127.0.0.1",
    [int]$PgPort = 5432,
    [string]$PgUser = "postgres",
    [string]$PgPassword = $env:PGPASSWORD,
    [string]$Service = "HDACS-App",
    [string]$BackupDir = "C:\HDACS\backup\db",
    [switch]$WhatIf
)
$ErrorActionPreference = "Stop"

function Find-PgBin {
    $cmd = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($cmd) { return Split-Path $cmd.Source }
    $dirs = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending
    if ($dirs) { return Split-Path $dirs[0].FullName }
    throw "psql.exe 를 찾지 못했습니다 — PostgreSQL bin 폴더를 PATH 에 넣거나 설치를 확인하세요."
}

$bin = Find-PgBin
$psql = Join-Path $bin "psql.exe"
$pgdump = Join-Path $bin "pg_dump.exe"
if (-not (Test-Path $DumpFile)) { throw "덤프 파일이 없습니다: $DumpFile" }
if (-not $PgPassword) {
    $sec = Read-Host "PostgreSQL '$PgUser' 비밀번호" -AsSecureString
    $PgPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}
$env:PGPASSWORD = $PgPassword
$env:PGCLIENTENCODING = "UTF8"
$conn = @("-h", $PgHost, "-p", "$PgPort", "-U", $PgUser)

function Q([string]$database, [string]$sql) {
    $out = & $psql @conn -d $database -v ON_ERROR_STOP=1 -At -c $sql
    if ($LASTEXITCODE -ne 0) { throw "psql 실패 (exit $LASTEXITCODE): $sql" }
    return $out
}

$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$renamed = "${Db}_backup_$ts"
Write-Host "==> psql: $psql ($(& $psql --version))"
Write-Host "==> 덤프: $DumpFile ($([math]::Round((Get-Item $DumpFile).Length / 1MB, 1)) MB)"

# 덤프 정리 — \restrict / \unrestrict 줄 제거(옛 psql 호환), 결과는 임시 파일
$clean = Join-Path $env:TEMP "hdacs_apply_$ts.sql"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$lines = [System.IO.File]::ReadAllLines((Resolve-Path $DumpFile), $utf8)
$kept = $lines | Where-Object { -not ($_.StartsWith('\restrict ') -or $_.StartsWith('\unrestrict ')) }
[System.IO.File]::WriteAllLines($clean, [string[]]$kept, $utf8)
if ($lines.Count -ne @($kept).Count) { Write-Host "    \restrict/\unrestrict $($lines.Count - @($kept).Count)줄 제거" }

$exists = (Q "postgres" "select count(*) from pg_database where datname = '$Db'") -eq "1"
if ($exists) {
    $info = (Q "postgres" "select pg_encoding_to_char(encoding) || '|' || datcollate || '|' || datctype from pg_database where datname = '$Db'").Split("|")
    $enc, $collate, $ctype = $info[0], $info[1], $info[2]
    $runs = $null
    try { $runs = Q $Db "select count(*) from run.scenario_run where state in ('RUNNING','WAITING_FLOOR_TRANSFER')" } catch { }
    Write-Host "==> 기존 DB '$Db' 있음 — 인코딩 $enc, 로케일 $collate / $ctype, 진행 중 run $runs"
    if ($runs -and [int]$runs -gt 0) { Write-Warning "진행 중인 run 이 $runs 건 있습니다 — 적용하면 그 run 은 덤프 시점 상태로 바뀝니다." }
} else {
    $enc, $collate, $ctype = "UTF8", $null, $null
    Write-Host "==> 기존 DB '$Db' 없음 — 새로 만든다"
}
if ($WhatIf) { Write-Host "==> -WhatIf: 여기까지 확인만 했습니다. 바꾼 것 없음."; Remove-Item $clean; exit 0 }

$svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
    Write-Host "==> 서비스 $Service 정지"
    Stop-Service -Name $Service -Force
    $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
}

try {
    if ($exists) {
        New-Item -ItemType Directory -Force $BackupDir | Out-Null
        $bak = Join-Path $BackupDir "${Db}_$ts.dump"
        Write-Host "==> 기존 DB 백업: $bak"
        & $pgdump @conn -d $Db -Fc -f $bak
        if ($LASTEXITCODE -ne 0) { throw "백업 실패 (exit $LASTEXITCODE) — 아무것도 바꾸지 않았습니다." }
        Write-Host "==> 기존 DB 이름 변경: $Db → $renamed"
        Q "postgres" "select pg_terminate_backend(pid) from pg_stat_activity where datname = '$Db' and pid <> pg_backend_pid()" | Out-Null
        Q "postgres" "ALTER DATABASE `"$Db`" RENAME TO `"$renamed`"" | Out-Null
    }

    $locale = if ($collate) { " LC_COLLATE '$collate' LC_CTYPE '$ctype'" } else { "" }
    Write-Host "==> 빈 DB 생성: $Db (ENCODING $enc$locale)"
    Q "postgres" "CREATE DATABASE `"$Db`" WITH TEMPLATE template0 ENCODING '$enc'$locale" | Out-Null

    Write-Host "==> 덤프 적용(한 트랜잭션)"
    $log = Join-Path $BackupDir "apply_$ts.log"
    New-Item -ItemType Directory -Force $BackupDir | Out-Null
    # 외부 프로그램의 stderr 를 리다이렉트하면 PS 5.1 이 경고까지 오류로 감싸므로 이 구간만 Continue 로 두고 종료 코드로 판정한다.
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    & $psql @conn -d $Db -v ON_ERROR_STOP=1 --single-transaction -q -f $clean 2>&1 | Out-File -FilePath $log -Encoding utf8
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) { throw "덤프 적용 실패 (exit $code) — 로그: $log" }
}
catch {
    Write-Warning $_
    if ($exists -and ((Q "postgres" "select count(*) from pg_database where datname = '$renamed'") -eq "1")) {
        Write-Host "==> 원상 복구: 새 $Db 삭제, $renamed → $Db"
        Q "postgres" "select pg_terminate_backend(pid) from pg_stat_activity where datname = '$Db' and pid <> pg_backend_pid()" | Out-Null
        Q "postgres" "DROP DATABASE IF EXISTS `"$Db`"" | Out-Null
        Q "postgres" "ALTER DATABASE `"$renamed`" RENAME TO `"$Db`"" | Out-Null
    }
    if ($svc) { Start-Service -Name $Service }
    Remove-Item $clean -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "==> 적용 완료 — 주요 테이블 행 수"
foreach ($t in "ref.tank_geometry", "ref.wall", "ref.inspection_area", "ref.area_task", "ref.map", "ref.map_calibration",
                "ref.map_calibration_point", "ref.scenario", "ref.scenario_area", "ref.robot", "run.scenario_run", "sys.audit_log") {
    Write-Host ("    {0,-28} {1}" -f $t, (Q $Db "select count(*) from $t"))
}
Remove-Item $clean -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "==> 서비스 $Service 시작"
    Start-Service -Name $Service
}
if ($exists) {
    Write-Host "==> 옛 DB 는 '$renamed' 로 남아 있습니다(백업 파일 $bak 도 있음). 운영 확인 후 필요 없으면 psql(postgres DB)에서:"
    Write-Host "      DROP DATABASE `"$renamed`";"
}
