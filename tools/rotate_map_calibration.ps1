# 저장된 T_W_D(map_calibration)의 회전만 보정 — [도면 좌표 입력 프레임 오류 복구]
# tools/rotate_map_calibration.sh 의 PowerShell 판(현장 서버는 Windows — bash 없음).
#
#   증상 → 원인 대조표 (실제 로봇 위치/방향 대비 화면 표시)
#   ┌──────────────────────────────────────────────┬──────────────┐
#   │ 화면 표시                                     │ yaw 오차      │
#   ├──────────────────────────────────────────────┼──────────────┤
#   │ 사분면·방향 모두 정반대                        │ 180°         │
#   │  (예: 실제 선수/좌현·선수 방향 → 선미/우현·선미) │ -Deg 180     │
#   │ 좌·우로 90° 돌아감 (선수/좌현 → 선미/좌현,      │ ±90°         │
#   │  방향도 90° 회전)                              │ -Deg ±90     │
#   └──────────────────────────────────────────────┴──────────────┘
#   ※ 180° 오차는 탱크가 중심 대칭이라 **마커가 탱크 안에 그대로 남는다** —
#     "벽 밖으로 나가지 않았으니 맞다"고 판단하면 놓친다. 방향(뱃머리)까지 함께 봐야 한다.
#
#   근거: 표시 좌표 = R(-yaw)·(map - t), 표시 방향 = theta - yaw
#         [src/HD.Acs.Core/Geometry/DrawingTransform.cs]
#         yaw 가 Δ 만큼 어긋나면 표시 좌표·방향이 도면 원점 기준으로 -Δ 회전한다.
#         → 표시를 +Δ 되돌리려면  yaw_new = yaw_old - Δ,  t 는 그대로.
#
#   ┌ 앱(지오메트리) 도면 프레임 [src/HD.Acs.Core/Planning/TankGeometry.cs] ┐
#   │   +X = 선수(F),  -X = 선미(A),  +Y = 좌현(P*),  -Y = 우현(S*)        │
#   │   방향: 0°=선수, +90°=좌현, ±180°=선미, -90°=우현                    │
#   │   부호 판정 기준은 탱크 중심 (origin_ox, origin_oy) — 기본 (0,0)      │
#   └──────────────────────────────────────────────────────────────────┘
#
#   이 도구는 **회전만** 고친다(평행이동 t·RMS 불변). 위치가 따로 벗어났거나
#   기준점 측정 자체가 틀렸으면 재측정·재산출(캘리브레이션 마법사)이 필요하다.
#
# 사용법 (PowerShell):
#   tools\rotate_map_calibration.ps1 CT1-L1                       # 미리보기(기본 180°)
#   tools\rotate_map_calibration.ps1 CT1-L1 -Apply                # 실제 적용
#   tools\rotate_map_calibration.ps1 CT1-L1,CT1-L2,CT1-L3,CT1-L4 -Apply   # 전 층
#   tools\rotate_map_calibration.ps1 CT1-L4 -Deg -90 -Apply       # 90° 오차 보정
#
#   DB 접속은 매개변수 또는 PG* 환경변수(psql 표준). 비밀번호를 안 주면 물어본다.
#   bash 의 `export PG...=` 는 PowerShell 에서 동작하지 않는다($env:PGPASSWORD='...').
#
# 필요: psql(PostgreSQL 설치 시 포함 — PATH 에 없으면 C:\Program Files\PostgreSQL\*\bin 자동 탐색)
#       HD_ACS App 기동(solve 재계산 호출)
param(
    [Parameter(Mandatory = $true, Position = 0)] [string[]]$MapId,
    [double]$Deg = 180,
    [switch]$Apply,
    [string]$BaseUrl = "http://localhost:5199",
    [string]$PgHost = $(if ($env:PGHOST) { $env:PGHOST } else { "localhost" }),
    [int]$PgPort = $(if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 }),
    [string]$PgDatabase = $(if ($env:PGDATABASE) { $env:PGDATABASE } else { "hdacs" }),
    [string]$PgUser = $(if ($env:PGUSER) { $env:PGUSER } else { "hdacs" }),
    [string]$PgPassword
)
$ErrorActionPreference = "Stop"

