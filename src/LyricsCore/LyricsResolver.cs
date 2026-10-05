using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace LyricsOverlay.Core
{
    public sealed class TrackInfo
    {
        /// <summary>Local file path of the track; may be null or a URL for streams.</summary>
        public string FilePath { get; set; }
        public string Artist { get; set; }
        public string Title { get; set; }
        public string Album { get; set; }
        public int DurationMs { get; set; }

        public int DurationSec => (int)Math.Round(DurationMs / 1000.0);
        /// <summary>Same identity as the cache key: two TrackInfos for the same song give the same key.</summary>
        public string Key => LyricsCache.MakeKey(Artist, Title, Album, DurationSec);
    }

    public enum LyricsSource { None, Tags, Sidecar, Cache, Online }

    public sealed class LyricsResult
    {
        public static readonly LyricsResult NotFound = new LyricsResult(null, false, LyricsSource.None);

        public LyricsResult(string text, bool instrumental, LyricsSource source, string provider = null)
        {
            Text = text;
            Instrumental = instrumental;
            Source = source;
            Provider = provider;
        }

        public string Text { get; }
        public bool Instrumental { get; }
        public LyricsSource Source { get; }
        /// <summary>Online provider that produced the lyrics (also for cache hits), or null.</summary>
        public string Provider { get; }
        public bool Found => Instrumental || !string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>
    /// Lookup chain: tag lyrics -> sidecar .lrc -> memory/disk cache -> online providers (LRCLIB first).
    /// Concurrent lookups of the same track share one request ("coalescing"); the shared request is
    /// cancelled only when every caller has given up on it.
    /// Everything here may block on disk or network, so call it off the UI thread.
    /// </summary>
    public sealed class LyricsResolver
    {
        readonly LyricsCache _cache;
        readonly ProviderChain _chain;
        readonly Action<string> _log;
        readonly object _gate = new object();
        readonly Dictionary<string, Flight> _flights = new Dictionary<string, Flight>();

        sealed class Flight
        {
            public Task<LyricsResult> Task;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public int Waiters;
        }

        /// <summary>LRCLIB only (used by the provider DLL and the original tests).</summary>
        public LyricsResolver(LyricsCache cache, LrclibClient client, Action<string> log = null)
            : this(cache, new ILyricsProvider[] { new LrclibProvider(client) }, null, log) { }

        public LyricsResolver(LyricsCache cache, IEnumerable<ILyricsProvider> providers, ProviderChainOptions options, Action<string> log = null)
        {
            _cache = cache;
            _log = log ?? (_ => { });
            _chain = new ProviderChain(providers, options, _log);
        }

        public ProviderChain Chain => _chain;

        public async Task<LyricsResult> ResolveAsync(TrackInfo track, string tagLyrics, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(tagLyrics))
                return new LyricsResult(tagLyrics, false, LyricsSource.Tags);

            var sidecar = ReadSidecar(track.FilePath);
            if (sidecar != null)
                return new LyricsResult(sidecar, false, LyricsSource.Sidecar);

            return await FetchAsync(track, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Background warm-up for an upcoming track: fills the cache without hedging (one provider at a
        /// time, gentler on the services). Never throws except for cancellation.
        /// </summary>
        public async Task PrefetchAsync(TrackInfo track, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(track.Title) || ReadSidecar(track.FilePath) != null) return;
            try
            {
                var r = await FetchAsync(track, ct, hedge: false).ConfigureAwait(false);
                _log($"prefetch '{track.Artist} - {track.Title}': {(r.Found ? r.Source + (r.Provider != null ? " via " + r.Provider : "") : "none")}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log($"prefetch '{track.Artist} - {track.Title}' failed: {ex.Message}"); }
        }

        /// <summary>Cache, then online providers. Also used by the MusicBee lyrics-provider plugin.</summary>
        public Task<LyricsResult> FetchAsync(TrackInfo track, CancellationToken ct) => FetchAsync(track, ct, hedge: true);

        async Task<LyricsResult> FetchAsync(TrackInfo track, CancellationToken ct, bool hedge)
        {
            if (string.IsNullOrWhiteSpace(track.Title))
                return LyricsResult.NotFound;

            var key = track.Key;
            if (TryCache(track, out var cached)) return cached;

            // Flights are keyed without the duration: a prefetch (duration from MusicBee's "m:ss" text)
            // and the live lookup (exact milliseconds) of the same song must share one request.
            var flightKey = LyricsCache.MakeKey(track.Artist, track.Title, track.Album, 0);
            Flight f;
            lock (_gate)
            {
                if (!_flights.TryGetValue(flightKey, out f))
                {
                    f = new Flight();
                    var flight = f;
                    _flights[flightKey] = f;
                    f.Task = Task.Run(() => FlyAsync(flightKey, key, track, hedge, flight));
                }
                else
                {
                    _log($"joining in-flight lookup: {track.Artist} - {track.Title}");
                }
                f.Waiters++;
            }

            try
            {
                return await WaitAsync(f.Task, ct).ConfigureAwait(false);
            }
            finally
            {
                bool abandon;
                lock (_gate)
                {
                    // Last interested caller gone and still running: stop the network work.
                    abandon = --f.Waiters == 0 && !f.Task.IsCompleted;
                    if (abandon && _flights.TryGetValue(flightKey, out var cur) && cur == f) _flights.Remove(flightKey);
                }
                // Outside the lock: cancellation callbacks run inline and may re-enter the resolver.
                if (abandon) f.Cts.Cancel();
            }
        }

        /// <summary>Cancels every pending online lookup (plugin shutdown).</summary>
        public void CancelAll()
        {
            List<Flight> pending;
            lock (_gate)
            {
                pending = new List<Flight>(_flights.Values);
                _flights.Clear();
            }
            foreach (var f in pending) f.Cts.Cancel();
        }

        /// <summary>Plugin shutdown: cancels pending lookups and stops cache writes from any that still finish.</summary>
        public void Shutdown()
        {
            _cache.Close();
            CancelAll();
        }

        /// <summary>Number of distinct online lookups in progress (for tests).</summary>
        public int InFlightCount { get { lock (_gate) return _flights.Count; } }

        async Task<LyricsResult> FlyAsync(string flightKey, string key, TrackInfo track, bool hedge, Flight f)
        {
            try
            {
                // A flight that just finished may have filled the cache since the caller checked.
                if (TryCache(track, out var cached)) return cached;
                return await QueryOnlineAsync(key, track, hedge, f.Cts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                    if (_flights.TryGetValue(flightKey, out var cur) && cur == f) _flights.Remove(flightKey);
            }
        }

        /// <summary>Exact key first, then the same song one second shorter or longer (MusicBee reports the
        /// playing track in milliseconds but other tracks only as whole "m:ss", which may round differently).</summary>
        bool TryCache(TrackInfo track, out LyricsResult result)
        {
            result = null;
            CacheEntry cached = null;
            int d = track.DurationSec;
            bool hit = _cache.TryGet(track.Key, out cached)
                       || (d > 0 && (_cache.TryGet(LyricsCache.MakeKey(track.Artist, track.Title, track.Album, d - 1), out cached)
                                     || _cache.TryGet(LyricsCache.MakeKey(track.Artist, track.Title, track.Album, d + 1), out cached)));
            if (!hit) return false;
            string via = cached.Provider != null ? ", " + cached.Provider : "";
            _log($"cache hit ({(cached.IsNegative ? "negative" : "lyrics")}{via}): {track.Artist} - {track.Title}");
            result = cached.IsNegative ? LyricsResult.NotFound : new LyricsResult(cached.Lyrics, cached.Instrumental, LyricsSource.Cache, cached.Provider);
            return true;
        }

        async Task<LyricsResult> QueryOnlineAsync(string key, TrackInfo track, bool hedge, CancellationToken ct)
        {
            var q = new TrackQuery
            {
                Artist = track.Artist,
                Title = track.Title,
                Album = track.Album,
                DurationSec = track.DurationMs / 1000.0,
            };
            var chain = await _chain.RunAsync(q, hedge, ct).ConfigureAwait(false);
            var best = chain.Best;

            if (best == null)
            {
                // Network errors propagate: we must not cache a failed lookup as "not found".
                if (chain.FirstError != null) ExceptionDispatchInfo.Capture(chain.FirstError).Throw();
                if (chain.Incomplete)
                {
                    _log($"no match for {track.Artist} - {track.Title}, but some providers were skipped; not caching");
                    return LyricsResult.NotFound;
                }
            }

            var entry = new CacheEntry { Artist = track.Artist, Title = track.Title };
            if (best != null)
            {
                entry.Instrumental = best.Instrumental && !best.HasAnyLyrics;
                entry.Lyrics = best.HasSynced ? best.SyncedLyrics : best.PlainLyrics;
                entry.Provider = best.Provider;
            }
            TryPut(key, entry);
            _log(best == null
                ? $"online: no match for {track.Artist} - {track.Title} (tried {string.Join(", ", chain.Started)})"
                : $"{best.Provider}: matched id {best.Id} ({(best.HasSynced ? "synced" : entry.Instrumental ? "instrumental" : "plain")}) for {track.Artist} - {track.Title}");

            return entry.IsNegative ? LyricsResult.NotFound : new LyricsResult(entry.Lyrics, entry.Instrumental, LyricsSource.Online, entry.Provider);
        }

        static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled || task.IsCompleted) return await task.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(ct);
            }
            return await task.ConfigureAwait(false);
        }

        void TryPut(string key, CacheEntry entry)
        {
            try { _cache.Put(key, entry); }
            catch (Exception ex) { _log("cache write failed: " + ex.Message); }
        }

        /// <summary>Reads "Song.lrc" next to "Song.mp3", or null.</summary>
        public static string ReadSidecar(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || filePath.Contains("://")) return null;
            try
            {
                var lrc = Path.ChangeExtension(filePath, ".lrc");
                if (!File.Exists(lrc)) return null;
                var text = File.ReadAllText(lrc); // detects BOM, defaults to UTF-8
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null; // bad path characters, access denied, ...
            }
        }
    }
}
