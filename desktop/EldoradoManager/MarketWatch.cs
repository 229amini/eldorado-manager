using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace EldoradoManager
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
    }

    /// <summary>One fall in another seller's stock: most likely a sale, at the price the offer had then.</summary>
    public sealed class Fall
    {
        public string Offer = "";
        public string Seller = "";
        public string Link = "";
        public double Price;
        public int Units;
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
                    var shop = LoadShop();
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
                foreach (var offer in group.Where(l => !mine.Contains(l.Id)))
                {
                    if (!stock.TryGetValue(offer.Id, out var before)) continue;
                    var fell = before - offer.Quantity;
                    if (fell <= 0 || fell > BiggestFall) continue;
                    if (!item.Days.TryGetValue(day, out var counted)) item.Days[day] = counted = new WatchedDay();
                    counted.Drops++;
                    counted.Units += fell;
                    if (!counted.Offers.Contains(offer.Id)) counted.Offers.Add(offer.Id);
                    counted.Falls.Add(new Fall { Offer = offer.Id, Seller = offer.Seller, Link = offer.Link, Price = offer.Price, Units = fell });
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
