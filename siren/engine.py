"""The poll loop: turns API state into alerts and keeps the dashboard snapshot fresh."""

from __future__ import annotations

import json
import logging
import os
import queue
import threading
import time

from .channels import Alert, Channel
from .config import Settings
from .deadline import Stage, format_remaining, repeat_interval, stage_for
from .eldorado import Order

log = logging.getLogger("siren")

# The order polls already cover these two, so the notification feed skips them.
COVERED_EVENTS = {"OrderCreated", "OrderDisputed"}
SALES_REFRESH_SECONDS = 300
DAY = 86400


def load_state(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as file:
            return json.load(file)
    except FileNotFoundError:
        return {}


def save_state(path: str, state: dict) -> None:
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(f"{path}.tmp", "w", encoding="utf-8") as file:
        json.dump(state, file)
    os.replace(f"{path}.tmp", path)


class Engine:
    def __init__(self, api, channels: list[Channel], settings: Settings, state=None, clock=time.time):
        self.api = api
        self.channels = channels
        self.settings = settings
        self.clock = clock
        self.state = state if state is not None else {}
        self.state.setdefault("alarms", {})     # order id -> alarm record
        self.state.setdefault("disputes", [])
        self.state.setdefault("notices", [])
        self.commands: queue.SimpleQueue = queue.SimpleQueue()  # filled by the dashboard thread
        self.wake = threading.Event()
        self.health = {channel.name: {"ok": None, "error": ""} for channel in channels}
        self.recent: list[dict] = []
        self.sales = {"day": None, "week": None, "day_count": 0, "week_count": 0}
        self.sales_checked = 0.0
        self.open_orders: list[Order] = []
        self.last_poll = None
        self.last_error = ""
        self.snapshot: dict = self._build_snapshot()

    # ---- commands from the dashboard ----

    def acknowledge(self, order_id: str) -> None:
        self.commands.put(("ack", order_id))
        self.wake.set()

    def go_online(self) -> None:
        self.commands.put(("online", ""))
        self.wake.set()

    # ---- one poll ----

    def tick(self) -> None:
        now = self.clock()
        self._take_commands()
        self._take_acks()
        self._watch_orders(self.api.orders("Paid"), now)
        self._watch_disputes()
        self._watch_notices()
        self._watch_sales(now)
        self._deadman(now)
        self.state["started"] = True
        self.last_poll = now
        self.last_error = ""
        self.snapshot = self._build_snapshot()

    def _take_commands(self):
        while not self.commands.empty():
            command, value = self.commands.get()
            if command == "ack":
                self._ack(value)
            elif command == "online":
                self.api.switch_online()
                self.state["deadman_tripped"] = False
                self._dispatch(Alert("system", 3, "You are online", "Siren switched you back online."))

    def _take_acks(self):
        for channel in self.channels:
            try:
                for order_id in channel.poll_acks():
                    self._ack(order_id)
            except Exception as error:
                log.warning("%s ack poll failed: %s", channel.name, error)

    def _ack(self, order_id):
        alarm = self.state["alarms"].get(order_id)
        if alarm and alarm.get("acked_stage") is None:
            alarm["acked_stage"] = alarm["stage"]
            log.info("order %s acknowledged", order_id)

    def _watch_orders(self, orders: list[Order], now: float):
        settings, alarms = self.settings, self.state["alarms"]
        self.open_orders = orders
        for order in orders:
            remaining = None if order.deadline is None else order.deadline - now
            stage = stage_for(remaining, order.window, settings.hurry_at, settings.critical_at)
            alarm = alarms.get(order.id)
            if alarm is None:
                alarms[order.id] = {"first": now, "last": now, "stage": int(stage), "stage_since": now,
                                    "acked_stage": None}
                log.info("new order %s: %s", order.id, order.summary)
                self._dispatch(self._order_alert(order, "New order", remaining))
                continue

            advanced = stage > alarm["stage"]
            if advanced:
                alarm.update(stage=int(stage), stage_since=now)
            acked = alarm["acked_stage"]
            # An ack buys silence until the order turns critical, then the alarm comes back once.
            if acked is not None:
                if stage >= Stage.CRITICAL and stage > acked:
                    alarm["acked_stage"] = None
                else:
                    continue
            over_budget = now - alarm["stage_since"] >= settings.stage_budget_minutes * 60
            due = now - alarm["last"] >= repeat_interval(stage, settings.repeat_seconds)
            if advanced or (due and not over_budget):
                alarm["last"] = now
                self._dispatch(self._order_alert(order, self._headline(stage), remaining, repeat=not advanced))

        open_ids = {order.id for order in orders}
        for order_id in [known for known in alarms if known not in open_ids]:
            del alarms[order_id]
            self._closed(order_id)

    @staticmethod
    def _headline(stage: Stage) -> str:
        return {Stage.CALM: "Order waiting", Stage.HURRY: "Deliver soon",
                Stage.CRITICAL: "Deadline close", Stage.LATE: "Order is late"}[stage]

    @staticmethod
    def _order_alert(order: Order, headline: str, remaining, repeat=False) -> Alert:
        if remaining is None:
            title = headline
        elif remaining > 0:
            title = f"{headline}: {format_remaining(remaining)} left"
        else:
            title = f"{headline}: {format_remaining(remaining)} over"
        return Alert("order", 5, title, order.summary, order.url, order.id, repeat, order.deadline, order.window,
                     headline)

    def _watch_disputes(self):
        seen = set(self.state["disputes"])
        for order in self.api.orders("Disputed"):
            if order.id not in seen:
                seen.add(order.id)
                self._dispatch(Alert("dispute", 4, "Order disputed", order.summary, order.url))
        self.state["disputes"] = sorted(seen)[-1000:]

    def _watch_notices(self):
        first_run = not self.state.get("started")
        seen = set(self.state["notices"])
        for notice in self.api.unread_notices():
            if notice.id in seen:
                continue
            seen.add(notice.id)
            # The first poll records the backlog without pushing it to your phone.
            if not first_run and notice.event not in COVERED_EVENTS:
                self._dispatch(Alert("notice", 3, notice.title, notice.text or notice.event, notice.url))
        self.state["notices"] = sorted(seen)[-2000:]

    def _watch_sales(self, now):
        if now - self.sales_checked < SALES_REFRESH_SECONDS:
            return
        self.sales_checked = now
        try:
            rows = self.api.sales_since(7 * DAY)
        except Exception as error:  # a chart must not stop the alarm
            log.warning("sales refresh failed: %s", error)
            return
        day = [net for when, net in rows if now - when <= DAY]
        self.sales = {"day": round(sum(day), 2), "week": round(sum(net for _, net in rows), 2),
                      "day_count": len(day), "week_count": len(rows)}

    def _deadman(self, now):
        settings = self.settings
        if not settings.deadman_enabled or self.state.get("deadman_tripped"):
            return
        limit = settings.deadman_minutes * 60
        ignored = [a for a in self.state["alarms"].values()
                   if a["acked_stage"] is None and now - a["first"] >= limit]
        if ignored:
            self.api.switch_offline()
            self.state["deadman_tripped"] = True
            self._dispatch(Alert("system", 5, "Siren took you offline",
                                 f"An alarm went unanswered for {settings.deadman_minutes} minutes. "
                                 "Buyers can't place new orders until you go online again."))

    # ---- delivery ----

    def _closed(self, order_id):
        for channel in self.channels:
            try:
                channel.closed(order_id)
            except Exception as error:
                log.warning("%s close failed: %s", channel.name, error)

    def _dispatch(self, alert: Alert):
        self.recent = [{"at": self.clock(), "title": alert.title, "body": alert.body, "kind": alert.kind}] \
            + self.recent[:29]
        for channel in self.channels:
            if alert.repeat and not channel.wants_repeats:
                continue
            try:
                channel.send(alert)
                self.health[channel.name] = {"ok": True, "error": ""}
            except Exception as error:  # one broken channel must not silence the others
                self.health[channel.name] = {"ok": False, "error": str(error)[:200]}
                log.warning("%s send failed: %s", channel.name, error)

    def _build_snapshot(self) -> dict:
        alarms = self.state["alarms"]
        orders = []
        for order in self.open_orders:
            alarm = alarms.get(order.id, {})
            orders.append({"id": order.id, "title": order.title, "quantity": order.quantity,
                           "price": order.price, "currency": order.currency, "buyer": order.buyer,
                           "url": order.url, "deadline": order.deadline, "window": order.window,
                           "acked": alarm.get("acked_stage") is not None})
        orders.sort(key=lambda o: (o["deadline"] is None, o["deadline"] or 0))
        return {"now": self.clock(), "last_poll": self.last_poll, "last_error": self.last_error,
                "poll_seconds": self.settings.poll_seconds, "orders": orders, "sales": self.sales,
                "hurry_at": self.settings.hurry_at, "critical_at": self.settings.critical_at,
                "channels": [{"name": name, **status} for name, status in self.health.items()],
                "recent": self.recent[:8],
                "deadman": {"enabled": self.settings.deadman_enabled,
                            "minutes": self.settings.deadman_minutes,
                            "tripped": bool(self.state.get("deadman_tripped"))}}

    # ---- forever ----

    def run(self, save=None) -> None:
        settings = self.settings
        last_ok, warned = self.clock(), False
        self._dispatch(Alert("system", 2, "Siren is on watch", f"Checking orders every {settings.poll_seconds} s."))
        while True:
            try:
                self.tick()
                if save:
                    save(self.state)
                last_ok = self.clock()
                if warned:
                    warned = False
                    self._dispatch(Alert("system", 3, "Siren recovered", "Order checks work again."))
            except Exception as error:
                log.error("poll failed: %s", error)
                self.last_error = str(error)[:300]
                self.snapshot = self._build_snapshot()
                # A silent watcher means missed orders, so say so once, at full volume.
                if not warned and self.clock() - last_ok > settings.unhealthy_after_seconds:
                    warned = True
                    minutes = settings.unhealthy_after_seconds // 60
                    self._dispatch(Alert("system", 5, "Siren can't reach Eldorado",
                                         f"No successful check for {minutes} minutes: {error}"))
            self.wake.wait(settings.poll_seconds)
            self.wake.clear()
