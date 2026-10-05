using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using LyricsOverlay.Core;

namespace MusicBeePlugin
{
    /// <summary>
    /// Optional second DLL: exposes the same LRCLIB fetcher (and the same disk cache) as a provider in
    /// MusicBee's own lyrics settings. A MusicBee plugin has exactly one PluginType, so this can't live
    /// in mb_LyricsOverlay.dll, which must be a General plugin to get player events.
    /// </summary>
    public partial class Plugin
    {
        const string ProviderName = "LRCLIB (Lyrics Overlay)";
        static readonly string UserAgent = ProductInfo.UserAgent("mb_LyricsOverlayProvider");

        MusicBeeApiInterface _api;
        readonly PluginInfo _about = new PluginInfo();
        HttpClient _http;
        LyricsResolver _resolver;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _api = new MusicBeeApiInterface();
            _api.Initialise(apiInterfacePtr);

            _about.PluginInfoVersion = PluginInfoVersion;
            _about.Name = "Lyrics Overlay – LRCLIB provider";
            _about.Description = "Adds LRCLIB as a lyrics provider (shares the Lyrics Overlay cache)";
            _about.Author = ProductInfo.Author;
            _about.TargetApplication = "";
            _about.Type = PluginType.LyricsRetrieval;
            _about.VersionMajor = (short)ProductInfo.Version.Major;
            _about.VersionMinor = (short)ProductInfo.Version.Minor;
            _about.Revision = (short)ProductInfo.Version.Build;
            _about.MinInterfaceVersion = MinInterfaceVersion;
            _about.MinApiRevision = MinApiRevision;
            _about.ReceiveNotifications = ReceiveNotificationFlags.StartupOnly;
            _about.ConfigurationPanelHeight = 0;

            try
            {
                var dataDir = Path.Combine(_api.Setting_GetPersistentStoragePath(), "LyricsOverlay");
                FileLog.Init("mb_LyricsOverlayProvider.log", dataDir);
                _http = LrclibClient.CreateHttpClient(UserAgent, TimeSpan.FromSeconds(8));
                var cache = new LyricsCache(Path.Combine(dataDir, "cache"), TimeSpan.FromDays(7));
                _resolver = new LyricsResolver(cache, new LrclibClient(_http), FileLog.Info);
            }
            catch (Exception ex)
            {
                FileLog.Error("initialise", ex);
            }
            return _about;
        }

        public string[] GetProviders() => new[] { ProviderName };

        /// <summary>
        /// Called by MusicBee on its own background thread, once per enabled provider in order.
        /// Returning null lets MusicBee try the next provider.
        /// </summary>
        public string RetrieveLyrics(string sourceFileUrl, string artist, string trackTitle, string album,
                                     bool synchronisedPreferred, string provider)
        {
            if (provider != ProviderName || _resolver == null) return null;
            try
            {
                var track = new TrackInfo
                {
                    FilePath = sourceFileUrl,
                    Artist = artist,
                    Title = trackTitle,
                    Album = album,
                    DurationMs = DurationMs(sourceFileUrl),
                };
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = _resolver.FetchAsync(track, cts.Token).GetAwaiter().GetResult();
                    if (!result.Found || result.Instrumental) return null;
                    if (synchronisedPreferred) return result.Text;
                    var parsed = LrcParser.Parse(result.Text);
                    return parsed.IsSynced ? parsed.ToPlainText() : result.Text;
                }
            }
            catch (Exception ex)
            {
                FileLog.Error($"RetrieveLyrics '{artist} - {trackTitle}'", ex);
                return null;
            }
        }

        int DurationMs(string url)
        {
            try
            {
                if (string.Equals(url, _api.NowPlaying_GetFileUrl(), StringComparison.OrdinalIgnoreCase))
                    return _api.NowPlaying_GetDuration();
                return DurationText.ParseToMs(_api.Library_GetFileProperty(url, FilePropertyType.Duration));
            }
            catch (Exception)
            {
                return 0; // duration is only a ranking hint
            }
        }

        public bool Configure(IntPtr panelHandle) => false;

        public void SaveSettings() { }

        public void Close(PluginCloseReason reason)
        {
            try
            {
                _resolver?.Shutdown();
                _http?.Dispose();
            }
            catch (Exception ex) { FileLog.Error("close", ex); }
        }

        /// <summary>
        /// The user clicked Uninstall: remove this DLL's log (and the zip's text files, unless the overlay
        /// DLL still uses them). The cache folder is shared with mb_LyricsOverlay.dll, whose own Uninstall
        /// removes it together with the settings.
        /// </summary>
        public void Uninstall()
        {
            Close(PluginCloseReason.UserDisabled);
            FileLog.CloseAndDelete();
            ProductInfo.DeleteShippedDocs("mb_LyricsOverlay.dll");
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type) { }
    }
}
