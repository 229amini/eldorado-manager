import json
import os
import tempfile
import unittest
from datetime import datetime, timezone

from sellerdesk.ledger import Conflict, Ledger, NotFound, fee_rates, parse_sales

CSV_HEAD = "﻿Order Date,Description,Amount $,Seller Fees $,Net Income $,Category\n"


def order(total=10.0, quantity=4, category="CustomItem", placed="2026-11-01T10:00:00Z", shares=None):
    return {"title": "Rifle", "placed": placed, "quantity": quantity, "total": total, "currency": "USD",
            "category": category, "shares": shares if shares is not None else []}


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, "ledger.json")
        self.ledger = Ledger(self.path)
        self.ann, _ = self.ledger.add_person("Ann", "")
        self.bob, _ = self.ledger.add_person("Bob", "partner")

    def tearDown(self):
        self.dir.cleanup()

    def test_people_and_revision(self):
        self.assertEqual(len(self.ann["id"]), 8)
        self.assertEqual(self.ledger.revision, 2)
        person, revision = self.ledger.update_person(self.bob["id"], {"archived": True})
        self.assertTrue(person["archived"])
        self.assertEqual(revision, 3)
        with self.assertRaises(NotFound):
            self.ledger.update_person("nobody", {"name": "x"})
        with self.assertRaises(ValueError):
            self.ledger.add_person("  ", "")

    def test_share_validation(self):
        a, b = self.ann["id"], self.bob["id"]
        bad = [
            [{"person": a, "units": 0}],
            [{"person": a, "units": 1.5}],
            [{"person": a, "units": 3}, {"person": b, "units": 2}],  # 5 > 4
            [{"person": "ghost", "units": 1}],
            [{"person": a, "units": 1}, {"person": a, "units": 1}],
        ]
        for shares in bad:
            with self.assertRaises(ValueError, msg=shares):
                self.ledger.put_order("o1", order(shares=shares))
        self.assertEqual(self.ledger.revision, 2)  # nothing stored
        stored, _ = self.ledger.put_order("o1", order(shares=[{"person": a, "units": 4}]))
        self.assertEqual(stored["net"], 8.5)  # 15 % fallback for items
        self.assertFalse(stored["netExact"])

    def test_empty_shares_delete_the_order(self):
        self.ledger.put_order("o1", order(shares=[{"person": self.ann["id"], "units": 1}]))
        result, _ = self.ledger.put_order("o1", order(shares=[]))
        self.assertIsNone(result)
        self.assertEqual(self.ledger.snapshot()["orders"], {})

    def test_payment_validation_and_delete(self):
        with self.assertRaises(ValueError):
            self.ledger.add_payment({"person": self.ann["id"], "amount": 0, "date": "2026-11-01"})
        with self.assertRaises(ValueError):
            self.ledger.add_payment({"person": self.ann["id"], "amount": 5, "date": "1/11/2026"})
        with self.assertRaises(ValueError):
            self.ledger.add_payment({"person": "ghost", "amount": 5, "date": "2026-11-01"})
        payment, _ = self.ledger.add_payment({"person": self.ann["id"], "amount": -2.5, "date": "2026-11-01"})
        self.ledger.delete_payment(payment["id"])
        with self.assertRaises(NotFound):
            self.ledger.delete_payment(payment["id"])

    def test_summary_math(self):
        a, b = self.ann["id"], self.bob["id"]
        self.ledger.put_order("o1", order(total=20.0, quantity=4, shares=[{"person": a, "units": 3},
                                                                          {"person": b, "units": 1}]))
        self.ledger.put_order("o2", order(total=100.0, quantity=1, category="Currency",
                                          shares=[{"person": a, "units": 1}]))
        self.ledger.add_payment({"person": a, "amount": 50, "date": "2026-11-02", "note": "cash"})
        rows = {row["person"]: row for row in self.ledger.summary()}
        # o1 net 17.00 (15 %), o2 net 95.00 (5 %)
        self.assertEqual(rows[a], {"person": a, "units": 4, "earned": 107.75, "paid": 50.0, "balance": 57.75,
                                   "orders": 2})
        self.assertEqual(rows[b]["earned"], 4.25)
        self.assertEqual(rows[b]["balance"], 4.25)

    def test_csv_exact_match_vs_estimate(self):
        a = self.ann["id"]
        self.ledger.put_order("exact", order(total=10.0, shares=[{"person": a, "units": 1}]))
        self.ledger.put_order("twins1", order(total=7.0, placed="2026-11-02T10:00:00Z",
                                              shares=[{"person": a, "units": 1}]))
        self.ledger.put_order("late", order(total=12.0, placed="2026-10-01T10:00:00Z",
                                            shares=[{"person": a, "units": 1}]))
        self.ledger.put_order("coins", order(total=50.0, category="Currency", shares=[{"person": a, "units": 1}]))
        csv_text = CSV_HEAD + "\n".join([
            "2026-11-03 08:00:00,Items,10.00,1.60,8.40,Items",
            "2026-11-04 08:00:00,Items,7.00,1.00,6.00,Items",
            "2026-11-05 08:00:00,Items,7.00,1.00,6.00,Items",     # two lines for one order: ambiguous
            "2026-11-05 08:00:00,Items,12.00,2.00,10.00,Items",   # 35 days after placed: too late
            "2026-11-02 08:00:00,Coins,50.00,2.00,48.00,Currency",
        ]) + "\n"
        before = self.ledger.revision
        self.assertTrue(self.ledger.reconcile(csv_text))
        orders = self.ledger.snapshot()["orders"]
        self.assertEqual((orders["exact"]["net"], orders["exact"]["netExact"]), (8.4, True))
        self.assertEqual((orders["coins"]["net"], orders["coins"]["netExact"]), (48.0, True))
        rate = (1.6 + 1.0 + 1.0 + 2.0) / (10 + 7 + 7 + 12)
        self.assertEqual(orders["twins1"]["net"], round(7.0 * (1 - rate), 2))
        self.assertFalse(orders["twins1"]["netExact"])
        self.assertFalse(orders["late"]["netExact"])
        self.assertEqual(self.ledger.revision, before + 1)
        # A second run changes nothing, and the used line is not handed to a later twin.
        self.assertFalse(self.ledger.reconcile(csv_text))
        # Re-saving shares keeps the exact net.
        stored, _ = self.ledger.put_order("exact", order(total=10.0, shares=[{"person": a, "units": 2}]))
        self.assertTrue(stored["netExact"])

    def test_sync_asks_for_45_days(self):
        asked = []
        self.ledger.sync(lambda since, until: asked.append((since, until)) or CSV_HEAD,
                         now=datetime(2026, 11, 10, tzinfo=timezone.utc))
        self.assertEqual((asked[0][1] - asked[0][0]).days, 45)

    def test_saved_atomically_and_reloaded(self):
        self.assertFalse(os.path.exists(self.path + ".new"))
        with open(self.path, encoding="utf-8") as file:
            self.assertEqual(json.load(file)["revision"], 2)
        again = Ledger(self.path)
        self.assertEqual([p["name"] for p in again.snapshot()["people"]], ["Ann", "Bob"])
        self.assertEqual(set(again.snapshot()), {"revision", "people", "orders", "payments", "summary", "feeRates", "costs", "buyers"})

    def test_missing_shares_is_refused(self):
        self.ledger.put_order("o1", order(shares=[{"person": self.ann["id"], "units": 1}]))
        body = order()
        del body["shares"]
        with self.assertRaises(ValueError):
            self.ledger.put_order("o1", body)
        with self.assertRaises(ValueError):
            self.ledger.put_order("o1", {})
        self.assertIn("o1", self.ledger.snapshot()["orders"])

    def test_stale_edit_is_refused(self):
        a, b = self.ann["id"], self.bob["id"]
        stored, _ = self.ledger.put_order("o1", order(shares=[{"person": a, "units": 1}]), expect=0)
        seen = stored["changed"]
        # Another PC saves first; this PC's save, based on what it saw, must not overwrite it.
        self.ledger.put_order("o1", order(shares=[{"person": b, "units": 2}]), expect=seen)
        with self.assertRaises(Conflict):
            self.ledger.put_order("o1", order(shares=[{"person": a, "units": 4}]), expect=seen)
        with self.assertRaises(Conflict):
            self.ledger.put_order("o1", order(shares=[]), expect=seen)
        self.assertEqual(self.ledger.snapshot()["orders"]["o1"]["shares"], [{"person": b, "units": 2}])
        with self.assertRaises(Conflict):
            self.ledger.put_order("o2", order(shares=[{"person": a, "units": 1}]), expect=5)  # it isn't there
        # Without If-Match the write goes through, as before.
        self.ledger.put_order("o1", order(shares=[{"person": a, "units": 4}]))
        person = self.ledger.snapshot()["people"][0]
        self.ledger.update_person(a, {"note": "x"}, expect=person["changed"])
        with self.assertRaises(Conflict):
            self.ledger.update_person(a, {"note": "y"}, expect=person["changed"])

    def test_sync_does_not_count_as_a_change(self):
        stored, _ = self.ledger.put_order("o1", order(total=10.0, shares=[{"person": self.ann["id"], "units": 1}]))
        self.assertTrue(self.ledger.reconcile(CSV_HEAD + "2026-11-03 08:00:00,Items,10.00,1.00,9.00,Items\n"))
        self.ledger.put_order("o1", order(total=10.0, shares=[{"person": self.ann["id"], "units": 2}]),
                              expect=stored["changed"])

    def test_number_limits(self):
        a = self.ann["id"]
        for amount in (10 ** 400, 1e308, -2e9, float("inf")):
            with self.assertRaises(ValueError, msg=amount):
                self.ledger.add_payment({"person": a, "amount": amount, "date": "2026-11-02"})
        with self.assertRaises(ValueError):
            self.ledger.put_order("o1", order(quantity=10 ** 400, shares=[{"person": a, "units": 1}]))
        with self.assertRaises(ValueError):
            self.ledger.put_order("o1", order(total=1e300, shares=[{"person": a, "units": 1}]))
        with self.assertRaises(ValueError):
            self.ledger.put_order("o1", order(shares=[{"person": a, "units": 10 ** 400}]))

    def test_duplicate_names(self):
        with self.assertRaises(ValueError):
            self.ledger.add_person("ann", "")
        with self.assertRaises(ValueError):
            self.ledger.update_person(self.bob["id"], {"name": "ANN"})
        self.ledger.update_person(self.ann["id"], {"name": "Ann"})  # renaming to itself is fine

    def test_costs_and_buyer_notes(self):
        stored, _ = self.ledger.set_cost("offer-1", {"cost": 0.25, "title": "Seeds"})
        self.assertEqual(stored["cost"], 0.25)
        self.ledger.set_buyer_note("NightOwl_77", {"note": "  Big orders, pays fast  "})
        snap = Ledger(self.path).snapshot()   # survives a reload
        self.assertEqual(snap["costs"]["offer-1"]["cost"], 0.25)
        self.assertEqual(snap["buyers"]["nightowl_77"], {"name": "NightOwl_77", "note": "Big orders, pays fast",
                                                         "changed": snap["buyers"]["nightowl_77"]["changed"]})
        self.assertIn("Items", snap["feeRates"])
        for bad in ({"cost": -1}, {"cost": "cheap"}, {"cost": True}, {"cost": 1e12}):
            with self.assertRaises(ValueError):
                self.ledger.set_cost("offer-1", bad)
        with self.assertRaises(ValueError):
            self.ledger.set_buyer_note("x", {"note": "n" * 301})
        self.ledger.set_cost("offer-1", {"cost": None})
        self.ledger.set_buyer_note("nightowl_77", {"note": ""})   # any case finds the same buyer
        snap = self.ledger.snapshot()
        self.assertEqual((snap["costs"], snap["buyers"]), ({}, {}))

    def test_damaged_cost_or_note_is_left_out(self):
        self.ledger.set_cost("good", {"cost": 1})
        with open(self.path, encoding="utf-8") as file:
            data = json.load(file)
        data["costs"]["bad"] = {"cost": "x"}
        data["buyers"] = {"a": {"name": "A"}}
        with open(self.path, "w", encoding="utf-8") as file:
            json.dump(data, file)
        snap = Ledger(self.path).snapshot()
        self.assertEqual((list(snap["costs"]), snap["buyers"]), (["good"], {}))

    def test_damaged_record_is_left_out(self):
        with open(self.path, encoding="utf-8") as file:
            data = json.load(file)
        data["orders"] = {"good": {"quantity": 1, "net": 1.0, "shares": [{"person": self.ann["id"], "units": 1}]},
                          "bad": {"quantity": 1, "shares": [{"units": 1}]}}
        data["payments"] = [{"id": "p1", "person": self.ann["id"], "amount": "lots"}]
        with open(self.path, "w", encoding="utf-8") as file:
            json.dump(data, file)
        again = Ledger(self.path)
        snap = again.snapshot()
        self.assertEqual(list(snap["orders"]), ["good"])
        self.assertEqual(snap["payments"], [])
        self.assertEqual(snap["summary"][0]["earned"], 1.0)
        self.assertTrue(any(name.startswith("ledger.json.bad-") for name in os.listdir(self.dir.name)))

    def test_unreadable_file_is_not_replaced(self):
        os.remove(self.path)
        os.mkdir(self.path)  # opening a directory raises an OSError that isn't damage
        with self.assertRaises(OSError):
            Ledger(self.path)
        self.assertTrue(os.path.isdir(self.path))

    def test_broken_json_is_moved_aside(self):
        with open(self.path, "w", encoding="utf-8") as file:
            file.write("{broken")
        again = Ledger(self.path)
        self.assertEqual(again.snapshot()["people"], [])
        self.assertTrue(any(name.startswith("ledger.json.bad-") for name in os.listdir(self.dir.name)))

    def test_fee_rates_fallback(self):
        rates = fee_rates(parse_sales(CSV_HEAD + "2026-11-03 08:00:00,Items,100.00,15.20,84.80,Items\n"))
        self.assertAlmostEqual(rates["Items"], 0.152)
        self.assertEqual(rates["Currency"], 0.05)


if __name__ == "__main__":
    unittest.main()
