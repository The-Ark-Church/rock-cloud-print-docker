#!/usr/bin/env python3
"""Proves what a blank label run actually sends to a printer.

Every check here is against a running container and a socket, not against the
source. The questions it answers are the ones that decide whether a stack of
labels is usable:

  1. Are the bytes the printer receives exactly the template with the code
     substituted, and does a whole run use one socket rather than one per
     label?
  2. Does every copy get its own code, shared by the labels within that copy -
     and does the parent receipt carry it on both halves?
  3. When a printer resets mid-run, does the run report a failure and a partial
     count, and are the codes it reserved still recorded so the next run
     carries on past them rather than repeating them?
  4. When a printer stops reading, does the run wait indefinitely rather than
     giving up, and does Cancel end it?
  5. Does a second run get turned away rather than interleaved?
  6. Does any of this work with Rock unreachable? The container is pointed at a
     dead address for the whole test, so every passing check above is also an
     answer to this one.

Usage:
    python3 tools/t4-gate.py
    IMAGE=asdfinit/rock-cloudprint:1.5.0-rc1 python3 tools/t4-gate.py
"""

import base64
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMAGE = os.environ.get("IMAGE", "rock-cloudprint:t4")
CONTAINER = os.environ.get("CONTAINER", "t4-gate")
UI_PORT = int(os.environ.get("UI_PORT", "18080"))
BASE = "http://127.0.0.1:%d" % UI_PORT

# Nothing listens here. The container is pointed at it for the whole run, so
# every check that passes below passed with Rock unreachable.
DEAD_ROCK = "http://127.0.0.1:19999"

CAPTURE_PORT = 19101
RESET_PORT = 19104
STALL_PORT = 19103

LABELS = ["Demo-Child-Label", "Demo-Parent-Receipt", "Demo-Roster-Label"]

checks = []
processes = []


def check(name, ok, detail=""):
    checks.append((name, bool(ok), detail))
    print("  %-62s %s%s" % (name, "PASS" if ok else "FAIL",
                            ("   [%s]" % detail) if detail else ""))


def api(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as ex:
        raw = ex.read()
        try:
            return ex.code, json.loads(raw or b"null")
        except ValueError:
            return ex.code, {"raw": raw.decode("utf-8", "replace")}
    except urllib.error.URLError:
        # Nothing listening yet. Callers poll for a 200, so a refused
        # connection is a "not ready", not a failure.
        return 0, None


def start_printer(port, mode, out, **options):
    args = [sys.executable, os.path.join(REPO, "tools", "fake-printer.py"),
            "--port", str(port), "--mode", mode, "--out", out]
    for key, value in options.items():
        args += ["--" + key.replace("_", "-"), str(value)]

    log = open(os.path.join(out, "events.jsonl"), "w")
    process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT)
    processes.append(process)
    return process


def printer_events(out):
    path = os.path.join(out, "events.jsonl")
    events = []
    if os.path.exists(path):
        with open(path) as handle:
            for line in handle:
                line = line.strip()
                if line:
                    events.append(json.loads(line))
    return events


def template(name):
    with open(os.path.join(REPO, "Rock.CloudPrint.Service", "Labels", name + ".zpl"), "rb") as handle:
        return handle.read()


def resolve(content, code):
    """The expectation, computed independently of the service.

    Deliberately naive: every ??? in the file, replaced. The templates only
    contain the token inside data fields, so for these three that is the same
    answer the service should give by a more careful route.
    """
    return content.replace(b"???", code.encode("latin-1"))


def wait_until(predicate, seconds, interval=0.2):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(interval)
    return False


def run_status():
    return api("GET", "/api/labels/print")[1].get("run")


def wait_for_run_to_end(seconds):
    return wait_until(lambda: (run_status() or {}).get("status") != "running", seconds)


