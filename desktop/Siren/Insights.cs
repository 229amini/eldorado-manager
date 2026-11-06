using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Siren
{
    /// <summary>One offer on one day. Rank and price history can't be read back from Eldorado, so it starts when Siren does.</summary>
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
                ["market"] = MarketToWeb(rows, watched, now),
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
                    "Buyers can't see your offers while Eldorado has you offline, so this time sells nothing. Siren counts this only while the dashboard is open.");

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

        private static Dictionary<string, object> MarketToWeb(List<OfferRow> rows, Watched watched, DateTime now)
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
                return new
                {
                    Item = pair.Value,
                    Key = pair.Key,
                    Drops = days.Sum(d => d.Drops),
                    Movers = days.SelectMany(d => d.Offers).Distinct().Count(),
                    Units = days.Sum(d => d.Units),
                    Row = row,
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
                    ["low"] = OfferRow.FormatPrice(x.Item.Low),
                    ["typical"] = OfferRow.FormatPrice(x.Item.Typical),
                    ["offer"] = x.Row?.Offer.Id ?? "",
                    ["yours"] = x.Row == null ? "" : OfferRow.FormatPrice(x.Row.LivePrice),
                }).ToList(),
            };
        }

        private static string Match(string kind, string name) => (kind + "|" + name).ToLowerInvariant();

        private static string Count(int count, string word) => count == 1 ? "1 " + word : $"{count} {word}s";
    }
}
