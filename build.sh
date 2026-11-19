#!/bin/bash
# Собирает Prostor.exe и установщик Prostor-Setup-<версия>.exe (Windows x64).
set -euo pipefail
cd "$(dirname "$0")"
VER=${VER:-1.2.0}
export GOOS=windows GOARCH=amd64 CGO_ENABLED=0
LD="-s -w -H windowsgui -X main.version=$VER"
mkdir -p build cmd/setup/payload

echo "== ресурсы"
python3 tools/mkres.py --ico assets/prostor.ico --out cmd/prostor/rsrc_windows_amd64.syso \
  --version "$VER.0" --name Prostor.App --desc "Prostor — очистка и анализ диска" --orig Prostor.exe
python3 tools/mkres.py --ico assets/prostor.ico --out cmd/setup/rsrc_windows_amd64.syso \
  --version "$VER.0" --name Prostor.Setup --desc "Установка Prostor" --orig "Prostor-Setup.exe" \
  --level requireAdministrator --dpi system

echo "== программа"
go vet ./cmd/prostor ./internal/...
go build -trimpath -ldflags "$LD" -o build/Prostor.exe ./cmd/prostor
gzip -9 -n -c build/Prostor.exe > cmd/setup/payload/Prostor.exe.gz

echo "== установщик"
go vet ./cmd/setup
go build -trimpath -ldflags "$LD" -o "build/Prostor-Setup-$VER.exe" ./cmd/setup

ls -la build