def cleanup():
    for process in processes:
        try:
            process.send_signal(signal.SIGTERM)
        except OSError:
            pass
    subprocess.run(["docker", "rm", "-f", CONTAINER],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main():
    work = tempfile.mkdtemp(prefix="t4-gate-")
    config = os.path.join(work, "config")
    os.makedirs(config)

    print("image      %s" % IMAGE)
    print("workdir    %s\n" % work)

    captures = {}
    for port, mode, options in ((CAPTURE_PORT, "capture", {}),
                                (RESET_PORT, "reset", {"after": 1024}),
                                (STALL_PORT, "stall", {"after": 1024})):
        out = os.path.join(work, "%s-%d" % (mode, port))
        os.makedirs(out)
        captures[port] = out
        start_printer(port, mode, out, **options)

    time.sleep(1)

    subprocess.run(["docker", "rm", "-f", CONTAINER],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.run(["docker", "run", "-d", "--name", CONTAINER, "--network", "host",
                    "-e", "Urls=http://+:%d" % UI_PORT,
                    "-e", "Url=" + DEAD_ROCK,
                    "-e", "Id=t4-gate", "-e", "Name=t4-gate",
                    "-v", "%s:/app/config" % config, IMAGE],
                   check=True, stdout=subprocess.DEVNULL)

    if not wait_until(lambda: api("GET", "/api/labels")[0] == 200, 60):
        print("FAIL: the container never answered")
        print(subprocess.run(["docker", "logs", CONTAINER], capture_output=True, text=True).stderr[-3000:])
        return 1

    # ── 1. one socket, byte-exact ───────────────────────────────────────────
    print("1. What reaches the printer")

    status, run = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % CAPTURE_PORT,
        "labels": LABELS, "quantity": 2,
        "mode": "sequential", "start": "1001"})

    check("a run is accepted and answers straight away", status == 202, "HTTP %d" % status)
    wait_for_run_to_end(30)
    final = run_status() or {}
    check("the run completed", final.get("status") == "completed", str(final.get("error")))
    check("both copies were handed over", final.get("copiesHandedToPrinter") == 2,
          str(final.get("copiesHandedToPrinter")))

    time.sleep(0.5)
    events = printer_events(captures[CAPTURE_PORT])
    accepts = [e for e in events if e["event"] == "accept"]
    closes = [e for e in events if e["event"] == "close"]

    # One socket for the whole run, as the tool this replaces does. Opening one
    # per label can change where a cutter cuts.
    check("the whole run used one socket", len(accepts) == 1, "%d accepts" % len(accepts))

    expected = b"".join(resolve(template(name), code)
                        for code in ("1001", "1002") for name in LABELS)

    if closes:
        with open(closes[0]["file"], "rb") as handle:
            captured = handle.read()
        check("the printer received exactly the expected bytes", captured == expected,
              "got %d bytes, expected %d" % (len(captured), len(expected)))
    else:
        check("the printer received exactly the expected bytes", False, "nothing captured")

    # ── 2. a code per copy, shared within it ────────────────────────────────
    print("\n2. Codes across 300 copies")

    status, _ = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % CAPTURE_PORT,
        "labels": LABELS, "quantity": 300,
        "mode": "random", "codeLength": 3})
    wait_for_run_to_end(120)
    final = run_status() or {}
    check("300 copies completed", final.get("status") == "completed"
          and final.get("copiesHandedToPrinter") == 300,
          "%s after %s" % (final.get("status"), final.get("copiesHandedToPrinter")))

    time.sleep(0.5)
    events = printer_events(captures[CAPTURE_PORT])
    closes = [e for e in events if e["event"] == "close"]

    if len(closes) >= 2:
        with open(closes[1]["file"], "rb") as handle:
            captured = handle.read()

        sizes = [len(template(name)) for name in LABELS]
        copy_size = sum(sizes)

        # A three character code replacing a three character token leaves every
        # size unchanged, so the stream divides exactly.
        check("the stream is exactly 300 copies long", len(captured) == 300 * copy_size,
              "%d bytes, expected %d" % (len(captured), 300 * copy_size))

        codes, shared, receipts = [], 0, 0
        for copy in range(300):
            at = copy * copy_size
            parts, offset = [], at
            for size in sizes:
                parts.append(captured[offset:offset + size])
                offset += size

            child = parts[0][119:122]     # where ???  sits in the child label
            found = [child.decode("latin-1")]

            roster_at = template(LABELS[2]).index(b"???")
            roster = parts[2][roster_at:roster_at + 3].decode("latin-1")

            parent_content = template(LABELS[1])
            first = parent_content.index(b"???")
            second = parent_content.index(b"???", first + 3)
            halves = (parts[1][first:first + 3].decode("latin-1"),
                      parts[1][second:second + 3].decode("latin-1"))

            child_at = template(LABELS[0]).index(b"???")
            found[0] = parts[0][child_at:child_at + 3].decode("latin-1")

            if found[0] == roster:
                shared += 1
            if halves[0] == halves[1] == found[0]:
                receipts += 1

            codes.append(found[0])

        check("every copy carries a different code", len(set(codes)) == 300,
              "%d distinct" % len(set(codes)))
        check("the child tag and roster label share their copy's code", shared == 300,
              "%d of 300" % shared)
        check("both halves of the parent receipt carry it too", receipts == 300,
              "%d of 300" % receipts)
        check("no code contains a character that gets misread",
              not any(set(code) & set("0O1I") for code in codes))
    else:
        check("the stream is exactly 300 copies long", False, "nothing captured")

    # ── 3. a printer that resets mid-run ────────────────────────────────────
    print("\n3. A printer that resets part way")

    before = api("GET", "/api/labels/print")[1]
    status, _ = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % RESET_PORT,
        "labels": LABELS, "quantity": 300,
        "mode": "sequential", "start": "5000"})
    wait_for_run_to_end(60)
    final = run_status() or {}

    check("the run reports a failure", final.get("status") == "failed", str(final.get("status")))
    check("it says how many copies it managed",
          0 <= final.get("copiesHandedToPrinter", -1) < 300,
          str(final.get("copiesHandedToPrinter")))
    check("it says why", bool(final.get("error")), str(final.get("error")))

    after = api("GET", "/api/labels/print")[1]
    check("the codes it reserved are still recorded",
          after.get("sequentialReservedThrough") == "5299",
          str(after.get("sequentialReservedThrough")))
    check("the next run carries on past them rather than repeating",
          after.get("sequentialNext") == "5300", str(after.get("sequentialNext")))

    status, run = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % CAPTURE_PORT,
        "labels": [LABELS[0]], "quantity": 1, "mode": "sequential"})
    wait_for_run_to_end(30)
    check("a run with no start continues from the record",
          (run or {}).get("firstCode") == "5300", str((run or {}).get("firstCode")))

    # ── 4 and 5. a printer that stops reading ───────────────────────────────
    print("\n4. A printer that stops reading, and a second run")

    status, stalled = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % STALL_PORT,
        "labels": LABELS, "quantity": 2000,
        "mode": "random", "codeLength": 4})
    check("the run starts", status == 202, "HTTP %d" % status)

    # Long enough that anything with a time limit in it would have given up.
    time.sleep(10)
    during = run_status() or {}
    check("it is still running rather than reporting a failure",
          during.get("status") == "running", str(during.get("status")))

    # And genuinely stuck rather than merely slow. The writes stop once the
    # socket buffers fill behind a printer that is not reading, so the count
    # climbs and then stops - which is what an out-of-labels printer looks
    # like, and it is the case a time limit would wrongly call a failure.
    time.sleep(4)
    later = run_status() or {}
    stuck_at = during.get("copiesHandedToPrinter")
    check("it is blocked part way rather than still making progress",
          later.get("status") == "running"
          and later.get("copiesHandedToPrinter") == stuck_at
          and 0 < stuck_at < 2000,
          "stopped at %s of 2000" % stuck_at)

    status, _ = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % CAPTURE_PORT,
        "labels": LABELS, "quantity": 1, "mode": "random"})
    check("a second run is turned away rather than interleaved", status == 409, "HTTP %d" % status)

    cancelled_at = time.monotonic()
    api("POST", "/api/labels/print/%s/cancel" % stalled["id"])
    ended = wait_for_run_to_end(10)
    took = time.monotonic() - cancelled_at

    check("Cancel ends it", ended and (run_status() or {}).get("status") == "cancelled",
          str((run_status() or {}).get("status")))
    check("and it ends promptly", took < 3, "%.1fs" % took)

    status, _ = api("POST", "/api/labels/print", {
        "address": "127.0.0.1:%d" % CAPTURE_PORT,
        "labels": [LABELS[0]], "quantity": 1, "mode": "random"})
    check("the next run can start again afterwards", status == 202, "HTTP %d" % status)
    wait_for_run_to_end(30)

    # ── 6. with Rock unreachable throughout ─────────────────────────────────
    print("\n5. With Rock unreachable")

    logs = subprocess.run(["docker", "logs", CONTAINER], capture_output=True, text=True)
    combined = logs.stdout + logs.stderr

    attempts = sum(1 for line in combined.splitlines()
                   if "Unable to connect to server" in line or "Failed to connect" in line)

    check("the proxy never reached Rock during any of this", attempts > 0,
          "%d failed connection attempts logged" % attempts)
    check("and printed anyway", any(ok for name, ok, _ in checks if "byte" in name))

    with open(os.path.join(work, "container.log"), "w") as handle:
        handle.write(combined)

    failed = sum(1 for _, ok, _ in checks if not ok)
    print()
    if failed:
        print("T4 GATE: FAILED (%d of %d)" % (failed, len(checks)))
        print("\ncontainer log tail:")
        print("\n".join(combined.splitlines()[-30:]))
        print("\nevidence kept in %s" % work)
        return 1

    print("T4 GATE: PASSED (%d checks)" % len(checks))
    shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        cleanup()
