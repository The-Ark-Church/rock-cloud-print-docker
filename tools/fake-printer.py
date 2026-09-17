#!/usr/bin/env python3
"""A stand-in for a Zebra label printer, for testing the proxy.

Real printers accept a TCP connection on port 9100 and swallow whatever
arrives. That is easy to fake. What is not easy to fake with a real printer is
the interesting half: a printer that reads slowly, one that stops reading
half way through a job, and one that resets the connection. Those are the
cases that decide whether the proxy behaves itself, and they are the reason
this exists.

Modes
-----
capture   Accept, read to EOF, save the bytes. The default.
slow      Read at a throttled rate so the sender blocks on its own writes.
stall     Read N bytes, then stop reading and hold the connection open
          forever. This is what a printer out of labels looks like from the
          other end: nothing is refused, nothing errors, the write just does
          not complete.
reset     Read N bytes, then send an RST. An abrupt, ugly failure.

A caveat about the last two, measured on this machine rather than assumed: on
loopback the kernel gives a sender a 2.5 MB send buffer, so a write smaller
than about 985 kB completes immediately even when the receiver stopped reading
after the first kilobyte. So these modes show what the *printer* did, and only
show what the *sender* saw once a job is large enough to fill those buffers.

That is worth knowing for its own sake and not only for testing: a completed
write is not evidence that a label printed, which is why a run should report
what it handed to the printer rather than what it printed.

Every connection is captured to its own file whatever the mode, so a partial
capture from a reset is still available for inspection.

Output
------
One JSON object per line on stdout, and the same records appended to
<out>/connections.jsonl. Nothing else is printed, so the stream can be parsed
directly by a test.

    {"event": "listening", "port": 19101, "mode": "capture"}
    {"event": "accept", "conn": 1, "peer": "127.0.0.1:53124", "t": 0.001}
    {"event": "close", "conn": 1, "bytes": 513, "sha256": "...", "file": "..."}

The "conn" number is what proves the one-socket-per-run rule: a run that
opens a socket per label shows up here as N accepts rather than one.

Usage
-----
    python3 tools/fake-printer.py --port 19101 --out /tmp/cap
    python3 tools/fake-printer.py --port 19102 --mode slow --bytes-per-sec 200
    python3 tools/fake-printer.py --port 19103 --mode stall --after 1024
    python3 tools/fake-printer.py --port 19104 --mode reset --after 1024

Stdlib only, deliberately: this runs on the dev box, in CI, and on a
Raspberry Pi, and none of them should need a pip install to test a printer.
"""

import argparse
import hashlib
import json
import os
import signal
import socket
import struct
import sys
import threading
import time

# Small enough that the kernel cannot quietly absorb a whole print job into
# the receive buffer and make a "slow" printer look instantaneous. Linux
# doubles this and enforces its own floor, so the effective size is larger
# than the number here - it is a request, not a guarantee. Applied to the
# listening socket because an accepted socket inherits the buffer size, and
# setting it afterwards is too late to affect window scaling.
SMALL_RCVBUF = 4096

# How much to read per syscall. Small in the throttled modes so the sleep
# granularity is fine enough to be believable, large otherwise.
CHUNK_FAST = 65536
CHUNK_SLOW = 256


class Recorder:
    """Writes the event stream, to stdout and to a file, from any thread."""

    def __init__(self, out_dir):
        self._lock = threading.Lock()
        self._out_dir = out_dir
        self._jsonl = open(os.path.join(out_dir, "connections.jsonl"), "a", buffering=1)
        self._started = time.monotonic()

    def emit(self, **fields):
        fields.setdefault("t", round(time.monotonic() - self._started, 4))
        line = json.dumps(fields, sort_keys=False)

        with self._lock:
            print(line, flush=True)
            self._jsonl.write(line + "\n")

    def close(self):
        with self._lock:
            self._jsonl.close()


