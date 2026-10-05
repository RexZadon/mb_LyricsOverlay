using System;
using System.IO;
using System.Windows.Forms;
using LyricsOverlay.Core;

namespace MusicBeePlugin
{
    /// <summary>
    /// MusicBee entry point (General plugin). MusicBee finds this class by name, calls Initialise,
    /// then sends notifications. Every callback is wrapped so an exception can never reach MusicBee.
    /// </summary>
    public partial class Plugin
    {
        public static readonly string UserAgent = ProductInfo.UserAgent("mb_LyricsOverlay");

        MusicBeeApiInterface _api;
        readonly PluginInfo _about = new PluginInfo();
        OverlayController _controller;
        string _dataDir;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _api = new MusicBeeApiInterface();
            _api.Initialise(apiInterfacePtr);

            _about.PluginInfoVersion = PluginInfoVersion;
            _about.Name = "Lyrics Overlay";
            _about.Description = "Floating, transparent synced-lyrics overlay with an LRCLIB-first lyrics fetcher";
            _about.Author = ProductInfo.Author;
            _about.TargetApplication = "";   // not a panel/skin plugin
            _about.Type = PluginType.General;
            _about.VersionMajor = (short)ProductInfo.Version.Major;
            _about.VersionMinor = (short)ProductInfo.Version.Minor;
            _about.Revision = (short)ProductInfo.Version.Build;
            _about.MinInterfaceVersion = MinInterfaceVersion;
            _about.MinApiRevision = MinApiRevision;
            _about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 0; // we show our own dialog from Configure

            Guard("initialise", () =>
            {
                _dataDir = Path.Combine(_api.Setting_GetPersistentStoragePath(), "LyricsOverlay");
                FileLog.Init("mb_LyricsOverlay.log", _dataDir);
                FileLog.Info("initialising, MusicBee API revision " + _api.ApiRevision);
                // The hotkey description makes each item assignable under Preferences > Hotkeys.
                _api.MB_AddMenuItem("mnuTools/Lyrics Overlay: Show or Hide", "Lyrics Overlay: Show or Hide",
                    (s, e) => Guard("menu toggle", () => _controller?.ToggleVisible()));
                _api.MB_AddMenuItem("mnuTools/Lyrics Overlay: Lock or Unlock", "Lyrics Overlay: Lock or Unlock",
                    (s, e) => Guard("menu lock", () => _controller?.ToggleLocked()));
            });
            return _about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            Guard("configure", () =>
            {
                var current = _controller?.Settings ?? OverlaySettings.Load(SettingsPath);
                using (var dlg = new SettingsForm(current))
                {
                    dlg.Changed += s => _controller?.ApplySettings(s, persist: false);
                    var result = dlg.ShowDialog(MainWindow());
                    var final = result == DialogResult.OK ? dlg.Result : dlg.Original;
                    if (_controller != null) _controller.ApplySettings(final, persist: true);
                    else if (result == DialogResult.OK) final.Save(SettingsPath);
                }
            });
            return true;
        }

        /// <summary>Called by MusicBee when the user saves preferences.</summary>
        public void SaveSettings() => Guard("save settings", () => _controller?.SaveSettings());

        public void Close(PluginCloseReason reason) => Guard("close", () =>
        {
            FileLog.Info("closing: " + reason);
            DisposeController();
        });

        /// <summary>The user clicked Uninstall: remove settings, cache and logs.</summary>
        public void Uninstall() => Guard("uninstall", () =>
        {
            DisposeController();
            // Stop logging first so a lookup that finishes late can't recreate the log after it's deleted.
            FileLog.CloseAndDelete();
            if (_dataDir != null && Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true);
            ProductInfo.DeleteShippedDocs("mb_LyricsOverlayProvider.dll");
        });

        public void ReceiveNotification(string sourceFileUrl, NotificationType type) => Guard("notification " + type, () =>
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    OnUi(() =>
                    {
                        _controller = new OverlayController(_api, _dataDir);
                        var state = _api.Player_GetPlayState();
                        if (state == PlayState.Playing || state == PlayState.Paused) _controller.OnTrackChanged();
                    });
                    break;
                case NotificationType.TrackChanged:
                    _controller?.OnTrackChanged();
                    break;
                case NotificationType.PlayStateChanged:
                    _controller?.OnPlayStateChanged();
                    break;
                case NotificationType.NowPlayingLyricsReady:
                    _controller?.OnLyricsReady();
                    break;
            }
        });

        // ---------------------------------------------------------------- helpers

        string SettingsPath => Path.Combine(_dataDir ?? "", "settings.json");

        void DisposeController()
        {
            var c = _controller;
            _controller = null;
            if (c != null) OnUi(c.Dispose);
        }

        Form MainWindow() => Control.FromHandle(_api.MB_GetWindowHandle()) as Form;

        /// <summary>Runs synchronously on MusicBee's UI thread (where our window and timer must live).</summary>
        void OnUi(Action action)
        {
            var main = MainWindow();
            if (main != null && main.InvokeRequired) main.Invoke(action);
            else action();
        }

        static void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { FileLog.Error(what, ex); }
        }
    }
}
