#!/usr/bin/env python3
"""Proves one asleep printer cannot hold up any other printer.

A Wi-Fi label printer that is asleep can take up to two minutes to answer a
connection. With printing on the receive loop, every other printer waited on
it too, and the proxy stopped answering the server's pings. The fix is to print off the receive loop. A previous fix did
that and also put a five second limit on the printer, which failed exactly the
printer this is about - one that is slow to wake - and was rolled back. So this
proves both halves: nothing else waits, and the slow printer still prints.

An asleep printer is faked without root by filling a listener's accept queue.
Linux then drops new connection attempts without answering, so the proxy's
connect sits retrying exactly as it does against a printer that is asleep.
Draining the queue later is the printer waking: the proxy's next retry gets in.
Linux retries at 1, 3, 7, 15, 31 and 63 seconds, so a printer woken at 45
seconds is reached by the 63 second retry - after the server's 60 second wait,
which is the late print the old time limit used to turn into a failure.

What it establishes:

  1. While a print to an asleep printer is pending, prints to two other
     printers complete in milliseconds.
  2. Pings keep being answered promptly for longer than the server's 60 second
     wait, and the connection is never dropped.
  3. The asleep printer still prints once it wakes, even though that is after
     the server stopped waiting. No time limit failed it.
  4. Only one connection attempt is ever open to the asleep printer, and the
     second label queued for it prints after the first.
  5. Labels sent back to back to one printer arrive in the order they were
     sent.

Usage:
    python3 tools/t5-gate.py                                   # rock-cloudprint:t5
    IMAGE=asdfinit/rock-cloudprint:1.5.2 python3 tools/t5-gate.py   # expect failures

Uses host networking and these ports on 127.0.0.1: 19120 (fake Rock), 19121
and 19122 (printers), 19150 (asleep printer), 18085 (proxy web UI).
"""

import hashlib
import json
import os
import queue
import socket
import subprocess
import sys
import tempfile
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMAGE = os.environ.get("IMAGE", "rock-cloudprint:t5")
CONTAINER = os.environ.get("CONTAINER", "t5-gate")

ROCK_PORT = 19120
PRINTER_A = 19121
PRINTER_B = 19122
ASLEEP_PORT = 19150
UI_PORT = 18085

WAKE_AFTER = 45          # seconds after the first asleep print
WATCH_FOR = 70           # seconds of pinging, longer than the server's 60s wait
PING_EVERY = 5

checks = []
processes = []


def check(name, ok, detail=""):
    checks.append((name, bool(ok), detail))
    print("  %-66s %s%s" % (name, "PASS" if ok else "FAIL",
                            ("   [%s]" % detail) if detail else ""), flush=True)


def label(name):
    return "^XA^FO50,50^A0N,40^FD%s^FS^XZ" % name


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


class AsleepPrinter:
    """A printer that does not answer until woken, then captures like any other."""

    def __init__(self, port):
        self.listener = socket.socket()
        self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.listener.bind(("127.0.0.1", port))
        self.listener.listen(0)

        # One queued connection fills a backlog of zero. From here on the
        # kernel drops new attempts rather than answering them.
        self.filler = socket.create_connection(("127.0.0.1", port))
        self.captures = []
        self.woke_at = None

    def wake(self):
        self.woke_at = time.monotonic()
        threading.Thread(target=self._serve, daemon=True).start()

    def _serve(self):
        filler, _ = self.listener.accept()
        filler.close()
        self.filler.close()

        while True:
            try:
                conn, _ = self.listener.accept()
            except OSError:
                return

            accepted = time.monotonic()
            data = b""

            while True:
                chunk = conn.recv(65536)
                if not chunk:
                    break
                data += chunk

            conn.close()
            self.captures.append({"accepted": accepted,
                                  "sha256": hashlib.sha256(data).hexdigest()})


