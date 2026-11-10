"""Market hub: the one place that reads Eldorado's public listings for every desktop PC.

Ported from the desktop app (desktop/EldoradoManager/Market.cs and MarketWatch.cs). Several PCs polling
the listings each on their own made the account hit HTTP 429 all night, so the server polls once and the
PCs read the result from GET /api/market.

The hub has its own token and its own call gate, so it never slows the order alarm loop.
"""

from __future__ import annotations

import copy
import json
import logging
import os
import re
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

from . import __version__

log = logging.getLogger("eldorado_manager")

SITE_PAGE_SIZE = 24
PICTURES = "https://assetsdelivery.eldorado.gg/v7/_offers-v2_/"  # the API only gives file names
PAGE_SIZE = 50  # the API rejects 100
LISTING = "/api/v1/item-management/offers"
MINE = "/api/v1/item-management/me/offers"
CURRENCY_LISTING = "/api/v1/currency-management/offers"
MY_CURRENCY = "/api/v1/currency-management/me/offers"
SOLD = "/api/v1/orders/me"
TOP_RIVALS = 5
CALL_GAP_SECONDS = 1.5
DEFAULT_HOLD_SECONDS = 60

SWEEP_PAGES = 30
SWEEP_GAP_SECONDS = 6
KEEP_DAYS = 14
BIGGEST_FALL = 1000  # beyond this it is a seller rewriting their stock, not a sale
SITE_EVERY = 5  # walk the site order on every 5th standings pass
MAX_PAGES = 100  # any walk stops here (5,000 rows), so a listing that never ends can't hold the hub

USER_AGENT = f"eldorado-manager/{__version__}"


class MarketError(Exception):
    def __init__(self, message: str, status: int = 0, retry_after: float | None = None):
        super().__init__(message)
        self.status = status
        self.retry_after = retry_after


# ---------- models ----------


@dataclass
class Offer:
    """One of your item or currency offers, and the filter that finds its rivals."""

    id: str = ""
    title: str = ""
    kind: str = ""
    game_id: str = ""
    category: str = ""
    environment_id: str = ""
    environment_values: list = field(default_factory=list)
    price: float = 0.0
    currency: str = "USD"
    quantity: int = 0
    state: str = ""
    picture: str = ""
    large_picture: str = ""
    unit: str = ""
    game: str = ""
    delivery: str = ""

    @property
    def is_listed(self) -> bool:
        return self.state == "Active"

    @property
    def is_currency(self) -> bool:
        return self.category == "Currency"


@dataclass
class Listed:
    """One line of a public listing, as far as the market watch needs it."""

    id: str = ""
    product: str = ""  # Eldorado's own key for the item, the same across sellers
    name: str = ""
    kind: str = ""
    picture: str = ""
    seller: str = ""
    link: str = ""
    quantity: int = 0
    price: float = 0.0


def new_standing() -> dict:
    return {"Total": 0, "Lowest": None, "Cheaper": 0, "Tied": 0, "Next": None, "PricePlace": None,
            "SitePlace": None, "Rivals": None, "You": None}


def new_watched() -> dict:
    return {"Started": "", "Last": "", "Sweeps": 0, "Seen": 0, "Total": 0, "Items": {}}


def empty_snapshot(problem: str | None = None) -> dict:
    return {"watched": new_watched(), "standings": {}, "checked": None, "siteChecked": None, "problem": problem}


def iso(moment: datetime) -> str:
    """Round-trip UTC time, the way C# writes DateTime.ToString("o") for UTC."""
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"


# ---------- reply parsing (Market.cs helpers) ----------


def part(mapping, key):
    value = mapping.get(key) if isinstance(mapping, dict) else None
    return value if isinstance(value, dict) else None


def items(mapping, key) -> list:
    value = mapping.get(key) if isinstance(mapping, dict) else None
    return [row for row in value if isinstance(row, dict)] if isinstance(value, list) else []


def results(reply) -> list:
    return items(reply, "results")


def inner(row: dict) -> dict:
    return part(row, "offer") or row


def rows_of(reply) -> list:
    return [inner(row) for row in results(reply)]


def text(mapping, key) -> str:
    value = mapping.get(key) if isinstance(mapping, dict) else None
    if value is None:
        return ""
    if isinstance(value, bool):
        return "True" if value else "False"
    return str(value)


def whole(mapping, key) -> int:
    value = mapping.get(key) if isinstance(mapping, dict) else None
    if value is None or value == "":
        return 0
    try:
        return int(round(float(value)))
    except (TypeError, ValueError):
        return 0


