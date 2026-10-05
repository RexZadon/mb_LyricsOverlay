using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LyricsOverlay.Core
{
    /// <summary>One record from LRCLIB's /api/get or /api/search.</summary>
    [DataContract]
    public sealed class LrclibTrack : IRankable
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "trackName")] public string TrackName { get; set; }
        [DataMember(Name = "artistName")] public string ArtistName { get; set; }
        [DataMember(Name = "albumName")] public string AlbumName { get; set; }
        [DataMember(Name = "duration")] public double? Duration { get; set; }
        [DataMember(Name = "instrumental")] public bool Instrumental { get; set; }
        [DataMember(Name = "plainLyrics")] public string PlainLyrics { get; set; }
        [DataMember(Name = "syncedLyrics")] public string SyncedLyrics { get; set; }

        public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
        public bool HasAnyLyrics => HasSynced || !string.IsNullOrWhiteSpace(PlainLyrics);
        public bool IsUsable => HasAnyLyrics || Instrumental;
    }

    /// <summary>
    /// Minimal LRCLIB client. API: https://lrclib.net/docs
    ///   GET /api/get?track_name&amp;artist_name[&amp;album_name][&amp;duration]  -> object, 404 when no match
    ///   GET /api/search?track_name&amp;artist_name  or  ?q=...                -> array (possibly empty)
    /// Throws on network errors and non-404 failures so callers don't cache them as "not found".
    /// </summary>
    public sealed class LrclibClient
    {
        public const string DefaultBaseUrl = "https://lrclib.net";
        readonly HttpClient _http;
        readonly string _baseUrl;

        public LrclibClient(HttpClient http, string baseUrl = DefaultBaseUrl)
        {
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        /// <summary>HttpClient with a short timeout and a descriptive User-Agent, as LRCLIB asks.</summary>
        public static HttpClient CreateHttpClient(string userAgent, TimeSpan timeout)
        {
            // MusicBee's process may default to old TLS versions; add 1.2 without removing anything.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            http.Timeout = timeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return http;
        }

        public async Task<LrclibTrack> GetAsync(string artist, string title, string album, int durationSec, CancellationToken ct)
        {
            var q = new StringBuilder("/api/get?track_name=").Append(Uri.EscapeDataString(title))
                .Append("&artist_name=").Append(Uri.EscapeDataString(artist));
            if (!string.IsNullOrWhiteSpace(album)) q.Append("&album_name=").Append(Uri.EscapeDataString(album));
            if (durationSec > 0) q.Append("&duration=").Append(durationSec);

            using (var resp = await _http.GetAsync(_baseUrl + q, ct).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                RateLimitedException.ThrowIfRateLimited(resp);
                resp.EnsureSuccessStatusCode();
                return Deserialize<LrclibTrack>(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }

        public Task<IReadOnlyList<LrclibTrack>> SearchAsync(string artist, string title, CancellationToken ct)
        {
            var q = "/api/search?track_name=" + Uri.EscapeDataString(title);
            if (!string.IsNullOrWhiteSpace(artist)) q += "&artist_name=" + Uri.EscapeDataString(artist);
            return SearchRawAsync(q, ct);
        }

        public Task<IReadOnlyList<LrclibTrack>> SearchFreeTextAsync(string query, CancellationToken ct) =>
            SearchRawAsync("/api/search?q=" + Uri.EscapeDataString(query), ct);

        async Task<IReadOnlyList<LrclibTrack>> SearchRawAsync(string pathAndQuery, CancellationToken ct)
        {
            using (var resp = await _http.GetAsync(_baseUrl + pathAndQuery, ct).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) return new LrclibTrack[0];
                RateLimitedException.ThrowIfRateLimited(resp);
                resp.EnsureSuccessStatusCode();
                var list = Deserialize<List<LrclibTrack>>(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false));
                return (IReadOnlyList<LrclibTrack>)list ?? new LrclibTrack[0];
            }
        }

        static T Deserialize<T>(Stream s) => (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s);
    }

    public sealed class TrackQuery
    {
        public string Artist { get; set; }
        public string Title { get; set; }
        public string Album { get; set; }
        /// <summary>Track length in seconds, 0 when unknown.</summary>
        public double DurationSec { get; set; }
    }

    /// <summary>
    /// Picks the best candidate (from any provider): title and artist must match after normalization, the
    /// duration must be within <see cref="MaxDurationDiffSec"/>, and the closest duration wins.
    /// Synced lyrics get a small head start (<see cref="SyncedBonusSec"/>) and a matching album a
    /// smaller one, so they win near-ties.
    /// </summary>
    public static class CandidateRanker
    {
        public const double MaxDurationDiffSec = 10;
        public const double SyncedBonusSec = 3;
        public const double AlbumBonusSec = 1;
        const double UnknownDurationPenaltySec = 8;

        public static T PickBest<T>(IEnumerable<T> candidates, TrackQuery q) where T : class, IRankable
        {
            string title = TitleNormalizer.Key(TitleNormalizer.NormalizeTitle(q.Title));
            string artist = TitleNormalizer.Key(TitleNormalizer.NormalizeArtist(q.Artist));
            string album = TitleNormalizer.Key(q.Album);

            T best = null;
            double bestScore = double.MaxValue;
            foreach (var c in candidates ?? Enumerable.Empty<T>())
            {
                if (c == null || !c.IsUsable) continue;
                // Titles must be equal after normalization ("Other Song" is not "Song"); artists may be a
                // superset because credits vary ("The Artist, Guest" vs "The Artist").
                if (title.Length == 0 || TitleNormalizer.Key(TitleNormalizer.NormalizeTitle(c.TrackName)) != title) continue;
                if (artist.Length > 0 && !ArtistMatches(TitleNormalizer.Key(c.ArtistName), artist)) continue;

                double score;
                if (q.DurationSec > 0 && c.Duration.HasValue && c.Duration.Value > 0)
                {
                    score = Math.Abs(c.Duration.Value - q.DurationSec);
                    if (score > MaxDurationDiffSec) continue;
                }
                else
                {
                    score = UnknownDurationPenaltySec;
                }
                if (c.HasSynced) score -= SyncedBonusSec;
                if (album.Length > 0 && TitleNormalizer.Key(c.AlbumName) == album) score -= AlbumBonusSec;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Equal, or one contains the other (handles "Artist A, Artist B" vs "Artist A").</summary>
        static bool ArtistMatches(string candidate, string wanted)
        {
            if (candidate.Length == 0 || wanted.Length == 0) return false;
            if (candidate == wanted) return true;
            if (Math.Min(candidate.Length, wanted.Length) < 3) return false;
            return candidate.Contains(wanted) || wanted.Contains(candidate);
        }
    }
}
