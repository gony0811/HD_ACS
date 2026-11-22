# 현장 실적 대조 — HD_AMR 앱 로그의 검사 실적을 그때의 실제 도착 자세·리프트로 시뮬레이터에 다시 넣어 판정을 비교한다 (조회 전용)
#   tools\compare_field_reach.ps1 -Log D:\logs\app-2026-11-12.log                 # 천장(T) 대조, 요약 출력 + CSV 저장
#   tools\compare_field_reach.ps1 -Log a.log,b.log -Wall PU                       # 여러 로그, 다른 면
#   tools\compare_field_reach.ps1 -Log a.log -Wall "" -BaseUrl http://localhost:5299   # 모든 면, 다른 서버
#
# 하는 일: 로그에서 지령·도착·검사 관련 줄만 추려 관제 서버 POST /api/reach-sim/tanks/{선창}/field-compare 로 보낸다.
#          서버가 줄을 해석(정차 지령·도착 자세, 용접선 taskId, 리프트, 성공/실패 단계·사유)하고 그 자세로 리치 시뮬레이터를 돌린다.
# 읽는 법: "성공 → 가능" 이 낮으면 시뮬레이터가 과하게 막는 것(정차 후보를 잃는다),
#          "도달 실패 → 불가" 가 낮으면 시뮬레이터가 놓치는 것(현장에서 실패한다). 센서·운영 실패는 대조 대상이 아니다.
# 주의: 용접선 좌표·캘리브레이션은 서버의 지금 DB 값이다 — 로그 이후 캘리브레이션을 다시 했거나 용접선을 고쳤으면 그만큼 어긋난다.
#       PowerShell 5.1 / 7 모두 가능. 실행 정책에 막히면: powershell -ExecutionPolicy Bypass -File tools\compare_field_reach.ps1 ...
param(
    [Parameter(Mandatory = $true)][string[]]$Log,
    [string]$Tank = "CT1",
    [string]$Wall = "T",
    [string]$BaseUrl = "http://localhost:5199",
    [string]$OutDir = "."
)
$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$keep = "Vda5050OrderExecutor|WeldInspectionOrchestrator|CobotReachPrecheck|Vda5050AdapterService"
$nameOf = @{ Ok = "성공"; Reach = "도달 실패"; Rule = "규칙 거부"; Sensor = "센서·정렬 실패"; Operation = "운영 중단"; Other = "기타 실패"; NotRun = "실행 안 함"; Unknown = "결과 없음" }
$simOf = @{ Clear = "가능"; Collision = "간섭"; Unreachable = "도달 불가" }

foreach ($path in $Log) {
    $file = Get-Item $path
    $date = if ($file.Name -match "(\d{4}-\d{2}-\d{2})") { $Matches[1] } else { "" }
    # 19 MB 로그도 관련 줄은 수백 KB — 줄 머리(시각·등급·클래스)를 그대로 둔 채 추린다
    $lines = [System.IO.File]::ReadLines($file.FullName, [System.Text.Encoding]::UTF8) | Where-Object { $_ -match $keep }
    $body = $utf8.GetBytes(($lines -join "`n"))
    $q = "date=$date"
    if ($Wall) { $q += "&wallCode=$Wall" }
    $url = "$BaseUrl/api/reach-sim/tanks/$Tank/field-compare?$q"

    Write-Host ""
    Write-Host "== $($file.Name)  (선창 $Tank, 면 $(if ($Wall) { $Wall } else { '전체' }))"
    try {
        $r = Invoke-RestMethod -Method Post -Uri $url -Body $body -ContentType "text/plain; charset=utf-8" -TimeoutSec 600
    } catch {
        Write-Host "   서버 요청 실패: $($_.Exception.Message)"
        if ($_.ErrorDetails.Message) { Write-Host "   $($_.ErrorDetails.Message)" }
        continue
    }
    $s = $r.summary
    Write-Host ("   실적 {0}건 (시뮬레이션하지 못한 것 {1}건)" -f $s.rows, $s.notSimulated)
    if ($s.ok -gt 0) { Write-Host ("   성공 → 시뮬레이터도 가능      {0} / {1}  ({2:P0})" -f $s.okClear, $s.ok, ($s.okClear / $s.ok)) }
    if ($s.reach -gt 0) { Write-Host ("   도달 실패 → 시뮬레이터도 불가  {0} / {1}  ({2:P0})" -f $s.reachCaught, $s.reach, ($s.reachCaught / $s.reach)) }
    if ($null -ne $s.arrivalDevMedianMm) { Write-Host ("   도착 오차(지령 대비) 중앙 {0} mm · 최대 {1} mm" -f $s.arrivalDevMedianMm, $s.arrivalDevMaxMm)
        Write-Host ("   도착 오차 때문에 판정이 달라진 것 {0}건 (지령 자세였다면 다른 판정)" -f $s.arrivalFlips) }

    Write-Host "   현장 결과 × 시뮬레이터 판정:"
    foreach ($c in $s.cells) { Write-Host ("     {0,-10} {1,-6} {2,4}" -f $nameOf[$c.outcome], $simOf[$c.simVerdict], $c.count) }
    if ($s.reachSteps.Count -gt 0) {
        Write-Host "   도달 실패 단계별 (시뮬레이터가 불가로 본 수 / 건수):"
        foreach ($st in $s.reachSteps) { Write-Host ("     {0,-22} {1} / {2}" -f $st.step, $st.caught, $st.count) }
    }
    if ($s.tooStrict.Count -gt 0) {
        Write-Host "   성공을 막은 시뮬레이터 사유:"
        foreach ($st in $s.tooStrict) { Write-Host ("     {0,4}건  {1}" -f $st.count, $st.step) }
    }
    if ($r.parseWarnings.Count -gt 0) { Write-Host "   해석하지 못한 줄 $($r.parseWarnings.Count)건 (첫 줄: $($r.parseWarnings[0]))" }

    $out = Join-Path $OutDir ("field-compare-{0}-{1}-{2}.csv" -f $Tank, $(if ($Wall) { $Wall } else { "ALL" }), $(if ($date) { $date } else { $file.BaseName }))
    Invoke-WebRequest -Method Post -Uri "$url&format=csv" -Body $body -ContentType "text/plain; charset=utf-8" -TimeoutSec 600 -UseBasicParsing -OutFile $out
    Write-Host "   건별 표: $out"
}