def money(mapping, key) -> float:
    value = mapping.get(key) if isinstance(mapping, dict) else None
    if isinstance(value, dict):
        try:
            return float(value.get("amount") or 0)
        except (TypeError, ValueError):
            return 0.0
    return 0.0


def unit_name(system: str) -> str:
    match = re.search(r"\d+$", system or "")
    size = match.group(0) if match else ""
    return {"1000000": "1M", "1000": "1K", "1": ""}.get(size, size)


def title_case(value: str) -> str:
    """Close to .NET TextInfo.ToTitleCase: capitalise each word, leave all-caps words alone."""
    return " ".join(word if word.isupper() else word[:1].upper() + word[1:].lower() for word in value.split(" "))


_SPAN = re.compile(r"^\s*(-)?(?:(\d+)\.)?(\d+):(\d+)(?::(\d+(?:\.\d+)?))?\s*$")


def span_minutes(value: str) -> float | None:
    """Parse a .NET TimeSpan such as 00:20:00 or 1.02:03:04 into minutes."""
    if re.fullmatch(r"\s*\d+\s*", value or ""):
        return int(value) * 1440.0  # a bare number is days to TimeSpan.Parse
    match = _SPAN.match(value or "")
    if not match:
        return None
    sign, days, hours, minutes, seconds = match.groups()
    total = int(days or 0) * 1440 + int(hours) * 60 + int(minutes) + float(seconds or 0) / 60
    return -total if sign else total


def picture_of(art, size: str) -> str:
    file = text(art, size) if art else ""
    return PICTURES + urllib.parse.quote(file, safe="") if file else ""


def to_offer(raw: dict) -> Offer:
    values = items(raw, "tradeEnvironmentValues")
    price = part(raw, "pricePerUnit")
    art = part(raw, "mainOfferImage")
    title = text(raw, "offerTitle")
    alias = text(raw, "gameSeoAlias").replace("-", " ")
    category = text(raw, "category")
    game = re.sub(r" (items|coins|gold|currency|accounts|top ups|gift cards|boosting)$", "", alias)
    return Offer(
        id=text(raw, "id"),
        title=title or title_case(alias),
        kind="Currency" if category == "Currency" else ", ".join(text(v, "value") for v in values),
        unit=unit_name(text(raw, "unitSystem")),
        game_id=text(raw, "gameId"),
        category=category,
        environment_id=text(values[0], "id") if values else "",
        environment_values=[text(v, "value") for v in values],
        price=float(price.get("amount") or 0) if price else 0.0,
        currency=text(price, "currency") if price else "USD",
        quantity=whole(raw, "quantity"),
        state=text(raw, "offerState"),
        game=title_case(game),
        delivery=text(raw, "guaranteedDeliveryTime"),
        picture=picture_of(art, "smallImage"),
        large_picture=picture_of(art, "largeImage"),
    )


def to_rival(raw: dict, place: int, mine: bool) -> dict:
    user = part(raw, "user")
    info = part(raw, "userOrderInfo")
    delivery = part(raw, "deliveryTime")
    median = text(delivery, "deliveryTimeMedian") if delivery else ""
    score = info.get("feedbackScore") if info else None
    ratings = info.get("ratingCount") if info else None
    return {
        "Place": place,
        "Seller": text(user, "username") if user else "",
        "Price": money(inner(raw), "pricePerUnitInUSD"),
        "Minutes": span_minutes(median),
        "Score": float(score) if isinstance(score, (int, float)) and not isinstance(score, bool) else None,
        "Ratings": int(ratings) if isinstance(ratings, (int, float)) and not isinstance(ratings, bool) else 0,
        "IsYou": mine,
    }


def to_listed(row: dict) -> Listed:
    raw = inner(row)
    user = part(row, "user")
    alias = text(raw, "gameSeoAlias")
    art = part(raw, "mainOfferImage")
    values = items(raw, "tradeEnvironmentValues")
    return Listed(
        id=text(raw, "id"),
        product=text(raw, "standardizedProductKey"),
        name=text(values[-1], "value") if values else text(raw, "offerTitle"),
        kind=text(values[0], "value") if len(values) > 1 else "",
        picture=picture_of(art, "smallImage"),
        seller=text(user, "username") if user else "",
        # the sweep reads item listings only, and items live under /oi/
        link=f"/{alias}/oi/{text(raw, 'id')}" if alias else "",
        quantity=whole(raw, "quantity"),
        price=money(raw, "pricePerUnitInUSD"),
    )


