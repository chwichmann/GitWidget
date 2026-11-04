#!/usr/bin/env bash
# Wraps a published osx-* GitWidget build into GitWidget.app (menu-bar agent app, no Dock icon), ad-hoc signs it
# and zips it with ditto (keeps executable bits and the signature).
#
# Usage: tools/package-macos.sh <publish-dir> <output-dir> <version>
set -euo pipefail

PUBLISH_DIR="$1"
OUTPUT_DIR="$2"
VERSION="$3"
SHORT_VERSION="${VERSION%%-*}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ICON_SOURCE="$SCRIPT_DIR/../src/GitStatusWidget.Desktop/Assets/gitwidget.png"

APP="$OUTPUT_DIR/GitWidget.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/GitWidget"

# App icon (.icns) from the 256 px PNG.
ICONSET="$(mktemp -d)/gitwidget.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256; do
  sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  if [ "$double" -le 256 ]; then
    sips -z "$double" "$double" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  fi
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/gitwidget.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>io.github.chwichmann.GitStatusWidget</string>
  <key>CFBundleName</key><string>GitWidget</string>
  <key>CFBundleDisplayName</key><string>GitWidget</string>
  <key>CFBundleExecutable</key><string>GitWidget</string>
  <key>CFBundleIconFile</key><string>gitwidget</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${SHORT_VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright (c) Christian Wichmann</string>
</dict>
</plist>
PLIST

# Ad-hoc signature (no Developer ID). Gatekeeper still asks on first start: right-click > Open.
codesign --force --deep --sign - "$APP"
codesign --verify --deep "$APP"

(cd "$OUTPUT_DIR" && ditto -c -k --keepParent GitWidget.app "GitWidget-osx-arm64.zip")
echo "Packaged $OUTPUT_DIR/GitWidget-osx-arm64.zip"
