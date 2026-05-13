#!/usr/bin/env python3
"""Console client for the LR message server.

Protocol is compatible with MessageHeaderPaisev:
    int messageType, int sizeBytes, int to, int status, int auxId
Text payload is UTF-16LE, matching wchar_t strings used by the Windows server.
"""

import socket
import struct
import sys
import threading
from typing import List, Optional, Tuple

MT_SEND_TEXT = 1
MT_CONFIRM = 5
MT_DISCONNECT = 6
MT_REFRESH_THREADS = 7
MT_CLIENT_LIST = 8
TARGET_ALL_THREADS = 0
HEADER_FORMAT = "<iiiii"
HEADER_SIZE = struct.calcsize(HEADER_FORMAT)
MAX_PAYLOAD = 1024 * 1024


class ConsoleClient:
    def __init__(self, host: str, port: int) -> None:
        self.host = host
        self.port = port
        self.sock: Optional[socket.socket] = None
        self.send_lock = threading.Lock()
        self.running = threading.Event()
        self.active_clients: List[int] = []

    def connect(self) -> None:
        self.sock = socket.create_connection((self.host, self.port))
        self.running.set()
        threading.Thread(target=self.receive_loop, daemon=True).start()

    def close(self) -> None:
        self.running.clear()
        if self.sock is not None:
            try:
                self.send_message(MT_DISCONNECT, TARGET_ALL_THREADS, "")
            except OSError:
                pass
            try:
                self.sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.sock.close()
            self.sock = None

    def recv_exact(self, size: int) -> bytes:
        assert self.sock is not None
        chunks: List[bytes] = []
        remaining = size
        while remaining:
            chunk = self.sock.recv(remaining)
            if not chunk:
                raise ConnectionError("server closed connection")
            chunks.append(chunk)
            remaining -= len(chunk)
        return b"".join(chunks)

    def receive_message(self) -> Tuple[int, int, int, int, str]:
        header = self.recv_exact(HEADER_SIZE)
        message_type, size_bytes, to, status, aux_id = struct.unpack(HEADER_FORMAT, header)
        if size_bytes < 0 or size_bytes % 2 != 0 or size_bytes > MAX_PAYLOAD:
            raise ValueError(f"invalid payload size: {size_bytes}")
        payload = self.recv_exact(size_bytes) if size_bytes else b""
        return message_type, to, status, aux_id, payload.decode("utf-16le")

    def send_message(self, message_type: int, to: int, text: str) -> None:
        if self.sock is None:
            raise ConnectionError("client is not connected")
        payload = text.encode("utf-16le")
        header = struct.pack(HEADER_FORMAT, message_type, len(payload), to, 0, 0)
        with self.send_lock:
            self.sock.sendall(header)
            if payload:
                self.sock.sendall(payload)

    def receive_loop(self) -> None:
        while self.running.is_set():
            try:
                message_type, _to, status, aux_id, text = self.receive_message()
            except Exception as exc:
                if self.running.is_set():
                    print(f"\n[connection lost] {exc}")
                self.running.clear()
                return

            if message_type == MT_CLIENT_LIST:
                self.active_clients = [int(part) for part in text.split(",") if part.strip().isdigit()]
                print(f"\n[clients: {aux_id}] {', '.join(map(str, self.active_clients)) or '-'}")
            elif message_type == MT_SEND_TEXT:
                print(f"\n{text}")
            elif message_type == MT_CONFIRM:
                state = "ok" if status else "error"
                print(f"\n[{state}] {text}")
            else:
                print(f"\n[message {message_type}] {text}")

    def interactive_loop(self) -> None:
        print("Commands: /to <id|all>, /refresh, /quit")
        target = TARGET_ALL_THREADS
        while self.running.is_set():
            try:
                line = input(f"to {target if target else 'all'}> ").strip()
            except (EOFError, KeyboardInterrupt):
                print()
                break

            if not line:
                continue
            if line == "/quit":
                break
            if line == "/refresh":
                self.send_message(MT_REFRESH_THREADS, TARGET_ALL_THREADS, "")
                continue
            if line.startswith("/to "):
                value = line[4:].strip().lower()
                target = TARGET_ALL_THREADS if value == "all" else int(value)
                continue

            self.send_message(MT_SEND_TEXT, target, line)


def main() -> int:
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 54000
    client = ConsoleClient(host, port)
    try:
        client.connect()
        client.interactive_loop()
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
