using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LyricsOverlay.Core;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// Glue between MusicBee, the lyrics resolver and the overlay window. All public methods may be
    /// called from any thread; UI work is marshalled onto the overlay's thread (MusicBee's UI thread).
    /// </summary>
    public sealed class OverlayController : IDisposable
    {
        const int PollPlayingMs = 60;
        const int PollPausedMs = 250;     // still notices seeks made while paused
        const int FrameMs = 15;           // ~60 fps; runs only while something moves
        const int PrefetchDelayMs = 4000; // let the current track's own lookup and rendering settle first

        readonly MusicBeeApiInterface _api;
        readonly string _settingsPath;
        readonly HttpClient _http;
        readonly LyricsResolver _resolver;
        readonly OverlayForm _form;
        readonly System.Windows.Forms.Timer _timer;      // polls the player position
        readonly System.Windows.Forms.Timer _frameTimer; // drives animation frames
        readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        readonly PlaybackClock _clock = new PlaybackClock();
        readonly ScrollAnimator _anim = new ScrollAnimator();
        readonly List<FrameItem> _items = new List<FrameItem>();

        OverlaySettings _settings;
        CancellationTokenSource _lookupCts = new CancellationTokenSource();
        CancellationTokenSource _prefetchCts = new CancellationTokenSource();
        int _generation;                 // bumps on every track change; stale lookups are dropped
        ParsedLyrics _lyrics = ParsedLyrics.Empty;
        string _status = "";             // shown when there are no lyrics to display
        int _targetIndex = int.MinValue; // synced line the animation is heading to
        long _lastFrameKey = long.MinValue;
        int _contentVersion;             // bumps when lyrics, settings or size change: forces a redraw
        int _unsyncedScroll;
        int _tickErrors;
        bool _playing;
        bool _stopped;
        bool _disposed;

        /// <summary>Must be called on MusicBee's UI thread.</summary>
        public OverlayController(MusicBeeApiInterface api, string dataDir)
        {
            _api = api;
            _settingsPath = Path.Combine(dataDir, "settings.json");
            _settings = OverlaySettings.Load(_settingsPath);

            // One shared client for every provider: gzip, 8 s per request, descriptive User-Agent.
            _http = LrclibClient.CreateHttpClient(Plugin.UserAgent, TimeSpan.FromSeconds(8));
            var cache = new LyricsCache(Path.Combine(dataDir, "cache"), TimeSpan.FromDays(7));
            var providers = new ILyricsProvider[]
            {
                new LrclibProvider(new LrclibClient(_http)),
                new NetEaseProvider(_http),
                new LyricsOvhProvider(_http),
            };
            var options = new ProviderChainOptions { IsEnabled = name => _settings.IsProviderEnabled(name) };
            _resolver = new LyricsResolver(cache, providers, options, FileLog.Info);

            _form = new OverlayForm(_settings);
            _form.BoundsCommitted += (s, e) => Safe("save bounds", SaveBounds);
            _form.ScrollRequested += d => Safe("scroll", () => ScrollUnsynced(d));
            _form.SizeChanged += (s, e) => _contentVersion++;
            _ = _form.Handle; // create the handle now so BeginInvoke works even while hidden
            if (_settings.Visible) _form.Show();
            ApplyAnimationSettings();

            _timer = new System.Windows.Forms.Timer { Interval = PollPlayingMs };
            _timer.Tick += (s, e) => OnTick();
            _frameTimer = new System.Windows.Forms.Timer { Interval = FrameMs };
            _frameTimer.Tick += (s, e) => OnFrame();
            UpdateTimers();
        }

        public OverlaySettings Settings => _settings;

        double Now => _stopwatch.Elapsed.TotalMilliseconds;

        // ---------------------------------------------------------------- MusicBee events

        public void OnTrackChanged()
        {
            Ui(() =>
            {
                int gen = ++_generation;
                _stopped = false;
                _playing = _api.Player_GetPlayState() == PlayState.Playing;
                _lookupCts.Cancel();
                _lookupCts.Dispose();
                _lookupCts = new CancellationTokenSource();
                var ct = _lookupCts.Token;

                ShowLyrics(ParsedLyrics.Empty, "Searching for lyrics…");
                // MusicBee API calls that may read tags from disk, plus the network, run off the UI thread.
                Task.Run(() => LookupAsync(gen, ct), ct);
            });
        }

        /// <summary>MusicBee finished its own lyrics download (e.g. from an enabled provider).</summary>
        public void OnLyricsReady()
        {
            Ui(() =>
            {
                if (!_lyrics.IsEmpty) return;
                int gen = _generation;
                Task.Run(() =>
                {
                    try
                    {
                        var text = _api.NowPlaying_GetDownloadedLyrics();
                        if (string.IsNullOrWhiteSpace(text)) return;
                        var parsed = LrcParser.Parse(text);
                        Ui(() => { if (gen == _generation && _lyrics.IsEmpty) ShowLyrics(parsed, ""); });
                    }
                    catch (Exception ex) { FileLog.Error("reading downloaded lyrics", ex); }
                });
            });
        }

        public void OnPlayStateChanged()
        {
            Ui(() =>
            {
                var state = _api.Player_GetPlayState();
                _playing = state == PlayState.Playing;
                if (state == PlayState.Stopped)
                {
                    _lookupCts.Cancel();
                    _generation++;
                    _stopped = true;
                    ShowLyrics(ParsedLyrics.Empty, "");
                }
                else if (state == PlayState.Playing && _stopped)
                {
                    // Stop then Play on the same track may not raise TrackChanged; reload ourselves.
                    OnTrackChanged();
                }
                UpdateTimers();
                Render(force: true); // re-anchor the clock and snap on pause/resume
            });
        }

        async Task LookupAsync(int gen, CancellationToken ct)
        {
            try
            {
                var track = new TrackInfo
                {
                    FilePath = _api.NowPlaying_GetFileUrl(),
                    Artist = _api.NowPlaying_GetFileTag(MetaDataType.Artist),
                    Title = _api.NowPlaying_GetFileTag(MetaDataType.TrackTitle),
                    Album = _api.NowPlaying_GetFileTag(MetaDataType.Album),
                    DurationMs = _api.NowPlaying_GetDuration(),
                };
                // Lyrics stored in the file's tags (MusicBee's view of them).
                string tagLyrics = _api.NowPlaying_GetLyrics();
                if (string.IsNullOrWhiteSpace(tagLyrics)) tagLyrics = _api.NowPlaying_GetDownloadedLyrics();

                var result = await _resolver.ResolveAsync(track, tagLyrics, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                FileLog.Info($"lyrics for '{track.Artist} - {track.Title}': {(result.Found ? result.Source + (result.Provider != null ? " (" + result.Provider + ")" : "") : "none")}");

                var parsed = result.Found && !result.Instrumental ? LrcParser.Parse(result.Text) : ParsedLyrics.Empty;
                string status = result.Instrumental ? "♪ Instrumental ♪" : parsed.IsEmpty ? "No lyrics" : "";
                Ui(() => { if (gen == _generation) ShowLyrics(parsed, status); });

                try { await PrefetchNextAsync(track.FilePath, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { FileLog.Error("prefetch", ex); } // must not turn into "lookup failed"
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (_disposed) return; // shutdown disposed the HttpClient under a running request
                // Timeouts surface as TaskCanceledException without our token being cancelled.
                FileLog.Error("lyrics lookup", ex);
                Ui(() => { if (gen == _generation) ShowLyrics(ParsedLyrics.Empty, "No lyrics (lookup failed)"); });
            }
        }

        /// <summary>
        /// Low-priority warm-up of the next track in the Now Playing list, a few seconds into the current
        /// one. The delay is cancelled by a track change (so skipping through tracks prefetches nothing);
        /// once started, the fetch runs to completion unless another prefetch or shutdown replaces it, so
        /// a quick skip to exactly that track can join it instead of starting over.
        /// </summary>
        async Task PrefetchNextAsync(string currentUrl, CancellationToken trackCt)
        {
            await Task.Delay(PrefetchDelayMs, trackCt).ConfigureAwait(false);
            int next = _api.NowPlayingList_GetNextIndex(1);
            if (next < 0) return;
            string url = _api.NowPlayingList_GetListFileUrl(next);
            if (string.IsNullOrEmpty(url) || string.Equals(url, currentUrl, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrWhiteSpace(_api.Library_GetLyrics(url, LyricsType.NotSpecified))) return; // has its own
            var track = new TrackInfo
            {
                FilePath = url,
                Artist = _api.Library_GetFileTag(url, MetaDataType.Artist),
                Title = _api.Library_GetFileTag(url, MetaDataType.TrackTitle),
                Album = _api.Library_GetFileTag(url, MetaDataType.Album),
                DurationMs = DurationText.ParseToMs(_api.Library_GetFileProperty(url, FilePropertyType.Duration)),
            };
            trackCt.ThrowIfCancellationRequested();

            var cts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _prefetchCts, cts);
            old.Cancel();
            if (_disposed) { cts.Cancel(); return; }
            await _resolver.PrefetchAsync(track, cts.Token).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- display

        void ShowLyrics(ParsedLyrics lyrics, string status)
        {
            if (!ReferenceEquals(lyrics, _lyrics)) _form.ClearTextCache();
            _lyrics = lyrics;
            _status = status;
            _unsyncedScroll = 0;
            _targetIndex = int.MinValue;
            _clock.Reset();
            _contentVersion++;
            UpdateTimers();
            Render(force: true);
        }

        void OnTick()
        {
            if (_disposed) return;
            try
            {
                if (!_form.Visible || !_lyrics.IsSynced) return;
                bool seek = Poll(Now);
                Advance(Now, seek);
            }
            catch (Exception ex)
            {
                // Log the first few, then stay quiet so a persistent failure can't flood the disk.
                if (++_tickErrors <= 5) FileLog.Error("timer tick", ex);
            }
        }

        void OnFrame()
        {
            if (_disposed) return;
            try
            {
                if (!_form.Visible || !_lyrics.IsSynced) { _frameTimer.Stop(); return; }
                Advance(Now, seek: false);
            }
            catch (Exception ex)
            {
                _frameTimer.Stop();
                if (++_tickErrors <= 5) FileLog.Error("frame tick", ex);
            }
        }

        /// <summary>Reads the player and re-anchors the interpolating clock. True if it looks like a seek.</summary>
        bool Poll(double now)
        {
            var state = _api.Player_GetPlayState();
            bool playing = state == PlayState.Playing;
            if (playing != _playing)
            {
                _playing = playing;
                UpdateTimers();
            }
            return _clock.Update(_api.Player_GetPosition(), playing, now);
        }

        /// <summary>Moves the synced display to the clock's position, starting or snapping a transition.</summary>
        void Advance(double now, bool seek)
        {
            double pos = _clock.PositionAt(now);
            var lines = _lyrics.Lines;
            int index = LyricSync.FindLineIndex(lines, (int)pos);
            if (index != _targetIndex)
            {
                if (!LineTransition.ShouldAnimate(_settings.Animation, _targetIndex, index, seek)) _anim.SnapTo(index);
                else if (_settings.Animation == AnimationStyle.Fade) _anim.CrossTo(index, now);
                else _anim.AnimateTo(index, now);
                _targetIndex = index;
            }
            RenderFrame(now);

            // Run frames while moving, and from just before the next line is due so it starts on time
            // instead of up to one poll late. Otherwise the frame timer sleeps.
            bool animating = _anim.IsAnimating(now);
            bool lineSoon = _playing && _settings.Animation != AnimationStyle.None && index + 1 < lines.Count
                            && lines[index + 1].TimeMs - pos <= _timer.Interval + FrameMs;
            SetFrameTimer(animating || lineSoon);
        }

        void RenderFrame(double now)
        {
            bool fade = _settings.Animation == AnimationStyle.Fade;
            double value = fade ? _anim.ProgressAt(now) : _anim.ValueAt(now);
            // Identical frames are skipped: the key covers everything that changes the picture.
            long key = unchecked(((long)_contentVersion << 40) ^ ((long)(fade ? 1 : 2) << 36)
                                 ^ ((long)_anim.From << 24) ^ ((long)_anim.Target << 12) ^ (long)Math.Round(value * 1000));
            if (key == _lastFrameKey) return;
            _lastFrameKey = key;

            if (fade && _anim.IsAnimating(now))
                SyncedLayout.Fade(_lyrics.Lines, (int)_anim.From, (int)_anim.Target, value, _settings.LinesShown, _items);
            else
                SyncedLayout.Slide(_lyrics.Lines, fade ? _anim.Target : value, _settings.LinesShown, _items);
            _form.SetFrame(_items, SyncedLayout.WindowHeight(_settings.LinesShown));
        }

        void Render(bool force)
        {
            if (_lyrics.IsEmpty)
            {
                if (!force) return;
                _form.SetLines(_status.Length == 0 ? new List<DisplayLine>() : new List<DisplayLine> { new DisplayLine(_status, LineRole.Status) });
            }
            else if (_lyrics.IsSynced)
            {
                if (!_form.Visible) return;
                _lastFrameKey = long.MinValue;
                double now = Now;
                bool seek = Poll(now);
                Advance(now, seek || force);
            }
            else
            {
                _form.SetLines(LyricSync.BuildUnsyncedWindow(_lyrics.Lines, _unsyncedScroll, _settings.LinesShown));
            }
        }

        /// <summary>The poll timer runs only while synced lyrics are visible and the player isn't stopped.</summary>
        void UpdateTimers()
        {
            if (_disposed || _timer == null) return;
            bool active = _form.Visible && _lyrics.IsSynced && !_stopped;
            int interval = _playing ? PollPlayingMs : PollPausedMs;
            if (_timer.Interval != interval) _timer.Interval = interval;
            _timer.Enabled = active;
            if (!active || !_playing) SetFrameTimer(false);
        }

        void SetFrameTimer(bool on)
        {
            if (_frameTimer == null || _frameTimer.Enabled == on) return;
            _frameTimer.Enabled = on;
        }

        void ScrollUnsynced(int delta)
        {
            if (_lyrics.IsEmpty || _lyrics.IsSynced) return;
            int max = Math.Max(0, _lyrics.Lines.Count - _settings.LinesShown);
            _unsyncedScroll = Math.Max(0, Math.Min(max, _unsyncedScroll + delta));
            Render(force: true);
        }

        void ApplyAnimationSettings()
        {
            _anim.DurationMs = _settings.Animation == AnimationStyle.None ? 0 : Math.Max(50, Math.Min(2000, _settings.AnimationMs));
            _anim.Easing = _settings.AnimationEasing;
            if (_settings.Animation == AnimationStyle.None) _anim.SnapTo(_anim.Target);
        }

        // ---------------------------------------------------------------- commands & settings

        public void ToggleVisible() => Ui(() =>
        {
            _settings.Visible = !_settings.Visible;
            if (_settings.Visible) { _form.Show(); UpdateTimers(); Render(force: true); }
            else { _form.Hide(); UpdateTimers(); }
            _settings.Save(_settingsPath);
        });

        public void ToggleLocked() => Ui(() =>
        {
            var s = _settings.Clone();
            s.Locked = !s.Locked;
            ApplySettings(s, persist: true);
        });

        /// <summary>Applies settings immediately (used for live preview and OK in the dialog).</summary>
        public void ApplySettings(OverlaySettings s, bool persist) => Ui(() =>
        {
            // Position and size belong to the window; the dialog doesn't edit them.
            s.X = _form.Left; s.Y = _form.Top; s.Width = _form.Width; s.Height = _form.Height;
            _settings = s;
            _contentVersion++;
            ApplyAnimationSettings();
            _form.ApplySettings(s);
            if (s.Visible && !_form.Visible) _form.Show();
            if (!s.Visible && _form.Visible) _form.Hide();
            UpdateTimers();
            Render(force: true);
            if (persist) _settings.Save(_settingsPath);
        });

        public void SaveSettings() => Ui(() => { SaveBounds(); });

        void SaveBounds()
        {
            _settings.X = _form.Left; _settings.Y = _form.Top;
            _settings.Width = _form.Width; _settings.Height = _form.Height;
            _settings.Save(_settingsPath);
        }

        // ---------------------------------------------------------------- plumbing

        /// <summary>Runs on the overlay's UI thread; exceptions are logged, never thrown into MusicBee.</summary>
        void Ui(Action action)
        {
            if (_disposed || _form.IsDisposed) return;
            if (_form.InvokeRequired)
            {
                try { _form.BeginInvoke((Action)(() => Safe("ui", action))); }
                catch (InvalidOperationException) { /* handle destroyed during shutdown */ }
            }
            else
            {
                Safe("ui", action);
            }
        }

        static void Safe(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { FileLog.Error(what, ex); }
        }

        /// <summary>Call on the UI thread. Stops the timers, cancels lookups and prefetches, destroys the window.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            Safe("save on close", SaveBounds);
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _frameTimer.Stop();
            _frameTimer.Dispose();
            _lookupCts.Cancel();
            _prefetchCts.Cancel();
            _resolver.Shutdown();
            _form.Close();
            _form.Dispose();
            _http.Dispose();
        }
    }
}
