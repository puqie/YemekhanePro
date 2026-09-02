#!/usr/bin/env bash
# Arayuz denetimi icin yerel API'yi baslatir.
#
#   scripts/uitest-api.sh start <veri-klasoru> <port>   -> API'yi arka planda baslatir (DB yoksa olusturur)
#   scripts/uitest-api.sh stop  <port>                  -> o porttaki API'yi durdurur
#
# Neden ayri betik: ajanlar ve gelistirici ayni komutu tekrar tekrar yaziyordu ve
# JWT anahtari / bootstrap parolasi / baglanti dizgisi her seferinde elle
# ortam degiskenine kopyalaniyordu. Bir yerde dursun.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"

cmd="${1:-}"
case "$cmd" in
  start)
    dir="${2:?veri klasoru gerekli}"; port="${3:-5255}"
    mkdir -p "$dir"
    win_dir="$(cygpath -w "$dir" 2>/dev/null || echo "$dir")"
    log="$dir/api-$port.log"
    (
      cd "$root/src/Yemekhane.Api"
      ConnectionStrings__Database="Data Source=$win_dir\\yemekhane.db" \
      Authentication__Jwt__SigningKey="test-imza-anahtari-en-az-32-bayt-olmali-1234567890" \
      Authentication__Jwt__AccessTokenMinutes=60 \
      Authentication__DeviceKeys__0="test-cihaz-anahtari-1234567890" \
      Authentication__Bootstrap__Enabled=true \
      Authentication__Bootstrap__Username="admin" \
      Authentication__Bootstrap__Password="TestParola123!" \
      ASPNETCORE_ENVIRONMENT=Development \
      ASPNETCORE_URLS="http://127.0.0.1:$port" \
      nohup dotnet run --no-build --no-launch-profile --project Yemekhane.Api.csproj >"$log" 2>&1 &
      echo $! >"$dir/api-$port.pid"
    )
    for _ in $(seq 1 60); do
      if curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/health" 2>/dev/null | grep -q 200; then
        echo "API hazir: http://127.0.0.1:$port  (db: $dir/yemekhane.db)"; exit 0
      fi
      sleep 1
    done
    echo "API $port baslamadi; log: $log" >&2; tail -20 "$log" >&2; exit 1
    ;;
  stop)
    port="${2:?port gerekli}"
    # Windows'ta nohup PID'i cocuk sureci kapsamaz; portu dinleyen sureci bul.
    pid="$(netstat -ano 2>/dev/null | grep -E "127\.0\.0\.1:$port +.*LISTENING" | awk '{print $NF}' | head -1 || true)"
    if [ -n "${pid:-}" ]; then taskkill //F //PID "$pid" >/dev/null 2>&1 || true; echo "durduruldu (pid $pid)"; else echo "port $port dinlenmiyor"; fi
    ;;
  *)
    echo "kullanim: $0 start <klasor> <port> | stop <port>" >&2; exit 2
    ;;
esac
