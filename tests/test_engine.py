import unittest

from siren.channels import Alert, Channel
from siren.config import Settings
from siren.deadline import Stage, parse_time, repeat_interval, stage_for
from siren.eldorado import Notice, Order
from siren.engine import Engine


class FakeClock:
    def __init__(self):
        self.now = 1_000_000.0

    def __call__(self):
        return self.now


class FakeApi:
    def __init__(self):
        self.paid, self.disputed, self.notices = [], [], []
        self.offline_calls = 0

    def orders(self, state):
        return self.paid if state == "Paid" else self.disputed

    def unread_notices(self):
        return self.notices

    def sales_since(self, seconds_ago):
        return []

    def switch_offline(self):
        self.offline_calls += 1

    def switch_online(self):
        pass


class Recorder(Channel):
    name = "recorder"

    def __init__(self):
        self.sent = []
        self.acks = set()

    def send(self, alert: Alert):
        self.sent.append(alert)

    def poll_acks(self):
        acks, self.acks = self.acks, set()
        return acks

    @property
    def titles(self):
        return [alert.title.split(":")[0] for alert in self.sent]


class ChatRoom(Recorder):
    name = "room"
    wants_repeats = False


class Broken(Channel):
    name = "broken"

    def send(self, alert):
        raise OSError("network down")


def order(clock, window=1200, used=0.0, order_id="o1"):
    return Order(order_id, "Gold", 1, 9.5, "USD", "bob", f"https://x/order/{order_id}",
                 clock.now - window * used, window)


class Harness(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.api = FakeApi()
        self.channel = Recorder()
        self.engine = self.build()

    def build(self, *channels, **settings):
        return Engine(self.api, list(channels) or [self.channel], Settings(**settings), clock=self.clock)

    def advance(self, seconds):
        self.clock.now += seconds
        self.engine.tick()


class Deadlines(unittest.TestCase):
    def test_parses_seven_fraction_digits(self):
        self.assertEqual(parse_time("2026-11-02T01:32:33.0344630Z"), parse_time("2026-11-02T01:32:33.034463Z"))
        self.assertIsNone(parse_time(None))

    def test_stages_follow_the_share_of_the_window_left(self):
        self.assertEqual(stage_for(900, 1000), Stage.CALM)
        self.assertEqual(stage_for(500, 1000), Stage.HURRY)
        self.assertEqual(stage_for(200, 1000), Stage.CRITICAL)
        self.assertEqual(stage_for(-1, 1000), Stage.LATE)
        self.assertEqual(stage_for(None, None), Stage.CALM)

    def test_repeats_speed_up(self):
        calm, hurry, critical = (repeat_interval(s, 60) for s in (Stage.CALM, Stage.HURRY, Stage.CRITICAL))
        self.assertGreater(calm, hurry)
        self.assertGreater(hurry, critical)


class Alarms(Harness):
    def test_new_order_alarms_at_top_level(self):
        self.api.paid = [order(self.clock)]
        self.engine.tick()
        self.assertEqual(self.channel.titles, ["New order"])
        self.assertEqual(self.channel.sent[0].level, 5)
        self.assertIn("left", self.channel.sent[0].title)

    def test_repeats_until_order_leaves_paid(self):
        self.api.paid = [order(self.clock)]
        self.engine.tick()
        self.advance(60)
        self.assertEqual(len(self.channel.sent), 2)
        self.api.paid = []
        self.advance(60)
        self.advance(60)
        self.assertEqual(len(self.channel.sent), 2)
        self.assertEqual(self.engine.state["alarms"], {})

    def test_ack_silences_then_alarm_returns_when_critical(self):
        self.api.paid = [order(self.clock)]
        self.engine.tick()
        self.channel.acks = {"o1"}
        self.advance(60)
        self.advance(60)
        self.assertEqual(len(self.channel.sent), 1)
        self.advance(1200 * 0.8 - 120)  # 20% of the window left: critical
        self.assertEqual(self.channel.titles[-1], "Deadline close")

    def test_stage_change_fires_at_once_and_says_so(self):
        self.api.paid = [order(self.clock)]
        self.engine.tick()
        self.advance(600)
        self.assertEqual(self.channel.titles[-1], "Deliver soon")
        self.assertFalse(self.channel.sent[-1].repeat)

    def test_budget_stops_repeats_on_long_orders(self):
        self.api.paid = [order(self.clock, window=7 * 86400)]
        self.engine.tick()
        for _ in range(40):
            self.advance(60)
        count = len(self.channel.sent)
        self.assertLess(count, 33)
        self.advance(60)
        self.assertEqual(len(self.channel.sent), count)

    def test_late_order_says_so(self):
        self.api.paid = [order(self.clock, used=0.9)]
        self.engine.tick()
        self.advance(200)
        self.assertEqual(self.channel.titles[-1], "Order is late")
        self.assertIn("over", self.channel.sent[-1].title)


class Delivery(Harness):
    def test_chat_rooms_skip_repeats(self):
        room = ChatRoom()
        self.engine = self.build(self.channel, room)
        self.api.paid = [order(self.clock)]
        self.engine.tick()
        self.advance(60)
        self.assertEqual(len(self.channel.sent), 2)
        self.assertEqual(len(room.sent), 1)

    def test_broken_channel_does_not_silence_others(self):
        self.engine = self.build(Broken(), self.channel)
        self.api.paid = [order(self.clock)]
        self.engine.tick()
        self.assertEqual(len(self.channel.sent), 1)
        health = {c["name"]: c for c in self.engine.snapshot["channels"]}
        self.assertFalse(health["broken"]["ok"])

    def test_dispute_alerts_once(self):
        self.api.disputed = [order(self.clock, order_id="d1")]
        self.engine.tick()
        self.advance(60)
        self.assertEqual(self.channel.titles, ["Order disputed"])

    def test_notices_skip_backlog_and_covered_events(self):
        self.api.notices = [Notice("n1", "Custom", "Old", "", None)]
        self.engine.tick()
        self.assertEqual(self.channel.sent, [])
        self.api.notices += [Notice("n2", "Custom", "Fresh", "hello", None),
                             Notice("n3", "OrderCreated", "OrderCreated", "", None)]
        self.advance(30)
        self.assertEqual(self.channel.titles, ["Fresh"])


class DeadMan(Harness):
    def setUp(self):
        super().setUp()
        self.engine = self.build(deadman_enabled=True, deadman_minutes=10)
        self.api.paid = [order(self.clock, window=86400)]

    def test_off_by_default(self):
        self.engine = self.build()
        self.engine.tick()
        self.advance(3600)
        self.assertEqual(self.api.offline_calls, 0)

    def test_trips_once_after_unanswered_alarm(self):
        self.engine.tick()
        self.advance(599)
        self.assertEqual(self.api.offline_calls, 0)
        self.advance(2)
        self.advance(60)
        self.assertEqual(self.api.offline_calls, 1)
        self.assertTrue(self.engine.snapshot["deadman"]["tripped"])

    def test_ack_disarms_it(self):
        self.engine.tick()
        self.channel.acks = {"o1"}
        self.advance(30)
        self.advance(900)
        self.assertEqual(self.api.offline_calls, 0)


class Snapshot(Harness):
    def test_orders_sort_by_deadline(self):
        self.api.paid = [order(self.clock, window=7200, order_id="later"),
                         order(self.clock, window=600, order_id="sooner")]
        self.engine.tick()
        self.assertEqual([o["id"] for o in self.engine.snapshot["orders"]], ["sooner", "later"])


if __name__ == "__main__":
    unittest.main()
