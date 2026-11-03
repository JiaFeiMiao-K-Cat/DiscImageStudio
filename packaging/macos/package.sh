#!/bin/bash
set -euo pipefail
# Run on macOS. The SDK is only needed on the build machine, not on the recipient's Mac.
root="$(cd "$(dirname "$0")/../.." && pwd)"
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'Usage: package.sh [osx-arm64|osx-x64]' >&2; exit 1 ;; esac
output="${2:-$root/artifacts/macos-$rid}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
app="$output/Disc Image Studio.app"
if [ -e "$app" ]; then echo "Output already exists: $app (choose a fresh output directory)" >&2; exit 1; fi
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
"${DOTNET_EXECUTABLE:-dotnet}" publish "$root/src/DiscImageStudio.Desktop/DiscImageStudio.Desktop.csproj" \
  -c Release -r "$rid" --self-contained true -o "$app/Contents/MacOS" -p:PublishSingleFile=false
cp "$root/packaging/macos/Info.plist" "$app/Contents/Info.plist"
version="$(sed -n 's/.*<VersionPrefix>\(.*\)<\/VersionPrefix>.*/\1/p' "$root/Directory.Build.props")"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app/Contents/Info.plist"
cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.txt" "$root/src/DiscImageStudio.App/Assets/Fonts/HarmonyOS-Sans-LICENSE.txt" "$app/Contents/Resources/"
iconset="$output/DiscImageStudio.iconset"
mkdir "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/src/DiscImageStudio.App/Assets/DiscImageStudio-256.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/src/DiscImageStudio.App/Assets/DiscImageStudio-256.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/DiscImageStudio.icns"
rm -r "$iconset"
# Ad-hoc signing permits local testing. Distribution still needs a maintainer's Developer ID and notarization.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --sequesterRsrc --keepParent "$app" "$output/DiscImageStudio-$rid.zip"
shasum -a 256 "$output/DiscImageStudio-$rid.zip" > "$output/DiscImageStudio-$rid.zip.sha256"
echo "$app"
