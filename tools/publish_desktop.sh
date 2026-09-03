#!/usr/bin/env bash
# HD.Acs.UI.Desktop(Avalonia) 배포 산출물 생성 — macOS .app 번들 / Windows·Linux 폴더.
#
#   tools/publish_desktop.sh osx-arm64      # Apple Silicon .app (기본)
#   tools/publish_desktop.sh osx-x64        # Intel Mac .app
#   tools/publish_desktop.sh win-x64        # Windows 폴더 배포
#   tools/publish_desktop.sh linux-x64      # Linux 폴더 배포
#   tools/publish_desktop.sh all            # 위 4종 모두
#
# 환경변수
#   HDACS_SIGN_IDENTITY   macOS 코드서명 identity (예: "Developer ID Application: ..."). 미지정=ad-hoc("-") 서명.
#   HDACS_VERSION         버전 문자열 오버라이드 (기본: csproj <Version>, 없으면 0.1.0)
#   HDACS_OUT             산출 루트 (기본: artifacts/desktop)
#   HDACS_NUGET_SOURCE    NuGet 복원 소스 오버라이드 (예: https://api.nuget.org/v3/index.json — Telerik 피드 접근 불가 환경용;
#                         Desktop 헤드는 Telerik을 쓰지 않아 공개 피드만으로 복원된다)
#
# 산출물
#   macOS : artifacts/desktop/<rid>/HD_ACS.app  (+ .zip)
#   기타  : artifacts/desktop/<rid>/            (실행 파일 + appsettings.json)
#
# 주의: 폐쇄망 배포는 ad-hoc 서명으로 충분(같은 Mac에서 실행). 외부 배포/다른 Mac 실행 시 Gatekeeper 경고를 피하려면
#       Developer ID 서명 + notarization(xcrun notarytool) 이 필요하다 — 이 스크립트는 서명까지만 수행한다.
#       .icns 아이콘은 macOS의 iconutil 이 있을 때만 생성(Linux/Windows 호스트에서는 아이콘 없이 번들).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/src/HD.Acs.UI.Desktop/HD.Acs.UI.Desktop.csproj"
PLIST_TPL="$ROOT/src/HD.Acs.UI.Desktop/macos/Info.plist"
ICON_SRC="$ROOT/src/HD.Acs.UI.Desktop/Assets/HHI_color_ko.png"
OUT_ROOT="${HDACS_OUT:-$ROOT/artifacts/desktop}"
EXE_NAME="HD.Acs.UI.Desktop"
APP_NAME="HD_ACS"

VERSION="${HDACS_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)}"
VERSION="${VERSION:-0.1.0}"

publish_rid() {
  local rid="$1"
  local out="$OUT_ROOT/$rid"
  echo "==> publish $rid (v$VERSION) → $out"
  rm -rf "$out"
  # 복원을 분리해 소스 오버라이드가 프로젝트 참조(UI.Core)까지 확실히 적용되게 한다
  local src_args=()
  [[ -n "${HDACS_NUGET_SOURCE:-}" ]] && src_args=(--source "$HDACS_NUGET_SOURCE")
  dotnet restore "$PROJ" -r "$rid" --nologo -v minimal "${src_args[@]}"
  dotnet publish "$PROJ" -c Release -r "$rid" --self-contained true --no-restore \
    -p:PublishSingleFile=false -p:DebugType=none -p:Version="$VERSION" \
    -o "$out/publish" --nologo -v minimal
  case "$rid" in
    osx-*) bundle_macos "$rid" "$out" ;;
    *)     mv "$out/publish" "$out/$APP_NAME-$rid"; echo "    폴더 배포: $out/$APP_NAME-$rid (실행: $EXE_NAME[.exe])" ;;
  esac
}

bundle_macos() {
  local rid="$1" out="$2"
  local app="$out/$APP_NAME.app"
  local contents="$app/Contents"
  mkdir -p "$contents/MacOS" "$contents/Resources"
  # 실행 파일·의존 어셈블리·appsettings.json 은 모두 MacOS/ 에 (AppContext.BaseDirectory 기준으로 appsettings 로드)
  cp -R "$out/publish/." "$contents/MacOS/"
  rm -rf "$out/publish"
  sed "s/{VERSION}/$VERSION/g" "$PLIST_TPL" > "$contents/Info.plist"
  printf 'APPL????' > "$contents/PkgInfo"
  chmod +x "$contents/MacOS/$EXE_NAME"

  if command -v iconutil >/dev/null 2>&1 && command -v sips >/dev/null 2>&1; then
    make_icns "$contents/Resources/$APP_NAME.icns"
  else
    echo "    (iconutil 없음 — 아이콘 생략, macOS 호스트에서 재실행 시 생성)"
    sed -i.bak '/CFBundleIconFile/,+1d' "$contents/Info.plist" && rm -f "$contents/Info.plist.bak"
  fi

  if command -v codesign >/dev/null 2>&1; then
    local identity="${HDACS_SIGN_IDENTITY:--}"
    echo "    codesign (identity: $identity)"
    codesign --force --deep --sign "$identity" --timestamp=none "$app"
    codesign --verify --verbose=1 "$app" || true
  else
    echo "    (codesign 없음 — 서명 생략. macOS에서: codesign --force --deep -s - '$app')"
  fi

  (cd "$out" && rm -f "$APP_NAME-$rid.zip" && zip -qry "$APP_NAME-$rid.zip" "$APP_NAME.app")
  echo "    번들: $app"
  echo "    압축: $out/$APP_NAME-$rid.zip"
}

make_icns() {
  local icns="$1" tmp
  tmp="$(mktemp -d)/$APP_NAME.iconset"; mkdir -p "$tmp"
  # 로고 PNG를 정사각 캔버스로 패딩 후 표준 크기 세트 생성
  for s in 16 32 64 128 256 512 1024; do
    sips -z "$s" "$s" "$ICON_SRC" --out "$tmp/icon_${s}x${s}.png" >/dev/null 2>&1 || true
  done
  cp "$tmp/icon_32x32.png"   "$tmp/icon_16x16@2x.png"   2>/dev/null || true
  cp "$tmp/icon_64x64.png"   "$tmp/icon_32x32@2x.png"   2>/dev/null || true
  cp "$tmp/icon_256x256.png" "$tmp/icon_128x128@2x.png" 2>/dev/null || true
  cp "$tmp/icon_512x512.png" "$tmp/icon_256x256@2x.png" 2>/dev/null || true
  cp "$tmp/icon_1024x1024.png" "$tmp/icon_512x512@2x.png" 2>/dev/null || true
  rm -f "$tmp/icon_64x64.png" "$tmp/icon_1024x1024.png"
  iconutil -c icns "$tmp" -o "$icns" && echo "    아이콘: $icns"
}

target="${1:-osx-arm64}"
case "$target" in
  all) for r in osx-arm64 osx-x64 win-x64 linux-x64; do publish_rid "$r"; done ;;
  osx-arm64|osx-x64|win-x64|linux-x64) publish_rid "$target" ;;
  *) echo "사용법: $0 [osx-arm64|osx-x64|win-x64|linux-x64|all]"; exit 2 ;;
esac
echo "완료. 서버 주소는 appsettings.json 의 Acs:BaseUrl 또는 환경변수 Acs__BaseUrl 로 지정."
