#!/usr/bin/env python3
"""A stand-in for the Rock server's cloud print WebSocket endpoint.

The proxy cannot originate a print. Rock drives everything: it opens nothing,
the proxy dials out, and then Rock pushes print jobs down the socket and waits
for a result string back. So without something on the other end there is no way
to exercise the print path at all, and every claim about it is a guess.

This speaks the real protocol, read from the source rather than inferred:

    byte 0   message type - 0 None, 1 Ping, 2 Response, 3 Print
    then     UTF-8 JSON of the message object, PascalCase property names
    then     raw label bytes, for Print only

sent as a single binary WebSocket message. The proxy locates the end of the
JSON with Utf8JsonReader.BytesConsumed and treats everything after it as the
label data, so the JSON must carry no trailing whitespace. Property names are
matched case-sensitively - System.Text.Json is configured with its defaults -
so "Address" works and "address" is silently dropped.

The proxy connects to:

    {Url}/api/v2/checkin/cloudprint/{Id}?name={Name}

with http:// rewritten to ws://.

Driving it
----------
Commands arrive as one JSON object per line on stdin; events leave as one JSON
object per line on stdout. That makes it scriptable from bash, from a C# test,
or by hand.

    {"cmd": "print", "address": "127.0.0.1:19101", "zpl_file": "label.zpl"}
    {"cmd": "print", "address": "...", "zpl": "^XA...^XZ", "repeat": 5,
     "interval_ms": 200, "wait": true}
    {"cmd": "ping", "wait": true}
    {"cmd": "close"}
    {"cmd": "quit"}

Events:

    {"event": "listening", "port": 19100}
    {"event": "connected", "path": "/api/v2/checkin/cloudprint/abc", "name": "x"}
    {"event": "print_sent", "id": "...", "bytes": 513, "sha256": "..."}
    {"event": "result", "id": "...", "result": "", "ok": true, "elapsed_ms": 4}
    {"event": "disconnected"}

The elapsed_ms on a result is measured here, at the server, which is the only
place it means anything: it is the number Rock's five second print budget is
spent against.

Stdlib only - RFC 6455 is about eighty lines and is not worth a dependency on
a machine that has to test a printer.
"""

import argparse
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import threading
import time
import uuid
from datetime import datetime, timezone

# RFC 6455 section 1.3. Concatenated with the client's key and hashed to prove
# the server understood the upgrade rather than blindly accepting it.
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

OP_CONTINUATION = 0x0
OP_TEXT = 0x1
OP_BINARY = 0x2
OP_CLOSE = 0x8
OP_PING = 0x9
OP_PONG = 0xA

# Message type codes, from CloudPrintMessageType.
MSG_NONE = 0
MSG_PING = 1
MSG_RESPONSE = 2
MSG_PRINT = 3


class Events:
    """Writes the event stream from any thread."""

    def __init__(self, path=None):
        self._lock = threading.Lock()
        self._file = open(path, "a", buffering=1) if path else None
        self._started = time.monotonic()

    def emit(self, **fields):
        fields.setdefault("t", round(time.monotonic() - self._started, 4))
        line = json.dumps(fields, sort_keys=False)

        with self._lock:
            print(line, flush=True)

            if self._file:
                self._file.write(line + "\n")


def recv_exact(sock, count):
    """Reads exactly count bytes, or returns None if the peer went away."""

    chunks = []
    remaining = count

    while remaining > 0:
        chunk = sock.recv(remaining)

        if not chunk:
            return None

        chunks.append(chunk)
        remaining -= len(chunk)

    return b"".join(chunks)


def read_frame(sock):
    """Reads one WebSocket frame. Returns (fin, opcode, payload) or None."""

    header = recv_exact(sock, 2)

    if header is None:
        return None

    fin = bool(header[0] & 0x80)
    opcode = header[0] & 0x0F
    masked = bool(header[1] & 0x80)
    length = header[1] & 0x7F

    if length == 126:
        extended = recv_exact(sock, 2)

        if extended is None:
            return None

        length = struct.unpack("!H", extended)[0]
    elif length == 127:
        extended = recv_exact(sock, 8)

        if extended is None:
            return None

        length = struct.unpack("!Q", extended)[0]

    mask = None

    if masked:
        mask = recv_exact(sock, 4)

        if mask is None:
            return None

    payload = recv_exact(sock, length) if length else b""

    if payload is None:
        return None

    if mask:
        # Every client-to-server frame is masked. Unmasking is a byte-wise
        # XOR against the repeating four byte key.
        payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))

    return fin, opcode, payload


