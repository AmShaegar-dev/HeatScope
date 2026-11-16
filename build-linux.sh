#!/usr/bin/env bash
# Сборка Linux-версии на Ubuntu/Debian. Требуется .NET 8 SDK.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Не найден dotnet. Установите .NET 8 SDK:" >&2
    echo "  sudo apt install dotnet-sdk-8.0   (или https://dot.net)" >&2
    exit 1
fi

VERSION="${VERSION:-0.1.0}"

echo "Публикация self-contained бинарника..."
dotnet publish src/HeatScope.Linux/HeatScope.Linux.csproj \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o dist-linux

STAGE="$ROOT/build/linux-tar"
rm -rf "$STAGE"
mkdir -p "$STAGE" release
cp dist-linux/HeatScope "$STAGE/"
cp packaging/linux/install.sh "$STAGE/"
cp packaging/linux/heatscope.desktop "$STAGE/"
cp packaging/linux/heatscope.png "$STAGE/"
cp README-LINUX.md "$STAGE/"

tar -czf "release/HeatScope-${VERSION}-linux-x64.tar.gz" -C "$STAGE" .
echo "Архив: release/HeatScope-${VERSION}-linux-x64.tar.gz"

if command -v dpkg-deb >/dev/null 2>&1; then
    bash packaging/linux/make-deb.sh
else
    echo "dpkg-deb не найден — .deb не собран (для Ubuntu он есть по умолчанию)."
fi
