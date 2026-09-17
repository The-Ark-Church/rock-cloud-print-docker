#!/usr/bin/env bash
#
# Proves the test harness against a published, unmodified proxy image.
#
# The harness is the thing every later proof rests on, so it has to be shown
# to work before any of those proofs mean anything. It is checked against a
# release rather than the working tree deliberately: if the harness only works
# against code written alongside it, it is measuring itself.
#
# What it establishes:
#
#   1. fake-rock speaks the protocol well enough that a real proxy accepts a
#      print job and answers it.
#   2. A successful print returns an empty result string - which is what Rock
#      treats as success.
#   3. The bytes the printer receives are byte-for-byte the bytes that were
#      sent. Nothing is re-encoded, truncated, or padded in between.
#   4. That holds for arbitrary binary, not just printable ASCII. This is the
#      assumption the whole label story rests on, so it is proven with a
#      payload containing all 256 byte values rather than taken on trust.
#   5. One print opens exactly one connection to the printer.
#   6. A failed print returns a non-empty reason rather than a silent success.
#   7. Ping works, so the connection stays alive the way the real server keeps
#      it alive.
#
# Usage:
#   tools/t0-gate.sh                                  # against the default image
#   IMAGE=asdfinit/rock-cloudprint:1.5.0-rc1 tools/t0-gate.sh
#   ZPL=blank-label-templates/ChildLabel.prn tools/t0-gate.sh
#
set -uo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

image="${IMAGE:-asdfinit/rock-cloudprint:1.4.0}"
rock_port="${ROCK_PORT:-19100}"
printer_port="${PRINTER_PORT:-19101}"
dead_port="${DEAD_PORT:-19199}"
ui_port="${UI_PORT:-18080}"
container="${CONTAINER:-t0-gate}"
zpl="${ZPL:-$repo/tools/sample-label.zpl}"

work="$(mktemp -d /tmp/t0-gate-XXXXXX)"
printer_pid=""
rock_pid=""

cleanup() {
    # Kill only what this script started, by recorded pid. Never by pattern:
    # a pgrep broad enough to catch a python harness is broad enough to catch
    # something that matters, and on this machine it already has.
    [ -n "$rock_pid" ] && kill "$rock_pid" 2>/dev/null
    [ -n "$printer_pid" ] && kill "$printer_pid" 2>/dev/null
    docker rm -f "$container" >/dev/null 2>&1
}
trap cleanup EXIT

echo "image      $image"
echo "workdir    $work"
echo "label      $zpl"
echo

if [ ! -f "$zpl" ]; then
    echo "FAIL: no such label file: $zpl"
    exit 1
fi

# A payload of every byte value. If this survives the round trip then the
# protocol's extra-data boundary is exact and nothing along the way assumes
# text.
python3 -c "import sys; sys.stdout.buffer.write(bytes(range(256)))" > "$work/binary.bin"

mkdir -p "$work/capture"

# ── the fake printer ─────────────────────────────────────────────────────────
python3 "$repo/tools/fake-printer.py" \
    --port "$printer_port" --out "$work/capture" \
    > "$work/printer.jsonl" 2>"$work/printer.err" &
printer_pid=$!

# ── the fake Rock server ─────────────────────────────────────────────────────
# Commands run in order at startup, and each print waits for its result, so
# the connection counting below is unambiguous. quit ends the process, which
# is what this script waits on.
python3 "$repo/tools/fake-rock.py" \
    --port "$rock_port" --ping-interval 0 --no-stdin \
    --command "{\"cmd\":\"print\",\"address\":\"127.0.0.1:$printer_port\",\"zpl_file\":\"$zpl\",\"wait\":true}" \
    --command "{\"cmd\":\"print\",\"address\":\"127.0.0.1:$printer_port\",\"zpl_file\":\"$work/binary.bin\",\"wait\":true}" \
    --command "{\"cmd\":\"print\",\"address\":\"127.0.0.1:$dead_port\",\"zpl_file\":\"$zpl\",\"wait\":true}" \
    --command '{"cmd":"ping","wait":true}' \
    --command '{"cmd":"quit"}' \
    > "$work/rock.jsonl" 2>"$work/rock.err" &
rock_pid=$!

# Give the listeners a moment to bind before the proxy tries to dial in.
for _ in $(seq 1 40); do
    grep -q '"event": "listening"' "$work/rock.jsonl" 2>/dev/null && break
    sleep 0.1
done

# ── the proxy under test ─────────────────────────────────────────────────────
# Host networking so 127.0.0.1 means the same thing inside the container as
# out. Urls is set as a config key rather than ASPNETCORE_URLS because the
# image's appsettings.json pins Urls and would otherwise win.
docker rm -f "$container" >/dev/null 2>&1
docker run -d --name "$container" --network host \
    -e Urls="http://+:$ui_port" \
    -e Url="http://127.0.0.1:$rock_port" \
    -e Id=t0-gate \
    -e Name=t0-gate \
    "$image" >/dev/null

