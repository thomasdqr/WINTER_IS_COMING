#!/bin/zsh
# Build WinterIsComing as ~/Applications/Snow.app (so Raycast or Spotlight can launch it as "Snow") and start it
cd "$(dirname "$0")"
APP=~/Applications/Snow.app
mkdir -p "$APP/Contents/MacOS"
swiftc -O WinterIsComing.swift -o "$APP/Contents/MacOS/Snow" || exit 1
cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Snow</string>
    <key>CFBundleIdentifier</key><string>local.winteriscoming.snow</string>
    <key>CFBundleExecutable</key><string>Snow</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>LSUIElement</key><true/>
</dict>
</plist>
EOF
codesign --force --sign - "$APP" || exit 1
echo "Build succeeded ❄︎  Use the ❄︎ menu bar icon to pause, change the snow amount or quit"
# Restart it so the new build is the one running
pkill -x Snow; pkill -x WinterIsComing
open "$APP"
