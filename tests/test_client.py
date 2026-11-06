import json
import unittest

from siren.channels import Alert, Discord, Ntfy, Pushover, Telegram, Webhook, build_channels
from siren.config import Settings
from siren.eldorado import EldoradoClient, Notice, Order
from siren.net import HttpError

RAW_ORDER = {
    "id": "abc", "purchaseQuantity": 2, "buyerUsername": "bob",
    "totalPrice": {"amount": 9.5, "currency": "USD"},
    "createdDate": "2026-11-02T01:32:33.0344630Z",
    "deliveryStartedDate": "2026-11-02T01:32:33.6445575Z",
    "orderOfferDetails": {"offerTitle": "1000 Robux", "guaranteedDeliveryTime": "Minute20"},
}


class Wire:
    """Records calls and replays canned answers."""

    def __init__(self, *answers):
        self.answers = list(answers)
        self.calls = []

    def __call__(self, method, url, **kwargs):
        self.calls.append((method, url, kwargs))
        answer = self.answers.pop(0) if self.answers else ""
        if isinstance(answer, Exception):
            raise answer
        return answer


TOKEN = json.dumps({"accessToken": "AT", "expiresIn": 900, "tokenType": "Bearer"})


class Client(unittest.TestCase):
    def client(self, wire):
        return EldoradoClient("https://e", "id", "secret", transport=wire, clock=lambda: 1_000_000.0)

    def test_order_model(self):
        order = Order.from_api(RAW_ORDER, "https://e")
        self.assertEqual(order.url, "https://e/order/abc")
        self.assertEqual(order.window, 1200)
        self.assertEqual(order.deadline, order.started_at + 1200)
        self.assertEqual(order.summary, "1000 Robux, x2, 9.50 USD, buyer bob")

    def test_unknown_guarantee_has_no_deadline(self):
        raw = dict(RAW_ORDER, orderOfferDetails={"guaranteedDeliveryTime": "NotApplicable"})
        self.assertIsNone(Order.from_api(raw, "https://e").deadline)

    def test_token_is_cached(self):
        wire = Wire(TOKEN, '{"results":[]}', '{"results":[]}')
        client = self.client(wire)
        client.orders("Paid")
        client.orders("Disputed")
        self.assertEqual(sum("token" in call[1] for call in wire.calls), 1)

    def test_retries_once_on_401(self):
        wire = Wire(TOKEN, HttpError(401, "u", ""), TOKEN, json.dumps({"results": [RAW_ORDER]}))
        self.assertEqual(len(self.client(wire).orders("Paid")), 1)

    def test_sales_csv(self):
        csv = chr(0xFEFF) + "Order Date,Description,Amount $,Seller Fees $,Net Income $,Category\r\n" \
              "2026-11-03 22:50:11,Coins,11.80,0.59,11.21,Currency\r\n"
        rows = self.client(Wire(TOKEN, csv)).sales_since(86400)
        self.assertEqual(rows[0][1], 11.21)

    def test_notice_shapes(self):
        custom = Notice.from_api({"customNotification": {"id": "1", "subject": "Hi", "text": "t"}})
        plain = Notice.from_api({"notification": {"id": "2", "event": "OrderReceived", "details": {"title": "Gold"}}})
        self.assertEqual((custom.title, plain.event, plain.text), ("Hi", "OrderReceived", "Gold"))
        self.assertIsNone(Notice.from_api({}))


ALERT = Alert("order", 5, "New order", "Gold", "https://e/order/1", "o1")


