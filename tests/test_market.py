import json
import os
import tempfile
import unittest
import urllib.parse
from datetime import datetime, timedelta, timezone

from eldorado_manager.config import Settings
from eldorado_manager.market import (BIGGEST_FALL, MAX_PAGES, Listed, MarketApi, MarketError, MarketHub, Offer,
                                     filter_of, new_watched, span_minutes, take, to_offer)

NOW = datetime(2026, 11, 10, 12, 0, tzinfo=timezone.utc)
TOKEN = json.dumps({"accessToken": "AT", "expiresIn": 900})


def row(offer_id, price, seller="s", unit=None, quantity=10):
    offer = {"id": offer_id, "pricePerUnitInUSD": {"amount": price, "currency": "USD"}, "quantity": quantity}
    if unit:
        offer["unitSystem"] = unit
    return {"offer": offer, "user": {"username": seller},
            "userOrderInfo": {"feedbackScore": 99.5, "ratingCount": 42},
            "deliveryTime": {"deliveryTimeMedian": "00:20:00"}}


def page(rows, pages=1, records=None):
    return json.dumps({"results": rows, "totalPages": pages, "recordCount": records or len(rows)})


class FakeTransport:
    """Answers by path. Each route holds a list of replies, or a function of the query."""

    def __init__(self, routes):
        self.routes = routes
        self.calls = []

    def __call__(self, method, url, headers, body):
        parsed = urllib.parse.urlparse(url)
        query = dict(urllib.parse.parse_qsl(parsed.query))
        self.calls.append((method, parsed.path, query, headers, body))
        answer = self.routes[parsed.path]
        if callable(answer):
            answer = answer(query)
        elif isinstance(answer, list):
            answer = answer.pop(0)
        if isinstance(answer, Exception):
            raise answer
        return answer


class Clock:
    def __init__(self):
        self.now = 1000.0
        self.slept = []

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.slept.append(seconds)
        self.now += seconds


def api(routes, clock=None):
    clock = clock or Clock()
    transport = FakeTransport(routes)
    return MarketApi("https://e", "id", "secret", transport=transport, clock=clock, sleep=clock.sleep), transport


def listed(offer_id, product, quantity, price=1.0, seller="rival"):
    return Listed(id=offer_id, product=product, name="Rifle", kind="Weapon", seller=seller,
                  link=f"/g/oi/{offer_id}", quantity=quantity, price=price)


class Take(unittest.TestCase):
    def test_counts_falls_and_skips_own_offers(self):
        watched, stock = new_watched(), {}
        take(watched, stock, [listed("a", "p", 10, 2.0), listed("me", "p", 10, 1.0)], {"me"}, NOW)
        self.assertEqual(watched["Items"]["p"]["Days"], {})
        take(watched, stock, [listed("a", "p", 7, 2.0), listed("me", "p", 4, 1.0)], {"me"}, NOW)
        day = watched["Items"]["p"]["Days"]["2026-11-10"]
        self.assertEqual((day["Drops"], day["Units"], day["Offers"]), (1, 3, ["a"]))
        self.assertEqual(day["Falls"], [{"Offer": "a", "Seller": "rival", "Link": "/g/oi/a", "Price": 2.0, "Units": 3}])
        self.assertEqual(watched["Sweeps"], 2)
        self.assertEqual(watched["Seen"], 2)

    def test_biggest_fall_and_rises_are_ignored(self):
        watched, stock = new_watched(), {"a": BIGGEST_FALL + 5, "b": 3, "c": BIGGEST_FALL}
        take(watched, stock, [listed("a", "p", 4), listed("b", "p", 9), listed("c", "p", 0)], set(), NOW)
        day = watched["Items"]["p"]["Days"]["2026-11-10"]
        self.assertEqual((day["Drops"], day["Units"], day["Offers"]), (1, BIGGEST_FALL, ["c"]))

    def test_item_stats_and_old_days_dropped(self):
        watched = new_watched()
        watched["Items"]["p"] = {"Name": "", "Kind": "", "Picture": "", "Sellers": 0, "Low": 0, "Typical": 0,
                                 "Days": {"2026-10-01": {"Drops": 1, "Units": 1, "Offers": [], "Falls": []},
                                          "2026-11-09": {"Drops": 1, "Units": 1, "Offers": [], "Falls": []}}}
        take(watched, {}, [listed("a", "p", 1, 3.0), listed("b", "p", 1, 1.0), listed("c", "p", 1, 2.0),
                           listed("x", "", 1, 9.0)], set(), NOW)
        item = watched["Items"]["p"]
        self.assertEqual((item["Sellers"], item["Low"], item["Typical"]), (3, 1.0, 2.0))
        self.assertEqual(list(item["Days"]), ["2026-11-09"])
        self.assertEqual(list(watched["Items"]), ["p"])
        self.assertTrue(watched["Started"].endswith("Z"))

    def test_stock_and_items_do_not_grow_without_end(self):
        watched, stock = new_watched(), {"gone": 5}
        watched["Items"]["old"] = {"Name": "", "Kind": "", "Picture": "", "Sellers": 1, "Low": 1, "Typical": 1,
                                   "Days": {"2026-10-01": {"Drops": 1, "Units": 1, "Offers": [], "Falls": []}}}
        watched["Items"]["kept"] = {"Name": "", "Kind": "", "Picture": "", "Sellers": 1, "Low": 1, "Typical": 1,
                                    "Days": {"2026-11-09": {"Drops": 1, "Units": 1, "Offers": [], "Falls": []}}}
        take(watched, stock, [listed("a", "p", 3)], set(), NOW)
        self.assertEqual(stock, {"a": 3})
        self.assertEqual(sorted(watched["Items"]), ["kept", "p"])


