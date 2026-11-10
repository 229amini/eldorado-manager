using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace EldoradoManager
{
    /// <summary>What the shared server last read from the public listings.</summary>
    public sealed class HubMarket
    {
        public Watched Watched;
        public Dictionary<string, Standing> Standings = new Dictionary<string, Standing>();
        public DateTime? Checked, SiteChecked;
        public string Problem;
    }

    /// <summary>
    /// Your own Eldorado Manager server. It reads the market once for every PC, so several dashboards don't
    /// share one rate limit, and it keeps the Finance ledger, so every PC sees the same people and balances.
    /// </summary>
    public sealed class Hub
    {
        private readonly string url;
        private readonly string token;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Hub(string url, string token)
        {
            this.url = url.TrimEnd('/');
            this.token = token;
        }

        public HubMarket Market()
        {
            var reply = Call("GET", "/api/market", null);
            var market = new HubMarket
            {
                Checked = Moment(reply, "checked"),
                SiteChecked = Moment(reply, "siteChecked"),
                Problem = reply.TryGetValue("problem", out var problem) ? problem as string : null,
            };
            if (reply.TryGetValue("watched", out var watched) && watched != null) market.Watched = json.ConvertToType<Watched>(watched);
            if (reply.TryGetValue("standings", out var standings) && standings is IDictionary all)
                foreach (DictionaryEntry entry in all)
                    market.Standings[(string)entry.Key] = json.ConvertToType<Standing>(entry.Value);
            return market;
        }

        public Dictionary<string, object> Ledger() => Call("GET", "/api/ledger", null);

        public Dictionary<string, object> AddPerson(string name, string note) =>
            Call("POST", "/api/ledger/people", new Dictionary<string, object> { ["name"] = name, ["note"] = note });

        /// <summary>seen is the person's "changed" value this PC showed; the server answers 409 if it moved on.</summary>
        public Dictionary<string, object> EditPerson(string id, Dictionary<string, object> change, long seen) =>
            Call("PUT", "/api/ledger/people/" + Uri.EscapeDataString(id), change, seen);

        public Dictionary<string, object> SaveOrder(string orderId, Dictionary<string, object> order, long seen) =>
            Call("PUT", "/api/ledger/orders/" + Uri.EscapeDataString(orderId), order, seen);

        public Dictionary<string, object> AddPayment(Dictionary<string, object> payment) =>
            Call("POST", "/api/ledger/payments", payment);

        public Dictionary<string, object> DeletePayment(string id) =>
            Call("DELETE", "/api/ledger/payments/" + Uri.EscapeDataString(id), null);

        private Dictionary<string, object> Call(string method, string path, object body, long? seen = null)
        {
            var request = (HttpWebRequest)WebRequest.Create(url + path);
            request.Method = method;
            request.Timeout = 20000;
            request.UserAgent = "EldoradoManager";
            request.Accept = "application/json";
            if (token.Length > 0) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            if (seen != null) request.Headers[HttpRequestHeader.IfMatch] = "\"" + seen.Value.ToString(CultureInfo.InvariantCulture) + "\"";
            if (method != "GET")
            {
                request.Headers["X-Manager"] = "1";   // the server refuses writes without it, which keeps other sites out
                var bytes = Encoding.UTF8.GetBytes(body == null ? "" : json.Serialize(body));
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                if (bytes.Length > 0)
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();
                    return text.Length == 0 ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(text);
                }
            }
            catch (WebException error) when (error.Response is HttpWebResponse response)
            {
                var status = (int)response.StatusCode;
                string said;
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) said = reader.ReadToEnd();
                var message = Regex.Match(said, "\"error\"\\s*:\\s*\"([^\"]+)\"");
                if (status == 401) throw new MarketError("The shared server refused the token. Check it in Settings, Eldorado key.", status);
                if (status == 404 && !message.Success) throw new MarketError("The shared server doesn't know this. Update it to the same version as this app.", status);
                throw new MarketError(message.Success ? message.Groups[1].Value : $"The shared server answered {status}.", status);
            }
            catch (WebException error)
            {
                throw new MarketError("Can't reach the shared server. " + error.Message);
            }
        }

        private static DateTime? Moment(Dictionary<string, object> reply, string key) =>
            reply.TryGetValue(key, out var value) && value is string text &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? when : (DateTime?)null;
    }
}
