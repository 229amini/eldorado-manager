"""Finance ledger shared by every desktop PC: who owns which units of an order, and what they were paid.

Eldorado's order API has no fee field, so each order's net comes from the sales export
(GET /api/v1/orders/me/sold-order-data, CSV). An order whose line in the export can be told apart gets
its exact net; the rest get an estimate from the fee rate the export shows for that kind of goods.
"""

from __future__ import annotations

import copy
import csv
import io
import json
import logging
import math
import os
import secrets
import shutil
import threading
import time
from datetime import date, datetime, timedelta, timezone

log = logging.getLogger("sellerdesk")

BOM = chr(0xFEFF)
FALLBACK_FEES = {"Items": 0.15, "Currency": 0.05}
MATCH_TOLERANCE = 0.005
MATCH_DAYS = 10
SYNC_DAYS = 45
SYNC_SECONDS = 30 * 60
FIRST_SYNC_SECONDS = 60


MAX_AMOUNT = 1e9
MAX_UNITS = 10_000_000_000


class NotFound(LookupError):
    """An id that isn't in the ledger. The dashboard answers 404."""


class Conflict(Exception):
    """The order or person changed since the caller read it. The dashboard answers 409."""


def fee_group(category: str) -> str:
    """Order category Currency matches the export's Currency, anything else its Items."""
    return "Currency" if (category or "").strip() == "Currency" else "Items"


def _number(value, name: str) -> float:
    try:
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError
        number = float(value)
    except (ValueError, OverflowError):
        raise ValueError(f"{name} must be a number") from None
    if abs(number) > MAX_AMOUNT:
        raise ValueError(f"{name} is too large")
    return number


def _whole(value, name: str) -> int:
    try:
        if (isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value)
                or value != int(value)):
            raise ValueError
    except (ValueError, OverflowError):
        raise ValueError(f"{name} must be a whole number") from None
    if abs(value) > MAX_UNITS:
        raise ValueError(f"{name} is too large")
    return int(value)


def _check(current, expect) -> None:
    """expect is the 'changed' value the caller saw, or None to skip the check."""
    if expect is not None and int(current or 0) != expect:
        raise Conflict("Changed on another PC. Check it and save again.")


def _valid_order(order) -> bool:
    try:
        return (isinstance(order, dict) and isinstance(order.get("shares", []), list)
                and all(isinstance(s, dict) and isinstance(s.get("person"), str)
                        and isinstance(s.get("units"), int) for s in order.get("shares", []))
                and isinstance(order.get("quantity", 0), int)
                and isinstance(order.get("net", 0.0), (int, float)))
    except (TypeError, AttributeError):
        return False


def _valid_person(person) -> bool:
    return isinstance(person, dict) and isinstance(person.get("id"), str) and isinstance(person.get("name"), str)


def _valid_payment(payment) -> bool:
    return (isinstance(payment, dict) and isinstance(payment.get("id"), str)
            and isinstance(payment.get("person"), str) and isinstance(payment.get("amount"), (int, float))
            and not isinstance(payment.get("amount"), bool))


def _string(value, name: str, required=False) -> str:
    if value is None:
        value = ""
    if not isinstance(value, str):
        raise ValueError(f"{name} must be text")
    value = value.strip()
    if required and not value:
        raise ValueError(f"{name} is required")
    return value


def parse_moment(value: str) -> datetime:
    """ISO date or date-time. A time without a zone is taken as UTC."""
    text = value.strip()
    if text.endswith("Z") or text.endswith("z"):
        text = text[:-1] + "+00:00"
    # Python before 3.11 can't read 7 fractional digits, which .NET writes.
    if "." in text:
        head, _, rest = text.partition(".")
        digits = ""
        while rest and rest[0].isdigit():
            digits, rest = digits + rest[0], rest[1:]
        text = f"{head}.{digits[:6].ljust(6, '0')}{rest}"
    try:
        moment = datetime.fromisoformat(text)
    except ValueError:
        moment = datetime.strptime(text, "%Y-%m-%d %H:%M:%S")
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc)


