using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SellerDesk
{
    /// <summary>
    /// The Finance tab: who each sold unit belongs to, what they earned from it after Eldorado's fee, and what
    /// you already paid them. The ledger lives on your shared server, so every PC sees the same balances.
    /// </summary>
    public partial class DashboardWindow
    {
        private Hub hub;
        private Dictionary<string, object> ledger;
        private string financeNote = "";
        private bool financeBusy;

        private Dictionary<string, object> FinanceToWeb() => new Dictionary<string, object>
        {
            ["ready"] = ledger != null,
            ["note"] = financeNote,
            ["busy"] = financeBusy,
            ["ledger"] = ledger,
        };

        /// <summary>Runs on the market thread every pass, so a change made on another PC shows up here too.</summary>
        private void ReadLedger()
        {
            if (hub == null) return;
            try
            {
                var fresh = hub.Ledger();
                Post(() =>
                {
                    ledger = fresh;
                    if (!financeBusy && financeNote.StartsWith("Can't", StringComparison.Ordinal)) financeNote = "";
                    Later();
                });
            }
            catch (MarketError error)
            {
                Post(() =>
                {
                    financeNote = error.Message;
                    Later();
                });
            }
        }

        private bool HandleFinance(string type, Func<string, string> text, Dictionary<string, object> message)
        {
            switch (type)
            {
                case "addPerson":
                    var name = text("name").Trim();
                    if (name.Length == 0) return Told("Give the person a name.");
                    Change(() => hub.AddPerson(name, text("note").Trim()), $"{name} added");
                    return true;
                case "editPerson":
                    var change = new Dictionary<string, object>();
                    if (message.ContainsKey("name")) change["name"] = text("name").Trim();
                    if (message.ContainsKey("note")) change["note"] = text("note").Trim();
                    if (message.ContainsKey("archived")) change["archived"] = text("archived") == "True" || text("archived") == "true";
                    var person = text("person");
                    var seenPerson = Seen(text("changed"), Items(ledger ?? new Dictionary<string, object>(), "people").FirstOrDefault(p => Str(p, "id") == person));
                    Change(() => hub.EditPerson(person, change, seenPerson), "Saved");
                    return true;
                case "assign":
                    Assign(text("order"), message.TryGetValue("shares", out var shares) ? shares as IEnumerable : null, text("changed"));
                    return true;
                case "addPayment":
                    if (!double.TryParse(text("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount == 0)
                        return Told("Enter the amount you paid, like 25 or 12.50.");
                    var day = text("date").Length > 0 ? text("date") : DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    Change(() => hub.AddPayment(new Dictionary<string, object>
                    {
                        ["person"] = text("person"), ["amount"] = Math.Round(amount, 2), ["date"] = day, ["note"] = text("note").Trim(),
                    }), "Payment recorded");
                    return true;
                case "deletePayment":
                    Change(() => hub.DeletePayment(text("payment")), "Payment removed");
                    return true;
                case "setCost":
                    var costText = text("cost").Trim().TrimStart('$');
                    double? cost = null;
                    if (costText.Length > 0)
                    {
                        if (!double.TryParse(costText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                            return Told("Enter what one unit costs you, like 0.05 or 2.");
                        cost = parsed;
                    }
                    Change(() => hub.SetCost(text("offer"), cost, text("title")), cost == null ? "Cost removed" : "Cost saved");
                    return true;
                case "buyerNote":
                    var buyer = text("buyer").Trim();
                    if (buyer.Length == 0) return true;
                    Change(() => hub.SetBuyerNote(buyer, text("note").Trim()), "Note saved");
                    return true;
                case "exportFinance":
                    Export(text("person"), text("from"), text("until"));
                    return true;
                default:
                    return false;
            }
        }

        private bool Told(string note)
        {
            financeNote = note;
            Later();
            return true;
        }

        /// <summary>The "changed" value the page showed, else the one this PC holds for the record (0 = not stored yet).</summary>
        private static long Seen(string fromPage, Dictionary<string, object> held) =>
            long.TryParse(fromPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shown) ? shown
            : held != null && long.TryParse(Str(held, "changed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var known) ? known : 0;

        private void Assign(string orderId, IEnumerable shares, string seenText)
        {
            if (!orders.TryGetValue(orderId, out var order))
            {
                Told("That order isn't in the last few months of orders this PC read.");
                return;
            }
            var list = new List<Dictionary<string, object>>();
            foreach (var raw in shares ?? new object[0])
            {
                if (!(raw is IDictionary share)) continue;
                var units = Convert.ToInt32(share["units"], CultureInfo.InvariantCulture);
                if (units > 0) list.Add(new Dictionary<string, object> { ["person"] = Convert.ToString(share["person"], CultureInfo.InvariantCulture), ["units"] = units });
            }
            if (list.Sum(s => (int)s["units"]) > order.Quantity)
            {
                Told($"This order has {order.Quantity} units, so the shares can't add up to more.");
                return;
            }
            var seen = Seen(seenText, ledger != null && Part(ledger, "orders").TryGetValue(order.Id, out var held) ? held : null);
            Change(() => hub.SaveOrder(order.Id, new Dictionary<string, object>
            {
                ["title"] = order.Title,
                ["placed"] = order.Placed.ToString("o", CultureInfo.InvariantCulture),
                ["quantity"] = order.Quantity,
                ["total"] = order.Total,
                ["currency"] = order.Currency,
                ["category"] = order.Category,
                ["shares"] = list,
            }, seen), list.Count == 0 ? "Order unassigned" : "Order assigned");
        }

        /// <summary>Every write is followed by a fresh read, so the page shows the server's numbers, never a guess.</summary>
        private async void Change(Func<Dictionary<string, object>> call, string done)
        {
            if (hub == null)
            {
                Told("Finance needs your shared server. Add it in Settings, Eldorado key.");
                return;
            }
            if (financeBusy) return;
            financeBusy = true;
            financeNote = "Saving";
            Later();
            try
            {
                var fresh = await Task.Run(() =>
                {
                    call();
                    return hub.Ledger();
                });
                ledger = fresh;
                financeNote = done;
            }
            catch (MarketError error)
            {
                Config.Log("finance: " + error.Message);
                financeNote = error.Message;
                if (error.Status == 409)   // another PC saved first: show what it saved
                {
                    try { ledger = await Task.Run(() => hub.Ledger()); }
                    catch (MarketError) { }
                }
            }
            financeBusy = false;
            Later();
        }

        // ---------- export ----------

        private void Export(string personId, string fromText, string untilText)
        {
            if (ledger == null) return;
            var from = Day(fromText) ?? DateTime.MinValue;
            var until = (Day(untilText) ?? DateTime.MaxValue.Date).AddDays(1);   // inclusive of the last day
            var people = Items(ledger, "people").Where(p => personId.Length == 0 || Str(p, "id") == personId).ToList();
            if (people.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = $"Eldorado {(people.Count == 1 ? Str(people[0], "name") : "settlement")} {Span(fromText, untilText)}.xlsx",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                Xlsx.Write(dialog.FileName, Workbook(people, from, until));
                financeNote = "Saved " + Path.GetFileName(dialog.FileName);
                Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                financeNote = "Couldn't save it: " + error.Message;
            }
            Later();
        }

        private List<Sheet> Workbook(List<Dictionary<string, object>> people, DateTime from, DateTime until)
        {
            var summary = new Sheet
            {
                Name = "Summary",
                Header = { "Person", "Units", "Earned in period", "Paid in period", "Balance today", "Note" },
                Widths = { 22, 8, 16, 16, 16, 40 },
            };
            var sheets = new List<Sheet> { summary };
            var ledgerOrders = Part(ledger, "orders");
            var payments = Items(ledger, "payments");
            var balances = Items(ledger, "summary").ToDictionary(s => Str(s, "person"), s => Num(s, "balance"));
            foreach (var person in people)
            {
                var id = Str(person, "id");
                var sheet = new Sheet
                {
                    Name = Str(person, "name"),
                    Header = { "Date", "Item", "Buyer", "Units", "Of", "Order total", "Order net", "Their share", "Net is", "Order id" },
                    Widths = { 17, 34, 16, 7, 6, 12, 12, 12, 10, 38 },
                };
                var units = 0;
                var earned = 0.0;
                foreach (var entry in ledgerOrders.OrderBy(e => Str(e.Value, "placed")))
                {
                    var o = entry.Value;
                    var placed = Moment(Str(o, "placed"));
                    if (placed == null || placed < from || placed >= until) continue;
                    foreach (var share in Items(o, "shares").Where(s => Str(s, "person") == id))
                    {
                        var mine = (int)Num(share, "units");
                        var quantity = Math.Max(1, (int)Num(o, "quantity"));
                        var part = Math.Round(Num(o, "net") * mine / quantity, 2);
                        units += mine;
                        earned += part;
                        sheet.Rows.Add(new object[]
                        {
                            placed.Value, Str(o, "title"), orders.TryGetValue(entry.Key, out var known) ? known.Buyer : "",
                            mine, quantity, Num(o, "total"), Num(o, "net"), part,
                            Str(o, "netExact") == "True" ? "Exact" : "Estimated", entry.Key,
                        });
                    }
                }
                sheet.Rows.Add(new object[] { "Total", "", "", units, null, null, null, new Xlsx.Total(Math.Round(earned, 2)) });
                sheet.Rows.Add(new object[0]);
                sheet.Rows.Add(new object[] { "Payments" });
                var paid = 0.0;
                foreach (var payment in payments.Where(p => Str(p, "person") == id).OrderBy(p => Str(p, "date")))
                {
                    var when = Day(Str(payment, "date"));
                    if (when == null || when < from || when >= until) continue;
                    paid += Num(payment, "amount");
                    sheet.Rows.Add(new object[] { when.Value, Str(payment, "note"), null, null, null, null, null, Num(payment, "amount") });
                }
                sheet.Rows.Add(new object[] { "Total paid", "", "", null, null, null, null, new Xlsx.Total(Math.Round(paid, 2)) });
                sheet.Rows.Add(new object[] { "Balance today", "", "", null, null, null, null, new Xlsx.Total(balances.TryGetValue(id, out var left) ? left : 0) });
                sheets.Add(sheet);
                summary.Rows.Add(new object[] { Str(person, "name"), units, Math.Round(earned, 2), Math.Round(paid, 2), balances.TryGetValue(id, out var b) ? b : 0, Str(person, "note") });
            }
            return sheets;
        }

        private static string Span(string from, string until) =>
            from.Length == 0 && until.Length == 0 ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : $"{(from.Length > 0 ? from : "start")} to {(until.Length > 0 ? until : "today")}";

        private static DateTime? Day(string text) =>
            DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateTime?)null;

        private static DateTime? Moment(string text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : (DateTime?)null;

        private static Dictionary<string, Dictionary<string, object>> Part(Dictionary<string, object> from, string key) =>
            from.TryGetValue(key, out var value) && value is Dictionary<string, object> all
                ? all.Where(e => e.Value is Dictionary<string, object>).ToDictionary(e => e.Key, e => (Dictionary<string, object>)e.Value)
                : new Dictionary<string, Dictionary<string, object>>();

        private static List<Dictionary<string, object>> Items(Dictionary<string, object> from, string key) =>
            from.TryGetValue(key, out var value) && value is IEnumerable list && !(value is string)
                ? list.OfType<Dictionary<string, object>>().ToList()
                : new List<Dictionary<string, object>>();

        private static string Str(Dictionary<string, object> from, string key) =>
            from.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";

        private static double Num(Dictionary<string, object> from, string key) =>
            from.TryGetValue(key, out var value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;
    }
}
