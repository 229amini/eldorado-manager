"""Local web dashboard: serves one page and a small JSON API from the engine's snapshot.

Also serves the market hub (GET /api/market) and the finance ledger (/api/ledger...) to the desktop app.
"""

from __future__ import annotations

import hmac
import json
import logging
import mimetypes
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from .ledger import Conflict, NotFound
from .market import empty_snapshot

log = logging.getLogger("sellerdesk")
STATIC = Path(__file__).parent / "static"
MAX_BODY = 64 * 1024
REQUEST_TIMEOUT = 30  # seconds a client may take to send its request; a stalled one would hold a thread
NEEDS_TOKEN = b'{"error":"set DASHBOARD_TOKEN on the server to share market and finance data"}'
mimetypes.add_type("font/woff2", ".woff2")


class BadRequest(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def make_handler(engine, token: str, market=None, ledger=None):
    class Handler(BaseHTTPRequestHandler):
        server_version = "SellerDesk"
        timeout = REQUEST_TIMEOUT

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

        def _json(self, status, value):
            self._send(status, json.dumps(value).encode())

        def _error(self, status, message):
            self._json(status, {"error": message})

        def _authorized(self) -> bool:
            if not token:
                return True
            # Compared as bytes: compare_digest refuses str with non-ASCII characters.
            sent = self.headers.get("Authorization", "").encode("utf-8", "surrogateescape")
            return hmac.compare_digest(sent, f"Bearer {token}".encode("utf-8"))

        def _shared(self) -> bool:
            """Market and finance data never go out without a token, even when the page itself runs open."""
            if not token:
                self._send(403, NEEDS_TOKEN)
                return False
            return True

        def _may_change(self) -> bool:
            # The custom header forces a CORS preflight, so other websites can't post here.
            if not self._authorized() or self.headers.get("X-Manager") != "1":
                self._send(401, b'{"error":"token required"}')
                return False
            return True

        def _expect(self):
            """The revision from If-Match ("12", W/"12" or 12), or None when the header is absent."""
            value = self.headers.get("If-Match")
            if value is None:
                return None
            value = value.strip()
            if value.startswith("W/"):
                value = value[2:]
            value = value.strip('"')
            if not value.isdigit() or len(value) > 18:
                raise BadRequest(400, "If-Match must be a revision number")
            return int(value)

        @property
        def _path(self) -> str:
            return self.path.split("?")[0]

        def _body(self) -> dict:
            # http.server can't read a chunked body; reading it as empty once turned a PUT into a delete.
            if self.headers.get("Transfer-Encoding"):
                self.close_connection = True
                raise BadRequest(400, "chunked bodies aren't supported; send Content-Length")
            if self.headers.get("Content-Length") is None:
                self.close_connection = True
                raise BadRequest(411, "Content-Length is required")
            try:
                length = int(self.headers["Content-Length"])
            except ValueError:
                length = -1
            if length < 0:
                self.close_connection = True
                raise BadRequest(400, "bad Content-Length")
            if length > MAX_BODY:
                self.close_connection = True
                raise BadRequest(413, "request body is over 64 KB")
            try:
                raw = self.rfile.read(length) if length > 0 else b""
            except OSError:  # includes the socket timeout
                self.close_connection = True
                raise BadRequest(408, "the body took too long") from None
            if len(raw) < length:
                raise BadRequest(400, "the body ended early")
            if not raw.strip():
                return {}
            try:
                value = json.loads(raw)
            except ValueError:
                raise BadRequest(400, "body must be JSON") from None
            if not isinstance(value, dict):
                raise BadRequest(400, "body must be a JSON object")
            return value

        def do_GET(self):
            path = self._path
            if path == "/healthz":
                self._send(200, b'{"ok":true}')
            elif path in ("/api/state", "/api/market", "/api/ledger"):
                if path != "/api/state" and not self._shared():
                    return
                if not self._authorized():
                    self._send(401, b'{"error":"token required"}')
                elif path == "/api/state":
                    self._send(200, json.dumps(engine.snapshot).encode())
                elif path == "/api/market":
                    self._json(200, market.snapshot() if market else
                               empty_snapshot("Market watch is turned off on the server."))
                elif ledger is None:
                    self._error(404, "not found")
                else:
                    self._json(200, ledger.snapshot())
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
            if not self._may_change():
                return
            path = self._path
            ack = re.fullmatch(r"/api/ack/([\w-]+)", path)
            if ack:
                engine.acknowledge(ack.group(1))
            elif path == "/api/online":
                engine.go_online()
            elif path == "/api/ledger/people":
                self._ledger(lambda body: ledger.add_person(body.get("name"), body.get("note")))
                return
            elif path == "/api/ledger/payments":
                self._ledger(lambda body: ledger.add_payment(body))
                return
            else:
                self._send(404, b'{"error":"not found"}')
                return
            self._send(202, b'{"ok":true}')

        def do_PUT(self):
            if not self._may_change():
                return
            person = re.fullmatch(r"/api/ledger/people/([\w-]+)", self._path)
            order = re.fullmatch(r"/api/ledger/orders/([\w-]+)", self._path)
            if person:
                self._ledger(lambda body: ledger.update_person(person.group(1), body, self._expect()))
            elif order:
                self._ledger(lambda body: ledger.put_order(order.group(1), body, self._expect()))
            else:
                self._send(404, b'{"error":"not found"}')

        def do_DELETE(self):
            if not self._may_change():
                return
            payment = re.fullmatch(r"/api/ledger/payments/([\w-]+)", self._path)
            if payment:
                self._ledger(lambda body: (None, ledger.delete_payment(payment.group(1))), body=False)
            else:
                self._send(404, b'{"error":"not found"}')

        def _ledger(self, change, body=True):
            """Run one ledger change and answer with the stored object plus the new revision."""
            if not self._shared():
                return
            if ledger is None:
                self._error(404, "not found")
                return
            try:
                result, revision = change(self._body() if body else {})
            except BadRequest as error:
                self._error(error.status, str(error))
            except NotFound as error:
                self._error(404, str(error))
            except Conflict as error:
                self._error(409, str(error))
            except ValueError as error:
                self._error(400, str(error))
            except Exception as error:
                log.exception("ledger change failed: %s", error)
                self._error(500, "the ledger could not be saved")
            else:
                reply = dict(result) if result is not None else {"ok": True}
                reply["revision"] = revision
                self._json(200, reply)

    return Handler


def start(engine, host: str, port: int, token: str, market=None, ledger=None) -> ThreadingHTTPServer:
    server = ThreadingHTTPServer((host, port), make_handler(engine, token, market, ledger))
    threading.Thread(target=server.serve_forever, daemon=True, name="dashboard").start()
    if not token:
        log.warning("DASHBOARD_TOKEN is empty: keep port %s bound to localhost; the market and finance API "
                    "stay off until a token is set", port)
    log.info("dashboard on http://%s:%s", host, port)
    return server
