#!/usr/bin/env bash
# Builds release zips from a compiled Belay.dll.
# Usage: installer/make-package.sh <version> <path/to/Belay.dll> <out-dir>
set -euo pipefail
VER="$1"; DLL="$2"; OUT="$3"
BEPINEX_VER="5.4.23.6"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

curl -sSLf -o "$WORK/bep.zip" "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VER/BepInEx_win_x64_$BEPINEX_VER.zip"

# Installer zip: double-click "Install Belay.bat".
PKG="$WORK/Belay-$VER"
mkdir -p "$PKG/installer/payload/BepInEx/plugins/Belay"
unzip -q "$WORK/bep.zip" -d "$PKG/installer/payload"
cp "$DLL" "$PKG/installer/payload/BepInEx/plugins/Belay/Belay.dll"
cp "$HERE/install.ps1" "$PKG/installer/"
cp "$HERE/Install Belay.bat" "$HERE/Uninstall Belay.bat" "$HERE/README.txt" "$PKG/"
sed -i "s/{VERSION}/$VER/g" "$PKG/README.txt"
(cd "$WORK" && zip -qr "$OUT/Belay-$VER-installer.zip" "Belay-$VER")

# Mod-only zip for people who manage BepInEx themselves.
mkdir -p "$WORK/modonly/BepInEx/plugins/Belay"
cp "$DLL" "$WORK/modonly/BepInEx/plugins/Belay/Belay.dll"
(cd "$WORK/modonly" && zip -qr "$OUT/Belay-$VER-mod-only.zip" BepInEx)
ls -la "$OUT"
