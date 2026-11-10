using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SellerDesk
{
    /// <summary>Streams both ntfy topics forever and resumes from the last message after a drop.</summary>
    public sealed class Listener
    {
        private readonly Config config;
        private readonly Action<Alarm> onAlarm;
        private readonly Action<string> onAnswered;

        public Listener(Config config, Action<Alarm> onAlarm, Action<string> onAnswered)
        {
            this.config = config;
            this.onAlarm = onAlarm;
            this.onAnswered = onAnswered;
        }

        public void Start() => new Thread(Run) { IsBackground = true, Name = "ntfy" }.Start();

        private void Run()
        {
            var serializer = new JavaScriptSerializer();
            string since = null;
            while (true)
            {
                try
                {
                    var url = $"{config.Url}/{config.Topic},{config.AckTopic}/json" + (since == null ? "" : "?since=" + since);
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 120000;  // ntfy sends a keepalive about every 45 s
                    config.Authorize(request);
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        Config.Log("connected");
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var message = serializer.Deserialize<Dictionary<string, object>>(line);
                            if (!message.TryGetValue("event", out var kind) || (string)kind != "message") continue;
                            since = (string)message["id"];
                            if ((string)message["topic"] == config.AckTopic)
                                onAnswered(((message["message"] as string) ?? "").Trim());
                            else if (Alarm.From(message, config.AckTopic) is Alarm alarm)
                                onAlarm(alarm);
                        }
                    }
                }
                catch (Exception error)
                {
                    Config.Log("stream error: " + error.Message);
                    Thread.Sleep(5000);
                }
            }
        }
    }
}
