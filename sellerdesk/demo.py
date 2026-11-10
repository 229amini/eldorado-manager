"""Demo mode: a fake seller account, so you can try SellerDesk without credentials."""

from __future__ import annotations

import logging
import time

from .channels import Alert, Channel
from .eldorado import Order

log = logging.getLogger("sellerdesk")

CATALOG = [
    ("13,500 V-Bucks top up", 1, 64.90, "nightowl_77", 20 * 60),
    ("Valorant Immortal account", 1, 118.00, "kestrel", 3600),
    ("OSRS gold 50M", 5, 42.50, "bluemoss", 5 * 3600),
    ("Roblox limited: Dominus", 1, 210.00, "tinkerfox", 86400),
]
# Share of the delivery window already used, per order, for each scene.
SCENES = {
    "quiet": [],
    "calm": [0.2, 0.1],
    "hurry": [0.6, 0.3, 0.1],
    "critical": [0.9, 0.55, 0.2, 0.1],
    "late": [1.04, 0.85, 0.3],
}


class ConsoleChannel(Channel):
    name = "console"

    def send(self, alert: Alert):
        log.info("ALERT level %s: %s | %s", alert.level, alert.title, alert.body)


class DemoApi:
    def __init__(self, scene: str, clock=time.time):
        self.clock = clock
        now = clock()
        self.open = []
        for index, used in enumerate(SCENES[scene]):
            title, quantity, price, buyer, window = CATALOG[index]
            self.open.append(Order(f"demo-{index}", title, quantity, price, "USD", buyer,
                                   f"https://www.eldorado.gg/order/demo-{index}", now - window * used, window))

    def orders(self, state):
        return self.open if state == "Paid" else []

    def unread_notices(self):
        return []

    def sales_since(self, seconds_ago):
        now = self.clock()
        return [(now - hours * 3600, net) for hours, net in
                [(1, 38.2), (3, 11.21), (5, 92.4), (9, 16.75), (20, 27.3), (30, 54.1), (70, 88.0), (120, 19.9)]]

    def switch_offline(self):
        log.info("demo: switched offline")

    def switch_online(self):
        log.info("demo: switched online")