def listing_of(offer: Offer) -> str:
    return CURRENCY_LISTING if offer.is_currency else LISTING


def filter_of(offer: Offer, page: int) -> dict:
    query = {"gameId": offer.game_id, "category": offer.category, "pageIndex": str(page), "pageSize": str(PAGE_SIZE)}
    if not offer.is_currency:
        query["includeDeliveryMedians"] = "true"
    if not offer.environment_values:
        if not offer.is_currency:
            query["searchQuery"] = offer.title  # a currency offer has no title of its own
        return query
    query["tradeEnvironmentId"] = offer.environment_id
    for i, value in enumerate(offer.environment_values[:3]):
        query[f"tradeEnvironmentValue{i}"] = value
    return query


def explain(status: int, body: str) -> str:
    if status in (401, 403):
        return "Eldorado refused the seller key."
    if status == 429:
        return "Eldorado asked for fewer requests."
    said = re.search(r'"(?:messages?|detail|title)"\s*:\s*\[?\s*"([^"]+)"', body or "")
    if said:
        return "Eldorado said: " + said.group(1)
    return f"Eldorado couldn't do that (status {status})."


# ---------- transport ----------


def urllib_transport(method: str, url: str, headers: dict, body: bytes | None) -> str:
    """Send one request. GET may carry a JSON body: Eldorado's currency search needs that."""
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        reply = error.read().decode("utf-8", "replace")
        retry = None
        try:
            retry = float(error.headers.get("Retry-After")) if error.headers.get("Retry-After") else None
        except ValueError:
            retry = None
        if error.code == 400:
            log.warning("eldorado 400 on %s %s: %s", method, url.split("?")[0], reply[:300])
        raise MarketError(explain(error.code, reply), error.code, retry) from error
    except (urllib.error.URLError, OSError) as error:
        raise MarketError(f"Can't reach Eldorado. {error}") from error