# ── wait for the scripted run to finish ──────────────────────────────────────
waited=0
while kill -0 "$rock_pid" 2>/dev/null; do
    sleep 0.5
    waited=$((waited + 1))

    if [ "$waited" -gt 120 ]; then
        echo "FAIL: the run did not finish within 60s."
        echo "--- fake-rock events ---";    cat "$work/rock.jsonl"
        echo "--- container log ---";       docker logs "$container" 2>&1 | tail -40
        exit 1
    fi
done

# The printer's last connection close is written as the socket ends, which can
# land just after the proxy has already answered. Give it a beat.
sleep 0.5
kill "$printer_pid" 2>/dev/null
wait "$printer_pid" 2>/dev/null
printer_pid=""

docker logs "$container" > "$work/container.log" 2>&1
docker rm -f "$container" >/dev/null 2>&1

# ── verdict ──────────────────────────────────────────────────────────────────
python3 - "$work" "$zpl" <<'PY'
import hashlib, json, os, sys

work, zpl = sys.argv[1], sys.argv[2]

def events(name):
    out = []
    with open(os.path.join(work, name)) as handle:
        for line in handle:
            line = line.strip()
            if line:
                out.append(json.loads(line))
    return out

rock = events("rock.jsonl")
printer = events("printer.jsonl")

results  = [e for e in rock if e["event"] == "result"]
sent     = [e for e in rock if e["event"] == "print_sent"]
pongs    = [e for e in rock if e["event"] == "pong"]
accepts  = [e for e in printer if e["event"] == "accept"]
closes   = [e for e in printer if e["event"] == "close"]
connected = [e for e in rock if e["event"] == "connected"]

checks = []

def check(name, ok, detail=""):
    checks.append((name, ok, detail))

check("proxy connected to fake-rock",
      len(connected) == 1,
      connected[0]["path"] if connected else "no connection")

check("three prints sent, three results returned",
      len(sent) == 3 and len(results) == 3,
      "sent=%d results=%d" % (len(sent), len(results)))

if len(results) == 3:
    good_zpl, good_bin, bad = results

    check("ZPL print returned success (empty result)",
          good_zpl["result"] == "",
          repr(good_zpl["result"]))

    check("binary print returned success (empty result)",
          good_bin["result"] == "",
          repr(good_bin["result"]))

    check("print to a closed port returned a reason",
          isinstance(bad["result"], str) and bad["result"] != "",
          repr(bad["result"]))

# Two prints reached the fake printer; the third went to a dead port.
check("exactly two connections to the printer, one per print",
      len(accepts) == 2 and len(closes) == 2,
      "accepts=%d closes=%d" % (len(accepts), len(closes)))

with open(zpl, "rb") as handle:
    zpl_sha = hashlib.sha256(handle.read()).hexdigest()
with open(os.path.join(work, "binary.bin"), "rb") as handle:
    bin_sha = hashlib.sha256(handle.read()).hexdigest()

captured = {c["conn"]: c for c in closes}

check("the printer received the ZPL byte for byte",
      1 in captured and captured[1]["sha256"] == zpl_sha,
      "got %s want %s" % (captured.get(1, {}).get("sha256", "-")[:16], zpl_sha[:16]))

check("the printer received all 256 byte values byte for byte",
      2 in captured and captured[2]["sha256"] == bin_sha,
      "got %s want %s" % (captured.get(2, {}).get("sha256", "-")[:16], bin_sha[:16]))

if sent:
    check("what fake-rock sent is what the printer captured",
          1 in captured and captured[1]["sha256"] == sent[0]["sha256"],
          "sent %s captured %s" % (sent[0]["sha256"][:16],
                                   captured.get(1, {}).get("sha256", "-")[:16]))

check("ping answered", len(pongs) == 1, "%d pong(s)" % len(pongs))

print()
failed = 0
for name, ok, detail in checks:
    print("  %-58s %s%s" % (name, "PASS" if ok else "FAIL",
                            ("   [%s]" % detail) if detail and not ok else ""))
    if not ok:
        failed += 1

print()
if failed:
    print("T0 GATE: FAILED (%d of %d)" % (failed, len(checks)))
    print("\n--- fake-rock events ---")
    for e in rock:
        print(json.dumps(e))
    print("\n--- fake-printer events ---")
    for e in printer:
        print(json.dumps(e))
    print("\n--- container log (tail) ---")
    with open(os.path.join(work, "container.log")) as handle:
        print("".join(handle.readlines()[-40:]))
    sys.exit(1)

print("T0 GATE: PASSED (%d checks)" % len(checks))
PY
verdict=$?

echo
echo "evidence kept in $work"
exit $verdict
