#!/usr/bin/env bash
# Установка HeatScope из распакованного архива (Ubuntu / Debian / Fedora).
set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="/opt/heatscope"
BIN_DIR="/usr/local/bin"
DESKTOP_DIR="/usr/local/share/applications"
ICON_DIR="/usr/local/share/icons/hicolor/256x256/apps"

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    SUDO="sudo"
fi

echo "Установка HeatScope в $APP_DIR"
$SUDO install -d "$APP_DIR" "$BIN_DIR" "$DESKTOP_DIR" "$ICON_DIR"
$SUDO install -m 0755 "$SRC_DIR/HeatScope" "$APP_DIR/HeatScope"
$SUDO install -m 0644 "$SRC_DIR/heatscope.png" "$ICON_DIR/heatscope.png"
$SUDO install -m 0644 "$SRC_DIR/heatscope.desktop" "$DESKTOP_DIR/heatscope.desktop"

$SUDO tee "$BIN_DIR/heatscope" >/dev/null <<'EOF'
#!/bin/sh
exec /opt/heatscope/HeatScope "$@"
EOF

$SUDO tee "$BIN_DIR/heatscope-root" >/dev/null <<'EOF'
#!/bin/sh
exec pkexec env DISPLAY="$DISPLAY" XAUTHORITY="$XAUTHORITY" /opt/heatscope/HeatScope "$@"
EOF

$SUDO chmod 0755 "$BIN_DIR/heatscope" "$BIN_DIR/heatscope-root"

echo
echo "Готово."
echo "  heatscope        — обычный запуск (без датчиков части процессов)"
echo "  heatscope-root   — запуск с правами root через pkexec (рекомендуется)"
echo "Также появится ярлык в меню приложений."
