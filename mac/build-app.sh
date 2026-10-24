#!/usr/bin/env bash
# Builds G-Diag.app for macOS and zips it.
#   mac/build-app.sh            -> Apple silicon (M chips), osx-arm64
#   mac/build-app.sh osx-x64    -> Intel Macs
# Needs the .NET 8 SDK. Output: publish-mac/G-Diag.app and GDiag-<rid>.zip
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/publish-mac"
APP="$OUT/G-Diag.app"
VERSION="${VERSION:-1.0.0}"

rm -rf "$OUT"
dotnet publish "$ROOT/GDiag/GDiag.csproj" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none -o "$OUT/bin"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/bin/." "$APP/Contents/MacOS/"
cp "$ROOT/README.md" "$ROOT/LICENSE" "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>G-Diag</string>
    <key>CFBundleDisplayName</key><string>G-Diag</string>
    <key>CFBundleIdentifier</key><string>io.github.bmwgxx</string>
    <key>CFBundleExecutable</key><string>GDiag</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSLocalNetworkUsageDescription</key><string>G-Diag talks to your car's gateway over the ENET cable.</string>
</dict>
</plist>
EOF

# Apple silicon refuses unsigned code, so sign ad hoc (no developer account needed)
codesign --force --deep --sign - "$APP"

(cd "$OUT" && ditto -c -k --keepParent G-Diag.app "$ROOT/GDiag-$RID.zip")
echo "Built $APP and GDiag-$RID.zip"
