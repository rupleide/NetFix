#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
swift build -c release --build-system native
BIN="$(swift build -c release --build-system native --show-bin-path)/NetFixMac"
python3 - << 'PY'
import struct
from pathlib import Path
data = Path("../Assets/icon.ico").read_bytes()
count = struct.unpack_from("<H", data, 4)[0]
frames = {}
for index in range(count):
    width, _, _, _, _, _, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
    frames[width or 256] = data[offset:offset + size]
out = Path("AppIcon.iconset")
out.mkdir(exist_ok=True)
names = {
    16: ["icon_16x16.png"],
    32: ["icon_16x16@2x.png", "icon_32x32.png"],
    64: ["icon_32x32@2x.png"],
    128: ["icon_128x128.png"],
    256: ["icon_128x128@2x.png", "icon_256x256.png"],
}
for size, files in names.items():
    for name in files:
        (out / name).write_bytes(frames[size])
PY
sips -z 512 512 AppIcon.iconset/icon_256x256.png --out AppIcon.iconset/icon_256x256@2x.png >/dev/null
cp AppIcon.iconset/icon_256x256@2x.png AppIcon.iconset/icon_512x512.png
sips -z 1024 1024 AppIcon.iconset/icon_256x256.png --out AppIcon.iconset/icon_512x512@2x.png >/dev/null
iconutil -c icns AppIcon.iconset -o AppIcon.icns
rm -rf AppIcon.iconset
rm -rf NetFix.app
mkdir -p NetFix.app/Contents/MacOS NetFix.app/Contents/Resources
cp "$BIN" NetFix.app/Contents/MacOS/NetFix
cp Info.plist NetFix.app/Contents/Info.plist
cp AppIcon.icns NetFix.app/Contents/Resources/AppIcon.icns
chmod +x NetFix.app/Contents/MacOS/NetFix
codesign -s - --force NetFix.app
echo "Собрано: $(pwd)/NetFix.app"
