"""Local web dashboard: serves one page and a small JSON API from the engine's snapshot."""

from __future__ import annotations

import hmac
import json
import logging
import mimetypes
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

log = logging.getLogger("siren")
STATIC = Path(__file__).parent / "static"
mimetypes.add_type("font/woff2", ".woff2")


def make_handler(engine, token: str):
    class Handler(BaseHTTPRequestHandler):
        server_version = "Siren"

        def log_message(self, *args):
            pass

        def _send(self, status, body=b"", content_type="application/json"):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(body)

        def _authorized(self) -> bool:
            if not token:
                return True
            return hmac.compare_digest(self.headers.get("Authorization", ""), f"Bearer {token}")

        def do_GET(self):
            path = self.path.split("?")[0]
            if path == "/healthz":
                self._send(200, b'{"ok":true}')
            elif path == "/api/state":
                if self._authorized():
                    self._send(200, json.dumps(engine.snapshot).encode())
                else:
                    self._send(401, b'{"error":"token required"}')
            else:
                self._static("index.html" if path == "/" else path.rsplit("/", 1)[-1])

        def _static(self, name):
            target = STATIC / name
            if name.startswith(".") or not target.is_file():
                self._send(404, b'{"error":"not found"}')
                return
            content_type = mimetypes.guess_type(name)[0] or "application/octet-stream"
            self._send(200, target.read_bytes(), content_type)

        def do_POST(self):
            # The custom header forces a CORS preflight, so other websites can't post here.
            if not self._authorized() or self.headers.get("X-Siren") != "1":
                self._send(401, b'{"error":"token required"}')
                return
            ack = re.fullmatch(r"/api/ack/([\w-]+)", self.path)
            if ack:
                engine.acknowledge(ack.group(1))
            elif self.path == "/api/online":
                engine.go_online()
            else:
                self._send(404, b'{"error":"not found"}')
                return
            self._send(202, b'{"ok":true}')

    return Handler


def start(engine, host: str, port: int, token: str) -> ThreadingHTTPServer:
    server = ThreadingHTTPServer((host, port), make_handler(engine, token))
    threading.Thread(target=server.serve_forever, daemon=True, name="dashboard").start()
    if not token:
        log.warning("DASHBOARD_TOKEN is empty: keep port %s bound to localhost", port)
    log.info("dashboard on http://%s:%s", host, port)
    return server
