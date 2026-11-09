using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace EldoradoManager
{
    public sealed class MarketError : Exception
    {
        public MarketError(string message, int status = 0) : base(message) => Status = status;
        public int Status { get; }
    }

    /// <summary>One of your item or currency offers, and the filter that finds its rivals.</summary>
    public sealed class Offer
    {
        public string Id;
        public string Title;
        public string Kind;
        public string GameId;
        public string Category;
        public string EnvironmentId;
        public string[] EnvironmentValues;
        public double Price;
        public string Currency;
        public int Quantity;
        public string State;
        public string Picture = "";        // full address of the small offer image, empty when there is none
        public string LargePicture = "";   // the same image for a poster, where the small one blurs
        public string Unit = "";           // what one price buys on a currency offer, such as 1M
        public string Game = "";           // the game's name, for choosing what a bulk change covers
        public string Delivery = "";       // the promise on the offer, such as Minute20

        public bool IsListed => State == "Active";
        public bool IsCurrency => Category == "Currency";
    }

    /// <summary>Where an offer stands against the other sellers of the same item.</summary>
    public sealed class Standing
    {
        public int Total;
        public double? Lowest;
        public int Cheaper;
        public int Tied;            // other sellers at exactly your price
        public double? Next;        // the lowest price above yours
        public int? PricePlace;
        public int? SitePlace;      // null when this pass did not walk the site order
        public List<Rival> Rivals;  // the top of the site order, null when this pass did not walk it
        public Rival You;           // your own line in the listing, once a walk reaches it
    }

    /// <summary>One seller in the public listing, with what a buyer sees next to the price.</summary>
    public sealed class Rival
    {
        public int Place;
        public string Seller = "";
        public double Price;
        public double? Minutes;     // median delivery time, when Eldorado has one
        public double? Score;       // positive feedback, 0 to 100
        public long Ratings;
        public bool IsYou;
    }

    /// <summary>An order someone placed with you.</summary>
    public sealed class SoldOrder
    {
        public string Id;
        public string OfferId;
        public string Title;
        public string Kind;
        public string Buyer;
        public int Quantity;
        public double Total;
        public string Currency;
        public string State;
        public DateTime Placed;
        public DateTime? Started;
        public int? WindowSeconds;
        public double? TookMinutes;
        public string Review = "";
        public string Dispute = "";
        public string Cancelled = "";   // Eldorado's reason, when the order was cancelled

        public bool IsOpen => State == "Paid" || State == "Disputed";
        public bool IsSale => State != "Canceled";
        public bool IsLate => Cancelled == "Not_Delivered_Within_Guaranteed_Delivery_Time";
    }

    /// <summary>One line of Eldorado's sales export, which only names the kind of goods.</summary>
    public sealed class Sale
    {
        public DateTime When;
        public string Kind;
        public double Amount;
        public double Fees;
        public double Net;
    }

    /// <summary>An unread Eldorado notification worth showing beside your offers.</summary>
    public sealed class Notice
    {
        public string Id;
        public string Title;
        public string Text;
        public string Link;
    }

    /// <summary>
    /// The Eldorado item and currency APIs. Your own offers need the seller token. The public listings
    /// do not, and their default order is the one buyers see on the site. For currency that is by price.
    /// </summary>
    public sealed class Market
    {
        public const int SitePageSize = 24;
        private const string Base = "https://www.eldorado.gg";
        private const string Pictures = "https://assetsdelivery.eldorado.gg/v7/_offers-v2_/";   // the API only gives file names
        private const int PageSize = 50;   // the API rejects 100
        private const string Listing = "/api/v1/item-management/offers";
        private const string Mine = "/api/v1/item-management/me/offers";
        private const string CurrencyListing = "/api/v1/currency-management/offers";
        private const string MyCurrency = "/api/v1/currency-management/me/offers";
        private const string Sold = "/api/v1/orders/me";
        private const string OrderPage = "/dashboard/orders/sold";
        private const int TopRivals = 5, MaxOrderPages = 8;

        // Orders are the alarm's job, so only what the alarm doesn't cover is listed here.
        private static readonly Dictionary<string, string> Worth = new Dictionary<string, string>
        {
            ["BuyerContactedSeller"] = "A buyer messaged you",
            ["ConversationCreated"] = "New conversation",
            ["ConversationCreatedByAdmin"] = "Eldorado opened a conversation",
            ["OrderDisputed"] = "Order disputed",
            ["ReviewCompleted"] = "New review",
            ["WarrantyClaimIssued"] = "Warranty claim",
            ["ItemAutoDeliveryCancelled"] = "Auto delivery cancelled",
            ["InactivityFeePreDeduction"] = "Inactivity fee coming",
            ["UserSuspended"] = "Your account was suspended",
        };
        private const int CallGapMs = 1500, DefaultHoldSeconds = 60, WriteAttempts = 5;

        private readonly string clientId;
        private readonly string clientSecret;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly object gate = new object();
        private string token;
        private DateTime tokenExpires;
        private DateTime nextCall, holdUntil;

        public Market(string clientId, string clientSecret)
        {
            this.clientId = clientId;
            this.clientSecret = clientSecret;
        }

        public List<Offer> Offers()
        {
            var offers = new List<Offer>();
            for (var page = 1; ; page++)
            {
                var reply = Read(Mine + "/me/search", new Dictionary<string, string>
                {
                    ["pageIndex"] = page.ToString(),
                    ["pageSize"] = PageSize.ToString(),
                }, true);
                offers.AddRange(Rows(reply).Select(ToOffer));
                if (page >= Whole(reply, "totalPages")) break;
            }
            // Eldorado has no "all my currency offers" call, so this asks per game the seller already trades in.
            foreach (var game in offers.Select(o => o.GameId).Distinct().ToList()) offers.AddRange(CurrencyOffers(game));
            return offers;
        }

        private List<Offer> CurrencyOffers(string game)
        {
            var offers = new List<Offer>();
            var filter = json.Serialize(new Dictionary<string, object>
            {
                ["augmentedGameFilter"] = new Dictionary<string, string> { ["gameId"] = game, ["category"] = "Currency" },
            });
            for (var page = 1; ; page++)
            {
                var text = Call("GET", $"{MyCurrency}?pageIndex={page}&pageSize={PageSize}", filter, true);
                var reply = json.Deserialize<Dictionary<string, object>>(text);
                offers.AddRange(Rows(reply).Select(ToOffer));
                if (page >= Whole(reply, "totalPages")) return offers;
            }
        }

        /// <summary>Walks the price order up to your price, then the site order if asked.</summary>
        public Standing Look(Offer offer, bool walkSite)
        {
            var standing = new Standing();
            // Listings carry USD prices only, so an offer priced in another currency has nothing to compare with.
            if (offer.Currency != "USD")
            {
                standing.Total = Whole(Read(ListingOf(offer), Filter(offer, 1), false), "recordCount");
                if (walkSite && offer.IsListed) WalkSite(offer, standing);
                return standing;
            }
            var seen = 0;
            for (var page = 1; ; page++)
            {
                var query = Filter(offer, page);
                query["offerSortingCriterion"] = "Price";
                query["isAscending"] = "true";
                var reply = Read(ListingOf(offer), query, false);
                standing.Total = Whole(reply, "recordCount");
                var rows = Results(reply).ToList();
                var passed = false;
                foreach (var raw in rows)
                {
                    var row = Inner(raw);
                    seen++;
                    if (offer.IsCurrency && UnitName(Text(row, "unitSystem")) != offer.Unit) continue;   // prices per 1K and per 1M don't compare
                    var price = Money(row, "pricePerUnitInUSD");
                    if (standing.Lowest == null || price < standing.Lowest) standing.Lowest = price;
                    if (price < offer.Price) standing.Cheaper++;
                    if (Text(row, "id") == offer.Id)
                    {
                        standing.PricePlace = seen;
                        standing.You = ToRival(raw, seen, true);
                    }
                    else if (price == offer.Price)
                    {
                        standing.Tied++;
                    }
                    if (price > offer.Price)
                    {
                        passed = true;
                        if (standing.Next == null || price < standing.Next) standing.Next = price;
                    }
                }
                // The currency listing ignores the price sort, so only a full walk finds its lowest price.
                if (!offer.IsCurrency && passed) break;
                if (rows.Count == 0 || page >= Whole(reply, "totalPages")) break;
            }
            if (offer.IsCurrency && standing.PricePlace != null) standing.PricePlace = standing.Cheaper + 1;
            if (walkSite && offer.IsListed) WalkSite(offer, standing);
            return standing;
        }

        /// <summary>One page of a game's public listing in the order buyers see it, for the market watch.</summary>
        public List<Listed> Sweep(string gameId, string category, int page, out int pages, out long records)
        {
            var reply = Read(Listing, new Dictionary<string, string>
            {
                ["gameId"] = gameId,
                ["category"] = category,
                ["pageIndex"] = page.ToString(),
                ["pageSize"] = PageSize.ToString(),
            }, false);
            pages = Whole(reply, "totalPages");
            records = Whole(reply, "recordCount");
            return Results(reply).Select(ToListed).ToList();
        }

        private static Listed ToListed(Dictionary<string, object> row)
        {
            var raw = Inner(row);
            var user = Part(row, "user");
            var alias = Text(raw, "gameSeoAlias");
            var art = Part(raw, "mainOfferImage");
            var file = art == null ? "" : Text(art, "smallImage");
            var values = List(raw, "tradeEnvironmentValues").ToList();
            return new Listed
            {
                Id = Text(raw, "id"),
                Product = Text(raw, "standardizedProductKey"),
                Name = values.Count > 0 ? Text(values[values.Count - 1], "value") : Text(raw, "offerTitle"),
                Kind = values.Count > 1 ? Text(values[0], "value") : "",
                Picture = file.Length == 0 ? "" : Pictures + Uri.EscapeDataString(file),
                Seller = user == null ? "" : Text(user, "username"),
                Link = alias.Length == 0 ? "" : $"/{alias}/oi/{Text(raw, "id")}",   // the sweep reads item listings only, and items live under /oi/
                Quantity = Whole(raw, "quantity"),
                Price = Money(raw, "pricePerUnitInUSD"),
            };
        }

        public void SetPrice(Offer offer, double amount) =>
            Send("PUT", offer.IsCurrency ? $"{MyCurrency}/{offer.Id}/change-price" : $"{Mine}/{offer.Id}/price",
                json.Serialize(new Dictionary<string, object> { ["amount"] = amount, ["currency"] = offer.Currency }));

        public void SetQuantity(Offer offer, int quantity) =>
            Send("PUT", offer.IsCurrency ? $"{MyCurrency}/{offer.Id}/change-quantity" : $"{Mine}/{offer.Id}/quantity",
                quantity.ToString(CultureInfo.InvariantCulture));

        public void Pause(Offer offer) => Switch(offer, "pause");

        public void Resume(Offer offer) => Switch(offer, "resume");

        private void Switch(Offer offer, string action)
        {
            if (offer.IsCurrency) Send("PUT", $"{MyCurrency}/{offer.Id}/{action}", null);
            else Send("POST", $"{Mine}/{offer.Id}/{action}", null);
        }

        private static string ListingOf(Offer offer) => offer.IsCurrency ? CurrencyListing : Listing;

        private void WalkSite(Offer offer, Standing standing)
        {
            var top = new List<Rival>();
            standing.Rivals = top;
            var seen = 0;
            for (var page = 1; ; page++)
            {
                var reply = Read(ListingOf(offer), Filter(offer, page), false);
                var rows = Results(reply).ToList();
                foreach (var raw in rows)
                {
                    seen++;
                    var mine = Text(Inner(raw), "id") == offer.Id;
                    if (seen <= TopRivals) top.Add(ToRival(raw, seen, mine));
                    if (!mine) continue;
                    standing.SitePlace = seen;
                    standing.You = ToRival(raw, seen, true);
                    return;
                }
                if (rows.Count == 0 || page >= Whole(reply, "totalPages")) return;
            }
        }

        private static Rival ToRival(Dictionary<string, object> raw, int place, bool mine)
        {
            var user = Part(raw, "user");
            var info = Part(raw, "userOrderInfo");
            var delivery = Part(raw, "deliveryTime");
            var median = delivery == null ? "" : Text(delivery, "deliveryTimeMedian");
            return new Rival
            {
                Place = place,
                Seller = user == null ? "" : Text(user, "username"),
                Price = Money(Inner(raw), "pricePerUnitInUSD"),
                Minutes = TimeSpan.TryParse(median, CultureInfo.InvariantCulture, out var span) ? span.TotalMinutes : (double?)null,
                Score = info != null && info.TryGetValue("feedbackScore", out var score) && score != null
                    ? Convert.ToDouble(score, CultureInfo.InvariantCulture) : (double?)null,
                Ratings = info != null && info.TryGetValue("ratingCount", out var count) && count != null
                    ? Convert.ToInt64(count, CultureInfo.InvariantCulture) : 0,
                IsYou = mine,
            };
        }

        // ---------- orders, sales and notices ----------

        /// <summary>Orders that still need you, which a walk of recent orders may have passed.</summary>
        public List<SoldOrder> OpenOrders() => OrdersIn("Paid").Concat(OrdersIn("Disputed")).ToList();

        private List<SoldOrder> OrdersIn(string state) =>
            List(Read(Sold + "/seller/orders", new Dictionary<string, string>
            {
                ["displayFilter"] = "DisplaySellingOrders",
                ["orderState"] = state,
                ["pageSize"] = PageSize.ToString(),
            }, true), "results").Select(ToOrder).ToList();

        /// <summary>Newest first, back to the given day. The sales export can't tell offers apart, so this can.</summary>
        public List<SoldOrder> RecentOrders(DateTime since)
        {
            var orders = new List<SoldOrder>();
            var cursor = "";
            for (var page = 0; page < MaxOrderPages; page++)
            {
                var query = new Dictionary<string, string>
                {
                    ["displayFilter"] = "DisplaySellingOrders",
                    ["pageSize"] = PageSize.ToString(),
                };
                if (cursor.Length > 0)
                {
                    query["pageDirection"] = "Next";
                    query["cursorValue"] = cursor;
                }
                var reply = Read(Sold + "/seller/orders", query, true);
                var rows = List(reply, "results").Select(ToOrder).ToList();
                orders.AddRange(rows);
                cursor = Text(reply, "nextPageCursor");
                if (rows.Count == 0 || cursor.Length == 0 || rows[rows.Count - 1].Placed < since) break;
            }
            return orders;
        }

        private static SoldOrder ToOrder(Dictionary<string, object> raw)
        {
            var offer = Part(raw, "orderOfferDetails") ?? new Dictionary<string, object>();
            var money = Part(raw, "totalPrice") ?? new Dictionary<string, object>();
            var state = Part(raw, "state");
            var review = Part(raw, "review");
            var dispute = Part(raw, "latestDispute");
            var cancelled = Part(raw, "cancelation");
            var title = Text(offer, "offerTitle");
            if (title.Length == 0) title = Text(offer, "gameCategoryTitle");
            var placed = Moment(Text(raw, "createdDate")) ?? DateTime.UtcNow;
            return new SoldOrder
            {
                Id = Text(raw, "id"),
                OfferId = Text(raw, "offerId"),
                Title = title.Length > 0 ? title : "Order",
                Kind = Text(offer, "gameCategoryTitle").Length > 0 ? Text(offer, "gameCategoryTitle") : "Other",
                Buyer = Text(raw, "buyerUsername"),
                Quantity = Whole(raw, "purchaseQuantity"),
                Total = money.TryGetValue("amount", out var amount) ? Convert.ToDouble(amount, CultureInfo.InvariantCulture) : 0,
                Currency = Text(money, "currency"),
                State = state == null ? "" : Text(state, "state"),
                Placed = placed,
                Started = Moment(Text(raw, "deliveryStartedDate")) ?? placed,
                WindowSeconds = WindowOf(Text(offer, "guaranteedDeliveryTime")),
                TookMinutes = TimeSpan.TryParse(Text(raw, "deliveryTime"), CultureInfo.InvariantCulture, out var took) ? took.TotalMinutes : (double?)null,
                Review = review == null ? "" : Text(review, "reviewRating"),
                Dispute = dispute == null ? "" : Text(dispute, "reason").Replace('_', ' '),
                Cancelled = cancelled == null ? "" : Text(cancelled, "reason"),
            };
        }

        private static DateTime? Moment(string text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? when : (DateTime?)null;

        private static int? WindowOf(string promise)
        {
            var match = Regex.Match(promise, @"^(Minute|Hour|Day)(\d+)$");
            if (!match.Success) return null;
            var size = match.Groups[1].Value == "Minute" ? 60 : match.Groups[1].Value == "Hour" ? 3600 : 86400;
            return size * int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        public void Deliver(string orderId) => Send("PUT", $"{Sold}/{orderId}/deliver", null);

        public void ExtendDelivery(string orderId, string time, string reason) =>
            Send("POST", $"{Sold}/{orderId}/extend-delivery-time", json.Serialize(new Dictionary<string, object>
            {
                ["time"] = time,
                ["reason"] = reason,
            }));

        /// <summary>
        /// Eldorado answers this one with CSV, opened by a byte order mark. It lists completed orders only, and
        /// "Order Date" is the day the money cleared, so this says what was paid out, not what was sold.
        /// </summary>
        public List<Sale> Sales(DateTime from, DateTime until)
        {
            string Stamp(DateTime moment) => Uri.EscapeDataString(moment.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            var text = Call("GET", $"{Sold}/sold-order-data?fromDate={Stamp(from)}&untilDate={Stamp(until)}", null, true);
            var lines = text.TrimStart('\uFEFF').Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var sales = new List<Sale>();
            if (lines.Length == 0) return sales;
            var names = Fields(lines[0]);
            foreach (var line in lines.Skip(1))
            {
                var values = Fields(line);
                string Value(string name)
                {
                    var at = names.IndexOf(name);
                    return at >= 0 && at < values.Count ? values[at] : "";
                }
                double Amount(string name) => double.TryParse(Value(name), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) ? number : 0;
                if (!DateTime.TryParse(Value("Order Date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) continue;
                sales.Add(new Sale { When = when, Kind = Value("Description"), Amount = Amount("Amount $"), Fees = Amount("Seller Fees $"), Net = Amount("Net Income $") });
            }
            return sales;
        }

        private static List<string> Fields(string line)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (c == ',' && !quoted)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }
            fields.Add(field.ToString());
            return fields;
        }

        public List<Notice> Notices()
        {
            var reply = Read("/api/notifications/me", new Dictionary<string, string>
            {
                ["notificationReadStatuses"] = "IsUnread",
                ["pageSize"] = "30",
            }, true);
            return List(reply, "results").Select(ToNotice).Where(n => n != null).ToList();
        }

        private static Notice ToNotice(Dictionary<string, object> raw)
        {
            var custom = Part(raw, "customNotification");
            if (custom != null)
                return new Notice { Id = Text(custom, "id"), Title = Text(custom, "subject"), Text = Text(custom, "text"), Link = Text(custom, "redirectUrl") };
            var plain = Part(raw, "notification");
            if (plain == null || !Worth.TryGetValue(Text(plain, "event"), out var title)) return null;
            var details = Part(plain, "details") ?? new Dictionary<string, object>();
            var bits = new[] { Text(details, "title"), Text(details, "buyerUsername") }.Where(b => b.Length > 0);
            return new Notice { Id = Text(plain, "id"), Title = title, Text = string.Join(", ", bits), Link = OrderPage };
        }

        /// <summary>Eldorado's own bulk call, which reports how many offers it changed.</summary>
        public int SetDelivery(string gameId, string category, string promise)
        {
            var path = category == "Currency" ? $"{MyCurrency}/bulk/change-delivery-time" : $"{Mine}/delivery-time";
            var text = Call("POST", path, json.Serialize(new Dictionary<string, object>
            {
                ["deliveryTime"] = promise,
                ["offerState"] = "Active",
                ["augmentedGameFilter"] = new Dictionary<string, string> { ["gameId"] = gameId, ["category"] = category },
            }), true);
            return Whole(json.Deserialize<Dictionary<string, object>>(text), "bulkActionOffersCount");
        }

        private static Dictionary<string, string> Filter(Offer offer, int page)
        {
            var query = new Dictionary<string, string>
            {
                ["gameId"] = offer.GameId,
                ["category"] = offer.Category,
                ["pageIndex"] = page.ToString(),
                ["pageSize"] = PageSize.ToString(),
            };
            if (!offer.IsCurrency) query["includeDeliveryMedians"] = "true";
            if (offer.EnvironmentValues.Length == 0)
            {
                if (!offer.IsCurrency) query["searchQuery"] = offer.Title;   // a currency offer has no title of its own
                return query;
            }
            query["tradeEnvironmentId"] = offer.EnvironmentId;
            for (var i = 0; i < Math.Min(offer.EnvironmentValues.Length, 3); i++)
                query["tradeEnvironmentValue" + i] = offer.EnvironmentValues[i];
            return query;
        }

        private static Offer ToOffer(Dictionary<string, object> raw)
        {
            var values = List(raw, "tradeEnvironmentValues").ToList();
            var money = raw.TryGetValue("pricePerUnit", out var m) ? m as Dictionary<string, object> : null;
            var art = raw.TryGetValue("mainOfferImage", out var a) ? a as Dictionary<string, object> : null;
            string Picture(string size)
            {
                var file = art == null ? "" : Text(art, size);
                return file.Length == 0 ? "" : Pictures + Uri.EscapeDataString(file);
            }
            var title = Text(raw, "offerTitle");
            var alias = Text(raw, "gameSeoAlias").Replace('-', ' ');
            var category = Text(raw, "category");
            return new Offer
            {
                Id = Text(raw, "id"),
                Title = title.Length > 0 ? title : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(alias),
                Kind = category == "Currency" ? "Currency" : string.Join(", ", values.Select(v => Text(v, "value"))),
                Unit = UnitName(Text(raw, "unitSystem")),
                GameId = Text(raw, "gameId"),
                Category = category,
                EnvironmentId = values.Count > 0 ? Text(values[0], "id") : "",
                EnvironmentValues = values.Select(v => Text(v, "value")).ToArray(),
                Price = money == null ? 0 : Convert.ToDouble(money["amount"], CultureInfo.InvariantCulture),
                Currency = money == null ? "USD" : Text(money, "currency"),
                Quantity = Whole(raw, "quantity"),
                State = Text(raw, "offerState"),
                Game = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(alias, " (items|coins|gold|currency|accounts|top ups|gift cards|boosting)$", "")),
                Delivery = Text(raw, "guaranteedDeliveryTime"),
                Picture = Picture("smallImage"),
                LargePicture = Picture("largeImage"),
            };
        }

        private static string UnitName(string system)
        {
            var size = Regex.Match(system, @"\d+$").Value;
            return size == "1000000" ? "1M" : size == "1000" ? "1K" : size == "1" ? "" : size;
        }

        // ---------- transport ----------

        private Dictionary<string, object> Read(string path, Dictionary<string, string> query, bool signed)
        {
            var pairs = query.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value));
            var text = Call("GET", path + "?" + string.Join("&", pairs), null, signed);
            return json.Deserialize<Dictionary<string, object>>(text);
        }

        private void Send(string method, string path, string body) => Call(method, path, body, true);

        private string Call(string method, string path, string body, bool signed)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return Once(method, path, body, signed);
                }
                catch (MarketError error) when (signed && error.Status == 401 && attempt == 1)
                {
                    token = null;   // revoked or expired early: retry once with a fresh token
                }
                catch (MarketError error) when (method != "GET" && error.Status == 429 && attempt < WriteAttempts)
                {
                    // Your change still matters after the limit lifts, and Pace waits that long.
                }
            }
        }

        /// <summary>Spaces every call out, and waits out a limit Eldorado has announced.</summary>
        private void Pace()
        {
            lock (gate)
            {
                var now = DateTime.UtcNow;
                var ready = nextCall > holdUntil ? nextCall : holdUntil;
                if (ready > now) Thread.Sleep(ready - now);
                nextCall = DateTime.UtcNow.AddMilliseconds(CallGapMs);
            }
        }

        private string Once(string method, string path, string body, bool signed)
        {
            var request = (HttpWebRequest)WebRequest.Create(Base + path);
            request.Method = method;
            request.Timeout = 30000;
            request.UserAgent = "EldoradoManager";
            request.Accept = "application/json";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (signed) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + Token();
            Pace();
            if (method != "GET" || body != null)
            {
                if (method == "GET") AllowBody(request);
                var bytes = Encoding.UTF8.GetBytes(body ?? "");
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                if (bytes.Length > 0)
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException error) when (error.Response is HttpWebResponse response)
            {
                var status = (int)response.StatusCode;
                if (status == 429)
                {
                    var seconds = int.TryParse(response.Headers["Retry-After"], out var told) ? told : DefaultHoldSeconds;
                    lock (gate) holdUntil = DateTime.UtcNow.AddSeconds(seconds);
                }
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var reply = reader.ReadToEnd();
                    if (status == 400) Config.Log($"eldorado 400 on {method} {path}: {reply}");
                    throw new MarketError(Explain(status, reply), status);
                }
            }
            catch (WebException error)
            {
                throw new MarketError("Can't reach Eldorado. " + error.Message);
            }
        }

        // Eldorado's currency search is a GET that takes its filter as a JSON body, which .NET Framework
        // refuses to send. Its verb table has a flag for that and no public way to change it.
        private static void AllowBody(HttpWebRequest request)
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var verb = typeof(HttpWebRequest).GetProperty("CurrentMethod", hidden).GetValue(request);
            verb.GetType().GetField("ContentBodyNotAllowed", hidden).SetValue(verb, false);
        }

        private string Token()
        {
            if (token != null && DateTime.UtcNow < tokenExpires) return token;
            var body = json.Serialize(new Dictionary<string, string> { ["clientId"] = clientId, ["clientSecret"] = clientSecret });
            var reply = json.Deserialize<Dictionary<string, object>>(
                Once("POST", "/api/authentication/seller/token", body, false));
            token = Text(reply, "accessToken");
            var life = reply.ContainsKey("expiresIn") ? Whole(reply, "expiresIn") : 900;
            tokenExpires = DateTime.UtcNow.AddSeconds(life - 60);
            return token;
        }

        private static string Explain(int status, string body)
        {
            if (status == 401 || status == 403) return "Eldorado refused the seller key. Check it in Settings.";
            if (status == 429) return "Eldorado asked for fewer requests. Wait a minute and try again.";
            // Eldorado's own errors look like {"code":400,"messages":["..."],"correlationId":"..."}.
            var said = Regex.Match(body, "\"(?:messages?|detail|title)\"\\s*:\\s*\\[?\\s*\"([^\"]+)\"");
            return said.Success
                ? "Eldorado said: " + said.Groups[1].Value
                : $"Eldorado couldn't do that (status {status}). Try again, or change it on the site.";
        }

        // ---------- reply parsing ----------

        private static IEnumerable<Dictionary<string, object>> Results(Dictionary<string, object> reply) => List(reply, "results");

        private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> reply) => Results(reply).Select(Inner);

        private static Dictionary<string, object> Inner(Dictionary<string, object> row) => Part(row, "offer") ?? row;

        private static Dictionary<string, object> Part(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

        private static IEnumerable<Dictionary<string, object>> List(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) && value is IEnumerable list && !(value is string)
                ? list.OfType<Dictionary<string, object>>()
                : Enumerable.Empty<Dictionary<string, object>>();

        private static string Text(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";

        private static int Whole(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) && value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

        private static double Money(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) && value is Dictionary<string, object> money
                ? Convert.ToDouble(money["amount"], CultureInfo.InvariantCulture)
                : 0;
    }
}