def parse_sales(text: str) -> list[dict]:
    """Rows of the sales export: {when (date), amount, fees, net, group}. Bad rows are skipped."""
    rows = []

    def amount(value):
        return float((value or "0").replace(",", "").replace("$", "").strip() or 0)

    for row in csv.DictReader(io.StringIO((text or "").lstrip(BOM))):
        try:
            when = parse_moment(row.get("Order Date") or "").date()
            rows.append({
                "when": when,
                "amount": amount(row.get("Amount $")),
                "fees": amount(row.get("Seller Fees $")),
                "net": amount(row.get("Net Income $")),
                "group": fee_group(row.get("Category") or ""),
            })
        except (ValueError, TypeError):
            continue
    return rows


def fee_rates(sales: list[dict]) -> dict:
    rates = dict(FALLBACK_FEES)
    for group in rates:
        amount = sum(row["amount"] for row in sales if row["group"] == group)
        fees = sum(row["fees"] for row in sales if row["group"] == group)
        if amount > 0:
            rates[group] = fees / amount
    return rates


class Ledger:
    def __init__(self, path: str):
        self.path = path
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._data = self._load()

    # ---------- storage ----------

    @staticmethod
    def _empty() -> dict:
        return {"revision": 0, "people": [], "orders": {}, "payments": [], "feeRates": dict(FALLBACK_FEES)}

    def _load(self) -> dict:
        data = self._empty()
        try:
            with open(self.path, encoding="utf-8") as file:
                saved = json.load(file)
        except FileNotFoundError:
            return data
        except ValueError as error:
            # Keep the damaged file for a person to look at; never write over it.
            aside = f"{self.path}.bad-{int(time.time())}"
            log.error("ledger: could not read %s (%s), moved it to %s", self.path, error, aside)
            try:
                os.replace(self.path, aside)
            except OSError:
                pass
            return data
        # Any other OSError (permissions, a busy disk) propagates: starting empty would hide the money records.
        if isinstance(saved, dict):
            for key in data:
                if key in saved and isinstance(saved[key], type(data[key])):
                    data[key] = saved[key]
        # One damaged record must not break every read, so leave it out and keep the whole file aside.
        dropped = 0
        for key, valid in (("people", _valid_person), ("payments", _valid_payment)):
            kept = [item for item in data[key] if valid(item)]
            dropped += len(data[key]) - len(kept)
            data[key] = kept
        for key in [key for key, order in data["orders"].items() if not _valid_order(order)]:
            data["orders"].pop(key)
            dropped += 1
        if dropped:
            aside = f"{self.path}.bad-{int(time.time())}"
            log.error("ledger: left out %d damaged record(s); the original file is kept as %s", dropped, aside)
            shutil.copyfile(self.path, aside)
        return data

    def _write(self, data: dict) -> None:
        directory = os.path.dirname(self.path)
        if directory:
            os.makedirs(directory, exist_ok=True)
        fresh = self.path + ".new"
        with open(fresh, "w", encoding="utf-8") as file:
            json.dump(data, file, indent=1)
            file.flush()
            os.fsync(file.fileno())
        os.replace(fresh, self.path)
        if hasattr(os, "O_DIRECTORY"):  # make the rename itself survive a power cut (POSIX only)
            try:
                handle = os.open(directory or ".", os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(handle)
                finally:
                    os.close(handle)
            except OSError:
                pass

    def _change(self, edit):
        """Run edit on a copy, save it, then make it current, so a failed save changes nothing."""
        with self._lock:
            data = copy.deepcopy(self._data)
            result = edit(data)
            data["revision"] = int(data.get("revision", 0)) + 1
            self._write(data)
            self._data = data
            return result, data["revision"]

    # ---------- reads ----------

    @property
    def revision(self) -> int:
        with self._lock:
            return self._data["revision"]

    def snapshot(self) -> dict:
        with self._lock:
            data = copy.deepcopy(self._data)
        return {"revision": data["revision"], "people": data["people"], "orders": data["orders"],
                "payments": data["payments"], "summary": self._summary(data)}

    def summary(self) -> list[dict]:
        with self._lock:
            return self._summary(copy.deepcopy(self._data))

    @staticmethod
    def _summary(data: dict) -> list[dict]:
        rows = {person["id"]: {"person": person["id"], "units": 0, "earned": 0.0, "paid": 0.0, "balance": 0.0,
                               "orders": 0} for person in data["people"]}
        for order in data["orders"].values():
            quantity = order.get("quantity") or 0
            net = order.get("net") or 0.0
            for share in order.get("shares") or []:
                row = rows.get(share["person"])
                if row is None or quantity <= 0:
                    continue
                row["units"] += share["units"]
                row["earned"] += net * share["units"] / quantity
                row["orders"] += 1
        for payment in data["payments"]:
            row = rows.get(payment["person"])
            if row is not None:
                row["paid"] += payment["amount"]
        for row in rows.values():
            row["earned"] = round(row["earned"], 2)
            row["paid"] = round(row["paid"], 2)
            row["balance"] = round(row["earned"] - row["paid"], 2)
        return list(rows.values())

    # ---------- people ----------

    @staticmethod
    def _name_free(data: dict, name: str, person_id: str = "") -> None:
        if any(p["name"].casefold() == name.casefold() and p["id"] != person_id for p in data["people"]):
            raise ValueError(f"{name} is already in the list")

    def add_person(self, name, note=None) -> tuple[dict, int]:
        person = {"id": "", "name": _string(name, "name", required=True), "note": _string(note, "note"),
                  "archived": False}

        def edit(data):
            self._name_free(data, person["name"])
            known = {p["id"] for p in data["people"]}
            person["id"] = secrets.token_hex(4)
            while person["id"] in known:
                person["id"] = secrets.token_hex(4)
            person["changed"] = int(data.get("revision", 0)) + 1
            data["people"].append(person)
            return dict(person)

        return self._change(edit)

    def update_person(self, person_id: str, fields: dict, expect: int | None = None) -> tuple[dict, int]:
        changes = {}
        if "name" in fields:
            changes["name"] = _string(fields["name"], "name", required=True)
        if "note" in fields:
            changes["note"] = _string(fields["note"], "note")
        if "archived" in fields:
            if not isinstance(fields["archived"], bool):
                raise ValueError("archived must be true or false")
            changes["archived"] = fields["archived"]

        def edit(data):
            for person in data["people"]:
                if person["id"] == person_id:
                    _check(person.get("changed"), expect)
                    if "name" in changes:
                        self._name_free(data, changes["name"], person_id)
                    person.update(changes)
                    person["changed"] = int(data.get("revision", 0)) + 1
                    return dict(person)
            raise NotFound(f"no person {person_id}")

        return self._change(edit)

    # ---------- orders ----------

    def put_order(self, order_id: str, body: dict, expect: int | None = None) -> tuple[dict | None, int]:
        """Store an order's shares. An empty shares list removes the order; the result is then None.

        shares is required, so a body lost on the way can't read as "remove this order".
        """
        order_id = _string(order_id, "order id", required=True)
        if "shares" not in body:
            raise ValueError("shares is required (an empty list removes the order)")
        shares_in = body["shares"]
        if not isinstance(shares_in, list):
            raise ValueError("shares must be a list")
        if not shares_in:
            def remove(data):
                _check((data["orders"].get(order_id) or {}).get("changed"), expect)
                data["orders"].pop(order_id, None)
                return None

            return self._change(remove)

        placed = _string(body.get("placed"), "placed", required=True)
        try:
            parse_moment(placed)
        except ValueError:
            raise ValueError("placed must be an ISO date and time") from None
        quantity = _whole(body.get("quantity"), "quantity")
        if quantity <= 0:
            raise ValueError("quantity must be positive")
        order = {
            "title": _string(body.get("title"), "title"),
            "placed": placed,
            "quantity": quantity,
            "total": _number(body.get("total"), "total"),
            "currency": _string(body.get("currency"), "currency") or "USD",
            "category": _string(body.get("category"), "category"),
            "net": 0.0,
            "netExact": False,
            "shares": [],
        }
        seen = set()
        for share in shares_in:
            if not isinstance(share, dict):
                raise ValueError("each share needs person and units")
            person = _string(share.get("person"), "share person", required=True)
            units = _whole(share.get("units"), "share units")
            if units <= 0:
                raise ValueError("share units must be positive")
            if person in seen:
                raise ValueError("a person can have only one share per order")
            seen.add(person)
            order["shares"].append({"person": person, "units": units})
        if sum(s["units"] for s in order["shares"]) > quantity:
            raise ValueError(f"shares add up to more than the order's {quantity} units")

        def edit(data):
            known = {p["id"] for p in data["people"]}
            for share in order["shares"]:
                if share["person"] not in known:
                    raise ValueError(f"no person {share['person']}")
            old = data["orders"].get(order_id)
            _check((old or {}).get("changed"), expect)
            order["changed"] = int(data.get("revision", 0)) + 1
            same_sale = (old and old.get("netExact") and abs(old.get("total", 0) - order["total"]) < 1e-9
                         and fee_group(old.get("category", "")) == fee_group(order["category"]))
            if same_sale:
                order["net"], order["netExact"] = old["net"], True
            else:
                order["net"] = self._estimate(order, data.get("feeRates") or FALLBACK_FEES)
            data["orders"][order_id] = order
            return dict(order)

        return self._change(edit)

    @staticmethod
    def _estimate(order: dict, rates: dict) -> float:
        group = fee_group(order.get("category", ""))
        rate = rates.get(group, FALLBACK_FEES[group])
        return round(order["total"] * (1 - rate), 2)

    # ---------- payments ----------

    def add_payment(self, body: dict) -> tuple[dict, int]:
        person = _string(body.get("person"), "person", required=True)
        amount = _number(body.get("amount"), "amount")
        if amount == 0:
            raise ValueError("amount can't be zero")
        day = _string(body.get("date"), "date", required=True)
        try:
            if len(day) != 10:
                raise ValueError
            date.fromisoformat(day)
        except ValueError:
            raise ValueError("date must be yyyy-MM-dd") from None
        payment = {"id": "", "person": person, "amount": amount, "date": day, "note": _string(body.get("note"), "note")}

        def edit(data):
            if person not in {p["id"] for p in data["people"]}:
                raise ValueError(f"no person {person}")
            known = {p["id"] for p in data["payments"]}
            payment["id"] = secrets.token_hex(4)
            while payment["id"] in known:
                payment["id"] = secrets.token_hex(4)
            data["payments"].append(payment)
            return dict(payment)

        return self._change(edit)

    def delete_payment(self, payment_id: str) -> int:
        def edit(data):
            kept = [p for p in data["payments"] if p["id"] != payment_id]
            if len(kept) == len(data["payments"]):
                raise NotFound(f"no payment {payment_id}")
            data["payments"] = kept

        return self._change(edit)[1]

    # ---------- net from the sales export ----------

    def reconcile(self, csv_text: str) -> bool:
        """Fold one sales export in. Returns True when any order's net changed."""
        sales = parse_sales(csv_text)
        rates = fee_rates(sales)
        with self._lock:
            data = copy.deepcopy(self._data)
            used = set()

            def candidates(order):
                try:
                    placed = parse_moment(order["placed"]).date()
                except (ValueError, KeyError, AttributeError):
                    return []
                group = fee_group(order.get("category", ""))
                return [i for i, row in enumerate(sales)
                        if i not in used and row["group"] == group
                        and abs(row["amount"] - order.get("total", 0)) <= MATCH_TOLERANCE
                        and timedelta(0) <= row["when"] - placed <= timedelta(days=MATCH_DAYS)]

            by_placed = sorted(data["orders"].items(), key=lambda pair: pair[1].get("placed", ""))
            # Lines that already gave an order its exact net are spoken for.
            for _, order in by_placed:
                if order.get("netExact"):
                    found = candidates(order)
                    if found:
                        used.add(found[0])
            changed = data.get("feeRates") != rates
            data["feeRates"] = rates
            for _, order in by_placed:
                if order.get("netExact"):
                    continue
                found = candidates(order)
                if len(found) == 1:
                    used.add(found[0])
                    net, exact = round(sales[found[0]]["net"], 2), True
                else:
                    net, exact = self._estimate(order, rates), False
                if order.get("net") != net or order.get("netExact") != exact:
                    order["net"], order["netExact"] = net, exact
                    changed = True
            if changed:
                data["revision"] = int(data.get("revision", 0)) + 1
                self._write(data)
                self._data = data
            return changed

    def sync(self, fetch, now=None) -> bool:
        """fetch(since, until) returns the sales export CSV text."""
        until = now or datetime.now(timezone.utc)
        return self.reconcile(fetch(until - timedelta(days=SYNC_DAYS), until))

    def start_sync(self, fetch) -> threading.Thread:
        def run():
            wait = FIRST_SYNC_SECONDS
            while not self._stop.wait(wait):
                wait = SYNC_SECONDS
                try:
                    if self.sync(fetch):
                        log.info("ledger: nets updated from the sales export")
                except Exception as error:  # never take the process down
                    log.warning("ledger sync: %s", error)

        thread = threading.Thread(target=run, daemon=True, name="ledger sync")
        thread.start()
        return thread

    def stop(self) -> None:
        self._stop.set()