# ── psql 찾기 ──────────────────────────────────────────────────────────────
$psqlExe = (Get-Command psql -ErrorAction SilentlyContinue)?.Source
if (-not $psqlExe) {
    $psqlExe = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $psqlExe) { throw "psql 을 찾을 수 없습니다. PostgreSQL bin 폴더를 PATH 에 넣거나 서버에서 실행하세요." }

$env:PGHOST = $PgHost; $env:PGPORT = "$PgPort"; $env:PGDATABASE = $PgDatabase; $env:PGUSER = $PgUser
if ($PgPassword) { $env:PGPASSWORD = $PgPassword }
elseif (-not $env:PGPASSWORD) {
    $sec = Read-Host "DB 비밀번호 ($PgUser@${PgHost}:$PgPort/$PgDatabase)" -AsSecureString
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

# 한 값 조회(없으면 빈 문자열) / 여러 열은 '|' 로 받아 쪼갠다
function Sql([string]$sql) {
    $out = & $psqlExe -At -F '|' -v ON_ERROR_STOP=1 -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql 실패: $out" }
    @($out | Where-Object { $_ -ne "" })
}
function Exec([string]$sql) {
    $out = & $psqlExe -q -v ON_ERROR_STOP=1 -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql 실패: $out" }
}

# ── 각도 ───────────────────────────────────────────────────────────────────
# 90° 배수는 부동소수 먼지 없이 정확한 정수로 고정(기준점 좌표가 .0000001 로 번지지 않게)
$m = ((($Deg % 360) + 360) % 360)
switch ($m) {
    0   { $rotCos = 1;  $rotSin = 0 }
    90  { $rotCos = 0;  $rotSin = 1 }
    180 { $rotCos = -1; $rotSin = 0 }
    270 { $rotCos = 0;  $rotSin = -1 }
    default { $r = $Deg * [Math]::PI / 180; $rotCos = [Math]::Cos($r); $rotSin = [Math]::Sin($r) }
}
function NormPi([double]$a) {
    while ($a -le -[Math]::PI) { $a += 2 * [Math]::PI }
    while ($a -gt [Math]::PI) { $a -= 2 * [Math]::PI }
    $a
}
function ToDeg([double]$rad) { $rad * 180 / [Math]::PI }

# 도면 좌표·방향 → 사람이 읽는 라벨 (부호 판정은 탱크 중심 기준)
function LabelPose([double]$x, [double]$y, $headDeg, [double]$ox, [double]$oy) {
    $e = 0.2
    $fx = if ($x -gt $ox + $e) { "선수" } elseif ($x -lt $ox - $e) { "선미" } else { "중앙" }
    $fy = if ($y -gt $oy + $e) { "좌현" } elseif ($y -lt $oy - $e) { "우현" } else { "중앙" }
    $s = "{0}/{1} ({2:F2}, {3:F2})" -f $fx, $fy, $x, $y
    if ($null -eq $headDeg) { return "$s, 방향 미보고" }
    $a = ToDeg (NormPi ([double]$headDeg * [Math]::PI / 180))
    $d = if ($a -gt -45 -and $a -le 45) { "선수" }
         elseif ($a -gt 45 -and $a -le 135) { "좌현" }
         elseif ($a -gt -135 -and $a -le -45) { "우현" }
         else { "선미" }
    "$s, 방향 {0}({1:F1}°)" -f $d, $a
}

Write-Host "== T_W_D 회전 보정 =="
Write-Host "   회전량 : $Deg° (표시 좌표·방향을 이만큼 되돌림, 평행이동 t 는 불변)"
Write-Host "   모드   : $(if ($Apply) { '실제 적용(-Apply)' } else { '미리보기' })"
Write-Host "   App    : $BaseUrl"
Write-Host "   DB     : ${PgHost}:$PgPort/$PgDatabase"

$failed = 0
foreach ($map in $MapId) {
    Write-Host ""
    Write-Host "────────────────────────────────────────────────────────────"
    Write-Host "[$map]"

    $ver = (Sql "SELECT version FROM ref.map WHERE map_id='$map';" | Select-Object -First 1)
    if (-not $ver) { Write-Warning "  ref.map 에 없는 mapId — 건너뜀"; $failed = 1; continue }

    $row = (Sql "SELECT tx, ty, yaw_rad, rms_m, point_count FROM ref.map_calibration
                  WHERE map_id='$map' AND map_version=$ver;" | Select-Object -First 1)
    if (-not $row) {
        Write-Warning "  맵버전 $ver 에 유효한 캘리브레이션이 없습니다 — 먼저 캘리브레이션을 등록하세요."
        $failed = 1; continue
    }
    $f = $row.Split('|')
    $tx = [double]$f[0]; $ty = [double]$f[1]; $yaw = [double]$f[2]; $rms = $f[3]
    $yawNew = NormPi ($yaw - $Deg * [Math]::PI / 180)

    $pts = [int](Sql "SELECT count(*) FROM ref.map_calibration_point
                       WHERE map_id='$map' AND map_version=$ver;" | Select-Object -First 1)

    Write-Host ("  맵버전 $ver · 기준점 ${pts}점 · RMS $rms m")
    Write-Host ("  yaw  : {0:F6} rad ({1:F3}°)  →  {2:F6} rad ({3:F3}°)" -f $yaw, (ToDeg $yaw), $yawNew, (ToDeg $yawNew))
    Write-Host ("  t    : ({0}, {1})  → 변경 없음" -f $tx, $ty)

    # 검증: 이 맵을 보고 중인 로봇의 현재 위치를 before/after 로 환산
    $org = (Sql "SELECT g.origin_ox, g.origin_oy FROM ref.map m
                  JOIN ref.tank_geometry g ON g.tank_id = m.tank_id WHERE m.map_id='$map';" |
            Select-Object -First 1)
    $ox = 0.0; $oy = 0.0
    if ($org) { $o = $org.Split('|'); $ox = [double]$o[0]; $oy = [double]$o[1] }

    foreach ($rc in Sql "SELECT robot_id, reported_x, reported_y, coalesce(reported_theta::text,'')
                           FROM run.robot_context
                          WHERE reported_map_id='$map' AND reported_x IS NOT NULL;") {
        $c = $rc.Split('|')
        $rid = $c[0]; $mx = [double]$c[1]; $my = [double]$c[2]
        $th = if ($c[3]) { [double]$c[3] } else { $null }
        foreach ($tag in @("before", "after")) {
            $y = if ($tag -eq "after") { $yawNew } else { $yaw }
            $ux = $mx - $tx; $uy = $my - $ty
            $cs = [Math]::Cos($y); $sn = [Math]::Sin($y)
            $dx = $cs * $ux + $sn * $uy      # drawing = R(-yaw)·(map - t)
            $dy = -$sn * $ux + $cs * $uy
            $hd = if ($null -ne $th) { ToDeg ($th - $y) } else { $null }
            Write-Host ("  로봇 {0,-8} {1,-6} → {2}" -f $rid, $tag, (LabelPose $dx $dy $hd $ox $oy))
        }
    }

    if (-not $Apply) {
        Write-Host "  >> 미리보기입니다. after 가 실제 로봇 위치·방향과 맞으면: tools\rotate_map_calibration.ps1 $map -Deg $Deg -Apply"
        continue
    }

    # ---------- 실제 적용 ----------
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $tmp = if ($env:TEMP) { $env:TEMP } else { [IO.Path]::GetTempPath() }
    $bk = Join-Path $tmp "mapcal_${map}_v${ver}_$stamp"
    & $psqlExe -c "\copy (SELECT * FROM ref.map_calibration       WHERE map_id='$map' AND map_version=$ver) TO STDOUT" |
        Set-Content -Encoding UTF8 "${bk}_cal.tsv"
    & $psqlExe -c "\copy (SELECT * FROM ref.map_calibration_point WHERE map_id='$map' AND map_version=$ver) TO STDOUT" |
        Set-Content -Encoding UTF8 "${bk}_pts.tsv"
    Write-Host "  백업: ${bk}_cal.tsv, ${bk}_pts.tsv"
    Write-Host ("  되돌리기: UPDATE ref.map_calibration SET yaw_rad={0} WHERE map_id='{1}' AND map_version={2};" -f $yaw, $map, $ver)
    Write-Host ("            (기준점도 함께 회전되므로 -Deg {0} 로 재실행하거나 백업 TSV 로 복구)" -f (-$Deg))

    if ($pts -ge 2) {
        # 기준점의 도면 좌표를 같은 각도로 회전 → solve 가 보정된 yaw 를 재현(이후 재계산에도 보존)
        # SET 의 우변은 모두 갱신 전 행 값으로 평가되므로 x,y 동시 치환이 안전하다.
        Exec ("UPDATE ref.map_calibration_point
                  SET drawing_x_m = {0}*drawing_x_m - {1}*drawing_y_m,
                      drawing_y_m = {1}*drawing_x_m + {0}*drawing_y_m
                WHERE map_id='{2}' AND map_version={3};" -f $rotCos, $rotSin, $map, $ver)
        Write-Host "  기준점 ${pts}점 도면좌표 $Deg° 회전 완료 → solve 재계산"
        try {
            $resp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/maps/$map/calibration/solve" -TimeoutSec 30
            Write-Host "  solve: $($resp | ConvertTo-Json -Compress)"
        }
        catch {
            Write-Warning "  solve 호출 실패 — App($BaseUrl) 기동 여부 확인. 기준점은 회전된 상태입니다."
            $failed = 1; continue
        }
    }
    else {
        Write-Host "  기준점이 ${pts}점(2점 미만) — solve 불가라 캘리브레이션 yaw 를 직접 갱신합니다."
        Exec ("UPDATE ref.map_calibration SET yaw_rad={0}, registered_by='rotate_map_calibration.ps1'
                WHERE map_id='{1}' AND map_version={2};" -f $yawNew, $map, $ver)
        Write-Warning "  기준점이 없어 이후 누군가 solve 를 돌리면 보정이 되돌아갑니다 — 기준점 재캡처를 권장합니다."
    }

    $after = (Sql "SELECT tx, ty, yaw_rad FROM ref.map_calibration
                    WHERE map_id='$map' AND map_version=$ver;" | Select-Object -First 1).Split('|')
    $yaw2 = [double]$after[2]
    $diff = [Math]::Abs($yaw2 - $yawNew)
    Write-Host ("  결과: yaw {0:F6} rad ({1:F3}°) · t ({2}, {3})" -f $yaw2, (ToDeg $yaw2), $after[0], $after[1])
    if ($diff -gt 0.001) {
        Write-Warning ("  예상 yaw({0:F6})와 {1:F6} rad 차이 — 기준점이 수동 편집된 적이 있는지 확인하세요." -f $yawNew, $diff)
        $failed = 1
    }
    else { Write-Host "  ✓ 예상값과 일치" }
}

Write-Host ""
Write-Host "────────────────────────────────────────────────────────────"
if ($Apply) {
    Write-Host "적용 완료. UI 는 캘리브레이션을 10초 캐시하므로 잠시 후(또는 프로젝트 다시 열기)"
    Write-Host "운영 탭 3D 뷰에서 마커 위치·뱃머리 방향이 실제와 맞는지 반드시 눈으로 확인하세요."
    Write-Host "180° 오차는 마커가 탱크 안에 그대로 남으므로 '방향'까지 같이 봐야 판별됩니다."
}
exit $failed
