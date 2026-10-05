using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    /// <summary>Serves recorded LRCLIB responses by URL path; no network.</summary>
    sealed class FixtureHandler : HttpMessageHandler
    {
        readonly List<(string Prefix, HttpStatusCode Status, string Fixture)> _routes = new List<(string, HttpStatusCode, string)>();
        public readonly List<Uri> Requests = new List<Uri>();

        public FixtureHandler On(string pathAndQueryPrefix, string fixture, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((pathAndQueryPrefix, status, fixture));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri);
            Assert.Contains("mb_LyricsOverlay", request.Headers.UserAgent.ToString());
            var pq = request.RequestUri.PathAndQuery;
            foreach (var r in _routes)
            {
                if (!pq.StartsWith(r.Prefix, StringComparison.Ordinal)) continue;
                var body = r.Fixture == null ? "" : File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", r.Fixture));
                return Task.FromResult(new HttpResponseMessage(r.Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            throw new InvalidOperationException("Unexpected request: " + pq);
        }
    }

    public sealed class FetcherTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "LyricsOverlayTests_" + Guid.NewGuid().ToString("N"));
        DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static readonly TrackInfo Track = new TrackInfo
        {
            Artist = "The Artist", Title = "Song (Remastered 2011)", Album = "Album", DurationMs = 200400,
        };

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        LyricsResolver Resolver(FixtureHandler h, out HttpClient http)
        {
            http = new HttpClient(h);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mb_LyricsOverlay-tests/1.0");
            var cache = new LyricsCache(Path.Combine(_dir, "cache"), TimeSpan.FromDays(7), () => _now);
            return new LyricsResolver(cache, new LrclibClient(http, "https://lrclib.test"));
        }

        [Fact]
        public async Task ExactMatchWithSyncedLyricsUsesNormalizedQuery()
        {
            var h = new FixtureHandler().On("/api/get?", "get_synced.json");
            var r = await Resolver(h, out _).ResolveAsync(Track, null, CancellationToken.None);

            Assert.Equal(LyricsSource.Online, r.Source);
            Assert.True(LrcParser.Parse(r.Text).IsSynced);
            var q = Uri.UnescapeDataString(h.Requests.Single().Query);
            Assert.Contains("track_name=Song&", q);            // "(Remastered 2011)" stripped
            Assert.Contains("artist_name=The Artist", q);
            Assert.Contains("album_name=Album", q);
            Assert.Contains("duration=200", q);
        }

        [Fact]
        public async Task FallsBackToSearchAndPicksClosestMatchingCandidate()
        {
            var h = new FixtureHandler()
                .On("/api/get?", "not_found.json", HttpStatusCode.NotFound)
                .On("/api/search?track_name=", "search_candidates.json");
            var r = await Resolver(h, out _).ResolveAsync(Track, null, CancellationToken.None);

            Assert.Contains("Studio placeholder", r.Text); // id 4002: right artist, 1 s off, has lyrics
            Assert.Equal(2, h.Requests.Count);
        }

        [Fact]
        public async Task PlainOnlyExactMatchIsUpgradedToSyncedFromSearch()
        {
            var h = new FixtureHandler()
                .On("/api/get?", "get_plain_only.json")
                .On("/api/search?track_name=", "search_candidates.json");
            var r = await Resolver(h, out _).ResolveAsync(Track, null, CancellationToken.None);
            Assert.Contains("Studio placeholder", r.Text);
        }

        [Fact]
        public async Task PlainOnlyExactMatchIsKeptWhenSearchHasNothingBetter()
        {
            var h = new FixtureHandler()
                .On("/api/get?", "get_plain_only.json")
                .On("/api/search?", "search_empty.json");
            var r = await Resolver(h, out _).ResolveAsync(Track, null, CancellationToken.None);
            Assert.Equal("Plain placeholder line", r.Text);
            Assert.False(LrcParser.Parse(r.Text).IsSynced);
        }

        [Fact]
        public async Task InstrumentalIsReportedAndCached()
        {
            var h = new FixtureHandler().On("/api/get?", "get_instrumental.json");
            var resolver = Resolver(h, out _);
            var r = await resolver.ResolveAsync(Track, null, CancellationToken.None);
            Assert.True(r.Instrumental);

            var again = await resolver.ResolveAsync(Track, null, CancellationToken.None);
            Assert.True(again.Instrumental);
            Assert.Equal(LyricsSource.Cache, again.Source);
            Assert.Single(h.Requests);
        }

        [Fact]
        public async Task NoMatchIsNegativelyCachedUntilExpiry()
        {
            var h = new FixtureHandler()
                .On("/api/get?", "not_found.json", HttpStatusCode.NotFound)
                .On("/api/search?", "search_empty.json");
            var resolver = Resolver(h, out _);

            Assert.False((await resolver.ResolveAsync(Track, null, CancellationToken.None)).Found);
            Assert.Equal(3, h.Requests.Count); // get, structured search, free-text search
            Assert.Equal("q=The Artist Song", Uri.UnescapeDataString(h.Requests[2].Query.TrimStart('?')));

            _now = _now.AddDays(6);
            Assert.False((await resolver.ResolveAsync(Track, null, CancellationToken.None)).Found);
            Assert.Equal(3, h.Requests.Count); // served from the negative cache

            _now = _now.AddDays(2);
            await resolver.ResolveAsync(Track, null, CancellationToken.None);
            Assert.Equal(6, h.Requests.Count); // expired: asked again
        }

        [Fact]
        public async Task PositiveResultIsCachedForever()
        {
            var h = new FixtureHandler().On("/api/get?", "get_synced.json");
            var resolver = Resolver(h, out _);
            await resolver.ResolveAsync(Track, null, CancellationToken.None);
            _now = _now.AddYears(5);
            var r = await resolver.ResolveAsync(Track, null, CancellationToken.None);
            Assert.Equal(LyricsSource.Cache, r.Source);
            Assert.Single(h.Requests);
        }

        [Fact]
        public async Task ServerErrorThrowsAndIsNotCached()
        {
            var h = new FixtureHandler().On("/api/get?", null, HttpStatusCode.InternalServerError);
            var resolver = Resolver(h, out _);
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveAsync(Track, null, CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(_dir, "cache")) && Directory.GetFiles(Path.Combine(_dir, "cache")).Any());
        }

        [Fact]
        public async Task TagLyricsWinWithoutAnyRequest()
        {
            var h = new FixtureHandler();
            var r = await Resolver(h, out _).ResolveAsync(Track, "[00:01.00]from tags", CancellationToken.None);
            Assert.Equal(LyricsSource.Tags, r.Source);
            Assert.Empty(h.Requests);
        }

        [Fact]
        public async Task SidecarLrcBeatsCacheAndNetwork()
        {
            Directory.CreateDirectory(_dir);
            var audio = Path.Combine(_dir, "track.flac");
            File.WriteAllText(Path.Combine(_dir, "track.lrc"), "[00:01.00]from sidecar");
            var h = new FixtureHandler();
            var t = new TrackInfo { FilePath = audio, Artist = "A", Title = "B", DurationMs = 1000 };
            var r = await Resolver(h, out _).ResolveAsync(t, null, CancellationToken.None);
            Assert.Equal(LyricsSource.Sidecar, r.Source);
            Assert.Empty(h.Requests);
        }

        [Fact]
        public async Task MissingArtistSkipsExactGetAndSearchesByTitle()
        {
            var h = new FixtureHandler().On("/api/search?", "search_empty.json");
            var t = new TrackInfo { Title = "Song", DurationMs = 200000 };
            var r = await Resolver(h, out _).ResolveAsync(t, null, CancellationToken.None);
            Assert.False(r.Found);
            Assert.All(h.Requests, u => Assert.StartsWith("/api/search", u.AbsolutePath));
        }

        [Fact]
        public void CorruptCacheFileIsTreatedAsMiss()
        {
            var cache = new LyricsCache(_dir, TimeSpan.FromDays(7));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "abc.json"), "{not json");
            Assert.False(cache.TryGet("abc", out _));
        }
    }
}
