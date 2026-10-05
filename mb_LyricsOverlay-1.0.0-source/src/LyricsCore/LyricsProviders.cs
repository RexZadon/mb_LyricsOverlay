using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LyricsOverlay.Core
{
    /// <summary>What <see cref="CandidateRanker"/> needs to compare a search result with the playing track.</summary>
    public interface IRankable
    {
        string TrackName { get; }
        string ArtistName { get; }
        string AlbumName { get; }
        /// <summary>Seconds, null when the source doesn't say.</summary>
        double? Duration { get; }
        bool HasSynced { get; }
        bool IsUsable { get; }
    }

    /// <summary>A search result from any provider, with or without its lyrics loaded yet.</summary>
    public sealed class LyricsCandidate : IRankable
    {
        public string Provider { get; set; }
        /// <summary>Provider-specific id, used by <see cref="ILyricsProvider.FetchAsync"/>.</summary>
        public string Id { get; set; }
        public string TrackName { get; set; }
        public string ArtistName { get; set; }
        public string AlbumName { get; set; }
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        public string SyncedLyrics { get; set; }
        public string PlainLyrics { get; set; }
        /// <summary>False when the search only returned metadata and FetchAsync still has to load the text.</summary>
        public bool LyricsLoaded { get; set; } = true;
        /// <summary>The provider itself matched artist, title and duration (LRCLIB /api/get): no ranking needed.</summary>
        public bool ExactMatch { get; set; }

        public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
        public bool HasAnyLyrics => HasSynced || !string.IsNullOrWhiteSpace(PlainLyrics);
        /// <summary>Unloaded candidates count as usable so they can be ranked before their lyrics are fetched.</summary>
        public bool IsUsable => !LyricsLoaded || HasAnyLyrics || Instrumental;
        /// <summary>Synced lyrics or a confirmed instrumental: nothing better can be found elsewhere.</summary>
        public bool IsDefinitive => HasSynced || (Instrumental && !HasAnyLyrics);
    }

    /// <summary>
    /// One online lyrics source. Implementations throw on network errors and server failures (so the
    /// chain can tell "failed" from "not found") and return empty results when the track isn't known.
    /// </summary>
    public interface ILyricsProvider
    {
        /// <summary>Short display name, also the key for the settings toggle and the cache record.</summary>
        string Name { get; }
        /// <summary>False for sources that only ever have plain text; they are skipped once plain lyrics are in hand.</summary>
        bool SupportsSynced { get; }
        Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackQuery query, CancellationToken ct);
        /// <summary>Loads lyrics for a candidate from <see cref="SearchAsync"/>; returns it unchanged if already loaded.</summary>
        Task<LyricsCandidate> FetchAsync(LyricsCandidate candidate, CancellationToken ct);
    }

    /// <summary>
    /// The service answered 429 Too Many Requests. The chain backs the provider off for at least
    /// <see cref="RetryAfter"/> (LRCLIB's docs require clients to honour Retry-After).
    /// </summary>
    public sealed class RateLimitedException : HttpRequestException
    {
        /// <summary>Used when the server sends no usable Retry-After header.</summary>
        public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

        public RateLimitedException(TimeSpan retryAfter)
            : base($"429 Too Many Requests, retry after {retryAfter.TotalSeconds:0} s") => RetryAfter = retryAfter;

        public TimeSpan RetryAfter { get; }

        /// <summary>Throws for a 429 response; does nothing otherwise.</summary>
        public static void ThrowIfRateLimited(HttpResponseMessage resp, Func<DateTime> utcNow = null)
        {
            if ((int)resp.StatusCode != 429) return;
            var ra = resp.Headers.RetryAfter;
            TimeSpan wait = DefaultRetryAfter;
            if (ra?.Delta != null) wait = ra.Delta.Value;
            else if (ra?.Date != null) wait = ra.Date.Value.UtcDateTime - (utcNow ?? (() => DateTime.UtcNow))();
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            throw new RateLimitedException(wait);
        }
    }

    static class JsonUtil
    {
        public static T Deserialize<T>(Stream s) => (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s);

        public static async Task<T> GetJsonAsync<T>(HttpClient http, string url, CancellationToken ct) where T : class
        {
            using (var resp = await http.GetAsync(url, ct).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                RateLimitedException.ThrowIfRateLimited(resp);
                resp.EnsureSuccessStatusCode();
                return Deserialize<T>(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }
    }

    /// <summary>Last-line sanity checks that apply to every provider.</summary>
    public static class LyricsSanity
    {
        /// <summary>Synced lyrics whose timestamps run well past the end of the track belong to another recording.</summary>
        public const double MaxOverrunSec = 15;

        public static bool IsPlausible(LyricsCandidate c, TrackQuery q)
        {
            if (c == null) return false;
            if (!c.HasAnyLyrics) return c.Instrumental;
            if (c.HasSynced && q.DurationSec > 0)
            {
                var lines = LrcParser.Parse(c.SyncedLyrics).Lines;
                if (lines.Count > 0 && lines[lines.Count - 1].TimeMs / 1000.0 > q.DurationSec + MaxOverrunSec) return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------ LRCLIB

    /// <summary>
    /// LRCLIB (https://lrclib.net/docs): free, open, documented API, no key. The search step keeps the
    /// original three-step flow: exact /api/get, then structured search, then free-text search.
    /// </summary>
    public sealed class LrclibProvider : ILyricsProvider
    {
        public const string ProviderName = "LRCLIB";
        readonly LrclibClient _client;

        public LrclibProvider(LrclibClient client) => _client = client;

        public string Name => ProviderName;
        public bool SupportsSynced => true;

        public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackQuery q, CancellationToken ct)
        {
            string title = TitleNormalizer.NormalizeTitle(q.Title);
            string artist = TitleNormalizer.NormalizeArtist(q.Artist);
            int durationSec = (int)Math.Round(q.DurationSec);
            var candidates = new List<LrclibTrack>();

            // 1. Exact match (LRCLIB requires artist and title; it matches duration within ~2 s).
            if (artist.Length > 0)
            {
                var exact = await _client.GetAsync(artist, title, q.Album, durationSec, ct).ConfigureAwait(false);
                if (exact != null && exact.IsUsable)
                {
                    if (exact.HasSynced || exact.Instrumental)
                    {
                        var c = Convert(exact);
                        c.ExactMatch = true;
                        return new[] { c };
                    }
                    candidates.Add(exact); // plain only: see whether search has a synced version
                }
            }

            // 2. Structured search, 3. free-text search.
            candidates.AddRange(await _client.SearchAsync(artist, title, ct).ConfigureAwait(false));
            if (CandidateRanker.PickBest(candidates, q) != null) return candidates.Select(Convert).ToList();

            var free = await _client.SearchFreeTextAsync((artist + " " + title).Trim(), ct).ConfigureAwait(false);
            return free.Select(Convert).ToList();
        }

        public Task<LyricsCandidate> FetchAsync(LyricsCandidate candidate, CancellationToken ct) => Task.FromResult(candidate);

        static LyricsCandidate Convert(LrclibTrack t) => new LyricsCandidate
        {
            Provider = ProviderName,
            Id = t.Id.ToString(),
            TrackName = t.TrackName,
            ArtistName = t.ArtistName,
            AlbumName = t.AlbumName,
            Duration = t.Duration,
            Instrumental = t.Instrumental,
            SyncedLyrics = t.SyncedLyrics,
            PlainLyrics = t.PlainLyrics,
        };
    }

    // ------------------------------------------------------------------------------------------ NetEase

    /// <summary>
    /// NetEase Cloud Music. Unofficial, undocumented endpoints used by many open-source lyric tools:
    ///   GET /api/search/get?s=artist+title&amp;type=1&amp;limit=10  -> result.songs[] {id, name, artists[], album, duration ms}
    ///   GET /api/song/lyric?id=..&amp;lv=1                          -> lrc.lyric (LRC text), nolyric (instrumental)
    /// No key or cookie. NetEase answers HTTP 200 with a non-200 "code" when it rate-limits or wants
    /// a captcha (e.g. -462); that is treated as a failure so the circuit breaker backs off.
    /// </summary>
    public sealed class NetEaseProvider : ILyricsProvider
    {
        public const string ProviderName = "NetEase";
        public const string DefaultBaseUrl = "https://music.163.com";
        const int SearchLimit = 10;

        // Credit lines NetEase prepends to the lyric text ("作词 : Someone"); they aren't lyrics.
        static readonly Regex CreditLine = new Regex(
            @"^(\[[\d:.]+\])+\s*(作词|作曲|编曲|制作人|监制|混音|母带|录音|和声|吉他|贝斯|鼓|键盘|弦乐|出品|发行|OP|SP)\s*[:：].*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        readonly HttpClient _http;
        readonly string _baseUrl;

        public NetEaseProvider(HttpClient http, string baseUrl = DefaultBaseUrl)
        {
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public string Name => ProviderName;
        public bool SupportsSynced => true;

        public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackQuery q, CancellationToken ct)
        {
            string term = (TitleNormalizer.NormalizeArtist(q.Artist) + " " + TitleNormalizer.NormalizeTitle(q.Title)).Trim();
            if (term.Length == 0) return new LyricsCandidate[0];
            var url = _baseUrl + "/api/search/get?s=" + Uri.EscapeDataString(term) + "&type=1&limit=" + SearchLimit + "&offset=0";
            var resp = await JsonUtil.GetJsonAsync<NeSearchResponse>(_http, url, ct).ConfigureAwait(false);
            if (resp == null) return new LyricsCandidate[0];
            if (resp.Code != 200) throw new HttpRequestException("NetEase search returned code " + resp.Code);
            var songs = resp.Result?.Songs;
            if (songs == null) return new LyricsCandidate[0];
            return songs.Where(s => s != null).Select(s => new LyricsCandidate
            {
                Provider = ProviderName,
                Id = s.Id.ToString(),
                TrackName = s.Name,
                ArtistName = s.Artists == null ? "" : string.Join(", ", s.Artists.Where(a => a != null).Select(a => a.Name)),
                AlbumName = s.Album?.Name,
                Duration = s.DurationMs > 0 ? s.DurationMs / 1000.0 : (double?)null,
                LyricsLoaded = false,
            }).ToList();
        }

        public async Task<LyricsCandidate> FetchAsync(LyricsCandidate c, CancellationToken ct)
        {
            if (c.LyricsLoaded) return c;
            var url = _baseUrl + "/api/song/lyric?id=" + Uri.EscapeDataString(c.Id) + "&lv=1";
            var resp = await JsonUtil.GetJsonAsync<NeLyricResponse>(_http, url, ct).ConfigureAwait(false);
            if (resp != null && resp.Code != 0 && resp.Code != 200) throw new HttpRequestException("NetEase lyric returned code " + resp.Code);

            c.LyricsLoaded = true;
            c.Instrumental = resp?.NoLyric == true;
            var text = CleanLyric(resp?.Lrc?.Lyric);
            if (text == null) return c;
            var parsed = LrcParser.Parse(text);
            // Untimed lyrics sometimes come as every line stamped [00:00.000]: that's plain text, not sync.
            bool reallySynced = parsed.IsSynced && parsed.Lines.Count > 1 && parsed.Lines[parsed.Lines.Count - 1].TimeMs > 0;
            if (reallySynced) c.SyncedLyrics = text;
            else if (!parsed.IsEmpty) c.PlainLyrics = parsed.ToPlainText();
            return c;
        }

        /// <summary>Removes NetEase's credit lines; null when nothing is left.</summary>
        public static string CleanLyric(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return null;
            var s = CreditLine.Replace(lyric.Replace("\r\n", "\n"), "");
            return string.IsNullOrWhiteSpace(s) || LrcParser.Parse(s).IsEmpty ? null : s;
        }

        [DataContract] sealed class NeSearchResponse
        {
            [DataMember(Name = "code")] public int Code { get; set; }
            [DataMember(Name = "result")] public NeResult Result { get; set; }
        }
        [DataContract] sealed class NeResult
        {
            [DataMember(Name = "songs")] public List<NeSong> Songs { get; set; }
        }
        [DataContract] sealed class NeSong
        {
            [DataMember(Name = "id")] public long Id { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "artists")] public List<NeNamed> Artists { get; set; }
            [DataMember(Name = "album")] public NeNamed Album { get; set; }
            [DataMember(Name = "duration")] public long DurationMs { get; set; }
        }
        [DataContract] sealed class NeNamed
        {
            [DataMember(Name = "name")] public string Name { get; set; }
        }
        [DataContract] sealed class NeLyricResponse
        {
            [DataMember(Name = "code")] public int Code { get; set; }
            [DataMember(Name = "nolyric")] public bool? NoLyric { get; set; }
            [DataMember(Name = "lrc")] public NeLrc Lrc { get; set; }
        }
        [DataContract] sealed class NeLrc
        {
            [DataMember(Name = "lyric")] public string Lyric { get; set; }
        }
    }

    // ------------------------------------------------------------------------------------------ lyrics.ovh

    /// <summary>
    /// lyrics.ovh (https://lyricsovh.docs.apiary.io): public, documented, keyless API that returns
    /// plain lyrics for an exact artist + title. No sync and no duration, so it is only a last resort.
    ///   GET /v1/{artist}/{title} -> {"lyrics": "..."} or 404 {"error": "No lyrics found"}
    /// </summary>
    public sealed class LyricsOvhProvider : ILyricsProvider
    {
        public const string ProviderName = "lyrics.ovh";
        public const string DefaultBaseUrl = "https://api.lyrics.ovh";
        // The service prefixes some texts with a French "Paroles de la chanson X par Y" header line.
        static readonly Regex Header = new Regex(@"^\s*Paroles de la chanson .*?(\r?\n|$)", RegexOptions.Compiled);

        readonly HttpClient _http;
        readonly string _baseUrl;

        public LyricsOvhProvider(HttpClient http, string baseUrl = DefaultBaseUrl)
        {
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public string Name => ProviderName;
        public bool SupportsSynced => false;

        public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackQuery q, CancellationToken ct)
        {
            string artist = TitleNormalizer.NormalizeArtist(q.Artist);
            string title = TitleNormalizer.NormalizeTitle(q.Title);
            if (artist.Length == 0 || title.Length == 0) return new LyricsCandidate[0];
            var url = _baseUrl + "/v1/" + Uri.EscapeDataString(artist) + "/" + Uri.EscapeDataString(title);
            var resp = await JsonUtil.GetJsonAsync<OvhResponse>(_http, url, ct).ConfigureAwait(false);
            var text = resp?.Lyrics == null ? null : Header.Replace(resp.Lyrics, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return new LyricsCandidate[0];
            // The service matched on exactly this artist and title, so those are the candidate's metadata.
            return new[]
            {
                new LyricsCandidate { Provider = ProviderName, TrackName = title, ArtistName = artist, PlainLyrics = text },
            };
        }

        public Task<LyricsCandidate> FetchAsync(LyricsCandidate candidate, CancellationToken ct) => Task.FromResult(candidate);

        [DataContract] sealed class OvhResponse
        {
            [DataMember(Name = "lyrics")] public string Lyrics { get; set; }
        }
    }
}
