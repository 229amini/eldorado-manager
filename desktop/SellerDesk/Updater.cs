using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SellerDesk
{
    /// <summary>A published version newer than this one.</summary>
    public sealed class Release
    {
        public Version Version;
        public string Tag = "";
        public string Url = "";
        public string Sha256 = "";   // lower-case hex, from the digest GitHub keeps for each release file
        public long Size;
        public List<string> Notes = new List<string>();   // the release's CHANGELOG lines, markup taken out
    }

    /// <summary>
    /// Finds a newer SellerDesk on GitHub and installs it. The download is checked against the SHA-256 digest
    /// GitHub records for the release file, so a broken or swapped file is refused before it runs. The new
    /// exe installs itself the usual way (Installer.Install), which stops this one, so settings and data stay.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "229amini/sellerdesk";
        private const string Latest = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string Asset = "SellerDeskSetup.exe";
        private const int MostNotes = 8;

        public static Version Current
        {
            get
            {
                var version = typeof(Updater).Assembly.GetName().Version;
                return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            }
        }

        /// <summary>The latest release when it is newer than this build, otherwise null. Throws WebException offline.</summary>
        public static Release Check()
        {
            var request = Request(Latest, 20000);
            request.Accept = "application/vnd.github+json";
            string text;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                text = reader.ReadToEnd();
            return Parse(text, Current);
        }

        public static Release Parse(string text, Version current)
        {
            var reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            var tag = reply.TryGetValue("tag_name", out var t) ? t as string ?? "" : "";
            if (reply.TryGetValue("draft", out var draft) && draft is bool d && d) return null;
            if (reply.TryGetValue("prerelease", out var pre) && pre is bool p && p) return null;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= current) return null;
            var asset = (reply.TryGetValue("assets", out var list) ? list as ArrayList : null)?.OfType<Dictionary<string, object>>()
                .FirstOrDefault(a => a.TryGetValue("name", out var name) && name as string == Asset);
            if (asset == null) return null;
            var digest = asset.TryGetValue("digest", out var dg) ? dg as string ?? "" : "";
            var body = reply.TryGetValue("body", out var b) ? b as string ?? "" : "";
            return new Release
            {
                Version = version,
                Tag = tag,
                Url = asset.TryGetValue("browser_download_url", out var url) ? url as string ?? "" : "",
                Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7).ToLowerInvariant() : "",
                Size = asset.TryGetValue("size", out var size) ? Convert.ToInt64(size) : 0,
                Notes = Notes(body),
            };
        }

        // The bullets of the release text, which the release workflow copies from CHANGELOG.md.
        private static List<string> Notes(string body) => body.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ") || line.StartsWith("* "))
            .Select(line => Regex.Replace(line.Substring(2), @"\*\*|`|\[([^\]]*)\]\([^)]*\)", m => m.Groups[1].Success ? m.Groups[1].Value : ""))
            .Where(line => line.Length > 0 && !line.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase))
            .Take(MostNotes).ToList();

        /// <summary>Downloads the release into a fresh folder and returns the exe, or throws with a reason a person can read.</summary>
        public static string Download(Release release, Action<int> progress)
        {
            if (release.Sha256.Length != 64) throw new InvalidDataException("GitHub gave no checksum for this release, so it can't be checked.");
            Tidy();
            var folder = Path.Combine(Path.GetTempPath(), "SellerDesk-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Asset);
            using (var response = Request(release.Url, 30000).GetResponse())
            using (var source = response.GetResponseStream())
            using (var target = File.Create(path))
            using (var sha = SHA256.Create())
            {
                var total = response.ContentLength > 0 ? response.ContentLength : release.Size;
                var buffer = new byte[81920];
                long done = 0;
                var shown = -1;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent != shown) progress(shown = percent);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                var hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (hex != release.Sha256)
                {
                    target.Close();
                    Directory.Delete(folder, true);
                    throw new InvalidDataException("The download doesn't match GitHub's checksum, so it was thrown away.");
                }
            }
            return path;
        }

        /// <summary>Folders left by earlier updates. The exe in the newest may still be running, so a locked one waits.</summary>
        public static void Tidy()
        {
            foreach (var folder in Directory.GetDirectories(Path.GetTempPath(), "SellerDesk-update-*"))
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Runs the new exe, which stops every SellerDesk process, installs over this one and starts it again.</summary>
        public static void Launch(string exe)
        {
            Config.Log($"update: {Current} -> {exe}");
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        private static HttpWebRequest Request(string url, int timeout)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "SellerDesk/" + Current;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            return request;
        }
    }
}