def serve_connection(conn, peer, index, args, recorder):
    """Reads one connection according to the chosen mode and records it."""

    path = os.path.join(args.out, "conn-%04d.bin" % index)
    digest = hashlib.sha256()
    total = 0
    note = ""

    recorder.emit(event="accept", conn=index, peer="%s:%d" % peer)

    try:
        with open(path, "wb") as capture:
            chunk_size = CHUNK_SLOW if args.mode == "slow" else CHUNK_FAST

            while True:
                if args.mode in ("stall", "reset") and total >= args.after:
                    break

                if args.mode == "slow":
                    # Pace the *reader*. The sender's writes then block once
                    # the socket buffers fill, which is the whole point: a
                    # slow printer must be slow for the proxy, not just for us.
                    time.sleep(chunk_size / float(args.bytes_per_sec))

                data = conn.recv(chunk_size)

                if not data:
                    note = "eof"
                    break

                capture.write(data)
                digest.update(data)
                total += len(data)

        if args.mode == "stall" and note != "eof":
            # Stop reading, keep the socket open, and do nothing else. The
            # sender sees neither an error nor a completion - exactly what a
            # printer that has run out of labels looks like. Held open until
            # the peer gives up or this process exits.
            note = "stalled"
            recorder.emit(event="stall", conn=index, bytes=total)

            while not SHUTDOWN.is_set():
                SHUTDOWN.wait(0.25)

        elif args.mode == "reset" and note != "eof":
            # SO_LINGER with a zero timeout turns close() into an RST rather
            # than a FIN, which is what the proxy sees when a printer is
            # power-cycled mid-job.
            note = "reset"
            conn.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER,
                            struct.pack("ii", 1, 0))

    except (ConnectionResetError, BrokenPipeError, OSError) as ex:
        note = "%s: %s" % (type(ex).__name__, ex)

    finally:
        try:
            conn.close()
        except OSError:
            pass

        recorder.emit(event="close",
                      conn=index,
                      bytes=total,
                      sha256=digest.hexdigest(),
                      file=path,
                      note=note)


SHUTDOWN = threading.Event()


def main():
    parser = argparse.ArgumentParser(description="A fake Zebra printer.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=19101)
    parser.add_argument("--out", default=None,
                        help="Directory for captures. Defaults to a temp dir.")
    parser.add_argument("--mode", default="capture",
                        choices=["capture", "slow", "stall", "reset"])
    parser.add_argument("--bytes-per-sec", type=int, default=200,
                        help="slow mode: how fast to drain the socket.")
    parser.add_argument("--after", type=int, default=1024,
                        help="stall/reset mode: bytes to read first.")
    args = parser.parse_args()

    if args.out is None:
        import tempfile
        args.out = tempfile.mkdtemp(prefix="fake-printer-%d-" % args.port)

    os.makedirs(args.out, exist_ok=True)
    recorder = Recorder(args.out)

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

    if args.mode in ("slow", "stall", "reset"):
        # Not for capture mode: there the point is to swallow a job as fast as a
        # real printer would, and a small buffer would make an ordinary print
        # look slow.
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, SMALL_RCVBUF)

    listener.bind((args.host, args.port))
    listener.listen(16)
    listener.settimeout(0.25)

    recorder.emit(event="listening", port=args.port, mode=args.mode,
                  out=args.out, pid=os.getpid())

    def stop(_signum, _frame):
        SHUTDOWN.set()

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    index = 0
    threads = []

    try:
        while not SHUTDOWN.is_set():
            try:
                conn, peer = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break

            index += 1
            thread = threading.Thread(target=serve_connection,
                                      args=(conn, peer, index, args, recorder),
                                      daemon=True)
            thread.start()
            threads.append(thread)
    finally:
        SHUTDOWN.set()
        listener.close()

        for thread in threads:
            thread.join(timeout=2)

        recorder.emit(event="stopped", connections=index)
        recorder.close()


if __name__ == "__main__":
    sys.exit(main())
