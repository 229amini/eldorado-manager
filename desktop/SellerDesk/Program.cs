using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace SellerDesk
{
    /// <summary>
    /// SellerDeskSetup.exe is both installer and app. Run from anywhere, it installs itself. Run from the
    /// install folder, it opens the dashboard and starts the alarm behind it. Flags: --background (the
    /// alarm alone, used at sign-in), --dashboard, --settings, --uninstall,
    /// --demo [calm|critical|late|flow], --snapshot DIR.
    /// </summary>
    public static class Program
    {
        private static Mutex instance;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        [STAThread]
        public static void Main(string[] args)
        {
            // The taskbar keeps an app's icon by identity, so without its own it shows what Windows cached for the exe path.
            SetCurrentProcessExplicitAppUserModelID("SellerDesk.App");
            WebHost.Prepare();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var demo = Array.IndexOf(args, "--demo");
            var snapshot = Array.IndexOf(args, "--snapshot");

            if (snapshot >= 0 && snapshot + 1 < args.Length)
            {
                Run(window =>
                {
                    Snapshot.Write(window, args[snapshot + 1]);
                    Application.Current.Shutdown();
                });
            }
            else if (demo >= 0)
            {
                Run(window => window.PlayDemo(demo + 1 < args.Length ? args[demo + 1] : "flow"));
            }
            else if (args.Contains("--dashboard"))
            {
                Dashboard();
            }
            else if (args.Contains("--uninstall"))
            {
                Installer.Uninstall();
            }
            else if (args.Contains("--settings"))
            {
                new Application();
                var before = Config.Load();
                var settings = new SettingsWindow(before);
                if (settings.ShowDialog() == true && System.IO.File.Exists(Installer.InstalledExe))
                {
                    var sameKey = before != null && before.SellerId == settings.Result.SellerId && before.SellerSecret == settings.Result.SellerSecret;
                    Installer.StopOthers(keepDashboard: sameKey);   // a dashboard holds the key it started with
                    Installer.StartAlarm();
                }
            }
            else if (!Installer.IsInstalledCopy)
            {
                Installer.Install();
            }
            else if (args.Contains("--background") || args.Contains("--welcome"))
            {
                Alarm(args.Contains("--welcome"));
            }
            else
            {
                Open();
            }
        }

        /// <summary>A double-click. First run goes to the alarm, which asks for settings before anything else.</summary>
        private static void Open()
        {
            var config = Config.Load();
            if (config == null || !config.IsSetUp)
            {
                Alarm(false);
                return;
            }
            Installer.StartAlarm();   // does nothing if the alarm is already running
            Dashboard();
        }

        private static void Alarm(bool welcome)
        {
            instance = new Mutex(false, @"Local\SellerDesk", out var createdNew);
            if (!createdNew) return;
            Run(window =>
            {
                var config = Config.Load();
                if (config == null || !config.IsSetUp)
                {
                    var settings = new SettingsWindow(config);
                    if (settings.ShowDialog() != true)
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                    config = settings.Result;
                    welcome = true;
                }
                // Silent still runs this process, because the market watch lives in it.
                var ring = config.HasAlarms && config.Ring;
                if (ring) window.Listen(config);
                else Config.ShareOrder(null, "");   // an order left from before would hang on in the dashboard
                new MarketWatch().Start();
                if (!welcome) return;
                if (ring) window.Welcome();
                if (config.HasSellerKey) Installer.StartDashboard();
            });
        }

        private static void Dashboard()
        {
            instance = new Mutex(false, @"Local\SellerDeskDashboard", out var createdNew);
            if (!createdNew) return;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, __) =>
            {
                var config = Config.Load();
                if (config == null || !config.HasSellerKey)
                {
                    var settings = new SettingsWindow(config, true);
                    if (settings.ShowDialog() != true)
                    {
                        app.Shutdown();
                        return;
                    }
                    config = settings.Result;
                }
                var window = new DashboardWindow(config, new Market(config.SellerId, config.SellerSecret));
                window.Closed += (s, e) => app.Shutdown();
                window.Show();
                window.Start();
            };
            app.Run();
        }

        private static void Run(Action<AlarmWindow> start)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, __) => start(new AlarmWindow());
            app.Run();
        }
    }
}
