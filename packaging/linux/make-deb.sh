#!/usr/bin/env bash
# Сборка .deb для Ubuntu/Debian. Запускать на Linux после build-linux.sh.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VER="${VERSION:-0.1.0}"
ARCH="amd64"
PKG="heatscope"
STAGE="$ROOT/build/deb/${PKG}_${VER}_${ARCH}"

BIN="$ROOT/dist-linux/HeatScope"
if [ ! -f "$BIN" ]; then
    echo "Нет $BIN — сначала выполните ./build-linux.sh" >&2
    exit 1
fi

rm -rf "$STAGE"
install -d "$STAGE/DEBIAN" \
           "$STAGE/opt/heatscope" \
           "$STAGE/usr/bin" \
           "$STAGE/usr/share/applications" \
           "$STAGE/usr/share/icons/hicolor/256x256/apps"

install -m 0755 "$BIN" "$STAGE/opt/heatscope/HeatScope"
install -m 0644 "$ROOT/packaging/linux/heatscope.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/heatscope.png"
install -m 0644 "$ROOT/packaging/linux/heatscope.desktop" "$STAGE/usr/share/applications/heatscope.desktop"

cat > "$STAGE/usr/bin/heatscope" <<'EOF'
#!/bin/sh
exec /opt/heatscope/HeatScope "$@"
EOF

cat > "$STAGE/usr/bin/heatscope-root" <<'EOF'
#!/bin/sh
exec pkexec env DISPLAY="$DISPLAY" XAUTHORITY="$XAUTHORITY" /opt/heatscope/HeatScope "$@"
EOF

chmod 0755 "$STAGE/usr/bin/heatscope" "$STAGE/usr/bin/heatscope-root"

cat > "$STAGE/DEBIAN/control" <<EOF
Package: $PKG
Version: $VER
Section: utils
Priority: optional
Architecture: $ARCH
Depends: libx11-6, libxrandr2, libxi6, libxcursor1, libgl1, libfontconfig1, libfreetype6
Maintainer: HeatScope contributors
Description: Monitor of process heat contribution
 HeatScope shows CPU/GPU temperatures and estimates how many degrees
 each process adds. It can pause (SIGSTOP) or terminate processes and
 protects critical system components.
EOF

mkdir -p "$ROOT/release"
dpkg-deb --build --root-owner-group "$STAGE" "$ROOT/release/heatscope_${VER}_${ARCH}.deb"
echo "Готово: $ROOT/release/heatscope_${VER}_${ARCH}.deb"
