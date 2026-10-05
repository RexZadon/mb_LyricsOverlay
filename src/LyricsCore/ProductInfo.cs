using System;
using System.Linq;
using System.Reflection;

namespace LyricsOverlay.Core
{
    /// <summary>
    /// Version, author and project URL of the assembly this file is compiled into. All of them come
    /// from Directory.Build.props, so a release only has to change them there.
    /// </summary>
    public static class ProductInfo
    {
        static readonly Assembly Self = typeof(ProductInfo).Assembly;

        public static Version Version => Self.GetName().Version;

        public static string ProjectUrl => Metadata("ProjectUrl");

        public static string Author => Metadata("Author");

        /// <summary>
        /// "component/1.2.3 (+https://project.url)": names the client, its version and where to reach its
        /// author, as LRCLIB's API docs require.
        /// </summary>
        public static string UserAgent(string component) =>
            $"{component}/{Version.ToString(3)} (+{ProjectUrl})";

        /// <summary>Text files the release zips put next to the DLLs (see tools/package.ps1).</summary>
        public static readonly string[] ShippedDocs =
        {
            "mb_LyricsOverlay_README.txt", "mb_LyricsOverlay_LICENSE.txt", "mb_LyricsOverlay_THIRD_PARTY_NOTICES.txt",
        };

        /// <summary>
        /// Uninstall: removes <see cref="ShippedDocs"/> from the plugin folder, unless <paramref name="siblingDll"/>
        /// (the other plugin of this project, which shares them) is still installed there. Never throws.
        /// </summary>
        public static void DeleteShippedDocs(string siblingDll)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Self.Location);
                if (string.IsNullOrEmpty(dir) || System.IO.File.Exists(System.IO.Path.Combine(dir, siblingDll))) return;
                foreach (var name in ShippedDocs)
                {
                    try { System.IO.File.Delete(System.IO.Path.Combine(dir, name)); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        static string Metadata(string key) =>
            Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
    }
}
