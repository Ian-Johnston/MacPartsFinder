#!/bin/bash
# Builds MacPartsFinder.app from a "dotnet publish" folder. Runs on a Mac (GitHub Actions macOS runner).
#
#   build/make-app-bundle.sh <publish folder> <output .app path> <version, e.g. 0.1.0>
#
# The .app is just folders: Contents/Info.plist (name, version, icon), Contents/MacOS (the program)
# and Contents/Resources (the icon).
set -euo pipefail

PUBLISH_DIR="$1"
APP="$2"
VERSION="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/MacPartsFinder"

# Icon: build/AppIcon.png (from WinPartsFinder's icon) -> AppIcon.icns, in all the sizes macOS wants.
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size "$HERE/AppIcon.png" --out "$ICONSET/icon_${size}x${size}.png" > /dev/null
    sips -z $((size * 2)) $((size * 2)) "$HERE/AppIcon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>MacPartsFinder</string>
    <key>CFBundleDisplayName</key>
    <string>MacPartsFinder</string>
    <key>CFBundleIdentifier</key>
    <string>com.ianjohnston.macpartsfinder</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleExecutable</key>
    <string>MacPartsFinder</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHumanReadableCopyright</key>
    <string>© 2026 Ian Johnston</string>
</dict>
</plist>
EOF

echo "Built $APP (version $VERSION)"
