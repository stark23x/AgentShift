#!/usr/bin/env bash
# AgentShift release builder.
#   packaging/build.sh windows   -> dist/AgentShift-<v>-win-x64.msi and .zip   (needs wixl from msitools)
#   packaging/build.sh linux     -> dist/AgentShift-<v>-x86_64.AppImage and .tar.gz
#   packaging/build.sh macos     -> dist/AgentShift-<v>-macos-<arch>.zip (and .dmg when run on a Mac)
#   packaging/build.sh all
# Everything is self-contained: people don't need to install .NET.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
DIST="$ROOT/dist"
OUT="$ROOT/artifacts"
mkdir -p "$DIST" "$OUT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

publish() { # rid dir
  local rid="$1" dir="$2"
  rm -rf "$dir"
  for proj in src/AgentShift.App src/AgentShift.Cli; do
    dotnet publish "$proj" -c Release -r "$rid" --self-contained true -o "$dir" \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -v q
  done
  cp LICENSE "$dir/LICENSE.txt"
  cp THIRD-PARTY-NOTICES.txt README.md "$dir/"
  mkdir -p "$dir/licenses"
  cp licenses/* "$dir/licenses/"
}

windows() {
  local dir="$OUT/win-x64"
  publish win-x64 "$dir"
  cp src/AgentShift.App/Assets/agentshift.ico "$dir/"
  (cd "$dir" && rm -f "$DIST/AgentShift-$VERSION-win-x64-portable.zip" &&
    zip -qr "$DIST/AgentShift-$VERSION-win-x64-portable.zip" AgentShift.exe agentshift-cli.exe LICENSE.txt THIRD-PARTY-NOTICES.txt README.md licenses)
  wixl -a x64 -D Version="$VERSION" -D Src="$dir" -o "$DIST/AgentShift-$VERSION-win-x64.msi" packaging/windows/AgentShift.wxs
  echo "Windows: $DIST/AgentShift-$VERSION-win-x64.msi"
}

linux() {
  local dir="$OUT/linux-x64" appdir="$OUT/AgentShift.AppDir"
  publish linux-x64 "$dir"
  tar -C "$OUT" -czf "$DIST/AgentShift-$VERSION-linux-x64.tar.gz" --transform "s:^linux-x64:AgentShift-$VERSION:" linux-x64

  rm -rf "$appdir"
  mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"
  cp -r "$dir"/. "$appdir/usr/bin/"
  cp packaging/linux/agentshift.desktop "$appdir/" && cp packaging/linux/agentshift.desktop "$appdir/usr/share/applications/"
  cp packaging/linux/agentshift.png "$appdir/agentshift.png"
  cp packaging/linux/agentshift.png "$appdir/usr/share/icons/hicolor/256x256/apps/agentshift.png"
  cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
if [ "$1" = "--cli" ]; then shift; exec "$HERE/usr/bin/agentshift-cli" "$@"; fi
exec "$HERE/usr/bin/AgentShift" "$@"
EOF
  chmod +x "$appdir/AppRun" "$appdir/usr/bin/AgentShift" "$appdir/usr/bin/agentshift-cli"

  if [ -n "${SKIP_APPIMAGE:-}" ]; then echo "Linux: $DIST/AgentShift-$VERSION-linux-x64.tar.gz (AppImage skipped)"; return; fi
  local tool="$OUT/appimagetool"
  if [ ! -x "$tool" ]; then
    curl -sSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$tool"
  fi
  ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" -n "$appdir" "$DIST/AgentShift-$VERSION-x86_64.AppImage" >/dev/null
  echo "Linux: $DIST/AgentShift-$VERSION-x86_64.AppImage"
}

macos_one() { # rid arch-label
  local rid="$1" label="$2" dir="$OUT/$1" app="$OUT/$2/AgentShift.app"
  publish "$rid" "$dir"
  rm -rf "$OUT/$label" && mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -r "$dir"/. "$app/Contents/MacOS/"
  cp packaging/macos/agentshift.icns "$app/Contents/Resources/"
  sed "s/@VERSION@/$VERSION/g" packaging/macos/Info.plist > "$app/Contents/Info.plist"
  chmod +x "$app/Contents/MacOS/AgentShift" "$app/Contents/MacOS/agentshift-cli"
  if command -v codesign >/dev/null; then codesign --force --deep -s - "$app"; fi  # ad-hoc signature
  (cd "$OUT/$label" && rm -f "$DIST/AgentShift-$VERSION-macos-$label.zip" && zip -qry "$DIST/AgentShift-$VERSION-macos-$label.zip" AgentShift.app)
  if command -v hdiutil >/dev/null; then
    rm -f "$DIST/AgentShift-$VERSION-macos-$label.dmg"
    hdiutil create -volname "AgentShift" -srcfolder "$OUT/$label" -ov -format UDZO "$DIST/AgentShift-$VERSION-macos-$label.dmg" >/dev/null
  fi
  echo "macOS ($label): $DIST/AgentShift-$VERSION-macos-$label.zip"
}

macos() {
  macos_one osx-arm64 apple-silicon
  macos_one osx-x64 intel
}

case "${1:-all}" in
  windows) windows ;;
  linux) linux ;;
  macos) macos ;;
  all) windows; linux; macos ;;
  *) echo "usage: $0 [windows|linux|macos|all]"; exit 2 ;;
esac
