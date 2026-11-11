import http.client
import json
import os
import socket
import tempfile
import unittest

from sellerdesk import dashboard
from sellerdesk.ledger import Ledger
from sellerdesk.market import empty_snapshot


class FakeEngine:
    snapshot = {"orders": []}

    def __init__(self):
        self.acked = []

    def acknowledge(self, order_id):
        self.acked.append(order_id)

    def go_online(self):
        pass


class FakeHub:
    def snapshot(self):
        snap = empty_snapshot()
        snap["standings"] = {"o1": {"Total": 3, "PricePlace": 1}}
        return snap


class Dashboard(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.ledger = Ledger(os.path.join(self.dir.name, "ledger.json"))
        self.engine = FakeEngine()
        self.server = dashboard.start(self.engine, "127.0.0.1", 0, "sekrit", market=FakeHub(), ledger=self.ledger)
        self.port = self.server.server_address[1]

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.dir.cleanup()

    def call(self, method, path, body=None, token="sekrit", manager=True, raw=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        headers = dict(headers or {})
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if manager:
            headers["X-Manager"] = "1"
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        if data is not None:
            headers["Content-Type"] = "application/json"
        connection.request(method, path, body=data, headers=headers)
        response = connection.getresponse()
        reply = json.loads(response.read() or b"null")
        connection.close()
        return response.status, reply

    def test_auth(self):
        self.assertEqual(self.call("GET", "/api/market", token="")[0], 401)
        self.assertEqual(self.call("GET", "/api/ledger", token="wrong")[0], 401)
        self.assertEqual(self.call("POST", "/api/ledger/people", {"name": "Ann"}, manager=False)[0], 401)
        self.assertEqual(self.call("DELETE", "/api/ledger/payments/x", token="")[0], 401)
        self.assertEqual(self.call("GET", "/healthz", token="")[0], 200)
        self.assertEqual(self.call("PUT", "/api/ledger/nothing", {})[0], 404)

    def test_market(self):
        status, reply = self.call("GET", "/api/market")
        self.assertEqual(status, 200)
        self.assertEqual(set(reply), {"watched", "standings", "checked", "siteChecked", "problem"})
        self.assertEqual(set(reply["watched"]), {"Started", "Last", "Sweeps", "Seen", "Total", "Items"})
        self.assertEqual(reply["standings"]["o1"]["PricePlace"], 1)

    def test_ledger_routes(self):
        status, person = self.call("POST", "/api/ledger/people", {"name": "Ann", "note": "n"})
        self.assertEqual(status, 200)
        self.assertEqual(person["revision"], 1)
        pid = person["id"]

        status, person = self.call("PUT", f"/api/ledger/people/{pid}", {"note": "changed"})
        self.assertEqual((status, person["note"], person["revision"]), (200, "changed", 2))

        body = {"title": "Rifle", "placed": "2026-11-01T10:00:00Z", "quantity": 2, "total": 10.0,
                "currency": "USD", "category": "CustomItem", "shares": [{"person": pid, "units": 2}]}
        status, stored = self.call("PUT", "/api/ledger/orders/ord-1", body)
        self.assertEqual(status, 200)
        self.assertEqual(stored["net"], 8.5)
        self.assertEqual(stored["revision"], 3)

        status, payment = self.call("POST", "/api/ledger/payments",
                                    {"person": pid, "amount": 3, "date": "2026-11-02", "note": ""})
        self.assertEqual((status, payment["amount"], payment["revision"]), (200, 3, 4))

        status, ledger = self.call("GET", "/api/ledger")
        self.assertEqual(status, 200)
        self.assertEqual(set(ledger), {"revision", "people", "orders", "payments", "summary", "feeRates", "costs", "buyers"})
        self.assertEqual(ledger["summary"][0]["balance"], 5.5)

        status, cost = self.call("PUT", "/api/ledger/costs/offer-9", {"cost": 0.4, "title": "Seeds"})
        self.assertEqual((status, cost["cost"]), (200, 0.4))
        status, note = self.call("PUT", "/api/ledger/buyers/Night%20Owl", {"note": "slow to reply"})
        self.assertEqual((status, note["name"]), (200, "Night Owl"))
        self.assertEqual(self.call("PUT", "/api/ledger/costs/offer-9", {"cost": -2})[0], 400)
        snap = self.call("GET", "/api/ledger")[1]
        self.assertEqual((snap["costs"]["offer-9"]["cost"], snap["buyers"]["night owl"]["note"]), (0.4, "slow to reply"))
        status, reply = self.call("DELETE", f"/api/ledger/payments/{payment['id']}")
        self.assertEqual((status, reply["revision"]), (200, 7))
        self.assertEqual(self.call("DELETE", f"/api/ledger/payments/{payment['id']}")[0], 404)

    def test_bad_requests(self):
        status, reply = self.call("POST", "/api/ledger/payments", {"person": "ghost", "amount": 1, "date": "2026-11-02"})
        self.assertEqual(status, 400)
        self.assertIn("error", reply)
        self.assertEqual(self.call("POST", "/api/ledger/people", raw=b"{not json")[0], 400)
        self.assertEqual(self.call("POST", "/api/ledger/people", raw=b"x" * (64 * 1024 + 1))[0], 413)
        self.assertEqual(self.call("PUT", "/api/ledger/people/nobody", {"name": "x"})[0], 404)

    def raw(self, request: bytes) -> bytes:
        with socket.create_connection(("127.0.0.1", self.port), timeout=5) as connection:
            connection.sendall(request)
            reply = b""
            while chunk := connection.recv(4096):
                reply += chunk
        return reply

    def test_body_without_length_never_deletes(self):
        pid = self.call("POST", "/api/ledger/people", {"name": "Ann"})[1]["id"]
        body = {"title": "Rifle", "placed": "2026-11-01T10:00:00Z", "quantity": 1, "total": 10.0,
                "category": "CustomItem", "shares": [{"person": pid, "units": 1}]}
        self.assertEqual(self.call("PUT", "/api/ledger/orders/o1", body)[0], 200)
        head = b"Authorization: Bearer sekrit\r\nX-Manager: 1\r\nConnection: close\r\n"
        chunked = (b"PUT /api/ledger/orders/o1 HTTP/1.1\r\nHost: x\r\n" + head
                   + b"Transfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n")
        self.assertIn(b" 400 ", self.raw(chunked).split(b"\r\n")[0])
        bare = b"PUT /api/ledger/orders/o1 HTTP/1.1\r\nHost: x\r\n" + head + b"\r\n"
        self.assertIn(b" 411 ", self.raw(bare).split(b"\r\n")[0])
        self.assertIn("o1", self.ledger.snapshot()["orders"])

    def test_if_match(self):
        pid = self.call("POST", "/api/ledger/people", {"name": "Ann"})[1]["id"]
        body = {"title": "Rifle", "placed": "2026-11-01T10:00:00Z", "quantity": 2, "total": 10.0,
                "category": "CustomItem", "shares": [{"person": pid, "units": 1}]}
        status, stored = self.call("PUT", "/api/ledger/orders/o1", body, headers={"If-Match": '"0"'})
        self.assertEqual(status, 200)
        seen = stored["changed"]
        self.assertEqual(self.call("PUT", "/api/ledger/orders/o1", body, headers={"If-Match": f'"{seen}"'})[0], 200)
        status, reply = self.call("PUT", "/api/ledger/orders/o1", body, headers={"If-Match": f'"{seen}"'})
        self.assertEqual(status, 409)
        self.assertIn("another PC", reply["error"])
        self.assertEqual(self.call("PUT", "/api/ledger/orders/o1", body, headers={"If-Match": "abc"})[0], 400)

    def test_non_ascii_token_is_refused_cleanly(self):
        request = ("GET /api/ledger HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer sékrit\r\n"
                   "Connection: close\r\n\r\n").encode("latin-1")
        self.assertIn(b" 401 ", self.raw(request).split(b"\r\n")[0])

    def test_empty_token_keeps_market_and_finance_closed(self):
        server = dashboard.start(self.engine, "127.0.0.1", 0, "", market=FakeHub(), ledger=self.ledger)
        try:
            port, self.port = self.port, server.server_address[1]
            self.assertEqual(self.call("GET", "/api/market", token="")[0], 403)
            self.assertEqual(self.call("GET", "/api/ledger", token="")[0], 403)
            self.assertEqual(self.call("POST", "/api/ledger/people", {"name": "Eve"}, token="")[0], 403)
            self.assertEqual(self.call("DELETE", "/api/ledger/payments/x", token="")[0], 403)
            self.assertEqual(self.ledger.snapshot()["people"], [])
            # The local page keeps working without a token, as it did before.
            self.assertEqual(self.call("GET", "/api/state", token="")[0], 200)
            self.assertEqual(self.call("POST", "/api/ack/z", token="")[0], 202)
        finally:
            self.port = port
            server.shutdown()
            server.server_close()

    def test_handler_has_a_timeout(self):
        self.assertGreater(self.server.RequestHandlerClass.timeout or 0, 0)

    def test_existing_routes_still_work(self):
        self.assertEqual(self.call("POST", "/api/ack/abc")[0], 202)
        self.assertEqual(self.engine.acked, ["abc"])
        self.assertEqual(self.call("GET", "/api/state")[1], {"orders": []})


if __name__ == "__main__":
    unittest.main()
