#!/usr/bin/env bash
# Builds bmwDecode.app and a .dmg for one macOS architecture.
#
#   installer/macos/make-app.sh <osx-arm64|osx-x64> [version] [data-folder]
#
# data-folder (optional, private builds only): a folder holding Ecu/ and/or sgbd.pack to put inside the app.
# Those are BMW files: never publish a .dmg built with them.
set -euo pipefail

rid="${1:-osx-arm64}"
version="${2:-0.2.0}"
data="${3:-}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/artifacts/macos/$rid"
app="$out/bmwDecode.app"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

dotnet publish "$repo/src/BmwDecode.Mac/BmwDecode.Mac.csproj" -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -p:Version="$version" -o "$app/Contents/MacOS"

sed "s/__VERSION__/$version/g" "$repo/installer/macos/Info.plist" > "$app/Contents/Info.plist"
cp "$repo/README.md" "$repo/LICENSE.md" "$app/Contents/Resources/"

if [[ -n "$data" ]]; then
  # The app looks for Data/sgbd.pack and Ecu/ next to its executable.
  [[ -f "$data/sgbd.pack" ]] && mkdir -p "$app/Contents/MacOS/Data" && cp "$data/sgbd.pack" "$app/Contents/MacOS/Data/"
  [[ -d "$data/Ecu" ]] && cp -R "$data/Ecu" "$app/Contents/MacOS/Ecu"
fi

chmod +x "$app/Contents/MacOS/bmwDecode"
# Apple Silicon only runs signed code: sign ad hoc (no Apple Developer ID needed for your own Mac).
codesign --force --deep --sign - "$app"

stage="$out/dmg"
mkdir -p "$stage"
cp -R "$app" "$stage/"
ln -s /Applications "$stage/Applications"
arch="${rid#osx-}"
hdiutil create -volname "bmwDecode" -srcfolder "$stage" -ov -format UDZO "$out/bmwDecode-$version-macos-$arch.dmg"
rm -rf "$stage"
echo "Built $out/bmwDecode-$version-macos-$arch.dmg"
