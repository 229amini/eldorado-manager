using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SellerDesk
{
    /// <summary>
    /// Who buys from you, when, what they buy together and who causes trouble. Read from your own orders
    /// only, so it covers the months this PC has read, never other sellers' buyers.
    /// </summary>
    public static class Customers
    {
        public const int Days = 90;
        private const int TopBuyers = 15, TopPairs = 8;

        public static Dictionary<string, object> Build(IEnumerable<SoldOrder> all, DateTime now)
        {
            var recent = all.Where(o => o.Placed >= now.AddDays(-Days) && o.Buyer.Length > 0).ToList();
            var sold = recent.Where(o => o.IsSale).ToList();
            var buyers = recent.GroupBy(o => o.Buyer).Select(g => Buyer(g.Key, g.ToList())).ToList();
            var paying = buyers.Where(b => b.Orders > 0).ToList();
            var repeat = paying.Where(b => b.Orders > 1).ToList();
            var revenue = sold.Sum(o => o.Total);
            var gaps = repeat.SelectMany(b => b.Gaps).OrderBy(g => g).ToList();

            // Day of week by hour, local time, Monday first: when buyers actually order.
            var grid = new int[7][];
            for (var d = 0; d < 7; d++) grid[d] = new int[24];
            foreach (var order in sold)
            {
                var local = order.Placed.ToLocalTime();
                grid[((int)local.DayOfWeek + 6) % 7][local.Hour]++;
            }

            return new Dictionary<string, object>
            {
                ["days"] = Days,
                ["buyers"] = paying.Count,
                ["repeat"] = repeat.Count,
                ["repeatShare"] = revenue > 0 ? Math.Round(repeat.Sum(b => b.Spent) / revenue * 100) : 0,
                ["average"] = sold.Count > 0 ? Math.Round(revenue / sold.Count, 2) : 0,
                ["gap"] = gaps.Count > 0 ? Span(gaps[gaps.Count / 2]) : "",
                ["newThisWeek"] = paying.Count(b => b.First >= now.AddDays(-7)),
                ["grid"] = grid,
                ["top"] = paying.OrderByDescending(b => b.Spent).Take(TopBuyers).Select(ToWeb).ToList(),
                ["trouble"] = buyers.Where(b => b.Problems.Count > 0).OrderByDescending(b => b.Problems.Count).ThenByDescending(b => b.Last).Take(TopBuyers).Select(ToWeb).ToList(),
                ["pairs"] = Pairs(sold),
            };
        }

        private sealed class Who
        {
            public string Name;
            public int Orders, Units;
            public double Spent;
            public DateTime First, Last;
            public List<string> Items = new List<string>();
            public List<string> Problems = new List<string>();
            public List<double> Gaps = new List<double>();   // days between one order and the next
        }

        private static Who Buyer(string name, List<SoldOrder> orders)
        {
            var sold = orders.Where(o => o.IsSale).OrderBy(o => o.Placed).ToList();
            var who = new Who
            {
                Name = name,
                Orders = sold.Count,
                Units = sold.Sum(o => o.Quantity),
                Spent = Math.Round(sold.Sum(o => o.Total), 2),
                First = orders.Min(o => o.Placed),
                Last = orders.Max(o => o.Placed),
                Items = sold.GroupBy(o => o.Title).OrderByDescending(g => g.Sum(o => o.Total)).Select(g => g.Key).Take(3).ToList(),
            };
            for (var i = 1; i < sold.Count; i++) who.Gaps.Add((sold[i].Placed - sold[i - 1].Placed).TotalDays);
            var disputes = orders.Count(o => o.Dispute.Length > 0 || o.State == "Disputed");
            var negative = orders.Count(o => o.Review == "Negative");
            // A cancel for late delivery is on you, not the buyer, so only the other cancels count against them.
            var cancelled = orders.Count(o => !o.IsSale && !o.IsLate);
            if (disputes > 0) who.Problems.Add(disputes == 1 ? "1 dispute" : $"{disputes} disputes");
            if (negative > 0) who.Problems.Add(negative == 1 ? "1 bad review" : $"{negative} bad reviews");
            if (cancelled > 1) who.Problems.Add($"{cancelled} cancelled");
            return who;
        }

        private static Dictionary<string, object> ToWeb(Who who) => new Dictionary<string, object>
        {
            ["name"] = who.Name,
            ["orders"] = who.Orders,
            ["units"] = who.Units,
            ["spent"] = who.Spent,
            ["first"] = Stamp(who.First),
            ["last"] = Stamp(who.Last),
            ["items"] = who.Items,
            ["problems"] = who.Problems,
            ["every"] = who.Gaps.Count > 0 ? Span(who.Gaps.OrderBy(g => g).ElementAt(who.Gaps.Count / 2)) : "",
        };

        /// <summary>Items the same buyer bought, counted once per buyer, so one big spender doesn't fill the list.</summary>
        private static List<Dictionary<string, object>> Pairs(List<SoldOrder> sold)
        {
            var counts = new Dictionary<string, int>();
            foreach (var buyer in sold.GroupBy(o => o.Buyer))
            {
                var items = buyer.Select(o => o.Title).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
                for (var a = 0; a < items.Count; a++)
                    for (var b = a + 1; b < items.Count; b++)
                    {
                        var key = items[a] + "\n" + items[b];
                        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                    }
            }
            return counts.Where(p => p.Value > 1).OrderByDescending(p => p.Value).Take(TopPairs).Select(p => new Dictionary<string, object>
            {
                ["a"] = p.Key.Split('\n')[0],
                ["b"] = p.Key.Split('\n')[1],
                ["buyers"] = p.Value,
            }).ToList();
        }

        private static string Span(double days) =>
            days < 1 ? $"{Math.Max(1, Math.Round(days * 24)):0} h" : days < 14 ? $"{days:0.#} days" : $"{days / 7:0.#} weeks";

        private static double Stamp(DateTime moment) => (moment - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }
}
