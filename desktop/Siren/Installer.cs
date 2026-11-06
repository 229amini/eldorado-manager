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
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Siren";
        public static readonly string InstalledExe = System.IO.Path.Combine(Config.Home, "Siren.exe");

        public static string CurrentExe => Process.GetCurrentProcess().MainModule.FileName;

        public static bool IsInstalledCopy =>
            string.Equals(CurrentExe, InstalledExe, StringComparison.OrdinalIgnoreCase);

        public static void Install()
        {
            StopOthers();
            Directory.CreateDirectory(Config.Home);
            File.Copy(CurrentExe, InstalledExe, true);
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue("Siren", $"\"{InstalledExe}\" --background");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Siren");
                key.SetValue("Publisher", "eldorado-siren");
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

        public static void StartAlarm() => Start("--background");

        public static void StartDashboard() => Start("--dashboard");

        private static void Start(string arguments) =>
            Process.Start(new ProcessStartInfo(InstalledExe, arguments) { UseShellExecute = false, WorkingDirectory = Config.Home });

        public static void Uninstall()
        {
            StopOthers();
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                key?.DeleteValue("Siren", false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            File.Delete(Dashboard);
            File.Delete(Settings);
            // A running exe can't delete itself, so a detached shell finishes after this process exits.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Config.Home}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetTempPath(),
            });
        }

        private static string Dashboard => StartMenu("Siren dashboard.lnk");
        private static string Settings => StartMenu("Siren settings.lnk");

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

        /// <param name="keepDashboard">Restarts only the alarm listener, for a change the dashboard doesn't depend on.</param>
        public static void StopOthers(bool keepDashboard = false)
        {
            var self = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcessesByName("Siren").Where(p => p.Id != self))
            {
                if (keepDashboard && process.MainWindowTitle == "Siren dashboard") continue;
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