class MarketApi:
    """The Eldorado item and currency APIs, with one call gate and its own seller token."""

    def __init__(self, base_url, client_id, client_secret, transport=urllib_transport,
                 clock=time.monotonic, sleep=time.sleep):
        self.base_url = base_url.rstrip("/")
        self._client_id = client_id
        self._client_secret = client_secret
        self._transport = transport
        self._clock = clock
        self._sleep = sleep
        self._gate = threading.Lock()
        self._token_lock = threading.Lock()
        self._token = None
        self._token_expires = 0.0
        self._next_call = 0.0
        self._hold_until = 0.0

    # --- pacing ---

    def _pace(self):
        """Spaces every call out, and waits out a limit Eldorado has announced."""
        with self._gate:
            now = self._clock()
            ready = max(self._next_call, self._hold_until)
            if ready > now:
                self._sleep(ready - now)
            self._next_call = self._clock() + CALL_GAP_SECONDS

    def _hold(self, seconds):
        seconds = DEFAULT_HOLD_SECONDS if seconds is None or seconds <= 0 else seconds
        with self._gate:
            now = self._clock()
            fresh = self._hold_until <= now
            self._hold_until = max(self._hold_until, now + seconds)
        if fresh:
            log.warning("market: Eldorado answered 429, holding market calls for %d s", int(seconds))

    @property
    def holding(self) -> bool:
        return self._hold_until > self._clock()

    # --- calls ---

    def _once(self, method, path, body, signed) -> str:
        headers = {"User-Agent": USER_AGENT, "Accept": "application/json, text/csv, */*"}
        if signed:
            headers["Authorization"] = "Bearer " + self._token_value()
        data = None
        if body is not None:
            data = body.encode() if isinstance(body, str) else body
            headers["Content-Type"] = "application/json"
        self._pace()
        try:
            return self._transport(method, self.base_url + path, headers, data)
        except MarketError as error:
            if error.status == 429:
                self._hold(error.retry_after)
            raise

    def _call(self, method, path, body=None, signed=False) -> str:
        try:
            return self._once(method, path, body, signed)
        except MarketError as error:
            if not (signed and error.status == 401):
                raise
            self._token = None  # revoked or expired early: retry once with a fresh token
            return self._once(method, path, body, signed)

    def _read(self, path, query, signed) -> dict:
        reply = self._call("GET", path + "?" + urllib.parse.urlencode(query), None, signed)
        try:
            parsed = json.loads(reply or "{}")
        except ValueError as error:
            raise MarketError("Eldorado sent a reply that isn't JSON.") from error
        return parsed if isinstance(parsed, dict) else {}

    def _token_value(self) -> str:
        with self._token_lock:
            if self._token and self._clock() < self._token_expires:
                return self._token
            body = json.dumps({"clientId": self._client_id, "clientSecret": self._client_secret})
            reply = json.loads(self._once("POST", "/api/authentication/seller/token", body, False) or "{}")
            self._token = text(reply, "accessToken")
            life = whole(reply, "expiresIn") if "expiresIn" in reply else 900
            self._token_expires = self._clock() + life - 60
            return self._token

    # --- your offers ---

    def offers(self) -> list[Offer]:
        offers = []
        page = 1
        while True:
            reply = self._read(MINE + "/me/search", {"pageIndex": str(page), "pageSize": str(PAGE_SIZE)}, True)
            rows = rows_of(reply)
            offers.extend(to_offer(row) for row in rows)
            if not rows or page >= min(whole(reply, "totalPages"), MAX_PAGES):
                break
            page += 1
        # Eldorado has no "all my currency offers" call, so this asks per game the seller already trades in.
        games = list(dict.fromkeys(offer.game_id for offer in offers))
        for game in games:
            offers.extend(self.currency_offers(game))
        return offers

    def currency_offers(self, game: str) -> list[Offer]:
        offers = []
        body = json.dumps({"augmentedGameFilter": {"gameId": game, "category": "Currency"}})
        page = 1
        while True:
            raw = self._call("GET", f"{MY_CURRENCY}?pageIndex={page}&pageSize={PAGE_SIZE}", body, True)
            reply = json.loads(raw or "{}")
            rows = rows_of(reply)
            offers.extend(to_offer(row) for row in rows)
            if not rows or page >= min(whole(reply, "totalPages"), MAX_PAGES):
                return offers
            page += 1

    # --- standings ---

    def look(self, offer: Offer, walk_site: bool) -> dict:
        """Walks the price order up to your price, then the site order if asked."""
        standing = new_standing()
        # Listings carry USD prices only, so an offer priced in another currency has nothing to compare with.
        if offer.currency != "USD":
            standing["Total"] = whole(self._read(listing_of(offer), filter_of(offer, 1), False), "recordCount")
            if walk_site and offer.is_listed:
                self.walk_site(offer, standing)
            return standing
        seen = 0
        page = 1
        while True:
            query = filter_of(offer, page)
            query["offerSortingCriterion"] = "Price"
            query["isAscending"] = "true"
            reply = self._read(listing_of(offer), query, False)
            standing["Total"] = whole(reply, "recordCount")
            rows = results(reply)
            passed = False
            for raw in rows:
                row = inner(raw)
                seen += 1
                if offer.is_currency and unit_name(text(row, "unitSystem")) != offer.unit:
                    continue  # prices per 1K and per 1M don't compare
                price = money(row, "pricePerUnitInUSD")
                if standing["Lowest"] is None or price < standing["Lowest"]:
                    standing["Lowest"] = price
                if price < offer.price:
                    standing["Cheaper"] += 1
                if text(row, "id") == offer.id:
                    standing["PricePlace"] = seen
                    standing["You"] = to_rival(raw, seen, True)
                elif price == offer.price:
                    standing["Tied"] += 1
                if price > offer.price:
                    passed = True
                    if standing["Next"] is None or price < standing["Next"]:
                        standing["Next"] = price
            # The currency listing ignores the price sort, so only a full walk finds its lowest price.
            if not offer.is_currency and passed:
                break
            if not rows or page >= min(whole(reply, "totalPages"), MAX_PAGES):
                break
            page += 1
        if offer.is_currency and standing["PricePlace"] is not None:
            standing["PricePlace"] = standing["Cheaper"] + 1
        if walk_site and offer.is_listed:
            self.walk_site(offer, standing)
        return standing

    def walk_site(self, offer: Offer, standing: dict) -> None:
        top = []
        standing["Rivals"] = top
        seen = 0
        page = 1
        while True:
            reply = self._read(listing_of(offer), filter_of(offer, page), False)
            rows = results(reply)
            for raw in rows:
                seen += 1
                mine = text(inner(raw), "id") == offer.id
                if seen <= TOP_RIVALS:
                    top.append(to_rival(raw, seen, mine))
                if not mine:
                    continue
                standing["SitePlace"] = seen
                standing["You"] = to_rival(raw, seen, True)
                return
            if not rows or page >= min(whole(reply, "totalPages"), MAX_PAGES):
                return
            page += 1

    # --- market watch ---

    def sweep(self, game_id: str, category: str, page: int) -> tuple[list[Listed], int, int]:
        """One page of a game's public listing in the order buyers see it. Returns (rows, pages, records)."""
        reply = self._read(LISTING, {"gameId": game_id, "category": category, "pageIndex": str(page),
                                     "pageSize": str(PAGE_SIZE)}, False)
        return [to_listed(row) for row in results(reply)], whole(reply, "totalPages"), whole(reply, "recordCount")

    # --- sales export, for the ledger ---

    def sales_csv(self, since: datetime, until: datetime) -> str:
        def stamp(moment):
            return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

        query = urllib.parse.urlencode({"fromDate": stamp(since), "untilDate": stamp(until)})
        return self._call("GET", f"{SOLD}/sold-order-data?{query}", None, True)


