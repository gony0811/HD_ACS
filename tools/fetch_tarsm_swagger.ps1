<#
  TARS-M v3 REST API 스펙(swagger.json / openapi.json) 자동 탐색·수집 — Windows PowerShell 판
  (Bash 판: tools/fetch_tarsm_swagger.sh)

  사용법:
      .\tools\fetch_tarsm_swagger.ps1 -RobotIp 10.10.100.200
      .\tools\fetch_tarsm_swagger.ps1 -RobotIp 10.10.100.200 -OutDir .\docs

  주의: PowerShell 에서 `curl` 은 Invoke-WebRequest 의 별칭이라 -s -I 같은 유닉스 플래그가 통하지 않는다.
        유닉스식으로 쓰려면 `curl.exe` 를 명시할 것.
#>
param(
    [Parameter(Mandatory=$true)][string]$RobotIp,
    [string]$OutDir = ".\docs"
)

$ErrorActionPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 프레임워크별 관례 경로 (앞쪽일수록 가능성 높음)
$candidates = @(
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

Write-Host "== 1) 관례 경로 탐색 ==========================================" -ForegroundColor Cyan
$found = $null
foreach ($p in $candidates) {
    $url = "http://$RobotIp$p"
    try {
        $r = Invoke-WebRequest -Uri $url -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        if ($r.StatusCode -eq 200 -and $r.Content -match '"(openapi|swagger)"\s*:') {
            Write-Host ("  [FOUND] {0}  ({1} bytes)" -f $url, $r.Content.Length) -ForegroundColor Green
            $found = $r.Content
            break
        } else {
            Write-Host ("  [ ---- ] {0}  ({1}, JSON 아님)" -f $url, $r.StatusCode)
        }
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if (-not $code) { $code = "no-response" }
        Write-Host ("  [ ---- ] {0}  ({1})" -f $url, $code)
    }
}

if ($found) {
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    $dest = Join-Path $OutDir ("ADENT_TARSM_V3_swagger_{0}.json" -f (Get-Date -Format "yyyyMMdd"))
    Set-Content -Path $dest -Value $found -Encoding UTF8
    Write-Host ""
    Write-Host "== 저장 완료: $dest" -ForegroundColor Green
    try {
        $spec = $found | ConvertFrom-Json
        $ver = if ($spec.openapi) { $spec.openapi } else { $spec.swagger }
        Write-Host ("  spec : {0}" -f $ver)
        Write-Host ("  title: {0} {1}" -f $spec.info.title, $spec.info.version)
        $names = $spec.paths.PSObject.Properties.Name | Sort-Object
        Write-Host ("  paths: {0}" -f $names.Count)
        foreach ($n in $names) {
            $methods = ($spec.paths.$n.PSObject.Properties.Name |
                        Where-Object { $_ -in @("get","post","put","delete","patch") } |
                        ForEach-Object { $_.ToUpper() }) -join ","
            Write-Host ("    {0,-12} {1}" -f $methods, $n)
        }
    } catch { Write-Host "  (JSON 파싱 실패 — 파일은 저장됨)" -ForegroundColor Yellow }
    exit 0
}

Write-Host ""
Write-Host "== 2) 관례 경로 실패 → 엔드포인트 존재 여부 직접 확인 ==" -ForegroundColor Cyan
@("robot/pose","robot/status","robot/go","robot/state","robot/map","robot/node") | ForEach-Object {
    $ep = $_
    $url = "http://$RobotIp/api/v3/$ep"
    try   { $code = (Invoke-WebRequest -Uri $url -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop).StatusCode }
    catch { $code = $_.Exception.Response.StatusCode.value__; if (-not $code) { $code = "no-response" } }
    Write-Host ("  {0,-14} {1}" -f $ep, $code)
}
Write-Host ""
Write-Host "  판독: 200=GET 가능 / 405=존재하나 GET 불가(POST 전용) / 404=없음 / no-response=로봇 미도달"
Write-Host ""
Write-Host "  스펙 파일 경로를 못 찾으면 브라우저 수동 절차:"
Write-Host "    (1) http://$RobotIp/api/v3 접속 -> F12 -> Network 탭 -> 새로고침"
Write-Host "    (2) 응답이 JSON 인 요청(이름에 swagger/api-docs/openapi 포함) 을 찾아 URL 확인"
Write-Host "    (3) 또는 Console 탭에서 아래 실행 후 붙여넣기 저장:"
Write-Host "        copy(JSON.stringify(window.ui.specSelectors.specJson().toJS(), null, 2))"
exit 3
