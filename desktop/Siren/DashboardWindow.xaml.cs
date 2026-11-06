using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Siren
{
    /// <summary>
    /// Your offers against the market. A pass takes a minute or two at Market's pace, and only every
    /// fifth one reads the site order, because that walks the whole listing of each item. The page
    /// only draws: every offer and every edit lives here, and each change is sent to it as one state.
    /// </summary>
    public partial class DashboardWindow : Window
    {
        private const int PassSeconds = 30, SlowDownSeconds = 180, SiteEvery = 5, SendDelaySeconds = 6, UndoSeconds = 20;
        private const int RecentEvery = 10, SoldDays = 30, HistoryDays = 90, TopItems = 10, MaxBulkPercent = 50;
        private const double BigChange = 0.5;
        private const string Waiting = "Sending 6 seconds after your last change. Enter sends now, Esc cancels.";
        private const string TooFine = "Eldorado takes 2 decimals, or up to 5 on a price below 0.01.";
        private const string Domain = "siren.local";
        private const string Origin = "https://" + Domain + "/";

        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly Dictionary<string, DispatcherTimer> waiting = new Dictionary<string, DispatcherTimer>();
        private readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer redraw = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Config config;
        private readonly Market market;
        private List<OfferRow> rows = new List<OfferRow>();
        private WebView2 web;
        private bool ready;
        private string sort = "attention";
        private bool descending;
        private DateTime? pricesChecked, siteChecked;
        private string problem;
        private string orderText;
        private DateTime themeRead;
        private Dictionary<string, object> order;
        private volatile bool closing;
        private readonly Dictionary<string, SoldOrder> orders = new Dictionary<string, SoldOrder>();
        private readonly Dictionary<string, string> orderNotes = new Dictionary<string, string>();
        private readonly HashSet<string> dismissed = new HashSet<string>();
        private List<Notice> notices = new List<Notice>();
        private List<Sale> sales;
        private bool recentRead;
        private int salesDays = SoldDays;
        private readonly Logbook book = Logbook.Load();
        private Watched watched = MarketWatch.Load();
        private string bulkNote = "";
        private bool bulkBusy;

        public DashboardWindow(Config config, Market market)
        {
            InitializeComponent();
            this.config = config;
            this.market = market;
            Skin.Use(config.Theme);
            SourceInitialized += (_, __) => Restyle();
            clock.Tick += (_, __) =>
            {
                ReadOrder();
                FollowTheme();
            };
            redraw.Tick += (_, __) =>
            {
                redraw.Stop();
                Push();
            };
            Closing += (_, e) =>
            {
                if (waiting.Count == 0) return;
                e.Cancel = true;   // a change is still on its timer, and closing now would lose it
                problem = "A change is waiting to send. Press Enter to send it or Esc to cancel it, then close.";
                Later();
            };
            Closed += (_, __) =>
            {
                closing = true;
                wake.Set();
            };
        }

        public async void Start()
        {
            clock.Start();
            new Thread(Watch) { IsBackground = true, Name = "market" }.Start();
            try
            {
                WebHost.Unpack();
                CoreWebView2Environment.SetLoaderDllFolderPath(WebHost.LoaderFolder);
                var environment = await CoreWebView2Environment.CreateAsync(null, WebHost.DataFolder);
                var surface = Skin.Current.Surface;
                web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(surface.R, surface.G, surface.B) };
                Host.Children.Add(web);
                await web.EnsureCoreWebView2Async(environment);
                Lock(web.CoreWebView2);
                web.CoreWebView2.Navigate(Origin + "index.html");
            }
            catch (Exception error) when (error is WebView2RuntimeNotFoundException || error is DllNotFoundException)
            {
                Config.Log("dashboard: " + error.Message);
                if (web != null) Host.Children.Remove(web);
                Missing.Visibility = Visibility.Visible;
            }
        }

        /// <summary>The page shows text from other sellers' listings, so it gets nothing it doesn't need.</summary>
        private void Lock(CoreWebView2 core)
        {
            var settings = core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Domain, WebHost.PageFolder, CoreWebView2HostResourceAccessKind.Deny);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source.StartsWith(Origin, StringComparison.Ordinal)) Handle(json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson));
            };
        }

        // ---------- what the page is told ----------

        private void Later()
        {
            if (!redraw.IsEnabled) redraw.Start();
        }

        private void Push()
        {
            if (!ready) return;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(new Dictionary<string, object>
            {
                ["theme"] = Skin.Current.Name,
                ["sort"] = sort,
                ["descending"] = descending,
                ["offline"] = rows.Any(r => r.State == "Offline"),
                ["problem"] = problem,
                ["checked"] = Stamp(pricesChecked),
                ["siteChecked"] = Stamp(siteChecked ?? pricesChecked),
                ["order"] = order,
                ["offers"] = rows.Select(r => r.ToWeb()).ToList(),
                ["orders"] = orders.Values.OrderByDescending(o => o.IsOpen).ThenByDescending(o => o.Placed).Select(OrderToWeb).ToList(),
                ["notices"] = notices.Where(n => !dismissed.Contains(n.Id)).Select(n => new Dictionary<string, object>
                {
                    ["id"] = n.Id, ["title"] = n.Title, ["text"] = n.Text, ["link"] = n.Link,
                }).ToList(),
                ["sales"] = SalesToWeb(),
                ["insights"] = recentRead ? Insights.Build(rows, orders.Values, book, watched, DateTime.UtcNow) : null,
                ["best"] = Sold(salesDays).Take(TopItems).Select(ItemToWeb).ToList(),
                ["back"] = Sold(HistoryDays).Where(i => i.Why.Length > 0).Take(TopItems).Select(ItemToWeb).ToList(),
                ["bulkNote"] = bulkNote,
                ["bulkBusy"] = bulkBusy,
            }));
        }

        private Dictionary<string, object> OrderToWeb(SoldOrder o) => new Dictionary<string, object>
        {
            ["id"] = o.Id,
            ["title"] = o.Title,
            ["buyer"] = o.Buyer,
            ["quantity"] = o.Quantity,
            ["total"] = o.Total.ToString("0.00", CultureInfo.InvariantCulture) + " " + o.Currency,
            ["state"] = o.State,
            ["open"] = o.IsOpen,
            ["placed"] = Stamp(o.Placed),
            ["deadline"] = o.Started == null || o.WindowSeconds == null ? (double?)null : Stamp(o.Started.Value.AddSeconds(o.WindowSeconds.Value)) / 1000,
            ["window"] = o.WindowSeconds,
            ["took"] = o.TookMinutes == null ? "" : OfferRow.Span(o.TookMinutes.Value),
            ["review"] = o.Review,
            ["dispute"] = o.Dispute,
            ["note"] = orderNotes.TryGetValue(o.Id, out var note) ? note : "",
        };

        /// <summary>What sold, best earner first, and where each thing stands in your shop today.</summary>
        private List<SoldItem> Sold(int days)
        {
            var since = DateTime.UtcNow.AddDays(-days);
            return orders.Values.Where(o => o.IsSale && o.Placed >= since).GroupBy(o => o.Title).Select(group =>
            {
                var live = rows.Where(r => r.Title == group.Key || group.Any(o => o.OfferId == r.Offer.Id)).OrderByDescending(r => r.IsListed).FirstOrDefault();
                var units = group.Sum(o => o.Quantity);
                return new SoldItem
                {
                    Title = group.Key,
                    Orders = group.Count(),
                    Units = units,
                    Amount = group.Sum(o => o.Total),
                    Currency = group.First().Currency,
                    Last = group.Max(o => o.Placed),
                    OfferId = live?.Offer.Id ?? "",
                    Why = WhyBack(live, units),
                };
            }).OrderByDescending(i => i.Amount).ToList();
        }

        /// <summary>Empty when the offer is fine as it is. Currency stock counts another unit than its orders, so it can't run low here.</summary>
        private static string WhyBack(OfferRow row, int units)
        {
            if (row == null || row.State == "Closed") return "Not listed";
            if (row.State == "Paused") return "Paused";
            if (row.Offer.Quantity == 0) return "Out of stock";
            return !row.Offer.IsCurrency && row.Offer.Quantity <= units ? $"{row.Offer.Quantity} left" : "";
        }

        private Dictionary<string, object> ItemToWeb(SoldItem item) => new Dictionary<string, object>
        {
            ["title"] = item.Title,
            ["orders"] = item.Orders,
            ["units"] = item.Units,
            ["amount"] = item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["currency"] = item.Currency,
            ["last"] = Stamp(item.Last),
            ["offer"] = item.OfferId,
            ["why"] = item.Why,
        };

        /// <summary>Sales come from the orders, which see everything sold. Only money already cleared comes from the export.</summary>
        private Dictionary<string, object> SalesToWeb()
        {
            if (sales == null || !recentRead) return new Dictionary<string, object> { ["days"] = salesDays, ["ready"] = false };
            var since = DateTime.UtcNow.AddDays(-salesDays);
            var sold = orders.Values.Where(o => o.IsSale).ToList();
            var current = sold.Where(o => o.Placed >= since).ToList();
            var earlier = sold.Where(o => o.Placed < since && o.Placed >= since.AddDays(-salesDays)).ToList();
            var clearing = current.Where(o => o.State != "Completed").ToList();
            var today = DateTime.Now.Date;
            var daily = Enumerable.Range(0, salesDays).Select(i => today.AddDays(i + 1 - salesDays)).Select(day =>
            {
                var inDay = current.Where(o => o.Placed.ToLocalTime().Date == day).ToList();
                return new Dictionary<string, object>
                {
                    ["day"] = day.ToString("MMM d", CultureInfo.InvariantCulture),
                    ["amount"] = Math.Round(inDay.Sum(o => o.Total), 2),
                    ["count"] = inDay.Count,
                };
            }).ToList();
            return new Dictionary<string, object>
            {
                ["days"] = salesDays,
                ["ready"] = true,
                ["count"] = current.Count,
                ["amount"] = Math.Round(current.Sum(o => o.Total), 2),
                ["earlierCount"] = earlier.Count,
                ["earlierAmount"] = Math.Round(earlier.Sum(o => o.Total), 2),
                ["clearing"] = Math.Round(clearing.Sum(o => o.Total), 2),
                ["clearingCount"] = clearing.Count,
                ["cleared"] = Math.Round(sales.Sum(x => x.Amount), 2),
                ["fees"] = Math.Round(sales.Sum(x => x.Fees), 2),
                ["net"] = Math.Round(sales.Sum(x => x.Net), 2),
                ["daily"] = daily,
                ["kinds"] = current.GroupBy(o => o.Kind).OrderByDescending(g => g.Sum(o => o.Total)).Select(g => new Dictionary<string, object>
                {
                    ["name"] = g.Key,
                    ["count"] = g.Count(),
                    ["amount"] = Math.Round(g.Sum(o => o.Total), 2),
                }).ToList(),
            };
        }

        private static double? Stamp(DateTime? moment) =>
            moment == null ? (double?)null : (moment.Value - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        /// <summary>The alarm is another process, so it leaves its open order in a file for this window.</summary>
        private void ReadOrder()
        {
            var text = Config.ReadOrder();
            if (text == orderText) return;
            orderText = text;
            order = text == null ? null : json.Deserialize<Dictionary<string, object>>(text);
            Later();
        }

        // ---------- what the page asks ----------

        private void Handle(Dictionary<string, object> message)
        {
            string Text(string key) => message.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
            var row = rows.FirstOrDefault(r => r.Offer.Id == Text("id"));
            var direction = message.TryGetValue("dir", out var dir) ? Convert.ToInt32(dir, CultureInfo.InvariantCulture) : 0;

            switch (Text("type"))
            {
                case "ready":
                    ready = true;
                    Push();
                    return;
                case "refresh": wake.Set(); return;
                case "sort":
                    sort = Text("value");
                    Resort();
                    Later();
                    return;
                case "sortFlip":
                    descending = !descending;
                    Resort();
                    Later();
                    return;
                case "theme": SetTheme(Text("value")); return;
                case "settings": Process.Start(Installer.CurrentExe, "--settings"); return;
                case "ack": Acknowledge(Text("id")); return;
                case "openOrder": OpenLink(order != null && order.TryGetValue("link", out var link) ? link as string : ""); return;
                case "openUrl": OpenLink(Text("url")); return;
                case "dismissNotice":
                    dismissed.Add(Text("id"));
                    Later();
                    return;
                case "deliver": Deliver(Text("id")); return;
                case "extend": Extend(Text("id"), Text("time"), Text("reason")); return;
                case "salesDays": ReadSales(Convert.ToInt32(message["days"], CultureInfo.InvariantCulture)); return;
                case "bulkPrice": BulkPrice(Text("game"), Convert.ToDouble(message["percent"], CultureInfo.InvariantCulture)); return;
                case "bulkDelivery": BulkDelivery(Text("game"), Text("value")); return;
            }
            if (row == null) return;
            switch (Text("type"))
            {
                case "stepPrice": Step(row, direction); break;
                case "typedPrice": PriceTyped(row, Text("text")); break;
                case "enterPrice": PriceEntered(row, Text("text")); break;
                case "match": MatchLowest(row); break;
                case "confirm": ConfirmPrice(row); break;
                case "cancelPrice": ResetPrice(row); break;
                case "undo": UndoLast(row); break;
                case "cancel": Cancel(row); break;
                case "stepStock": StepQuantity(row, direction); break;
                case "typedStock": QuantityTyped(row, Text("text")); break;
                case "enterStock": QuantityEntered(row, Text("text")); break;
                case "toggle": Toggle(row); break;
            }
        }

        /// <summary>Settings runs as its own process, so a theme picked there reaches this window through the file.</summary>
        private void FollowTheme()
        {
            var stamp = Config.SavedAt;
            if (stamp == themeRead) return;
            var saved = Config.Load();
            if (saved == null) return;   // caught mid-write: the next tick reads it again
            themeRead = stamp;
            if (saved.Theme == Skin.Current.Name) return;
            Skin.Use(saved.Theme);
            Restyle();
            Later();
        }

        private void SetTheme(string name)
        {
            var saved = Config.Load();
            if (saved == null) return;
            saved.Theme = name;
            saved.Save();
            Skin.Use(name);
            Restyle();
            Later();
        }

        private void Acknowledge(string id)
        {
            if (id.Length == 0) return;
            Task.Run(() => config.PublishAck(id));
            order = null;
            Later();
        }

        /// <summary>Addresses arrive through ntfy and Eldorado, so only Eldorado's own pages open from here.</summary>
        private static void OpenLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return;
            if (link.StartsWith("/", StringComparison.Ordinal)) link = "https://www.eldorado.gg" + link;
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && (uri.Host.Equals("eldorado.gg", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".eldorado.gg", StringComparison.OrdinalIgnoreCase)))
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }

        // ---------- orders and sales ----------

        private async void Deliver(string id) => await OrderWrite(id, () => market.Deliver(id), "Marked as delivered");

        private async void Extend(string id, string time, string reason) =>
            await OrderWrite(id, () => market.ExtendDelivery(id, time, reason), "Delivery time extended");

        private async Task OrderWrite(string id, Action call, string done)
        {
            orderNotes[id] = "Saving";
            Later();
            try
            {
                await Task.Run(call);
                orderNotes[id] = done;
                wake.Set();
            }
            catch (MarketError error)
            {
                Config.Log("order write: " + error.Message);
                orderNotes[id] = error.Message;
            }
            Later();
        }

        private async void ReadSales(int days)
        {
            salesDays = days;
            sales = null;
            Later();
            await Task.Run(() => FetchSales(days));
        }

        private void FetchSales(int days)
        {
            try
            {
                var now = DateTime.UtcNow;
                var read = market.Sales(now.AddDays(-days), now);
                Post(() =>
                {
                    if (days != salesDays) return;
                    sales = read;
                    Later();
                });
            }
            catch (MarketError error)
            {
                Config.Log("sales: " + error.Message);
                Post(() =>
                {
                    problem = error.Message;
                    Later();
                });
            }
        }

        /// <summary>Open orders come every pass. An order that left that list is gone until the recent walk finds it again.</summary>
        private void TakeOrders(List<SoldOrder> open, List<SoldOrder> recent, List<Notice> unread)
        {
            foreach (var stale in orders.Values.Where(o => o.IsOpen && open.All(x => x.Id != o.Id)).ToList()) orders.Remove(stale.Id);
            foreach (var o in (recent ?? new List<SoldOrder>()).Concat(open)) orders[o.Id] = o;
            notices = unread;
            if (recent != null)
            {
                recentRead = true;
                var since = DateTime.UtcNow.AddDays(-SoldDays);
                var counts = recent.Where(o => o.IsSale && o.Placed >= since).GroupBy(o => o.OfferId).ToDictionary(g => g.Key, g => g.Count());
                foreach (var row in rows) row.Sold = counts.TryGetValue(row.Offer.Id, out var count) ? count : 0;
            }
            Later();
        }

        // ---------- bulk ----------

        /// <summary>
        /// Eldorado's bulk price call puts one price on every offer it matches, which would flatten a shop of
        /// different items. Sending each offer its own new price keeps them apart, at Market's pace.
        /// </summary>
        private async void BulkPrice(string game, double percent)
        {
            if (bulkBusy || percent == 0 || Math.Abs(percent) > MaxBulkPercent) return;
            var targets = rows.Where(r => r.Offer.GameId == game && r.IsListed && r.PendingPrice == null).ToList();
            bulkBusy = true;
            var done = 0;
            var same = 0;
            try
            {
                foreach (var row in targets)
                {
                    bulkNote = $"Sending {done + same + 1} of {targets.Count}";
                    Later();
                    var price = OfferRow.Fit(row.LivePrice * (1 + percent / 100));
                    if (price <= 0 || Math.Abs(price - row.LivePrice) < 0.0000001)
                    {
                        same++;   // 5% off 0.04 rounds back to 0.04
                        continue;
                    }
                    await Task.Run(() => market.SetPrice(row.Offer, price));
                    row.Offer.Price = price;
                    row.LivePrice = price;
                    row.PriceDraft = OfferRow.FormatPrice(price);
                    done++;
                }
                bulkNote = same == 0 ? $"{done} prices changed" : $"{done} prices changed, {same} too low for this change to move";
            }
            catch (MarketError error)
            {
                Config.Log("bulk price: " + error.Message);
                bulkNote = $"Stopped after {done} of {targets.Count}. {error.Message}";
            }
            bulkBusy = false;
            wake.Set();
            Later();
        }

        private async void BulkDelivery(string game, string promise)
        {
            if (bulkBusy) return;
            var categories = rows.Where(r => r.Offer.GameId == game && r.IsListed).Select(r => r.Offer.Category).Distinct().ToList();
            bulkBusy = true;
            bulkNote = "Saving";
            Later();
            try
            {
                var changed = 0;
                foreach (var category in categories) changed += await Task.Run(() => market.SetDelivery(game, category, promise));
                bulkNote = $"Delivery time changed on {changed} offers";
            }
            catch (MarketError error)
            {
                Config.Log("bulk delivery: " + error.Message);
                bulkNote = error.Message;
            }
            bulkBusy = false;
            wake.Set();
            Later();
        }

        // ---------- refresh ----------

        private void Watch()
        {
            for (var pass = 0; !closing; pass++)
            {
                var wait = PassSeconds;
                var site = pass % SiteEvery == 0;
                var recentPass = pass % RecentEvery == 0;
                try
                {
                    var offers = market.Offers();
                    Post(() => Merge(offers));
                    MarketWatch.SaveShop(new Shop
                    {
                        Offers = offers.Select(o => o.Id).ToList(),
                        Listings = offers.Where(o => !o.IsCurrency).Select(o => o.GameId + "|" + o.Category).Distinct().ToList(),
                    });
                    var seen = MarketWatch.Load();
                    var open = market.OpenOrders();
                    var recent = recentPass ? market.RecentOrders(DateTime.UtcNow.AddDays(-HistoryDays * 2)) : null;
                    var unread = market.Notices();
                    Post(() => TakeOrders(open, recent, unread));
                    if (recentPass) FetchSales(salesDays);
                    foreach (var offer in offers.Where(o => o.State != "Closed"))
                    {
                        if (closing) return;
                        var standing = market.Look(offer, site);
                        Post(() => rows.FirstOrDefault(r => r.Offer.Id == offer.Id)?.Apply(standing));
                    }
                    Post(() =>
                    {
                        pricesChecked = DateTime.UtcNow;
                        if (site) siteChecked = pricesChecked;
                        problem = null;
                        if (seen != null) watched = seen;
                        book.NotePresence(offers.Any(o => o.State == "Offline"), DateTime.Now.Hour);
                        if (site)
                        {
                            book.Note(rows, DateTime.UtcNow);
                            book.Save();
                        }
                        Resort();
                        Later();
                    });
                }
                catch (MarketError error)
                {
                    Config.Log("market: " + error.Message);
                    if (error.Status == 429) wait = SlowDownSeconds;
                    Post(() =>
                    {
                        problem = error.Message;
                        Later();
                    });
                }
                wake.WaitOne(TimeSpan.FromSeconds(wait));
            }
        }

        private void Post(Action action) => Dispatcher.BeginInvoke(action);

        private sealed class SoldItem
        {
            public string Title, OfferId, Why, Currency;
            public int Orders, Units;
            public double Amount;
            public DateTime Last;
        }

        private void Merge(List<Offer> offers)
        {
            foreach (var offer in offers)
            {
                var row = rows.FirstOrDefault(r => r.Offer.Id == offer.Id);
                if (row == null)
                {
                    row = new OfferRow(offer);
                    row.PropertyChanged += (_, __) => Later();
                    rows.Add(row);
                }
                else
                {
                    row.Sync(offer, false);
                }
            }
            rows.RemoveAll(r => offers.All(o => o.Id != r.Offer.Id));
            Later();
        }

        /// <summary>Holds still while an edit is open, so offers never jump under the pointer.</summary>
        private void Resort()
        {
            if (rows.Any(r => r.PendingPrice != null || r.PendingQuantity != null || r.NeedsConfirm)) return;
            switch (sort)
            {
                case "site": rows = Ordered(r => r.SiteValue); break;
                case "price": rows = Ordered(r => r.PriceValue); break;
                case "cost": rows = Ordered(r => r.LivePrice); break;
                case "stock": rows = Ordered(r => r.Offer.Quantity); break;
                case "sold": rows = Ordered(r => r.Sold); break;
                case "name":
                    rows = rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                    if (descending) rows.Reverse();
                    break;
                default: rows = rows.OrderBy(r => r.SiteMove >= 0).ThenByDescending(r => r.SiteValue ?? 0).ToList(); break;
            }
        }

        /// <summary>Offers with no value yet stay last either way, so a flip never puts them on top.</summary>
        private List<OfferRow> Ordered(Func<OfferRow, double?> value)
        {
            var known = rows.Where(r => value(r) != null);
            return (descending ? known.OrderByDescending(value) : known.OrderBy(value)).Concat(rows.Where(r => value(r) == null)).ToList();
        }

        // ---------- price ----------

        private void Step(OfferRow row, int direction)
        {
            if (!TryPrice(row.PriceDraft, out var price)) price = row.LivePrice;
            var tiny = price < 0.01 || (price <= 0.01 && direction < 0);
            var next = OfferRow.Fit(price + (tiny ? 0.001 : price < 1 ? 0.01 : 0.1) * direction);
            if (next <= 0) return;
            row.PriceDraft = OfferRow.FormatPrice(next);
            row.PendingPrice = next;
            row.Note = Waiting;
            Soon(row, "price", () => CommitPrice(row));
        }

        private void PriceTyped(OfferRow row, string text)
        {
            row.PriceDraft = text;
            if (!TryPrice(text, out var price) || price <= 0) return;
            if (!Fits(price))
            {
                StopWaiting(row, "price");
                row.PendingPrice = null;
                row.Note = TooFine;
                return;
            }
            row.PendingPrice = price;
            row.Note = Waiting;
            Soon(row, "price", () => CommitPrice(row));
        }

        private void MatchLowest(OfferRow row)
        {
            if (row.LowestValue == null) return;
            var lowest = OfferRow.Fit(row.LowestValue.Value);
            row.PriceDraft = OfferRow.FormatPrice(lowest);
            row.PendingPrice = lowest;
            CommitPrice(row);
        }

        private void PriceEntered(OfferRow row, string text)
        {
            row.PriceDraft = text;
            StopWaiting(row, "price");
            if (!TryPrice(text, out var price) || price <= 0)
            {
                row.Note = "Enter a price above zero, like 0.25.";
            }
            else if (!Fits(price))
            {
                row.PendingPrice = null;
                row.Note = TooFine;
            }
            else
            {
                row.PendingPrice = price;
                CommitPrice(row);
            }
        }

        private void CommitPrice(OfferRow row)
        {
            var price = row.PendingPrice;
            if (price == null) return;
            if (Math.Abs(price.Value - row.LivePrice) < 0.00001)
            {
                ResetPrice(row);
                return;
            }
            if (row.LivePrice > 0 && Math.Abs(price.Value - row.LivePrice) / row.LivePrice > BigChange)
            {
                row.Note = $"Big change: {OfferRow.FormatPrice(row.LivePrice)} to {OfferRow.FormatPrice(price.Value)}. Confirm to send it.";
                row.NeedsConfirm = true;
                return;
            }
            SendPrice(row, price.Value);
        }

        private void ConfirmPrice(OfferRow row)
        {
            row.NeedsConfirm = false;
            if (row.PendingPrice != null) SendPrice(row, row.PendingPrice.Value);
        }

        private void UndoLast(OfferRow row)
        {
            if (row.UndoPrice == null) return;
            var price = row.UndoPrice.Value;
            row.UndoPrice = null;
            row.PriceDraft = OfferRow.FormatPrice(price);
            row.PendingPrice = price;
            SendPrice(row, price, false);
        }

        private void Cancel(OfferRow row)
        {
            StopWaiting(row, "price");
            StopWaiting(row, "stock");
            ResetPrice(row);
            ResetQuantity(row);
            problem = null;
            Later();
        }

        private static void ResetPrice(OfferRow row)
        {
            row.PendingPrice = null;
            row.NeedsConfirm = false;
            row.Note = "";
            row.PriceDraft = OfferRow.FormatPrice(row.LivePrice);
        }

        private void SendPrice(OfferRow row, double price, bool canUndo = true)
        {
            var before = row.LivePrice;
            Run(row, () => market.SetPrice(row.Offer, price), () =>
            {
                row.Offer.Price = price;
                row.LivePrice = price;
                row.PendingPrice = null;
                row.Note = "Price saved";
                if (canUndo) OfferUndo(row, before);
                wake.Set();
            }, () => KeepNote(row, ResetPrice));
        }

        private static void OfferUndo(OfferRow row, double price)
        {
            row.UndoPrice = price;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(UndoSeconds) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                if (row.UndoPrice == price) row.UndoPrice = null;
            };
            timer.Start();
        }

        private static void KeepNote(OfferRow row, Action<OfferRow> reset)
        {
            var note = row.Note;
            reset(row);
            row.Note = note;
        }

        private static bool Fits(double price) => Math.Abs(OfferRow.Fit(price) - price) < 0.0000001;

        private static bool TryPrice(string text, out double price) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out price);

        // ---------- stock ----------

        private void StepQuantity(OfferRow row, int direction)
        {
            if (!int.TryParse(row.QuantityDraft, out var quantity)) quantity = row.Offer.Quantity;
            var next = quantity + direction;
            if (next < 0) return;
            row.QuantityDraft = next.ToString(CultureInfo.InvariantCulture);
            row.PendingQuantity = next;
            row.Note = Waiting;
            Soon(row, "stock", () => SendQuantity(row));
        }

        private void QuantityTyped(OfferRow row, string text)
        {
            row.QuantityDraft = text;
            if (!int.TryParse(text, out var quantity) || quantity < 0) return;
            row.PendingQuantity = quantity;
            row.Note = Waiting;
            Soon(row, "stock", () => SendQuantity(row));
        }

        private void QuantityEntered(OfferRow row, string text)
        {
            row.QuantityDraft = text;
            StopWaiting(row, "stock");
            if (int.TryParse(text, out var quantity) && quantity >= 0)
            {
                row.PendingQuantity = quantity;
                SendQuantity(row);
            }
            else
            {
                row.Note = "Enter a whole number, like 20.";
            }
        }

        private void SendQuantity(OfferRow row)
        {
            var quantity = row.PendingQuantity;
            if (quantity == null) return;
            Run(row, () => market.SetQuantity(row.Offer, quantity.Value), () =>
            {
                row.Offer.Quantity = quantity.Value;
                row.PendingQuantity = null;
                row.Note = "Stock saved";
            }, () => KeepNote(row, ResetQuantity));
        }

        private static void ResetQuantity(OfferRow row)
        {
            row.PendingQuantity = null;
            row.Note = "";
            row.QuantityDraft = row.Offer.Quantity.ToString(CultureInfo.InvariantCulture);
        }

        // ---------- pause ----------

        private void Toggle(OfferRow row)
        {
            var listed = row.IsListed;
            Run(row, () =>
            {
                if (listed) market.Pause(row.Offer);
                else market.Resume(row.Offer);
            }, () =>
            {
                row.State = listed ? "Paused" : "Active";
                row.Offer.State = row.State;
                row.Note = listed ? "Offer paused" : "Offer resumed";
                wake.Set();
            }, () => { });
        }

        // ---------- sending ----------

        /// <summary>Holds your edits back until they stop, so they reach Eldorado as one change.</summary>
        private void Soon(OfferRow row, string field, Action send)
        {
            StopWaiting(row, field);
            var key = row.Offer.Id + field;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SendDelaySeconds) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                waiting.Remove(key);
                send();
            };
            waiting[key] = timer;
            timer.Start();
        }

        private void StopWaiting(OfferRow row, string field)
        {
            var key = row.Offer.Id + field;
            if (!waiting.TryGetValue(key, out var timer)) return;
            timer.Stop();
            waiting.Remove(key);
        }

        private async void Run(OfferRow row, Action call, Action saved, Action failed)
        {
            row.Note = "Saving";
            try
            {
                await Task.Run(call);
                saved();
            }
            catch (MarketError error)
            {
                Config.Log("market write: " + error.Message);
                row.Note = error.Message;
                failed();
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>The parts of the window the page can't reach.</summary>
        private void Restyle()
        {
            const int ImmersiveDarkMode = 20;
            Host.Background = new SolidColorBrush(Skin.Current.Surface);
            var on = Skin.Current == Skin.Dark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, ImmersiveDarkMode, ref on, sizeof(int));
        }
    }
}
