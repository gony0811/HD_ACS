# HD.Acs.UI.Desktop(Avalonia) Windows 배포 폴더 생성 (PowerShell). macOS .app 번들은 tools/publish_desktop.sh 사용.
#   tools\publish_desktop.ps1                 # win-x64
#   tools\publish_desktop.ps1 -Rid win-arm64
param(
    [string]$Rid = "win-x64",
    [string]$Version = "",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root "src\HD.Acs.UI.Desktop\HD.Acs.UI.Desktop.csproj"
if (-not $Version) {
    $m = Select-String -Path $proj -Pattern "<Version>(.*)</Version>" | Select-Object -First 1
    $Version = if ($m) { $m.Matches[0].Groups[1].Value } else { "0.1.0" }
}
if (-not $Out) { $Out = Join-Path $root "artifacts\desktop\$Rid\HD_ACS-$Rid" }
Write-Host "==> publish $Rid (v$Version) → $Out"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
dotnet publish $proj -c Release -r $Rid --self-contained true -p:DebugType=none -p:Version=$Version -o $Out --nologo -v minimal
Write-Host "완료: $Out\HD.Acs.UI.Desktop.exe  (서버 주소: appsettings.json Acs:BaseUrl 또는 환경변수 Acs__BaseUrl)"
