using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Siren
{
    public enum Stage { Calm, Hurry, Critical, Late }

    /// <summary>One thing on screen: an order alarm that needs an answer, or a passing notice.</summary>
    public sealed class Alarm
    {
        public string Id;
        public string Header;
        public string Text;
        public string Link;
        public bool IsNotice;
        public int Priority = 3;
        public double? Deadline;     // Unix seconds, sent by the Siren server inside the ntfy action
        public double? Window;       // delivery window in seconds
        public double HurryAt = 0.5;
        public double CriticalAt = 0.2;

        public static double Now => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        public double? Remaining => Deadline - Now;

        // The server sends an order as "title[, xN], 9.50 USD, buyer name". Commas inside a title
        // ("13,500 V-Bucks") have no space after them, so ", " is a safe separator.
        private string[] Parts => (Text ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
        private static bool IsPrice(string part) => Regex.IsMatch(part, @"^\d+\.\d{2} [A-Z]{3,}$");
        private static bool IsBuyer(string part) => part.StartsWith("buyer ", StringComparison.Ordinal);

        public string Price => IsNotice ? "" : Parts.FirstOrDefault(IsPrice) ?? "";
        public string Buyer => IsNotice ? "" : Parts.FirstOrDefault(IsBuyer) ?? "";
        public string Title => IsNotice ? Text : string.Join(", ", Parts.Where(p => !IsPrice(p) && !IsBuyer(p)));

        public Stage Stage
        {
            get
            {
                if (Deadline == null || !(Window > 0)) return IsNotice && Priority >= 4 ? Stage.Hurry : Stage.Calm;
                var left = Deadline.Value - Now;
                if (left <= 0) return Stage.Late;
                var share = left / Window.Value;
                return share <= CriticalAt ? Stage.Critical : share <= HurryAt ? Stage.Hurry : Stage.Calm;
            }
        }

        public string Clock
        {
            get
            {
                if (Remaining == null) return "";
                var left = Remaining.Value;
                var total = (long)Math.Abs(Math.Truncate(left));
                long days = total / 86400, hours = total % 86400 / 3600, minutes = total % 3600 / 60, seconds = total % 60;
                var text = days > 0 ? $"{days}d {hours:00}h" : hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
                return left < 0 ? "+" + text : text;
            }
        }

        /// <summary>Turns a parsed ntfy message into an alarm, or null if there is nothing to show.</summary>
        public static Alarm From(Dictionary<string, object> message, string ackTopic)
        {
            var priority = message.TryGetValue("priority", out var p) ? Convert.ToInt32(p, CultureInfo.InvariantCulture) : 3;
            var title = Str(message, "title");
            var colon = title.IndexOf(':');
            var alarm = new Alarm
            {
                Header = colon > 0 ? title.Substring(0, colon) : title.Length > 0 ? title : "Eldorado Manager",
                Text = Str(message, "message"),
                Link = Str(message, "click"),
                Priority = priority,
            };

            foreach (var entry in message.TryGetValue("actions", out var raw) && raw is IEnumerable list ? list : new object[0])
            {
                if (!(entry is Dictionary<string, object> action)) continue;
                if (Str(action, "action") == "view" && alarm.Link.Length == 0) alarm.Link = Str(action, "url");
                if (Str(action, "action") != "http" || !Str(action, "url").TrimEnd('/').EndsWith("/" + ackTopic)) continue;
                alarm.Id = Str(action, "body");
                if (action.TryGetValue("headers", out var h) && h is Dictionary<string, object> headers)
                {
                    alarm.Deadline = Number(headers, "X-Siren-Deadline");
                    alarm.Window = Number(headers, "X-Siren-Window");
                    alarm.HurryAt = Number(headers, "X-Siren-Hurry-At") ?? alarm.HurryAt;
                    alarm.CriticalAt = Number(headers, "X-Siren-Critical-At") ?? alarm.CriticalAt;
                }
            }

            if (alarm.Id != null) return alarm;
            if (priority < 3) return null;
            alarm.IsNotice = true;
            alarm.Id = Str(message, "id");
            return alarm;
        }

        private static string Str(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) ? value as string ?? "" : "";

        private static double? Number(Dictionary<string, object> map, string key) =>
            double.TryParse(Str(map, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null;
    }
}
