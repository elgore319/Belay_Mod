#!/usr/bin/env bash
# Builds release zips from a compiled Belay.dll.
# Usage: installer/make-package.sh <version> <path/to/Belay.dll> <out-dir>
set -euo pipefail
VER="$1"
DLL="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
mkdir -p "$3"
OUT="$(cd "$3" && pwd)"
BEPINEX_VER="5.4.23.6"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

curl -sSLf -o "$WORK/bep.zip" "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VER/BepInEx_win_x64_$BEPINEX_VER.zip"

# Installer zip: double-click "Install Not Alone.bat".
# The DLL and its plugin folder keep the old name "Belay" so existing installs keep updating.
PKG="$WORK/NotAlone-$VER"
mkdir -p "$PKG/installer/payload/BepInEx/plugins/Belay"
unzip -q "$WORK/bep.zip" -d "$PKG/installer/payload"
cp "$DLL" "$PKG/installer/payload/BepInEx/plugins/Belay/Belay.dll"
cp "$HERE/install.ps1" "$PKG/installer/"
cp "$HERE/Install Not Alone.bat" "$HERE/Uninstall Not Alone.bat" "$HERE/README.txt" "$PKG/"
sed -i "s/{VERSION}/$VER/g" "$PKG/README.txt"
(cd "$WORK" && zip -qr "$OUT/NotAlone-$VER-installer.zip" "NotAlone-$VER")

# Mod-only zip for people who manage BepInEx themselves.
mkdir -p "$WORK/modonly/BepInEx/plugins/Belay"
cp "$DLL" "$WORK/modonly/BepInEx/plugins/Belay/Belay.dll"
(cd "$WORK/modonly" && zip -qr "$OUT/NotAlone-$VER-mod-only.zip" BepInEx)
ls -la "$OUT"
