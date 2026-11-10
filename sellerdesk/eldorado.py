"""Client for the Eldorado Seller API, plus the Order model.

API reference: https://www.eldorado.gg/swagger/seller/index.html (requires a seller login).
"""

from __future__ import annotations

import csv
import io
import json
import time
import urllib.parse
from dataclasses import dataclass
from datetime import datetime, timezone

from .deadline import GUARANTEE_SECONDS, parse_time
from .net import HttpError, request

BOM = chr(0xFEFF)


@dataclass(frozen=True)
class Order:
    id: str
    title: str
    quantity: int
    price: float | None
    currency: str
    buyer: str
    url: str
    started_at: float | None
    window: int | None  # guaranteed delivery time in seconds, None if the offer has none

    @property
    def deadline(self) -> float | None:
        if self.started_at is None or self.window is None:
            return None
        return self.started_at + self.window

    @property
    def summary(self) -> str:
        parts = [self.title]
        if self.quantity > 1:
            parts.append(f"x{self.quantity}")
        if self.price is not None:
            parts.append(f"{self.price:.2f} {self.currency}")
        if self.buyer:
            parts.append(f"buyer {self.buyer}")
        return ", ".join(parts)

    @classmethod
    def from_api(cls, raw: dict, base_url: str) -> "Order":
        offer = raw.get("orderOfferDetails") or {}
        money = raw.get("totalPrice") or {}
        return cls(
            id=raw["id"],
            title=offer.get("offerTitle") or offer.get("gameCategoryTitle") or "Order",
            quantity=raw.get("purchaseQuantity") or 1,
            price=money.get("amount"),
            currency=money.get("currency") or "USD",
            buyer=raw.get("buyerUsername") or "",
            url=f"{base_url}/order/{raw['id']}",
            started_at=parse_time(raw.get("deliveryStartedDate") or raw.get("createdDate")),
            window=GUARANTEE_SECONDS.get(offer.get("guaranteedDeliveryTime")),
        )


@dataclass(frozen=True)
class Notice:
    id: str
    event: str
    title: str
    text: str
    url: str | None

    @classmethod
    def from_api(cls, raw: dict) -> "Notice | None":
        custom = raw.get("customNotification")
        if custom:
            return cls(custom.get("id"), "Custom", custom.get("subject") or "Eldorado",
                       custom.get("text") or "", custom.get("redirectUrl"))
        plain = raw.get("notification") or {}
        if not plain.get("id"):
            return None
        details = plain.get("details") or {}
        bits = [details.get("title"), details.get("gameCategoryTitle"), details.get("buyerUsername")]
        event = plain.get("event") or "Unknown"
        return cls(plain["id"], event, event, ", ".join(b for b in bits if b), None)


class EldoradoClient:
    def __init__(self, base_url, client_id, client_secret, transport=request, clock=time.time):
        self.base_url = base_url
        self.client_id = client_id
        self.client_secret = client_secret
        self._transport = transport
        self._clock = clock
        self._token = None
        self._expires_at = 0.0

    def _authenticate(self):
        if self._token and self._clock() < self._expires_at - 60:
            return
        raw = self._transport("POST", f"{self.base_url}/api/authentication/seller/token",
                              json_body={"clientId": self.client_id, "clientSecret": self.client_secret})
        reply = json.loads(raw)
        self._token = reply["accessToken"]
        self._expires_at = self._clock() + int(reply.get("expiresIn", 900))

    def _call(self, method, path, params=None) -> str:
        self._authenticate()
        query = f"?{urllib.parse.urlencode(params)}" if params else ""
        url = f"{self.base_url}{path}{query}"
        try:
            return self._transport(method, url, headers={"Authorization": f"Bearer {self._token}"})
        except HttpError as error:
            if error.status != 401:
                raise
            self._token = None  # revoked or expired early: retry once with a fresh token
            self._authenticate()
            return self._transport(method, url, headers={"Authorization": f"Bearer {self._token}"})

    def orders(self, state: str) -> list[Order]:
        reply = json.loads(self._call("GET", "/api/v1/orders/me/seller/orders",
                                      {"orderState": state, "pageSize": 50}) or "{}")
        return [Order.from_api(raw, self.base_url) for raw in reply.get("results") or []]

    def unread_notices(self) -> list[Notice]:
        reply = json.loads(self._call("GET", "/api/notifications/me",
                                      {"notificationReadStatuses": "IsUnread", "pageSize": 30}) or "{}")
        notices = (Notice.from_api(raw) for raw in reply.get("results") or [])
        return [notice for notice in notices if notice]

    def sales_since(self, seconds_ago: float) -> list[tuple[float, float]]:
        """Return (timestamp, net income) rows. The endpoint answers with CSV, not JSON."""
        now = self._clock()

        def iso(ts):
            return datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

        text = self._call("GET", "/api/v1/orders/me/sold-order-data",
                          {"fromDate": iso(now - seconds_ago), "untilDate": iso(now)})
        rows = []
        for row in csv.DictReader(io.StringIO(text.lstrip(BOM))):
            when = datetime.strptime(row["Order Date"], "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc)
            rows.append((when.timestamp(), float(row["Net Income $"])))
        return rows

    def switch_offline(self):
        self._call("PUT", "/api/offerUser/me/switchOffline")

    def switch_online(self):
        self._call("PUT", "/api/offerUser/me/switchOnline")
