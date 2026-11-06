using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Siren
{
    /// <summary>One line of the dashboard. Draft values hold what you typed until Eldorado accepts it.</summary>
    public sealed class OfferRow : INotifyPropertyChanged
    {
        private string priceDraft = "", quantityDraft = "", note = "", insight = "";
        private string priceRank = "Checking", lowest = "", cheaper = "";
        private double livePrice;
        private bool needsConfirm;
        private double? undoPrice;
        private string state = "";
        private int? firstSite;
        private List<Rival> rivals = new List<Rival>();
        private Rival you;
        private int cheaperCount;
        private int sold;

        public OfferRow(Offer offer)
        {
            Offer = offer;
            Sync(offer, true);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public Offer Offer { get; private set; }
        public string Title => Offer.Title;
        public double? PendingPrice;
        public int? PendingQuantity;
        public double? LowestValue { get; private set; }
        public int? SiteValue { get; private set; }
        public int? PriceValue { get; private set; }
        public double? NextPrice { get; private set; }
        public int Tied { get; private set; }
        public int CheaperCount => cheaperCount;

        /// <summary>Places gained since the dashboard opened. Negative means the offer slipped.</summary>
        public int SiteMove => firstSite == null || SiteValue == null ? 0 : firstSite.Value - SiteValue.Value;

        public string PriceDraft { get => priceDraft; set => Set(ref priceDraft, value); }
        public string QuantityDraft { get => quantityDraft; set => Set(ref quantityDraft, value); }
        public double LivePrice { get => livePrice; set => Set(ref livePrice, value); }
        public string Note { get => note; set => Set(ref note, value); }
        public bool NeedsConfirm { get => needsConfirm; set => Set(ref needsConfirm, value); }
        public double? UndoPrice { get => undoPrice; set => Set(ref undoPrice, value); }
        public string State { get => state; set => Set(ref state, value); }
        public bool IsListed => state == "Active";

        // Offline follows your Eldorado status, so only a listed or paused offer has a switch of its own.
        private bool CanToggle => IsListed || state == "Paused";

        /// <summary>Takes fresh server values, except fields you are editing right now.</summary>
        public void Sync(Offer offer, bool everything)
        {
            Offer = offer;
            State = offer.State;
            LivePrice = offer.Price;
            if (everything || PendingPrice == null) PriceDraft = FormatPrice(offer.Price);
            if (everything || PendingQuantity == null) QuantityDraft = offer.Quantity.ToString(CultureInfo.InvariantCulture);
        }

        public void Apply(Standing standing)
        {
            PriceValue = standing.PricePlace;
            LowestValue = standing.Lowest;
            cheaperCount = standing.Cheaper;
            NextPrice = standing.Next;
            Tied = standing.Tied;
            var usd = Offer.Currency == "USD";
            lowest = !usd ? "USD only" : standing.Lowest == null ? "None" : FormatPrice(standing.Lowest.Value);
            cheaper = !usd ? "" : standing.Cheaper == 0 ? "Nobody cheaper" : $"{standing.Cheaper} cheaper";
            priceRank = !usd ? "USD only" : standing.PricePlace == null ? "Unlisted" : $"{standing.PricePlace} of {standing.Total}";

            if (standing.SitePlace != null)
            {
                SiteValue = standing.SitePlace;
                if (firstSite == null) firstSite = SiteValue;
            }
            else if (!IsListed)
            {
                SiteValue = null;
            }

            if (standing.Rivals != null) rivals = standing.Rivals;
            if (standing.You != null) you = standing.You;
            insight = Explain(standing);
            Changed(nameof(Apply));
        }

        private string Explain(Standing standing)
        {
            if (state == "Offline") return "You're offline on Eldorado, so buyers can't see this offer. Go online on the site to list it again.";
            if (state == "Paused") return "This offer is paused, so buyers can't see it.";
            if (!IsListed) return $"Eldorado lists this offer as {state.ToLowerInvariant()}, so buyers can't see it.";
            if (SiteValue == null) return "Checking where buyers see this offer.";
            if (SiteValue <= Market.SitePageSize) return "Buyers see this offer on page 1.";
            var gap = SiteValue - Market.SitePageSize;
            if (Offer.Currency != "USD") return $"{gap} places from page 1. Siren compares prices in USD only.";
            if (standing.PricePlace <= 3)
                return $"You're among the cheapest, but the site still lists you at #{SiteValue}. Price isn't what's holding this offer back.";
            return $"{gap} places from page 1, with {standing.Cheaper} sellers cheaper than you.";
        }

        /// <summary>Sales from your order list, because Eldorado's own count comes back empty.</summary>
        public int Sold
        {
            get => sold;
            set
            {
                if (sold != value) Set(ref sold, value);
            }
        }

        /// <summary>You against the sellers on top. It says where you trail them, never that this is why they rank there.</summary>
        private Dictionary<string, object> Gaps()
        {
            var others = rivals.Where(r => !r.IsYou).ToList();
            if (you == null || others.Count == 0) return null;
            var speed = Middle(others.Where(r => r.Minutes != null).Select(r => r.Minutes.Value));
            var ratings = Middle(others.Select(r => (double)r.Ratings));
            var price = Middle(others.Select(r => r.Price));
            return new Dictionary<string, object>
            {
                ["deliveryYou"] = you.Minutes == null ? "" : Span(you.Minutes.Value),
                ["deliveryTop"] = speed == null ? "" : Span(speed.Value),
                ["deliveryBehind"] = you.Minutes > speed * 1.5,
                ["ratingsYou"] = you.Ratings.ToString("N0", CultureInfo.InvariantCulture),
                ["ratingsTop"] = (ratings ?? 0).ToString("N0", CultureInfo.InvariantCulture),
                ["ratingsBehind"] = ratings > you.Ratings * 3,
                ["priceTop"] = Offer.Currency == "USD" ? FormatPrice(price ?? 0) : "",
                ["priceBehind"] = Offer.Currency == "USD" && livePrice > price,
            };
        }

        private static double? Middle(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? (double?)null : sorted[sorted.Count / 2];
        }

        public static string Span(double minutes) =>
            minutes < 90 ? $"{Math.Max(1, Math.Round(minutes)):0} min" : $"{minutes / 60:0.#} h";

        private List<Dictionary<string, object>> RivalsToWeb()
        {
            var list = rivals.ToList();
            if (you != null && list.Count > 0 && !list.Any(r => r.IsYou)) list.Add(you);
            return list.Select(r => new Dictionary<string, object>
            {
                ["place"] = r.Place,
                ["seller"] = r.IsYou ? "You" : r.Seller,
                ["price"] = FormatPrice(r.Price),
                ["delivers"] = r.Minutes == null ? "" : Span(r.Minutes.Value),
                ["score"] = r.Score == null ? "" : $"{r.Score:0.#}%",
                ["ratings"] = r.Ratings.ToString("N0", CultureInfo.InvariantCulture),
                ["you"] = r.IsYou,
            }).ToList();
        }

        /// <summary>What the page needs to draw this offer. See Web\app.js.</summary>
        public Dictionary<string, object> ToWeb() => new Dictionary<string, object>
        {
            ["id"] = Offer.Id,
            ["title"] = Offer.Title,
            ["kind"] = Offer.Kind,
            ["game"] = Offer.Game,
            ["gameId"] = Offer.GameId,
            ["category"] = Offer.Category,
            ["delivery"] = Offer.Delivery,
            ["sold"] = sold,
            ["gaps"] = Gaps(),
            ["cheaperCount"] = cheaperCount,
            ["rivals"] = RivalsToWeb(),
            ["pendingPrice"] = PendingPrice != null,
            ["pendingStock"] = PendingQuantity != null,
            ["picture"] = Offer.Picture,
            ["largePicture"] = Offer.LargePicture.Length > 0 ? Offer.LargePicture : Offer.Picture,
            ["currency"] = Offer.Currency,
            ["unit"] = Offer.Unit,
            ["listed"] = IsListed,
            ["stateText"] = state,
            ["canToggle"] = CanToggle,
            ["toggleText"] = IsListed ? "Pause offer" : "Resume offer",
            ["site"] = SiteValue,
            ["move"] = SiteMove,
            ["rank"] = priceRank,
            ["lowest"] = LowestValue,
            ["lowestText"] = lowest,
            ["cheaper"] = cheaper,
            ["livePrice"] = livePrice,
            ["priceDraft"] = priceDraft,
            ["stockDraft"] = quantityDraft,
            ["insight"] = insight,
            ["note"] = note,
            ["needsConfirm"] = needsConfirm,
            ["showUndo"] = undoPrice != null,
        };

        /// <summary>Eldorado refuses more than 2 decimals, or 5 on a price below 0.01.</summary>
        public static double Fit(double price) => Math.Round(price, price >= 0.01 ? 2 : 5);

        public static string FormatPrice(double price) => price.ToString(price >= 0.01 ? "0.00" : "0.00###", CultureInfo.InvariantCulture);

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            field = value;
            Changed(name);
        }

        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
