using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Siren
{
    /// <summary>
    /// The installer copies one exe, so the WebView2 libraries and the dashboard's files ride inside it.
    /// The managed libraries load from memory; the page and the native loader have to exist on disk.
    /// </summary>
    public static class WebHost
    {
        private const string Library = "lib/", Page = "web/", Loader = "loader/";

        public static string PageFolder => Path.Combine(Config.Home, "web");
        public static string DataFolder => Path.Combine(Config.Home, "webview");
        public static string LoaderFolder => Path.Combine(Config.Home, "loader");

        /// <summary>Call first thing in Main, before anything touches a WebView2 type.</summary>
        public static void Prepare() => AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            using (var stream = Open(Library + name + ".dll"))
            {
                if (stream == null) return null;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return Assembly.Load(copy.ToArray());
                }
            }
        };

        public static void Unpack()
        {
            Directory.CreateDirectory(LoaderFolder);
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            Write(Loader + arch + "/WebView2Loader.dll", Path.Combine(LoaderFolder, "WebView2Loader.dll"));
            foreach (var name in typeof(WebHost).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Page, StringComparison.Ordinal)))
                Write(name, Path.Combine(PageFolder, name.Substring(Page.Length).Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
        }

        private static Stream Open(string name) => typeof(WebHost).Assembly.GetManifestResourceStream(name);

        private static void Write(string resource, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = Open(resource))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                var bytes = copy.ToArray();
                // The loader stays mapped while a dashboard runs, so an unchanged file is left alone.
                if (File.Exists(path) && new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).SequenceEqual(bytes)) return;
                File.WriteAllBytes(path, bytes);
            }
        }
    }
}