class Channels(unittest.TestCase):
    def test_ntfy_button_publishes_to_ack_topic(self):
        wire = Wire()
        Ntfy("http://ntfy", "https://public", "topic", "tk", transport=wire).send(ALERT)
        body = wire.calls[0][2]["json_body"]
        action = body["actions"][0]
        self.assertEqual((body["priority"], action["url"], action["body"]), (5, "https://public/topic-ack", "o1"))

    def test_ntfy_opens_the_order_from_a_button_only(self):
        wire = Wire()
        Ntfy("http://ntfy", "https://public", "topic", "tk", transport=wire).send(ALERT)
        body = wire.calls[0][2]["json_body"]
        self.assertNotIn("click", body)
        self.assertEqual(body["actions"][1]["url"], "https://e/order/1")

    def test_ntfy_carries_the_countdown_for_the_windows_app(self):
        wire = Wire()
        alert = Alert("order", 5, "New order", "Gold", None, "o1", deadline=1000.0, window=1200.0)
        Ntfy("http://ntfy", "https://public", "topic", "", transport=wire).send(alert)
        headers = wire.calls[0][2]["json_body"]["actions"][0]["headers"]
        self.assertEqual((headers["X-Siren-Deadline"], headers["X-Siren-Window"]), ("1000.0", "1200.0"))

    def test_ntfy_reads_acks(self):
        line = json.dumps({"id": "m1", "event": "message", "message": "o1"})
        channel = Ntfy("http://ntfy", "", "topic", "", transport=Wire(line))
        self.assertEqual(channel.poll_acks(), {"o1"})
        self.assertEqual(channel.since, "m1")

    def test_telegram_ignores_strangers(self):
        def tap(update_id, chat):
            return {"update_id": update_id, "callback_query": {"id": "c", "data": "ack:o1",
                                                               "message": {"chat": {"id": chat}}}}
        channel = Telegram("t", 42, transport=Wire(json.dumps({"result": [tap(7, 99)]})))
        self.assertEqual(channel.poll_acks(), set())
        self.assertEqual(channel.offset, 8)
        channel = Telegram("t", 42, transport=Wire(json.dumps({"result": [tap(7, 42)]})))
        self.assertEqual(channel.poll_acks(), {"o1"})

    def test_discord_pings_on_top_level(self):
        wire = Wire()
        Discord("https://hook", transport=wire).send(ALERT)
        self.assertEqual(wire.calls[0][2]["json_body"]["content"], "@here")

    def test_pushover_uses_siren_sound(self):
        wire = Wire()
        Pushover("app", "user", transport=wire).send(ALERT)
        self.assertIn("sound=siren", wire.calls[0][2]["data"])

    def test_only_configured_channels_load(self):
        channels = build_channels(Settings(ntfy_url="http://n", discord_webhook_url="https://d"))
        self.assertEqual([c.name for c in channels], ["ntfy", "discord"])


class Env(unittest.TestCase):
    def test_public_url_falls_back_to_ntfy_url(self):
        settings = Settings.from_env({"NTFY_URL": "https://n/", "POLL_SECONDS": "15", "DEADMAN_ENABLED": "yes"})
        self.assertEqual((settings.ntfy_public_url, settings.poll_seconds, settings.deadman_enabled),
                         ("https://n", 15, True))



class WebhookChannel(unittest.TestCase):
    def sent(self, alert):
        calls = []
        channel = Webhook("http://hub", "key", transport=lambda *a, **k: calls.append(k.get("json_body")))
        channel.send(alert)
        return calls

    def test_a_new_order_rings_without_the_time_left(self):
        alert = Alert("order", 5, "New order: 19 min left", "item", "http://o", "o1", False, 2000.0, 1200, "New order")
        message = self.sent(alert)[0]
        self.assertEqual((message["title"], message["key"], message["ring"]), ("New order", "o1", False))

    def test_the_last_minutes_ring_again(self):
        alert = Alert("order", 5, "Deadline close: 3 min left", "item", None, "o1", False, 2000.0, 1200, "Deadline close")
        self.assertTrue(self.sent(alert)[0]["ring"])

    def test_skips_the_startup_note_and_the_hurry_stage(self):
        self.assertEqual(self.sent(Alert("system", 2, "Siren is on watch", "")), [])
        self.assertEqual(self.sent(Alert("order", 5, "x", "", None, "o1", False, 1.0, 1, "Deliver soon")), [])

    def test_a_notice_is_a_plain_notification(self):
        self.assertEqual(self.sent(Alert("notice", 3, "Review", "text"))[0]["style"], "notify")

    def test_closing_an_order_resolves_it(self):
        calls = []
        Webhook("http://hub", "key", transport=lambda *a, **k: calls.append(k.get("json_body"))).closed("o1")
        self.assertEqual(calls, [{"key": "o1", "resolved": True}])


if __name__ == "__main__":
    unittest.main()
