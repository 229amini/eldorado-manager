using System;
using System.Collections.Generic;
using System.Threading;

namespace SellerDesk
{
    /// <summary>
    /// Your notes on buyers, read from the shared server's ledger every few minutes so an alarm can show the
    /// note for the buyer who just ordered. Without a shared server there are no notes and nothing runs.
    /// </summary>
    public static class BuyerNotes
    {
        private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
        private static Dictionary<string, string> notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static int started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 1) return;
            new Thread(Watch) { IsBackground = true, Name = "buyer notes" }.Start();
        }

        /// <summary>The note for an alarm's "buyer name" part, or empty.</summary>
        public static string For(string buyer)
        {
            var name = buyer.StartsWith("buyer ", StringComparison.Ordinal) ? buyer.Substring(6) : buyer;
            return Volatile.Read(ref notes).TryGetValue(name.Trim(), out var note) ? note : "";
        }

        private static void Watch()
        {
            while (true)
            {
                var config = Config.Load();   // read each time, so a server added in Settings is picked up
                if (config != null && config.HasHub)
                {
                    try
                    {
                        var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (new Hub(config.HubUrl, config.HubToken).Ledger().TryGetValue("buyers", out var all) && all is Dictionary<string, object> buyers)
                        {
                            foreach (var entry in buyers.Values)
                            {
                                if (entry is Dictionary<string, object> b && b.TryGetValue("name", out var n) && b.TryGetValue("note", out var t))
                                    fresh[Convert.ToString(n) ?? ""] = Convert.ToString(t) ?? "";
                            }
                        }
                        Volatile.Write(ref notes, fresh);
                    }
                    catch (MarketError error)
                    {
                        Config.Log("buyer notes: " + error.Message);
                    }
                }
                Thread.Sleep(Every);
            }
        }
    }
}
