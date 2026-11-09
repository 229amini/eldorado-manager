using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Siren
{
    public sealed class Config
    {
        public static readonly string Home =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldoradoManager");
        private static readonly string ConfigPath = System.IO.Path.Combine(Home, "config.json");
        private static readonly string LogPath = System.IO.Path.Combine(Home, "manager.log");
        private static readonly string OrderPath = System.IO.Path.Combine(Home, "open-order.json");

        public string Url = "https://ntfy.sh";
        public string Topic = "eldorado";
        public string Token = "";
        public string Sound = "";   // optional path to your own .wav
        public int Volume = 15;     // percent of the sound file's own level
        public bool Ring = true;    // off while another app already rings for the same orders
        public string Theme = "dark";   // a Skin name
        public string SellerId = "";
        public string SellerSecret = "";   // stored encrypted for this Windows user

        public string AckTopic => Topic + "-ack";
        public bool HasSellerKey => SellerId.Length > 0 && SellerSecret.Length > 0;
        public bool HasAlarms => Topic.Length > 0;
        public bool IsSetUp => HasAlarms || HasSellerKey;   // the dashboard works with no alarm server

        public static DateTime SavedAt => File.GetLastWriteTimeUtc(ConfigPath);

        public static Config Load()
        {
            try
            {
                var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath));
                string Get(string key) => raw.TryGetValue(key, out var value) ? value as string ?? "" : "";
                return new Config
                {
                    Url = Get("url").TrimEnd('/'),
                    Topic = Get("topic"),
                    Token = Get("token"),
                    Sound = Get("sound"),
                    Volume = int.TryParse(Get("volume"), out var volume) ? volume : new Config().Volume,
                    Ring = Get("ring") != "off",
                    Theme = Get("theme").Length > 0 ? Get("theme") : new Config().Theme,
                    SellerId = Get("sellerId"),
                    SellerSecret = Unprotect(Get("sellerSecret")),
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Home);
            var values = new Dictionary<string, string>
            {
                ["url"] = Url,
                ["topic"] = Topic,
                ["token"] = Token,
                ["sound"] = Sound,
                ["volume"] = Volume.ToString(),
                ["ring"] = Ring ? "on" : "off",
                ["theme"] = Theme,
                ["sellerId"] = SellerId,
                ["sellerSecret"] = Protect(SellerSecret),
            };
            File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(values));
        }

        // This key can change live prices, so it stays unreadable to other Windows users and to a copied file.
        private static string Protect(string text) => text.Length == 0 ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

        private static string Unprotect(string text)
        {
            if (text.Length == 0) return "";
            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception error) when (error is CryptographicException || error is FormatException)
            {
                return "";
            }
        }

        public void Authorize(WebRequest request)
        {
            if (Token.Length > 0) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + Token;
        }

        /// <summary>Returns a message for the settings window, or null when the topic is readable.</summary>
        public string TestConnection()
        {
            try
            {
                var request = WebRequest.Create($"{Url}/{Topic}/json?poll=1");
                request.Timeout = 15000;
                Authorize(request);
                using (request.GetResponse()) return null;
            }
            catch (WebException error) when (error.Response is HttpWebResponse response)
            {
                return $"The server answered {(int)response.StatusCode}. Check the topic and token.";
            }
            catch (Exception error)
            {
                return "Eldorado Manager can't reach that server. " + error.Message;
            }
        }

        public void PublishAck(string orderId)
        {
            try
            {
                var request = WebRequest.Create($"{Url}/{AckTopic}");
                request.Method = "POST";
                request.Timeout = 20000;
                Authorize(request);
                var body = Encoding.UTF8.GetBytes(orderId);
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (request.GetResponse()) { }
            }
            catch (Exception error)
            {
                Log("ack failed: " + error.Message);
            }
        }

        /// <summary>Leaves the most urgent open order for the dashboard, which runs as a separate process.</summary>
        public static void ShareOrder(Alarm order, string more)
        {
            try
            {
                Directory.CreateDirectory(Home);
                if (order == null)
                {
                    File.Delete(OrderPath);
                    return;
                }
                File.WriteAllText(OrderPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    ["id"] = order.Id,
                    ["title"] = order.Title,
                    ["price"] = order.Price,
                    ["more"] = more,
                    ["link"] = order.Link,
                    ["deadline"] = order.Deadline,
                    ["window"] = order.Window,
                }));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Log("order file: " + error.Message);
            }
        }

        /// <summary>The open order as JSON, or null when there is none.</summary>
        public static string ReadOrder()
        {
            try
            {
                return File.Exists(OrderPath) ? File.ReadAllText(OrderPath) : null;
            }
            catch (IOException)
            {
                return null;   // mid-write: the next second reads it
            }
        }

        public static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(Home);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