def build_frame(opcode, payload):
    """Builds one unmasked server-to-client frame."""

    header = bytearray()
    header.append(0x80 | opcode)
    length = len(payload)

    # Server frames are never masked, so the high bit of the second byte
    # stays clear.
    if length < 126:
        header.append(length)
    elif length < 65536:
        header.append(126)
        header.extend(struct.pack("!H", length))
    else:
        header.append(127)
        header.extend(struct.pack("!Q", length))

    return bytes(header) + payload


class Connection:
    """One connected proxy."""

    def __init__(self, sock, path, query, events):
        self.sock = sock
        self.path = path
        self.query = query
        self._events = events
        self._send_lock = threading.Lock()
        self._pending = {}
        self._pending_lock = threading.Lock()
        self.closed = threading.Event()

    def send_frame(self, opcode, payload):
        with self._send_lock:
            self.sock.sendall(build_frame(opcode, payload))

    def send_message(self, message, extra=b""):
        """Encodes and sends one cloud print message."""

        # separators removes the spaces that json.dumps adds by default. The
        # proxy does not care, but keeping the frame byte-exact makes the
        # captured stream easier to reason about.
        body = json.dumps(message, separators=(",", ":")).encode("utf-8")

        self.send_frame(OP_BINARY, bytes([message["Type"]]) + body + extra)

    def expect(self, message_id):
        """Registers interest in a response and returns a waitable slot."""

        slot = {"event": threading.Event(), "result": None, "sent_at": time.monotonic()}

        with self._pending_lock:
            self._pending[message_id] = slot

        return slot

    def complete(self, message_id, result):
        with self._pending_lock:
            slot = self._pending.pop(message_id, None)

        if slot is None:
            return None

        slot["result"] = result
        slot["elapsed_ms"] = round((time.monotonic() - slot["sent_at"]) * 1000, 2)
        slot["event"].set()

        return slot

    def close(self, code=1000):
        try:
            self.send_frame(OP_CLOSE, struct.pack("!H", code))
        except OSError:
            pass

        try:
            self.sock.close()
        except OSError:
            pass

        self.closed.set()


