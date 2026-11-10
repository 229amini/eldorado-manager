using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace SellerDesk
{
    /// <summary>One line of a public listing, as far as the market watch needs it.</summary>
    public sealed class Listed
    {
        public string Id;
        public string Product;   // Eldorado's own key for the item, the same across sellers
        public string Name;
        public string Kind = "";   // a weapon and its blueprint share a name, so this tells them apart
        public string Picture = "";
        public string Seller = "";
        public string Link = "";   // the offer's page on Eldorado, relative to the site
        public int Quantity;
        public double Price;
        public int Size = 1;   // how many of the item one unit holds, read from the title
    }

    /// <summary>One fall in another seller's stock: most likely a sale, at the price the offer had then.</summary>
    public sealed class Fall
    {
        public string Offer = "";
        public string Seller = "";
        public string Link = "";
        public double Price;
        public int Units;
        public int Size;   // 0 on falls recorded before 1.6.2, whose price can't be split into items
    }

    /// <summary>
    /// How many of the item one unit of an offer holds: "100x Seeds", "x500", "1000 Seeds" and "Seeds 50".
    /// Eldorado has no field for it, so the price of one unit means nothing until it is divided by this.
    /// The item's own name is taken out first, so a digit in it is not read as a count, and a number
    /// followed by a time ("Trade in 8 min") or led by a level word ("Tier 3") is not one either.
    /// Mirrors bundle_size in sellerdesk/market.py; the tests there hold the cases.
    /// </summary>
    public static class Bundle
    {
        public const int Biggest = 1000000;
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Number = @"(\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s*([km](?![a-z]))?";
        // Not \w: .NET counts marks such as the emoji variation selector as word characters, Python does not.
        private const string Word = @"\p{L}\p{Nd}_";
        private const string Count = @"(?<![" + Word + @".,])" + Number;
        private static readonly Regex[] Marked =
        {
            new Regex(Count + @"\s*[x×*](?![a-z])", Options),
            new Regex(@"(?<![a-z])[x×*]\s*" + Number, Options),
        };
        private static readonly Regex Leading = new Regex(@"^[^" + Word + @"]*" + Count + @"(?=\s|$)", Options);
        private static readonly Regex Trailing = new Regex(@"(?:^|(?<=\s))" + Count + @"[^" + Word + @"]*$", Options);
        private static readonly Regex NotACountAfter = new Regex(@"\G\s*(?:mins?|minutes?|h|hrs?|hours?|secs?|s|days?|%|\$|usd)(?![a-z])", Options);
        private static readonly Regex NotACountBefore = new Regex(@"(?:(?<![a-z])(?:tier|lvl?|level|rank|mk|no|t|v)|#)\s*$", Options);

        public static int Size(string title, string name = "")
        {
            var text = title ?? "";
            if (!string.IsNullOrEmpty(name)) text = Regex.Replace(text, Regex.Escape(name), " ", Options);
            text = text.Trim();
            foreach (var pattern in Marked)
                foreach (Match match in pattern.Matches(text))
                    if (Read(match) > 0) return Read(match);
            foreach (var pattern in new[] { Leading, Trailing })
            {
                var match = pattern.Match(text);
                if (!match.Success || Read(match) == 0) continue;
                var end = match.Groups[2].Success ? match.Groups[2].Index + match.Groups[2].Length : match.Groups[1].Index + match.Groups[1].Length;
                if (NotACountAfter.Match(text, end).Success || NotACountBefore.IsMatch(text.Substring(0, match.Index))) continue;
                return Read(match);
            }
            return 1;
        }

        private static int Read(Match match)
        {
            if (!double.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return 0;
            var unit = match.Groups[2].Value.ToLowerInvariant();
            number *= unit == "k" ? 1000 : unit == "m" ? 1000000 : 1;
            if (number < 0.5 || number > Biggest) return 0;
            return (int)Math.Round(number, MidpointRounding.ToEven);
        }
    }

    /// <summary>What the shop trades in. The dashboard leaves it for the market watch, which runs without the seller key.</summary>
    public sealed class Shop
    {
        public List<string> Offers = new List<string>();
        public List<string> Listings = new List<string>();   // gameId|category
    }

    public sealed class WatchedDay
    {
        public int Drops;
        public long Units;
        public List<string> Offers = new List<string>();
        public List<Fall> Falls = new List<Fall>();   // empty on days recorded before 1.3.0
    }

    public sealed class WatchedItem
    {
        public string Name = "";
        public string Kind = "";
        public string Picture = "";
        public int Sellers;
        public double Low;
        public double Typical;
        public List<double> Each = new List<double>();   // one item's price per listing, cheapest first; Low and Typical mix bundle sizes
        public Dictionary<string, WatchedDay> Days = new Dictionary<string, WatchedDay>();
    }

    /// <summary>Everything the watch has seen, kept on disk so a restart doesn't lose the days behind it.</summary>
    public sealed class Watched
    {
        public string Started = "";
        public string Last = "";
        public int Sweeps;
        public int Seen;      // offers read by the last sweep
        public long Total;    // offers in those listings
        public Dictionary<string, WatchedItem> Items = new Dictionary<string, WatchedItem>();
    }

    /// <summary>
    /// Eldorado publishes no sales numbers, so this estimates demand: it reads the top of the public listing
    /// every half hour and counts each time another seller's stock falls. A fall usually is a sale, but a
    /// seller can also cut their own stock, so everything built on this is called an estimate. It runs in the
    /// alarm process, which is always on, and must never take that process down.
    /// </summary>
    public sealed class MarketWatch
    {
        public const int SweepPages = 30;
        private const int GapMs = 6000, SweepMinutes = 30, HoldMinutes = 15, KeepDays = 14, FirstWaitSeconds = 60;
        private const int BiggestFall = 1000;   // beyond this it is a seller rewriting their stock, not a sale
        private const int MostEach = 200;

        private static readonly string WatchPath = Path.Combine(Config.Home, "market.json");
        private static readonly string ShopPath = Path.Combine(Config.Home, "shop.json");
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private readonly Market market = new Market("", "");
        private readonly Dictionary<string, int> stock = new Dictionary<string, int>();
        private Watched watched;

        public void Start() => new Thread(Run) { IsBackground = true, Name = "market watch", Priority = ThreadPriority.BelowNormal }.Start();

        private void Run()
        {
            Thread.Sleep(TimeSpan.FromSeconds(FirstWaitSeconds));
            watched = Load() ?? new Watched();
            for (;;)
            {
                var wait = SweepMinutes;
                try
                {
                    // A shared server sweeps for every PC; a second sweep here would only spend the rate limit.
                    var shop = Config.Load()?.HasHub == true ? null : LoadShop();
                    if (shop != null && shop.Listings.Count > 0) Sweep(shop);
                }
                catch (MarketError error)
                {
                    Config.Log("market watch: " + error.Message);
                    if (error.Status == 429) wait = HoldMinutes;
                }
                catch (Exception error)
                {
                    Config.Log("market watch stopped a sweep: " + error.Message);
                }
                Thread.Sleep(TimeSpan.FromMinutes(wait));
            }
        }

        private void Sweep(Shop shop)
        {
            var listed = new List<Listed>();
            long total = 0;
            foreach (var listing in shop.Listings)
            {
                var parts = listing.Split('|');
                for (var page = 1; page <= SweepPages; page++)
                {
                    var read = market.Sweep(parts[0], parts[1], page, out var pages, out var records);
                    listed.AddRange(read);
                    if (page == 1) total += records;
                    if (page >= pages || read.Count == 0) break;
                    Thread.Sleep(GapMs);
                }
            }
            Take(watched, stock, listed, new HashSet<string>(shop.Offers), DateTime.UtcNow);
            watched.Total = total;
            Save(WatchPath, watched);
        }

        /// <summary>Folds one sweep in. Your own offers are left out, so this shows other sellers' buyers.</summary>
        public static void Take(Watched watched, Dictionary<string, int> stock, List<Listed> listed, HashSet<string> mine, DateTime now)
        {
            var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var group in listed.Where(l => l.Product.Length > 0).GroupBy(l => l.Product))
            {
                if (!watched.Items.TryGetValue(group.Key, out var item)) watched.Items[group.Key] = item = new WatchedItem();
                var prices = group.Select(l => l.Price).OrderBy(p => p).ToList();
                item.Name = group.First().Name;
                item.Kind = group.First().Kind;
                item.Picture = group.Select(l => l.Picture).FirstOrDefault(p => p.Length > 0) ?? item.Picture;
                item.Sellers = group.Count();
                item.Low = prices[0];
                item.Typical = prices[prices.Count / 2];
                item.Each = group.Select(l => Math.Round(l.Price / Math.Max(1, l.Size), 8)).OrderBy(p => p).Take(MostEach).ToList();
                // Falls from before 1.6.2 have no size; one whose offer is still listed takes the size its title has now.
                var sizes = group.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First().Size);
                foreach (var fall in item.Days.Values.SelectMany(d => d.Falls ?? new List<Fall>()).Where(f => f.Size == 0))
                    if (sizes.TryGetValue(fall.Offer, out var size)) fall.Size = size;
                foreach (var offer in group.Where(l => !mine.Contains(l.Id)))
                {
                    if (!stock.TryGetValue(offer.Id, out var before)) continue;
                    var fell = before - offer.Quantity;
                    if (fell <= 0 || fell > BiggestFall) continue;
                    if (!item.Days.TryGetValue(day, out var counted)) item.Days[day] = counted = new WatchedDay();
                    counted.Drops++;
                    counted.Units += fell;
                    if (!counted.Offers.Contains(offer.Id)) counted.Offers.Add(offer.Id);
                    counted.Falls.Add(new Fall { Offer = offer.Id, Seller = offer.Seller, Link = offer.Link, Price = offer.Price, Units = fell, Size = offer.Size });
                }
            }
            foreach (var offer in listed) stock[offer.Id] = offer.Quantity;

            var oldest = now.AddDays(-KeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var item in watched.Items.Values)
                foreach (var stale in item.Days.Keys.Where(d => string.CompareOrdinal(d, oldest) < 0).ToList()) item.Days.Remove(stale);
            if (watched.Started.Length == 0) watched.Started = now.ToString("o", CultureInfo.InvariantCulture);
            watched.Last = now.ToString("o", CultureInfo.InvariantCulture);
            watched.Sweeps++;
            watched.Seen = listed.Count;
        }

        public static Watched Load() => Read<Watched>(WatchPath);

        private static Shop LoadShop() => Read<Shop>(ShopPath);

        public static void SaveShop(Shop shop) => Save(ShopPath, shop);

        private static T Read<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? Json.Deserialize<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException)
            {
                return null;   // mid-write or damaged: the next read or sweep replaces it
            }
        }

        /// <summary>Written beside the file and moved over it, because the dashboard reads this from another process.</summary>
        private static void Save(string path, object value)
        {
            try
            {
                Directory.CreateDirectory(Config.Home);
                var fresh = path + ".new";
                File.WriteAllText(fresh, Json.Serialize(value));
                if (File.Exists(path)) File.Replace(fresh, path, null);
                else File.Move(fresh, path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Config.Log("market watch: " + error.Message);
            }
        }
    }
}
