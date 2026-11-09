using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Siren
{
    /// <summary>The exe installs itself for the current user. No admin rights, no installer framework.</summary>
    public static class Installer
    {
        private const string Name = "EldoradoManager";
        private const string DashboardTitle = "Eldorado Manager";   // DashboardWindow.xaml
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;
        public static readonly string InstalledExe = System.IO.Path.Combine(Config.Home, Name + ".exe");

        // Up to 1.2.0 the app was called Siren and lived here.
        private const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Siren";
        private static readonly string OldHome =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Siren");
        private static readonly string OldExe = System.IO.Path.Combine(OldHome, "Siren.exe");
        private static readonly string[] Kept = { "config.json", "history.json", "market.json", "shop.json" };

        public static string CurrentExe => Process.GetCurrentProcess().MainModule.FileName;

        public static bool IsInstalledCopy =>
            string.Equals(CurrentExe, InstalledExe, StringComparison.OrdinalIgnoreCase);

        public static void Install()
        {
            StopOthers();
            Directory.CreateDirectory(Config.Home);
            MoveOldInstall();
            File.Copy(CurrentExe, InstalledExe, true);
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(Name, $"\"{InstalledExe}\" --background");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Eldorado Manager");
                key.SetValue("Publisher", "eldorado-manager");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version.ToString(3));
                key.SetValue("InstallLocation", Config.Home);
                key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            AddShortcut(Dashboard, "--dashboard");
            AddShortcut(Settings, "--settings");
            Config.Log("installed " + InstalledExe);
            Start("--welcome");
        }

        /// <summary>
        /// Carries settings, history and the market watch over from a Siren install, then removes it, so
        /// the old name leaves the Start menu and the installed apps list. The Siren alert app is a
        /// different program in another folder and isn't touched.
        /// </summary>
        private static void MoveOldInstall()
        {
            if (!File.Exists(OldExe)) return;
            foreach (var process in Process.GetProcessesByName("Siren"))
            {
                try
                {
                    if (!string.Equals(process.MainModule.FileName, OldExe, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                {
                }
            }
            foreach (var name in Kept)
            {
                var from = System.IO.Path.Combine(OldHome, name);
                var to = System.IO.Path.Combine(Config.Home, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key?.GetValue("Siren") is string run && run.IndexOf(OldExe, StringComparison.OrdinalIgnoreCase) >= 0)
                    key.DeleteValue("Siren", false);
            }
            bool owned;
            using (var key = Registry.CurrentUser.OpenSubKey(OldUninstallKey))
                owned = key?.GetValue("InstallLocation") is string where && string.Equals(where.TrimEnd('\\'), OldHome, StringComparison.OrdinalIgnoreCase);
            if (owned) Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, false);
            foreach (var name in new[] { "Siren dashboard.lnk", "Siren settings.lnk" })
            {
                var link = StartMenu(name);
                if (File.Exists(link) && PointsTo(link, OldExe)) File.Delete(link);
            }
            RemoveLater(OldHome);
            Config.Log("moved over from " + OldHome);
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

        private static string Dashboard => StartMenu("Eldorado Manager.lnk");
        private static string Settings => StartMenu("Eldorado Manager settings.lnk");

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