class FakeRock:
    def __init__(self, args, events):
        self.args = args
        self.events = events
        self.connection = None
        self._connection_ready = threading.Event()
        self._stop = threading.Event()

    # -- connection handling -------------------------------------------------

    def handshake(self, sock):
        """Completes the RFC 6455 upgrade. Returns (path, query) or None."""

        buffer = b""

        while b"\r\n\r\n" not in buffer:
            chunk = sock.recv(4096)

            if not chunk:
                return None

            buffer += chunk

            if len(buffer) > 65536:
                return None

        head = buffer.split(b"\r\n\r\n", 1)[0].decode("latin-1")
        lines = head.split("\r\n")
        request_line = lines[0].split(" ")

        if len(request_line) < 2:
            return None

        target = request_line[1]
        path, _, query = target.partition("?")

        key = None

        for line in lines[1:]:
            name, _, value = line.partition(":")

            if name.strip().lower() == "sec-websocket-key":
                key = value.strip()

        if key is None:
            sock.sendall(b"HTTP/1.1 400 Bad Request\r\n\r\n")
            return None

        accept = base64.b64encode(
            hashlib.sha1((key + WS_GUID).encode("ascii")).digest()
        ).decode("ascii")

        sock.sendall(
            (
                "HTTP/1.1 101 Switching Protocols\r\n"
                "Upgrade: websocket\r\n"
                "Connection: Upgrade\r\n"
                "Sec-WebSocket-Accept: %s\r\n"
                "\r\n" % accept
            ).encode("ascii")
        )

        return path, query

    def reader_loop(self, connection):
        """Reads frames until the socket ends, dispatching responses."""

        fragments = []
        fragment_opcode = None

        try:
            while not self._stop.is_set():
                frame = read_frame(connection.sock)

                if frame is None:
                    break

                fin, opcode, payload = frame

                if opcode == OP_CLOSE:
                    break

                if opcode == OP_PING:
                    # The proxy's ClientWebSocket sends protocol level keep
                    # alive pings. Not answering them eventually kills the
                    # connection for reasons that look like the proxy's fault.
                    connection.send_frame(OP_PONG, payload)
                    continue

                if opcode == OP_PONG:
                    continue

                if opcode == OP_CONTINUATION:
                    fragments.append(payload)
                else:
                    fragments = [payload]
                    fragment_opcode = opcode

                if not fin:
                    continue

                data = b"".join(fragments)
                fragments = []

                if fragment_opcode == OP_BINARY:
                    self.on_message(connection, data)
        except OSError:
            pass
        finally:
            connection.closed.set()

            if self.connection is connection:
                self.connection = None
                self._connection_ready.clear()

            self.events.emit(event="disconnected", path=connection.path)

    def on_message(self, connection, data):
        if not data:
            return

        message_type = data[0]

        try:
            message = json.loads(data[1:].decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as ex:
            self.events.emit(event="error", detail="undecodable message: %s" % ex)
            return

        if message_type != MSG_RESPONSE:
            self.events.emit(event="unexpected", type=message_type, message=message)
            return

        message_id = message.get("Id")
        result = message.get("Result")
        slot = connection.complete(message_id, result)

        if isinstance(result, dict) and "RequestedAt" in result:
            self.events.emit(event="pong", id=message_id,
                             elapsed_ms=slot["elapsed_ms"] if slot else None)
            return

        self.events.emit(event="result",
                         id=message_id,
                         result=result,
                         ok=(result == ""),
                         elapsed_ms=slot["elapsed_ms"] if slot else None)

    def accept_loop(self, listener):
        while not self._stop.is_set():
            try:
                sock, peer = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break

            try:
                handshake = self.handshake(sock)
            except OSError:
                handshake = None

            if handshake is None:
                try:
                    sock.close()
                except OSError:
                    pass
                continue

            path, query = handshake
            connection = Connection(sock, path, query, self.events)

            # The proxy identifies itself entirely through the URL: the device
            # id is the last path segment and the proxy name is the query
            # string. Reporting both makes "why did Rock send this nothing"
            # answerable, which on the real server it was not.
            self.events.emit(event="connected",
                             path=path,
                             query=query,
                             device_id=path.rstrip("/").rsplit("/", 1)[-1],
                             peer="%s:%d" % peer)

            self.connection = connection
            self._connection_ready.set()

            threading.Thread(target=self.reader_loop, args=(connection,),
                             daemon=True).start()

    # -- commands ------------------------------------------------------------

    def wait_for_connection(self, timeout_ms):
        if self._connection_ready.wait(timeout_ms / 1000.0):
            return self.connection

        return None

    def do_print(self, command):
        connection = self.wait_for_connection(command.get("timeout_ms", 30000))

        if connection is None:
            self.events.emit(event="error", detail="no proxy connected")
            return

        payload = self.resolve_zpl(command)

        if payload is None:
            return

        repeat = int(command.get("repeat", 1))
        interval_ms = int(command.get("interval_ms", 0))
        wait = bool(command.get("wait", False))
        count = int(command.get("count", 1))
        address = command["address"]

        for attempt in range(repeat):
            message_id = str(uuid.uuid4())
            slot = connection.expect(message_id)

            message = {
                "Type": MSG_PRINT,
                "Id": message_id,
                "Address": address,
                "Count": count,
            }

            try:
                connection.send_message(message, payload)
            except OSError as ex:
                self.events.emit(event="error", detail="send failed: %s" % ex)
                return

            self.events.emit(event="print_sent",
                             id=message_id,
                             address=address,
                             count=count,
                             bytes=len(payload),
                             sha256=hashlib.sha256(payload).hexdigest(),
                             index=attempt + 1,
                             of=repeat)

            if wait:
                # Waits without a deadline on purpose. A print that never
                # comes back is a finding, not something to paper over with
                # a timeout that would then have to be explained.
                slot["event"].wait()

            if interval_ms and attempt + 1 < repeat:
                time.sleep(interval_ms / 1000.0)

    def resolve_zpl(self, command):
        if "zpl_file" in command:
            try:
                with open(command["zpl_file"], "rb") as handle:
                    return handle.read()
            except OSError as ex:
                self.events.emit(event="error", detail="cannot read zpl: %s" % ex)
                return None

        if "zpl_b64" in command:
            return base64.b64decode(command["zpl_b64"])

        if "zpl" in command:
            return command["zpl"].encode("utf-8")

        return b"^XA^FO50,50^A0N,40^FDfake-rock^FS^XZ"

    def do_ping(self, command):
        connection = self.wait_for_connection(command.get("timeout_ms", 30000))

        if connection is None:
            self.events.emit(event="error", detail="no proxy connected")
            return

        message_id = str(uuid.uuid4())
        slot = connection.expect(message_id)

        try:
            connection.send_message({
                "Type": MSG_PING,
                "Id": message_id,
                "SentAt": datetime.now(timezone.utc).isoformat(),
            })
        except OSError as ex:
            self.events.emit(event="error", detail="ping failed: %s" % ex)
            return

        if command.get("wait", False):
            # The answer arrives on the reader thread, so without this a ping
            # followed immediately by quit closes the socket before the pong
            # lands and looks like a protocol failure when it is only a race.
            slot["event"].wait()

    def ping_loop(self):
        """Mimics Rock's own monitor loop, which pings every ten seconds."""

        while not self._stop.wait(self.args.ping_interval):
            if self.connection is not None:
                self.do_ping({"timeout_ms": 1000})

    def dispatch(self, command):
        name = command.get("cmd")

        if name == "print":
            self.do_print(command)
        elif name == "ping":
            self.do_ping(command)
        elif name == "close":
            if self.connection:
                self.connection.close()
        elif name == "quit":
            self._stop.set()
        else:
            self.events.emit(event="error", detail="unknown command %r" % name)

    def run(self):
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((self.args.host, self.args.port))
        listener.listen(8)
        listener.settimeout(0.25)

        self.events.emit(event="listening", port=self.args.port, pid=os.getpid())

        threading.Thread(target=self.accept_loop, args=(listener,),
                         daemon=True).start()

        if self.args.ping_interval > 0:
            threading.Thread(target=self.ping_loop, daemon=True).start()

        try:
            if self.args.command:
                for raw in self.args.command:
                    self.dispatch(json.loads(raw))

            if not self.args.no_stdin:
                for line in sys.stdin:
                    line = line.strip()

                    if not line or line.startswith("#"):
                        continue

                    try:
                        command = json.loads(line)
                    except ValueError as ex:
                        self.events.emit(event="error", detail="bad command: %s" % ex)
                        continue

                    self.dispatch(command)

                    if self._stop.is_set():
                        break
            else:
                while not self._stop.wait(0.25):
                    pass
        except KeyboardInterrupt:
            pass
        finally:
            self._stop.set()

            if self.connection:
                self.connection.close()

            listener.close()
            self.events.emit(event="stopped")


def main():
    parser = argparse.ArgumentParser(description="A fake Rock cloud print server.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=19100)
    parser.add_argument("--events", default=None,
                        help="Also append the event stream to this file.")
    parser.add_argument("--ping-interval", type=float, default=10.0,
                        help="Seconds between pings, as Rock does. 0 disables.")
    parser.add_argument("--command", action="append", default=[],
                        help="A JSON command to run at startup. Repeatable.")
    parser.add_argument("--no-stdin", action="store_true",
                        help="Do not read commands from stdin; run until killed.")
    args = parser.parse_args()

    FakeRock(args, Events(args.events)).run()


if __name__ == "__main__":
    sys.exit(main())
