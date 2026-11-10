using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SellerDesk
{
    /// <summary>The exe installs itself for the current user. No admin rights, no installer framework.</summary>
    public static class Installer
    {
        private const string Name = "SellerDesk";
        private const string DashboardTitle = "SellerDesk";   // DashboardWindow.xaml
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;
        public static readonly string InstalledExe = System.IO.Path.Combine(Config.Home, Name + ".exe");

        // Earlier names of this app: Siren up to 1.2.0, Eldorado Manager from 1.3.0 to 1.5.x. Newest first, so its
        // files win when both are still on the PC.
        private static readonly OldInstall[] OldInstalls =
        {
            new OldInstall("EldoradoManager", "Eldorado Manager.lnk", "Eldorado Manager settings.lnk"),
            new OldInstall("Siren", "Siren dashboard.lnk", "Siren settings.lnk"),
        };
        private static readonly string[] Kept = { "config.json", "history.json", "market.json", "shop.json", "open-order.json" };

        private sealed class OldInstall
        {
            public readonly string Name, Home, Exe, UninstallKey;
            public readonly string[] Links;

            public OldInstall(string name, params string[] links)
            {
                Name = name;
                Home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
                Exe = System.IO.Path.Combine(Home, name + ".exe");
                UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
                Links = links;
            }
        }

        public static string CurrentExe => Process.GetCurrentProcess().MainModule.FileName;

        public static bool IsInstalledCopy =>
            string.Equals(CurrentExe, InstalledExe, StringComparison.OrdinalIgnoreCase);

        /// <param name="updated">Started by the in-app update: come back quietly, alarm and dashboard, no welcome.</param>
        public static void Install(bool updated = false)
        {
            StopOthers();
            Directory.CreateDirectory(Config.Home);
            foreach (var old in OldInstalls) MoveOldInstall(old);
            CopyOver();
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(Name, $"\"{InstalledExe}\" --background");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "SellerDesk");
                key.SetValue("Publisher", "sellerdesk");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version.ToString(3));
                key.SetValue("InstallLocation", Config.Home);
                key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            AddShortcut(Dashboard, "--dashboard");
            AddShortcut(Settings, "--settings");
            Config.Log((updated ? "updated " : "installed ") + InstalledExe);
            if (!updated)
            {
                Start("--welcome");
                return;
            }
            StartAlarm();
            StartDashboard();
        }

        // A process stopped a moment ago can hold its exe a little longer, so the copy tries again for a few seconds.
        private static void CopyOver()
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(CurrentExe, InstalledExe, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    System.Threading.Thread.Sleep(500);
                }
            }
        }

        /// <summary>
        /// Carries settings, history and the market watch over from an install under an old name, then removes
        /// it, so the old name leaves the Start menu and the installed apps list. The Siren alert app is a
        /// different program in another folder and isn't touched. The seller key is encrypted for the Windows
        /// user, not the folder, so the copied config still opens.
        /// </summary>
        private static void MoveOldInstall(OldInstall old)
        {
            if (!File.Exists(old.Exe)) return;
            foreach (var process in Process.GetProcessesByName(old.Name))
            {
                try
                {
                    if (!string.Equals(process.MainModule.FileName, old.Exe, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                {
                }
            }
            foreach (var name in Kept)
            {
                var from = System.IO.Path.Combine(old.Home, name);
                var to = System.IO.Path.Combine(Config.Home, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key?.GetValue(old.Name) is string run && run.IndexOf(old.Exe, StringComparison.OrdinalIgnoreCase) >= 0)
                    key.DeleteValue(old.Name, false);
            }
            bool owned;
            using (var key = Registry.CurrentUser.OpenSubKey(old.UninstallKey))
                owned = key?.GetValue("InstallLocation") is string where && string.Equals(where.TrimEnd('\\'), old.Home, StringComparison.OrdinalIgnoreCase);
            if (owned) Registry.CurrentUser.DeleteSubKeyTree(old.UninstallKey, false);
            foreach (var name in old.Links)
            {
                var link = StartMenu(name);
                if (File.Exists(link) && PointsTo(link, old.Exe)) File.Delete(link);
            }
            RemoveLater(old.Home);
            Config.Log("moved over from " + old.Home);
        }

        public static void StartAlarm() => Start("--background");

        public static void StartDashboard() => Start("--dashboard");

        private static void Start(string arguments) =>
            Process.Start(new ProcessStartInfo(InstalledExe, arguments) { UseShellExecute = false, WorkingDirectory = Config.Home });

        public static void Uninstall()
        {
            StopOthers();
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                key?.DeleteValue(Name, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            File.Delete(Dashboard);
            File.Delete(Settings);
            RemoveLater(Config.Home);
        }

        // A running or just-stopped exe can't be deleted at once, so a detached shell finishes after a pause.
        private static void RemoveLater(string folder) =>
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetTempPath(),
            });

        private static string Dashboard => StartMenu("SellerDesk.lnk");
        private static string Settings => StartMenu("SellerDesk settings.lnk");

        private static string StartMenu(string name) =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name);

        private static void AddShortcut(string path, string arguments)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.Arguments = arguments;
            link.WorkingDirectory = Config.Home;
            link.IconLocation = InstalledExe;
            link.Save();
        }

        private static bool PointsTo(string path, string exe)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            return string.Equals((string)shell.CreateShortcut(path).TargetPath, exe, StringComparison.OrdinalIgnoreCase);
        }

        /// <param name="keepDashboard">Restarts only the alarm listener, for a change the dashboard doesn't depend on.</param>
        public static void StopOthers(bool keepDashboard = false)
        {
            var self = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcessesByName(Name).Where(p => p.Id != self))
            {
                if (keepDashboard && process.MainWindowTitle == DashboardTitle) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }
}
