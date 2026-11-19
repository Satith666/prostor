#!/bin/bash
# Полная проверка Prostor для разработчика (Linux или macOS):
#   форматирование, go vet под Linux и Windows, модульные тесты, синтаксис JS,
#   самопроверка программы с настоящим браузером на тестовом «диске C:»,
#   сборка установщика и проверка ресурсов .exe.
# Нужны Go 1.24+, Python 3 с Pillow, Node.js и Chrome/Chromium.
# Браузер можно указать явно: PROSTOR_BROWSER=/путь/к/chrome ./check.sh
set -uo pipefail
cd "$(dirname "$0")"
FAILED=()
step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
run() { local name="$1"; shift; if "$@"; then echo "   ok"; else echo "   ОШИБКА: $name"; FAILED+=("$name"); fi; }

step "Форматирование Go"
run "gofmt" bash -c 'out=$(gofmt -l cmd internal); [ -z "$out" ] || { echo "$out"; false; }'

step "go vet (Linux)"
run "vet linux" go vet ./...

step "go vet (Windows)"
run "vet windows" env GOOS=windows go vet ./cmd/prostor ./internal/...

step "Модульные тесты"
run "go test" go test -count=1 ./...

step "Синтаксис JavaScript"
run "node --check" bash -c 'node --check cmd/prostor/ui/app.js && node --check cmd/prostor/ui/icons.js'

step "Самопроверка с интерфейсом в браузере"
if [ -z "${PROSTOR_BROWSER:-}" ]; then
  for c in /opt/pw-browsers/chromium-*/chrome-linux64/chrome ~/.cache/ms-playwright/chromium-*/chrome-linux*/chrome \
           "$(command -v google-chrome || true)" "$(command -v chromium || true)" "$(command -v chromium-browser || true)"; do
    if [ -n "$c" ] && [ -x "$c" ]; then export PROSTOR_BROWSER="$c"; break; fi
  done
fi
SB=$(mktemp -d)
python3 tools/mksandbox.py "$SB" >/dev/null
go build -o "$SB/prostor" ./cmd/prostor
U=$SB/C/Users/user
run "самопроверка" env HOME=$U USERPROFILE=$U LOCALAPPDATA=$U/AppData/Local APPDATA=$U/AppData/Roaming \
  TEMP=$U/AppData/Local/Temp TMPDIR=$U/AppData/Local/Temp SystemRoot=$SB/C/Windows SystemDrive=$SB/C \
  ProgramData=$SB/C/ProgramData USERNAME=tester PROSTOR_FAKE_DISK=$SB/C:256 \
  "$SB/prostor" --selftest --no-open
mkdir -p build && cp "$U"/AppData/Local/Prostor/selftest/report-*.html build/ 2>/dev/null || true
rm -rf "$SB"

step "Помощник с правами администратора"
HB=$(mktemp -d)
go build -o "$HB/prostor" ./cmd/prostor
helper_check() {
  local log="$HB/out.log" url tok base info
  env HOME="$HB" LOCALAPPDATA="$HB/la" TEMP="$HB" PROSTOR_NOBROWSER=1 PROSTOR_FORCE_HELPER=1 "$HB/prostor" >"$log" 2>&1 &
  local pid=$!
  for _ in $(seq 1 50); do url=$(grep -o 'http://[^ ]*' "$log" | head -1); [ -n "$url" ] && break; sleep 0.2; done
  tok=${url##*t=}; base=${url%%/?t=*}
  info=$(curl -s -H "X-Token: $tok" "$base/api/info")
  kill $pid; sleep 0.5
  for _ in $(seq 1 30); do pgrep -f "$HB/prostor --helper" >/dev/null || break; sleep 0.5; done
  echo "$info" | grep -q '"admin":true' && echo "$info" | grep -q '"helper":true' && ! pgrep -f "$HB/prostor --helper" >/dev/null
}
run "помощник" helper_check
rm -rf "$HB"

step "Сборка под Windows"
run "build.sh" ./build.sh

step "Ресурсы exe"
run "checkpe" bash -c 'python3 tools/checkpe.py build/Prostor.exe >/dev/null && python3 tools/checkpe.py build/Prostor-Setup-*.exe >/dev/null'
run "vet setup" env GOOS=windows go vet ./cmd/setup

echo
if [ ${#FAILED[@]} -eq 0 ]; then
  echo "Всё в порядке: проверки пройдены, установщик в папке build."
else
  echo "Ошибки: ${FAILED[*]}"
  exit 1
fi
