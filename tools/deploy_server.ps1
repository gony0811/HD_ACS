# 현장 서버 PC로 HD_ACS App·UI 재배포 (개발 PC에서 실행, PowerShell)
#   tools\deploy_server.ps1                      # App+UI 배포 (기본 서버 user@10.10.100.4)
#   tools\deploy_server.ps1 -Only app            # App만 / -Only ui
#   tools\deploy_server.ps1 -Force               # 진행 중 run이 있어도 배포
#   tools\deploy_server.ps1 -Sql db\migrations\2026-10-20_x.sql   # 배포 전 DB 마이그레이션 적용
#   tools\deploy_server.ps1 -Server user@192.168.0.19 -Jump eggplant@100.116.213.68
#                                                # 경유 PC(HD_AMR 원격 PC)를 거쳐 배포 — 서버 API 확인도 서버 안에서
#
# 전제: 서버에 SSH 키 접속(administrators_authorized_keys), 서비스 HDACS-App·postgresql-x64-16 설치,
#       배치 C:\HDACS\{app,ui}. 서버의 appsettings.json은 덮어쓰지 않는다(현장 설정 보존).
#       교체 전 현재 버전을 C:\HDACS\backup\<시각>\ 에 백업 — 되돌리기: 그 폴더를 app/ui로 복사 후 서비스 재시작.
param(
    [string]$Server = "user@10.10.100.4",
    [ValidateSet("all", "app", "ui")] [string]$Only = "all",
    [switch]$Force,
    [string[]]$Sql = @(),
    # 경유 PC(user@host) — 서버에 직접 닿지 않을 때. ssh·scp 에 -J 로 넘기고, 서버 API 확인은 ssh 로 서버 안에서 한다.
    [string]$Jump = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$stage = Join-Path (Split-Path -Parent $root) "deploy_pkg\staging"
$ssh = @("-o", "BatchMode=yes", "-o", "ConnectTimeout=10")
if ($Jump) { $ssh += @("-J", $Jump) }
$host_ = $Server.Split("@")[-1]
# Windows OpenSSH 우선 — Git 의 ssh 는 한글 사용자 폴더 경로를 깨뜨려 known_hosts·키를 못 찾는다
$sshDir = "$env:SystemRoot\System32\OpenSSH"
$sshExe = if (Test-Path "$sshDir\ssh.exe") { "$sshDir\ssh.exe" } else { "ssh" }
$scpExe = if (Test-Path "$sshDir\scp.exe") { "$sshDir\scp.exe" } else { "scp" }

function Remote([string]$cmd) {
    & $sshExe @ssh $Server $cmd
    if ($LASTEXITCODE -ne 0) { throw "원격 명령 실패 (exit $LASTEXITCODE)" }
}

# 서버 API GET — 경유 접속이면 서버 안에서 127.0.0.1 로 묻는다(이 PC 에서 서버 5199 에 닿지 않으므로)
function ServerApi([string]$path) {
    if (-not $Jump) { return Invoke-RestMethod "http://${host_}:5199$path" -TimeoutSec 5 }
    $json = & $sshExe @ssh $Server "Invoke-RestMethod 'http://127.0.0.1:5199$path' -TimeoutSec 5 | ConvertTo-Json -Compress -Depth 4"
    if ($LASTEXITCODE -ne 0) { throw (New-Object System.Net.WebException "서버 API 응답 없음") }
    # 빈 결과는 아무것도 내보내지 않는다 — return @() 는 $null 이 되어 @($null).Count = 1 로 세어진다.
    # 배열은 항목별로 풀어 내보내 호출부의 @(...).Count 가 실제 건수가 되게 한다(PS 5.1 ConvertFrom-Json 은 배열을 한 덩어리로 낸다)
    if (-not $json) { return }
    ($json -join "`n") | ConvertFrom-Json | ForEach-Object { $_ }
}

$parts = if ($Only -eq "all") { @("app", "ui") } else { @($Only) }
$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = if (git -C $root status --porcelain) { " (+커밋 안 된 변경)" } else { "" }
Write-Host "==> 배포: $($parts -join '+') / $commit$dirty → $Server"

# 1) 진행 중 run 확인 — 배포는 App 재시작이라 run 진행 중이면 중단(-Force로 무시)
try {
    $running = ServerApi "/api/runs?status=RUNNING"
    if (@($running).Count -gt 0 -and -not $Force) { throw "진행 중인 run $(@($running).Count)건 — 끝난 뒤 배포하거나 -Force" }
} catch [System.Net.WebException] { Write-Host "   (서버 API 응답 없음 — 확인 생략)" }

# 2) publish (self-contained, 서버에 .NET 불필요). appsettings.json은 빼서 서버 설정 보존
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$proj = @{ app = "src\HD.Acs.App\HD.Acs.App.csproj"; ui = "src\HD.Acs.UI.Desktop\HD.Acs.UI.Desktop.csproj" }
foreach ($p in $parts) {
    Write-Host "==> publish $p"
    dotnet publish (Join-Path $root $proj[$p]) -c Release -r win-x64 --self-contained true -p:DebugType=none -o (Join-Path $stage $p) --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish 실패: $p" }
    Remove-Item (Join-Path $stage "$p\appsettings.json") -ErrorAction SilentlyContinue
}

# 3) 전송
Write-Host "==> 전송"
Remote "if (Test-Path C:\HDACS\staging) { Remove-Item -Recurse -Force C:\HDACS\staging }; New-Item -ItemType Directory C:\HDACS\staging | Out-Null"
& $scpExe -q @ssh -r ($parts | ForEach-Object { Join-Path $stage $_ }) "${Server}:C:/HDACS/staging/"
if ($LASTEXITCODE -ne 0) { throw "scp 실패" }
foreach ($f in $Sql) {
    & $scpExe -q @ssh (Join-Path $root $f) "${Server}:C:/HDACS/staging/"
    if ($LASTEXITCODE -ne 0) { throw "scp 실패: $f" }
}

# 4) 교체: App 정지(+서버 UI 종료) → 백업 → SQL → 복사 → App 시작
$sqlNames = ($Sql | ForEach-Object { "'" + (Split-Path $_ -Leaf) + "'" }) -join ","
$swap = @"
`$ErrorActionPreference = 'Stop'
`$ts = Get-Date -Format yyyyMMdd_HHmmss
`$parts = @($(($parts | ForEach-Object { "'$_'" }) -join ","))
if (`$parts -contains 'ui') { Get-Process HD.Acs.UI.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force }
if (`$parts -contains 'app') { Stop-Service HDACS-App; (Get-Service HDACS-App).WaitForStatus('Stopped', '00:00:30') }
foreach (`$p in `$parts) {
  New-Item -ItemType Directory -Force C:\HDACS\backup\`$ts | Out-Null
  robocopy C:\HDACS\`$p C:\HDACS\backup\`$ts\`$p /E /NFL /NDL /NJH /NJS /NP | Out-Null
  robocopy C:\HDACS\staging\`$p C:\HDACS\`$p /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if (`$LASTEXITCODE -ge 8) { throw "복사 실패: `$p" }
}
`$env:PGPASSWORD = 'postgres'
foreach (`$s in @($sqlNames)) {
  & 'C:\Program Files\PostgreSQL\16\bin\psql.exe' -h 127.0.0.1 -U postgres -d hdacs -v ON_ERROR_STOP=1 -q -f "C:\HDACS\staging\`$s"
  if (`$LASTEXITCODE -ne 0) { throw "SQL 실패: `$s" }
  "SQL 적용: `$s"
}
if (`$parts -contains 'app') { Start-Service HDACS-App }
Set-Content C:\HDACS\VERSION.txt "$commit$dirty  `$(Get-Date -Format 'yyyy-MM-dd HH:mm')  (`$(`$parts -join '+'))" -Encoding UTF8
Get-ChildItem C:\HDACS\backup -Directory | Sort-Object Name -Descending | Select-Object -Skip 5 | Remove-Item -Recurse -Force
"백업: C:\HDACS\backup\`$ts"
"@
# 따옴표·줄바꿈이 ssh 명령줄에서 깨지지 않도록 스크립트 파일로 보내 실행 (BOM 포함 UTF-8 — PS 5.1 한글)
$swapFile = Join-Path $stage "swap.ps1"
[IO.File]::WriteAllText($swapFile, $swap, (New-Object Text.UTF8Encoding $true))
& $scpExe -q @ssh $swapFile "${Server}:C:/HDACS/staging/"
if ($LASTEXITCODE -ne 0) { throw "scp 실패: swap.ps1" }
Write-Host "==> 교체"
Remote "powershell -NoProfile -ExecutionPolicy Bypass -File C:\HDACS\staging\swap.ps1"

# 5) 확인
if ($parts -contains "app") {
    Write-Host "==> App 기동 확인"
    $ok = $false
    for ($i = 0; $i -lt 12 -and -not $ok; $i++) {
        Start-Sleep 5
        try { ServerApi "/api/robots" | Out-Null; $ok = $true } catch { }
    }
    if (-not $ok) { throw "App 응답 없음 — 서버 C:\HDACS\logs\app-*.log 확인, 되돌리기는 C:\HDACS\backup 최신 폴더" }
}
Write-Host "완료: $commit$dirty 배포됨 (서버 C:\HDACS\VERSION.txt)"