def connect_attempts(port):
    """Connections the proxy has open but unanswered to the given port."""

    out = subprocess.run(["ss", "-Htn", "state", "syn-sent", "( dport = :%d )" % port],
                         capture_output=True, text=True).stdout
    return len([line for line in out.splitlines() if line.strip()])


def main():
    work = tempfile.mkdtemp(prefix="t5-gate-")
    print("image      %s" % IMAGE)
    print("workdir    %s" % work)
    print()

    printers = {}
    for port in (PRINTER_A, PRINTER_B):
        out = os.path.join(work, "printer-%d" % port)
        os.makedirs(out)
        log = open(os.path.join(work, "printer-%d.jsonl" % port), "w")
        processes.append(subprocess.Popen(
            [sys.executable, os.path.join(REPO, "tools", "fake-printer.py"),
             "--port", str(port), "--out", out],
            stdout=log, stderr=subprocess.DEVNULL))
        printers[port] = os.path.join(work, "printer-%d.jsonl" % port)

    asleep = AsleepPrinter(ASLEEP_PORT)

    rock = subprocess.Popen(
        [sys.executable, os.path.join(REPO, "tools", "fake-rock.py"),
         "--port", str(ROCK_PORT), "--ping-interval", "0"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, bufsize=1)
    processes.append(rock)

    events = []
    arrivals = queue.Queue()

    def read_events():
        rock_log = open(os.path.join(work, "rock.jsonl"), "w")
        for line in rock.stdout:
            line = line.strip()
            if not line:
                continue
            rock_log.write(line + "\n")
            rock_log.flush()
            event = json.loads(line)
            event["_at"] = time.monotonic()
            events.append(event)
            arrivals.put(event)

    threading.Thread(target=read_events, daemon=True).start()

    def send(command):
        rock.stdin.write(json.dumps(command) + "\n")
        rock.stdin.flush()

    def wait_for(predicate, seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if any(predicate(e) for e in list(events)):
                return True
            time.sleep(0.05)
        return False

    wait_for(lambda e: e["event"] == "listening", 5)

    subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True)
    subprocess.run(["docker", "run", "-d", "--name", CONTAINER, "--network", "host",
                    "-e", "Urls=http://+:%d" % UI_PORT,
                    "-e", "Url=http://127.0.0.1:%d" % ROCK_PORT,
                    "-e", "Id=t5-gate", "-e", "Name=t5-gate", IMAGE],
                   check=True, capture_output=True)

    if not wait_for(lambda e: e["event"] == "connected", 60):
        check("proxy connected to fake-rock", False, "no connection in 60s")
        return finish(work)

    sent = {}   # message id -> name, filled from print_sent events in send order

    def print_to(port, name):
        before = len([e for e in events if e["event"] == "print_sent"])
        send({"cmd": "print", "address": "127.0.0.1:%d" % port, "zpl": label(name)})
        wait_for(lambda e: False, 0)  # yield
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            printed = [e for e in events if e["event"] == "print_sent"]
            if len(printed) > before:
                sent[printed[before]["id"]] = name
                return
            time.sleep(0.01)
        sent["missing-" + name] = name

    def result_for(name):
        for e in events:
            if e["event"] == "result" and sent.get(e["id"]) == name:
                return e
        return None

    # ── an asleep printer gets two labels ───────────────────────────────────
    started = time.monotonic()
    print_to(ASLEEP_PORT, "ASLEEP-1")
    print_to(ASLEEP_PORT, "ASLEEP-2")
    time.sleep(1)

    # ── meanwhile, other printers ──────────────────────────────────────────
    print_to(PRINTER_A, "A-1")
    print_to(PRINTER_B, "B-1")

    for n in range(1, 6):
        print_to(PRINTER_A, "ORDER-%d" % n)

    # ── keep pinging past the server's 60s wait; wake the printer at 45s ────
    attempts_seen = []
    next_ping = time.monotonic()

    while time.monotonic() - started < WATCH_FOR:
        if time.monotonic() >= next_ping:
            send({"cmd": "ping"})
            next_ping += PING_EVERY

        if asleep.woke_at is None:
            attempts_seen.append(connect_attempts(ASLEEP_PORT))

            if time.monotonic() - started >= WAKE_AFTER:
                asleep.wake()

        time.sleep(0.5)

    # ── give the woken printer's labels time to land ───────────────────────
    wait_for(lambda e: False, 0)
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline and not (result_for("ASLEEP-1") and result_for("ASLEEP-2")):
        time.sleep(0.5)

    time.sleep(1)

    # ── verdict ─────────────────────────────────────────────────────────────
    print()

    for name in ("A-1", "B-1"):
        r = result_for(name)
        check("%s printed in under 2s while the asleep printer was pending" % name,
              r is not None and r["ok"] and r["elapsed_ms"] < 2000,
              "no result" if r is None else "ok=%s %sms" % (r["ok"], r["elapsed_ms"]))

    pongs = [e for e in events if e["event"] == "pong"]
    pings_sent = int(WATCH_FOR / PING_EVERY)
    slowest = max([p["elapsed_ms"] or 0 for p in pongs], default=None)
    check("every ping answered in under 1s for %ds" % WATCH_FOR,
          len(pongs) >= pings_sent and slowest is not None and slowest < 1000,
          "%d of %d pongs, slowest %sms" % (len(pongs), pings_sent, slowest))

    dropped = [e for e in events if e["event"] == "disconnected"]
    check("the connection to the server was never dropped",
          not dropped, "%d disconnect(s)" % len(dropped))

    r1, r2 = result_for("ASLEEP-1"), result_for("ASLEEP-2")
    check("the asleep printer printed once it woke, after the server's 60s",
          r1 is not None and r1["ok"] and r1["elapsed_ms"] > 60000,
          "no result" if r1 is None else "ok=%s %sms %r" % (r1["ok"], r1["elapsed_ms"], r1["result"]))

    check("its second label printed too",
          r2 is not None and r2["ok"],
          "no result" if r2 is None else "ok=%s %sms %r" % (r2["ok"], r2["elapsed_ms"], r2["result"]))

    want = [sha(label("ASLEEP-1")), sha(label("ASLEEP-2"))]
    got = [c["sha256"] for c in asleep.captures]
    check("the asleep printer received both labels, in order, byte for byte",
          got == want, "captured %d, order %s" % (len(got), "ok" if got == want else "wrong"))

    check("never more than one connection attempt open to it at a time",
          attempts_seen and max(attempts_seen) == 1,
          "max %s across %d samples" % (max(attempts_seen) if attempts_seen else "-", len(attempts_seen)))

    closes = []
    with open(printers[PRINTER_A]) as handle:
        for line in handle:
            e = json.loads(line)
            if e["event"] == "close":
                closes.append(e["sha256"])
    order_want = [sha(label("ORDER-%d" % n)) for n in range(1, 6)]
    order_got = [s for s in closes if s in order_want]
    check("five back-to-back labels to one printer arrived in order",
          order_got == order_want, "received %d of 5" % len(order_got))

    return finish(work)


def finish(work):
    log = subprocess.run(["docker", "logs", CONTAINER], capture_output=True, text=True)
    with open(os.path.join(work, "container.log"), "w") as handle:
        handle.write(log.stdout + log.stderr)

    check("no unhandled print errors in the proxy log",
          "Print handling failed" not in log.stdout + log.stderr)

    failed = [c for c in checks if not c[1]]
    print()
    if failed:
        print("T5 GATE: FAILED (%d of %d)" % (len(failed), len(checks)))
    else:
        print("T5 GATE: PASSED (%d checks)" % len(checks))
    print()
    print("evidence kept in %s" % work)
    return 1 if failed else 0


def cleanup():
    # Only what this script started, by handle. Never by pattern.
    for process in processes:
        process.kill()
    subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True)


if __name__ == "__main__":
    try:
        code = main()
    finally:
        cleanup()
    sys.exit(code)