class Look(unittest.TestCase):
    ITEM = Offer(id="me", title="Rifle", game_id="g", category="CustomItem", environment_id="env",
                 environment_values=["Weapon", "Rifle"], price=5.0, currency="USD", state="Active")

    def test_price_walk_counts_cheaper_tied_next(self):
        pages = [page([row("a", 3.0), row("b", 5.0), row("me", 5.0)], pages=3, records=120),
                 page([row("c", 5.0), row("d", 6.0), row("e", 7.0)], pages=3, records=120)]
        market, transport = api({"/api/v1/item-management/offers": pages})
        standing = market.look(self.ITEM, False)
        self.assertEqual(standing["Total"], 120)
        self.assertEqual(standing["Lowest"], 3.0)
        self.assertEqual(standing["Cheaper"], 1)
        self.assertEqual(standing["Tied"], 2)
        self.assertEqual(standing["Next"], 6.0)
        self.assertEqual(standing["PricePlace"], 3)
        self.assertEqual(standing["You"]["Place"], 3)
        self.assertTrue(standing["You"]["IsYou"])
        self.assertEqual(standing["You"]["Minutes"], 20.0)
        self.assertIsNone(standing["SitePlace"])
        self.assertIsNone(standing["Rivals"])
        self.assertEqual(len(transport.calls), 2)  # stopped at the page that passed your price
        query = transport.calls[0][2]
        self.assertEqual(query["offerSortingCriterion"], "Price")
        self.assertEqual(query["tradeEnvironmentValue1"], "Rifle")
        self.assertNotIn("Authorization", transport.calls[0][3])

    def test_site_walk_finds_place_and_top_rivals(self):
        price = [page([row("a", 4.0), row("me", 5.0), row("z", 9.0)])]
        site = [page([row(f"r{i}", 6.0) for i in range(6)], pages=2), page([row("me", 5.0)], pages=2)]

        def listing(query):
            return (price if "offerSortingCriterion" in query else site).pop(0)

        market, _ = api({"/api/v1/item-management/offers": listing})
        standing = market.look(self.ITEM, True)
        self.assertEqual(standing["PricePlace"], 2)
        self.assertEqual(standing["SitePlace"], 7)
        self.assertEqual([r["Place"] for r in standing["Rivals"]], [1, 2, 3, 4, 5])
        self.assertEqual(standing["You"]["Place"], 7)
        self.assertEqual(standing["Rivals"][0]["Ratings"], 42)

    def test_currency_skips_other_units_and_places_by_cheaper(self):
        offer = Offer(id="me", game_id="g", category="Currency", price=2.0, currency="USD", unit="1M", state="Active")
        rows = [row("a", 1.0, unit="Unit1000"), row("b", 1.5, unit="Unit1000000"), row("me", 2.0, unit="Unit1000000"),
                row("c", 1.8, unit="Unit1000000")]
        market, _ = api({"/api/v1/currency-management/offers": [page(rows)]})
        standing = market.look(offer, False)
        self.assertEqual(standing["Lowest"], 1.5)
        self.assertEqual(standing["Cheaper"], 2)
        self.assertEqual(standing["PricePlace"], 3)

    def test_filter_without_environment_searches_title(self):
        query = filter_of(Offer(title="Gold bar", game_id="g", category="CustomItem"), 2)
        self.assertEqual(query["searchQuery"], "Gold bar")
        self.assertEqual(query["pageIndex"], "2")