# ---------- market watch ----------


def take(watched: dict, stock: dict, listed: list[Listed], mine: set, now: datetime) -> None:
    """Folds one sweep in. Your own offers are left out, so this shows other sellers' buyers."""
    day = now.strftime("%Y-%m-%d")
    groups: dict[str, list[Listed]] = {}
    for line in listed:
        if line.product:
            groups.setdefault(line.product, []).append(line)
    for key, group in groups.items():
        item = watched["Items"].get(key)
        if item is None:
            item = watched["Items"][key] = {"Name": "", "Kind": "", "Picture": "", "Sellers": 0, "Low": 0.0,
                                            "Typical": 0.0, "Days": {}}
        prices = sorted(line.price for line in group)
        item["Name"] = group[0].name
        item["Kind"] = group[0].kind
        item["Picture"] = next((line.picture for line in group if line.picture), item.get("Picture", ""))
        item["Sellers"] = len(group)
        item["Low"] = prices[0]
        item["Typical"] = prices[len(prices) // 2]
        for offer in group:
            if offer.id in mine or offer.id not in stock:
                continue
            fell = stock[offer.id] - offer.quantity
            if fell <= 0 or fell > BIGGEST_FALL:
                continue
            counted = item["Days"].setdefault(day, {"Drops": 0, "Units": 0, "Offers": [], "Falls": []})
            counted["Drops"] += 1
            counted["Units"] += fell
            if offer.id not in counted["Offers"]:
                counted["Offers"].append(offer.id)
            counted["Falls"].append({"Offer": offer.id, "Seller": offer.seller, "Link": offer.link,
                                     "Price": offer.price, "Units": fell})
    # Only this sweep's offers are compared next time; keeping every id ever seen grew without end.
    stock.clear()
    for offer in listed:
        stock[offer.id] = offer.quantity

    oldest = (now - timedelta(days=KEEP_DAYS)).strftime("%Y-%m-%d")
    for key in list(watched["Items"]):
        item = watched["Items"][key]
        for stale in [d for d in item["Days"] if d < oldest]:
            del item["Days"][stale]
        if not item["Days"] and key not in groups:
            del watched["Items"][key]
    if not watched.get("Started"):
        watched["Started"] = iso(now)
    watched["Last"] = iso(now)
    watched["Sweeps"] = watched.get("Sweeps", 0) + 1
    watched["Seen"] = len(listed)


# ---------- the hub ----------


class MarketHub:
    """Polls standings and sweeps listings on a daemon thread, and serves the result to the dashboard API."""

    def __init__(self, api_client: MarketApi, settings, path: str, now=lambda: datetime.now(timezone.utc),
                 sleep=time.sleep):
        self.api = api_client
        self.path = path
        self.pass_seconds = max(10, int(getattr(settings, "market_pass_seconds", 180)))
        self.sweep_seconds = max(60, int(getattr(settings, "market_sweep_minutes", 30)) * 60)
        self._now = now
        self._sleep = sleep
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._stock: dict[str, int] = {}
        self._mine: set[str] = set()
        self._listings: list[tuple[str, str]] = []
        self._passes = 0
        self._data = self._load()

    # --- persistence ---

    def _load(self) -> dict:
        data = empty_snapshot()
        try:
            with open(self.path, encoding="utf-8") as file:
                saved = json.load(file)
        except FileNotFoundError:
            return data
        except (OSError, ValueError) as error:
            log.warning("market: could not read %s (%s), starting fresh", self.path, error)
            return data
        if isinstance(saved, dict):
            for key in data:
                if key in saved:
                    data[key] = saved[key]
            if not isinstance(data["watched"], dict):
                data["watched"] = new_watched()
            for key, value in new_watched().items():
                data["watched"].setdefault(key, value)
            if not isinstance(data["standings"], dict):
                data["standings"] = {}
        return data

    def _save(self) -> None:
        try:
            with self._lock:
                body = json.dumps(self._data)
            directory = os.path.dirname(self.path)
            if directory:
                os.makedirs(directory, exist_ok=True)
            fresh = self.path + ".new"
            with open(fresh, "w", encoding="utf-8") as file:
                file.write(body)
            os.replace(fresh, self.path)
        except OSError as error:
            log.warning("market: could not save %s: %s", self.path, error)

    def snapshot(self) -> dict:
        with self._lock:
            return copy.deepcopy(self._data)

    # --- work ---

    def standings_pass(self) -> None:
        """One read of your offers and where each stands. Raises on an Eldorado error."""
        site = self._passes % SITE_EVERY == 0
        self._passes += 1
        offers = self.api.offers()
        self._mine = {offer.id for offer in offers}
        self._listings = list(dict.fromkeys((o.game_id, o.category) for o in offers if not o.is_currency))
        with self._lock:
            previous = dict(self._data["standings"])
        fresh = {}
        for offer in offers:
            if offer.state == "Closed" or self._stop.is_set():
                continue
            standing = self.api.look(offer, site)
            stamp = iso(self._now())
            standing["At"] = stamp
            old = previous.get(offer.id) or {}
            if site:
                standing["SiteAt"] = stamp if offer.is_listed else None
            else:
                # Between site walks, keep what the last walk found.
                standing["SitePlace"] = old.get("SitePlace")
                standing["Rivals"] = old.get("Rivals")
                standing["SiteAt"] = old.get("SiteAt")
                if standing["You"] is None:
                    standing["You"] = old.get("You")
            fresh[offer.id] = standing
            with self._lock:
                self._data["standings"][offer.id] = standing
        with self._lock:
            self._data["standings"] = fresh
            self._data["checked"] = iso(self._now())
            if site:
                self._data["siteChecked"] = self._data["checked"]
            self._data["problem"] = None

    def sweep(self) -> None:
        """Reads the top of each listing you trade in and counts other sellers' stock falls."""
        if not self._listings:
            return
        listed: list[Listed] = []
        total = 0
        for game_id, category in self._listings:
            for page in range(1, SWEEP_PAGES + 1):
                if self._stop.is_set():
                    return
                read, pages, records = self.api.sweep(game_id, category, page)
                listed.extend(read)
                if page == 1:
                    total += records
                if page >= pages or not read:
                    break
                self._sleep(SWEEP_GAP_SECONDS)
        with self._lock:
            watched = self._data["watched"]
            take(watched, self._stock, listed, self._mine, self._now())
            watched["Total"] = total

    def _guarded(self, name, work) -> None:
        try:
            work()
        except MarketError as error:
            log.warning("market %s: %s", name, error)
            with self._lock:
                self._data["problem"] = str(error)
        except Exception as error:  # never take the process down
            log.exception("market %s stopped: %s", name, error)
            with self._lock:
                self._data["problem"] = f"Market {name} failed: {error}"
        self._save()

    def run(self) -> None:
        next_pass = 0.0
        next_sweep = None
        while not self._stop.is_set():
            now = time.monotonic()
            if now >= next_pass:
                self._guarded("pass", self.standings_pass)
                next_pass = time.monotonic() + self.pass_seconds
                if next_sweep is None:
                    next_sweep = time.monotonic()  # first sweep right after the first pass found your listings
            if next_sweep is not None and time.monotonic() >= next_sweep:
                self._guarded("sweep", self.sweep)
                next_sweep = time.monotonic() + self.sweep_seconds
            wait = next_pass - time.monotonic()
            if next_sweep is not None:
                wait = min(wait, next_sweep - time.monotonic())
            self._stop.wait(max(1.0, wait))

    def start(self) -> threading.Thread:
        thread = threading.Thread(target=self.run, daemon=True, name="market hub")
        thread.start()
        log.info("market hub: standings every %d s, sweep every %d min", self.pass_seconds, self.sweep_seconds // 60)
        return thread

    def stop(self) -> None:
        self._stop.set()
