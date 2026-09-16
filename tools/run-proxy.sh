#!/usr/bin/env bash
#
# Starts a proxy container pointed at the fake Rock server, for testing.
#
# Uses host networking, so 127.0.0.1 means the same thing inside the container
# and out, and a fake printer started on this machine is reachable without
# any further thought.
#
#   HOST=$(tools/run-proxy.sh --name t1 --rock-port 19100)
#   ... --command "{\"cmd\":\"print\",\"address\":\"$HOST:19101\"}"
#
set -euo pipefail

image="${IMAGE:-asdfinit/rock-cloudprint:1.4.0}"
name="cp-test"
rock_port=19100
ui_port=18080
proxy_id="harness"
proxy_name="harness"

while [ $# -gt 0 ]; do
    case "$1" in
        --image)       image="$2"; shift 2 ;;
        --name)        name="$2"; shift 2 ;;
        --rock-port)   rock_port="$2"; shift 2 ;;
        --ui-port)     ui_port="$2"; shift 2 ;;
        --id)          proxy_id="$2"; shift 2 ;;
        --proxy-name)  proxy_name="$2"; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

docker rm -f "$name" >/dev/null 2>&1 || true

# Urls is set as a config key rather than ASPNETCORE_URLS because the image's
# own appsettings.json pins Urls and would otherwise win.
docker run -d --name "$name" --network host \
    -e Urls="http://+:$ui_port" \
    -e Url="http://127.0.0.1:$rock_port" \
    -e Id="$proxy_id" \
    -e Name="$proxy_name" \
    "$image" >/dev/null

echo "127.0.0.1"