class Transport(unittest.TestCase):
    def test_calls_are_spaced_and_429_holds(self):
        clock = Clock()
        replies = [page([]), MarketError("slow", 429, retry_after=30), page([])]
        market, _ = api({"/api/v1/item-management/offers": replies}, clock)
        market.sweep("g", "CustomItem", 1)
        with self.assertRaises(MarketError):
            market.sweep("g", "CustomItem", 1)
        self.assertTrue(market.holding)
        market.sweep("g", "CustomItem", 1)
        self.assertEqual(clock.slept, [1.5, 30])

    def test_currency_offers_are_a_get_with_a_body_and_signed(self):
        mine = page([{"id": "o1", "gameId": "g", "category": "CustomItem", "offerState": "Active",
                      "pricePerUnit": {"amount": 1, "currency": "USD"}, "quantity": 3}])
        currency = page([{"id": "c1", "gameId": "g", "category": "Currency", "unitSystem": "Unit1000000",
                          "pricePerUnit": {"amount": 2, "currency": "USD"}, "gameSeoAlias": "rust-coins"}])
        market, transport = api({"/api/authentication/seller/token": [TOKEN],
                                 "/api/v1/item-management/me/offers/me/search": [mine],
                                 "/api/v1/currency-management/me/offers": [currency]})
        offers = market.offers()
        self.assertEqual([o.id for o in offers], ["o1", "c1"])
        self.assertEqual(offers[1].unit, "1M")
        self.assertEqual(offers[1].title, "Rust Coins")
        method, path, _, headers, body = transport.calls[-1]
        self.assertEqual((method, path), ("GET", "/api/v1/currency-management/me/offers"))
        self.assertEqual(json.loads(body), {"augmentedGameFilter": {"gameId": "g", "category": "Currency"}})
        self.assertEqual(headers["Authorization"], "Bearer AT")

    def test_walks_stop_on_empty_pages(self):
        # A reply that claims 999 pages but holds no rows must not loop.
        market, transport = api({"/api/authentication/seller/token": [TOKEN],
                                 "/api/v1/item-management/me/offers/me/search": lambda q: page([], pages=999)})
        self.assertEqual(market.offers(), [])
        self.assertEqual(sum(1 for call in transport.calls if call[1].endswith("/me/search")), 1)

    def test_currency_walk_has_a_page_cap(self):
        coin = Offer(id="me", title="Coins", game_id="g", category="Currency", environment_id="", environment_values=[],
                     price=5.0, currency="USD", state="Active", unit="1M")
        endless = lambda q: page([row(f"r{q.get('pageIndex')}", 1.0, unit="Unit1000000")], pages=10 ** 6)
        market, transport = api({"/api/v1/currency-management/offers": endless})
        market.look(coin, False)
        self.assertEqual(len(transport.calls), MAX_PAGES)

    def test_span_and_offer_parsing(self):
        self.assertEqual(span_minutes("1.02:03:00"), 1563.0)
        self.assertIsNone(span_minutes("soon"))
        self.assertEqual(to_offer({"offerState": "Closed"}).currency, "USD")


class Hub(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, "market.json")

    def tearDown(self):
        self.dir.cleanup()

    def test_pass_keeps_site_walk_between_site_passes_and_skips_closed(self):
        offers = [Offer(id="o1", state="Active", category="CustomItem"), Offer(id="o2", state="Closed")]
        looks = []

        class FakeApi:
            def offers(self):
                return offers

            def look(self, offer, walk_site):
                looks.append((offer.id, walk_site))
                return {"Total": 3, "Lowest": 1.0, "Cheaper": 0, "Tied": 0, "Next": None, "PricePlace": 1,
                        "SitePlace": 2 if walk_site else None, "Rivals": [] if walk_site else None,
                        "You": {"Place": 2} if walk_site else None}

        clock = [NOW]
        hub = MarketHub(FakeApi(), Settings(), self.path, now=lambda: clock[0], sleep=lambda s: None)
        hub.standings_pass()
        clock[0] = NOW + timedelta(minutes=3)
        hub.standings_pass()
        snap = hub.snapshot()
        self.assertEqual(looks, [("o1", True), ("o1", False)])
        standing = snap["standings"]["o1"]
        self.assertEqual((standing["SitePlace"], standing["Rivals"], standing["You"]), (2, [], {"Place": 2}))
        self.assertEqual(standing["SiteAt"], "2026-11-10T12:00:00.000000Z")
        self.assertEqual(standing["At"], "2026-11-10T12:03:00.000000Z")
        self.assertEqual(snap["siteChecked"], "2026-11-10T12:00:00.000000Z")
        self.assertNotIn("o2", snap["standings"])

    def test_errors_are_kept_as_problem_and_state_survives_restart(self):
        class Broken:
            def offers(self):
                raise MarketError("Eldorado asked for fewer requests.", 429)

        hub = MarketHub(Broken(), Settings(), self.path)
        hub._guarded("pass", hub.standings_pass)
        self.assertEqual(hub.snapshot()["problem"], "Eldorado asked for fewer requests.")
        again = MarketHub(Broken(), Settings(), self.path)
        self.assertEqual(again.snapshot()["problem"], "Eldorado asked for fewer requests.")
        self.assertEqual(set(again.snapshot()), {"watched", "standings", "checked", "siteChecked", "problem"})


if __name__ == "__main__":
    unittest.main()
