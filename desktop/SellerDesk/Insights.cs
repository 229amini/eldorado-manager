using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace SellerDesk
{
    /// <summary>One offer on one day. Rank and price history can't be read back from Eldorado, so it starts when the app does.</summary>
    public sealed class Mark
    {
        public string Day;
        public string Title;
        public double Price;
        public int? Site;
        public int Stock;
    }

    /// <summary>What the dashboard remembers between runs.</summary>
    public sealed class Logbook
    {
        private const int KeepDays = 90;
        private static readonly string BookPath = Path.Combine(Config.Home, "history.json");
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Dictionary<string, List<Mark>> Offers = new Dictionary<string, List<Mark>>();
        public int[] Online = new int[24];    // passes that found your offers visible, by local hour
        public int[] Offline = new int[24];   // passes that found Eldorado had you offline
        public Dictionary<string, int> Bundles = new Dictionary<string, int>();   // offer id -> items per unit, where the title reads wrong

        /// <summary>Keeps the day's last reading of each listed offer. Only the price at the end of a day counts as a change.</summary>
        public void Note(IEnumerable<OfferRow> rows, DateTime now)
        {
            var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var oldest = now.AddDays(-KeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var row in rows.Where(r => r.State != "Closed"))
            {
                if (!Offers.TryGetValue(row.Offer.Id, out var marks)) Offers[row.Offer.Id] = marks = new List<Mark>();
                var earlier = marks.FirstOrDefault(m => m.Day == day);
                marks.RemoveAll(m => m.Day == day || string.CompareOrdinal(m.Day, oldest) < 0);
                // An offline offer has no place, so the day keeps the last one it had.
                marks.Add(new Mark { Day = day, Title = row.Title, Price = row.LivePrice, Site = row.SiteValue ?? earlier?.Site, Stock = row.Offer.Quantity });
            }
        }

        public void NotePresence(bool offline, int hour)
        {
            if (offline) Offline[hour]++;
            else Online[hour]++;
        }

        public static Logbook Load()
        {
            try
            {
                return File.Exists(BookPath) ? Json.Deserialize<Logbook>(File.ReadAllText(BookPath)) ?? new Logbook() : new Logbook();
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException)
            {
                Config.Log("logbook: " + error.Message);
                return new Logbook();
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(BookPath, Json.Serialize(this));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Config.Log("logbook: " + error.Message);
            }
        }
    }

    /// <summary>
    /// Turns orders, offers and the market watch into what a seller can act on. It only reads and suggests:
    /// nothing here changes an offer. Each line says what was measured, and an estimate is called one.
    /// </summary>
    public static class Insights
    {
        public const int Days = 30;
        private const int MarketDays = 7, MarketRows = 30, EarlyHours = 48, MostRaises = 5, MostChanges = 8, MostLow = 3;
        private const int LowStockDays = 7, EffectDays = 7, FewestEffectDays = 3, FewestPresenceChecks = 60;
        private const double SmallestRaise = 0.05;

        public static Dictionary<string, object> Build(List<OfferRow> rows, IEnumerable<SoldOrder> all, Logbook book, Watched watched, DateTime now)
        {
            var since = now.AddDays(-Days);
            var recent = all.Where(o => o.Placed >= since).ToList();
            var sold = recent.Where(o => o.IsSale).ToList();
            var late = recent.Where(o => o.IsLate).ToList();
            var took = sold.Where(o => o.TookMinutes != null).Select(o => o.TookMinutes.Value).OrderBy(m => m).ToList();
            var promise = sold.Where(o => o.WindowSeconds != null).GroupBy(o => o.WindowSeconds.Value).OrderByDescending(g => g.Count()).Select(g => g.Key / 60.0).FirstOrDefault();
            var buyers = sold.Where(o => o.Buyer.Length > 0).GroupBy(o => o.Buyer).ToList();
            var hours = Enumerable.Range(0, 24).Select(hour => new Dictionary<string, object>
            {
                ["hour"] = hour,
                ["sold"] = sold.Count(o => o.Placed.ToLocalTime().Hour == hour),
                ["late"] = late.Count(o => o.Placed.ToLocalTime().Hour == hour),
            }).ToList();
            return new Dictionary<string, object>
            {
                ["days"] = Days,
                ["sold"] = sold.Count,
                ["lostAmount"] = Math.Round(late.Sum(o => o.Total), 2),
                ["lostCount"] = late.Count,
                ["typical"] = took.Count == 0 ? "" : OfferRow.Span(took[took.Count / 2]),
                ["promise"] = promise > 0 ? OfferRow.Span(promise) : "",
                ["over"] = promise > 0 ? took.Count(m => m > promise) : 0,
                ["buyers"] = buyers.Count,
                ["again"] = buyers.Count(g => g.Count() > 1),
                ["good"] = recent.Count(o => o.Review == "Positive"),
                ["bad"] = recent.Count(o => o.Review == "Negative"),
                ["unreviewed"] = sold.Count(o => o.Review.Length == 0),
                ["hours"] = hours,
                ["moves"] = Moves(rows, sold, late, book),
                ["changes"] = Changes(book, all.Where(o => o.IsSale).ToList(), now),
                ["market"] = MarketToWeb(rows, watched, book, now),
            };
        }

        private static List<Dictionary<string, object>> Moves(List<OfferRow> rows, List<SoldOrder> sold, List<SoldOrder> late, Logbook book)
        {
            var moves = new List<Dictionary<string, object>>();
            void Add(string key, string kind, string title, string text, string offer = "") => moves.Add(new Dictionary<string, object>
            {
                ["key"] = key, ["kind"] = kind, ["title"] = title, ["text"] = text, ["offer"] = offer,
            });

            if (late.Count > 0)
            {
                var worst = late.GroupBy(o => o.Placed.ToLocalTime().Hour / 6).OrderByDescending(g => g.Count()).First();
                var from = worst.Key * 6;
                Add("late", "fix", $"Late orders cost you ${late.Sum(o => o.Total):0.00}",
                    $"Eldorado cancelled {Count(late.Count, "order")} because delivery missed the promise. {worst.Count()} of {late.Count} came in between {from:00}:00 and {(from + 6) % 24:00}:00. "
                    + "A longer promise, or pausing offers while you're away, stops this. Change many on the Offers tab sets the promise for a whole game.");
            }

            var offline = Enumerable.Range(0, 24).Sum(h => book.Offline[h]);
            var checks = offline + Enumerable.Range(0, 24).Sum(h => book.Online[h]);
            if (checks >= FewestPresenceChecks && offline * 10 >= checks)
                Add("offline", "fix", $"Offline for {offline * 100 / checks}% of checks",
                    "Buyers can't see your offers while Eldorado has you offline, so this time sells nothing. This is counted only while the dashboard is open.");

            var live = rows.Where(r => IsLive(r) && r.Offer.Currency == "USD").ToList();
            foreach (var raise in live.Select(r => Raise(r, sold)).Where(r => r != null).OrderByDescending(r => r.Gain).Take(MostRaises))
                Add("raise" + raise.Row.Offer.Id, "price", $"{raise.Row.Title}: room to raise",
                    $"You're the only seller at {OfferRow.FormatPrice(raise.Row.LivePrice)} and the next is at {OfferRow.FormatPrice(raise.Row.NextPrice.Value)}. "
                    + $"{OfferRow.FormatPrice(raise.Price)} still keeps you cheapest."
                    + (raise.Gain >= 0.01 ? $" At the last {Days} days' sales that's about ${raise.Gain:0.00} more, if sales hold." : ""),
                    raise.Row.Offer.Id);

            var low = rows.Where(r => IsLive(r) && !r.Offer.IsCurrency).Select(r => new
            {
                Row = r,
                Units = sold.Where(o => o.OfferId == r.Offer.Id).Sum(o => o.Quantity),
            }).Where(x => x.Units >= 2).Select(x => new { x.Row, x.Units, Left = x.Row.Offer.Quantity / (x.Units / (double)Days) })
                .Where(x => x.Left < LowStockDays).OrderBy(x => x.Left).Take(MostLow);
            foreach (var item in low)
                Add("low" + item.Row.Offer.Id, "stock", $"{item.Row.Title}: running low",
                    $"{item.Row.Offer.Quantity} left and {item.Units} sold in {Days} days, so about {Math.Max(item.Left, 0):0} days of stock at this pace.", item.Row.Offer.Id);

            var earners = sold.GroupBy(o => o.OfferId).OrderByDescending(g => g.Sum(o => o.Total)).ToList();
            var liveCount = rows.Count(IsLive);
            var idle = rows.Count(r => IsLive(r) && sold.All(o => o.OfferId != r.Offer.Id));
            if (earners.Count > 0 && idle > 0 && sold.Sum(o => o.Total) > 0)
            {
                var top = earners[0];
                var share = top.Sum(o => o.Total) / sold.Sum(o => o.Total);
                Add("idle", "note", $"{idle} of {liveCount} live offers sold nothing",
                    $"{top.First().Title} brought in {share:P0} of your sales in {Days} days. The market list below shows what other sellers are moving, if you want to swap the quiet ones.");
            }
            return moves;
        }

        // Offline follows your Eldorado status, so these offers come back by themselves.
        private static bool IsLive(OfferRow row) => row.IsListed || row.State == "Offline";

        private sealed class Lift
        {
            public OfferRow Row;
            public double Price;
            public double Gain;
        }

        /// <summary>Being cheapest by a wide gap gives away margin, and price alone doesn't decide the site order.</summary>
        private static Lift Raise(OfferRow row, List<SoldOrder> sold)
        {
            if (row.CheaperCount != 0 || row.Tied != 0 || row.NextPrice == null || row.LivePrice <= 0) return null;
            var next = row.NextPrice.Value;
            var price = OfferRow.Fit(next >= 0.02 ? next - 0.01 : next * 0.9);
            if (price < row.LivePrice * (1 + SmallestRaise) || price >= next) return null;
            var earned = sold.Where(o => o.OfferId == row.Offer.Id).Sum(o => o.Total);
            return new Lift { Row = row, Price = price, Gain = earned * (price / row.LivePrice - 1) };
        }

        /// <summary>What each price change did. Sales either side of it come from your orders, place from the logbook.</summary>
        private static List<Dictionary<string, object>> Changes(Logbook book, List<SoldOrder> sold, DateTime now)
        {
            var changes = new List<Dictionary<string, object>>();
            foreach (var pair in book.Offers)
            {
                var marks = pair.Value.OrderBy(m => m.Day, StringComparer.Ordinal).ToList();
                for (var i = 1; i < marks.Count; i++)
                {
                    if (Math.Abs(marks[i].Price - marks[i - 1].Price) < 0.0000001) continue;
                    var when = DateTime.SpecifyKind(DateTime.ParseExact(marks[i].Day, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    var after = Math.Min(EffectDays, (int)(now - when).TotalDays);
                    var orders = sold.Where(o => o.OfferId == pair.Key).ToList();
                    changes.Add(new Dictionary<string, object>
                    {
                        ["key"] = pair.Key + marks[i].Day,
                        ["offer"] = pair.Key,
                        ["title"] = marks[i].Title,
                        ["day"] = when.ToString("d MMM", CultureInfo.InvariantCulture),
                        ["sort"] = marks[i].Day,
                        ["from"] = OfferRow.FormatPrice(marks[i - 1].Price),
                        ["to"] = OfferRow.FormatPrice(marks[i].Price),
                        ["up"] = marks[i].Price > marks[i - 1].Price,
                        ["placeBefore"] = marks[i - 1].Site,
                        ["placeNow"] = marks[marks.Count - 1].Site,
                        ["soldBefore"] = orders.Count(o => o.Placed < when && o.Placed >= when.AddDays(-Math.Max(after, 1))),
                        ["soldAfter"] = orders.Count(o => o.Placed >= when && o.Placed < when.AddDays(Math.Max(after, 1))),
                        ["daysAfter"] = after,
                        ["early"] = after < FewestEffectDays,
                    });
                }
            }
            return changes.OrderByDescending(c => (string)c["sort"], StringComparer.Ordinal).Take(MostChanges).ToList();
        }

        /// <summary>How many of the item one unit of your offer holds, and whether you set it or the title said so.</summary>
        public static int SizeOf(OfferRow row, Logbook book, out bool yours)
        {
            yours = book != null && book.Bundles.TryGetValue(row.Offer.Id, out var set) && set > 0;
            if (yours) return book.Bundles[row.Offer.Id];
            var values = row.Offer.EnvironmentValues;
            return Bundle.Size(row.Offer.Title, values.Length > 0 ? values.Last() : "");
        }

        // Falls from before 1.6.2 have no size, so their price can't be split into items; they are left out.
        private static List<Fall> Sized(IEnumerable<Fall> falls) => falls.Where(f => f.Size > 0).ToList();

        // A fall's price as the price of `size` items.
        private static double Scaled(Fall fall, int size) => fall.Price / fall.Size * size;

        private static Dictionary<string, object> MarketToWeb(List<OfferRow> rows, Watched watched, Logbook book, DateTime now)
        {
            if (watched == null || watched.Sweeps == 0)
                return new Dictionary<string, object> { ["sweeps"] = 0, ["rows"] = new List<object>() };
            var since = now.AddDays(-MarketDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var mine = rows.Where(r => r.State != "Closed" && !r.Offer.IsCurrency && r.Offer.EnvironmentValues.Length > 0)
                .GroupBy(r => Match(r.Offer.EnvironmentValues.First(), r.Offer.EnvironmentValues.Last())).ToDictionary(g => g.Key, g => g.First());
            var started = DateTime.Parse(watched.Started, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var list = watched.Items.Select(pair =>
            {
                var days = pair.Value.Days.Where(d => string.CompareOrdinal(d.Key, since) >= 0).Select(d => d.Value).ToList();
                mine.TryGetValue(Match(pair.Value.Kind, pair.Value.Name), out var row);
                var falls = Sized(days.SelectMany(d => d.Falls ?? new List<Fall>()));
                // Priced for your offer's size when you sell it, otherwise for the size most sales came in.
                var size = row != null ? SizeOf(row, book, out _)
                    : falls.Count == 0 ? 1 : falls.GroupBy(f => f.Size).OrderByDescending(g => g.Count()).First().Key;
                return new
                {
                    Item = pair.Value,
                    Key = pair.Key,
                    Drops = days.Sum(d => d.Drops),
                    Movers = days.SelectMany(d => d.Offers).Distinct().Count(),
                    Units = days.Sum(d => d.Units),
                    Row = row,
                    Size = size,
                    Low = pair.Value.Each != null && pair.Value.Each.Count > 0 ? pair.Value.Each[0] * size : (double?)null,
                    SoldAt = SoldAt(falls, size),
                    Top = falls.GroupBy(f => f.Offer).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(f => f.Units))
                        .Select(g => new { Last = g.Last(), Sales = g.Count() }).FirstOrDefault(),
                };
            }).Where(x => x.Drops > 0).OrderByDescending(x => x.Movers).ThenByDescending(x => x.Drops).ThenByDescending(x => x.Units).Take(MarketRows);
            return new Dictionary<string, object>
            {
                ["sweeps"] = watched.Sweeps,
                ["since"] = started.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture),
                ["early"] = (now - started).TotalHours < EarlyHours,
                ["seen"] = watched.Seen,
                ["total"] = watched.Total,
                ["rows"] = list.Select(x => new Dictionary<string, object>
                {
                    ["key"] = x.Key,
                    ["name"] = x.Item.Name,
                    ["kind"] = x.Item.Kind,
                    ["picture"] = x.Item.Picture,
                    ["drops"] = x.Drops,
                    ["movers"] = x.Movers,
                    ["low"] = x.Low == null ? "" : OfferRow.FormatPrice(x.Low.Value),
                    ["per"] = x.Size,
                    ["soldAt"] = x.SoldAt == null ? "" : OfferRow.FormatPrice(x.SoldAt.Value),
                    ["topSeller"] = x.Top?.Last.Seller ?? "",
                    ["topPrice"] = x.Top == null ? "" : OfferRow.FormatPrice(Scaled(x.Top.Last, x.Size)),
                    ["topSales"] = x.Top?.Sales ?? 0,
                    ["topLink"] = x.Top?.Last.Link ?? "",
                    ["offer"] = x.Row?.Offer.Id ?? "",
                    ["yours"] = x.Row == null ? "" : OfferRow.FormatPrice(x.Row.LivePrice),
                    ["above"] = x.Row != null && x.SoldAt != null && x.Row.LivePrice > x.SoldAt.Value * (1 + SmallestRaise),
                }).ToList(),
            };
        }

        /// <summary>
        /// The middle price of the sales seen, one vote per sale. Listed prices mislead: a few sellers park
        /// offers far above the market and never sell them, which drags a middle of listed prices up.
        /// </summary>
        private static double? SoldAt(List<Fall> falls, int size)
        {
            if (falls.Count == 0) return null;
            var prices = falls.Select(f => Scaled(f, size)).OrderBy(p => p).ToList();
            return prices[prices.Count / 2];
        }

        /// <summary>
        /// Where each of your offers stands against the sales the market watch saw for the same item in the
        /// last week: the price range most of them happened in, their middle, the unit-weighted average, and the
        /// offer that sold most. The cheapest listing alone pushes a seller to give margin away for nothing.
        /// </summary>
        public static Dictionary<string, object> Pricing(List<OfferRow> rows, Watched watched, Logbook book, DateTime now)
        {
            var result = new Dictionary<string, object>();
            if (watched == null || watched.Sweeps == 0) return result;
            var since = now.AddDays(-MarketDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var items = watched.Items.Values.GroupBy(i => Match(i.Kind, i.Name)).ToDictionary(g => g.Key, g => g.First());
            foreach (var row in rows.Where(r => !r.Offer.IsCurrency && r.Offer.Currency == "USD" && r.Offer.EnvironmentValues.Length > 0))
            {
                if (!items.TryGetValue(Match(row.Offer.EnvironmentValues.First(), row.Offer.EnvironmentValues.Last()), out var item)) continue;
                var falls = Sized(item.Days.Where(d => string.CompareOrdinal(d.Key, since) >= 0).SelectMany(d => d.Value.Falls ?? new List<Fall>()));
                // Every price below is for as many items as one unit of your offer holds, so it compares with yours.
                var size = SizeOf(row, book, out var yours);
                var listed = (item.Each ?? new List<double>()).Select(p => p * size).ToList();
                var entry = new Dictionary<string, object> { ["sales"] = falls.Count, ["size"] = size, ["sizeYours"] = yours, ["listed"] = listed };
                if (listed.Count > 0) entry["low"] = listed[0];
                result[row.Offer.Id] = entry;
                if (falls.Count == 0) continue;
                var prices = falls.Select(f => Scaled(f, size)).OrderBy(p => p).ToList();
                var from = prices[prices.Count / 4];
                var to = prices[Math.Min(prices.Count - 1, prices.Count * 3 / 4)];
                var middle = prices[prices.Count / 2];
                var sold = falls.Sum(f => (double)f.Units * f.Size);
                var average = falls.Sum(f => Scaled(f, size) * f.Units * f.Size) / Math.Max(1, sold);
                var top = falls.GroupBy(f => f.Offer).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(f => (double)f.Units * f.Size)).First();
                entry["soldAt"] = middle;
                entry["average"] = Math.Round(average, 6);
                entry["from"] = from;
                entry["to"] = to;
                entry["topPrice"] = Scaled(top.Last(), size);
                entry["topSeller"] = top.Last().Seller;
                entry["topSales"] = top.Count();
                entry["topLink"] = top.Last().Link;
                entry["units"] = (long)sold;
                Advise(entry, row.LivePrice, from, middle, to, falls.Count);
            }
            return result;
        }

        /// <summary>A suggestion, never a change: few sales make a weak signal, so it says so instead of advising.</summary>
        private static void Advise(Dictionary<string, object> entry, double yours, double from, double middle, double to, int sales)
        {
            const int FewestSales = 4;
            if (sales < FewestSales)
            {
                entry["verdict"] = "few";
                entry["advice"] = $"Too few sales to judge ({sales}).";
                return;
            }
            var target = OfferRow.Fit(middle);
            entry["suggest"] = target;
            var range = $"{OfferRow.FormatPrice(from)} to {OfferRow.FormatPrice(to)}";
            if (yours < from * (1 - SmallestRaise))
            {
                entry["verdict"] = "low";
                entry["advice"] = $"Below the sales range, {range}. Room to raise.";
            }
            else if (yours > to * (1 + SmallestRaise))
            {
                entry["verdict"] = "high";
                entry["advice"] = $"Above the sales range, {range}.";
            }
            else
            {
                entry["verdict"] = "fair";
                entry["advice"] = $"Inside the sales range, {range}.";
            }
        }

        private static string Match(string kind, string name) => (kind + "|" + name).ToLowerInvariant();

        private static string Count(int count, string word) => count == 1 ? "1 " + word : $"{count} {word}s";
    }
}
