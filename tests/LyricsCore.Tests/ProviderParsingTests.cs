using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    /// <summary>NetEase and lyrics.ovh response parsing against recorded response shapes (made-up text, no network).</summary>
    public sealed class ProviderParsingTests : IDisposable
    {
        static readonly TrackQuery Q = new TrackQuery { Artist = "The Artist", Title = "Song (Remastered 2011)", Album = "Album", DurationSec = 200.4 };
        readonly string _dir = Path.Combine(Path.GetTempPath(), "LyricsOverlayParseTests_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        static HttpClient Http(FixtureHandler h)
        {
            var http = new HttpClient(h);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mb_LyricsOverlay-tests/1.0");
            return http;
        }

        static ProviderChain Chain(params ILyricsProvider[] p) => new ProviderChain(p);

        [Fact]
        public async Task NetEaseRanksSearchResultsAndLoadsCleanSyncedLyrics()
        {
            var h = new FixtureHandler()
                .On("/api/search/get?s=", "netease_search.json")
                .On("/api/song/lyric?id=222", "netease_lyric.json");
            var r = await Chain(new NetEaseProvider(Http(h), "https://netease.test")).RunAsync(Q, true, CancellationToken.None);

            Assert.Equal("NetEase", r.Best.Provider);
            Assert.Equal("222", r.Best.Id); // not the live version (262 s) or the cover band
            Assert.True(r.Best.HasSynced);
            Assert.DoesNotContain("作词", r.Best.SyncedLyrics);
            Assert.Equal(new[] { "Made-up placeholder line one", "Made-up placeholder line two" },
                LrcParser.Parse(r.Best.SyncedLyrics).Lines.Select(l => l.Text));
            Assert.Equal("s=The Artist Song&type=1&limit=10&offset=0", Uri.UnescapeDataString(h.Requests[0].Query.TrimStart('?')));
        }

        [Fact]
        public async Task NetEaseUntimedLyricsAreTreatedAsPlain()
        {
            var h = new FixtureHandler()
                .On("/api/search/get?", "netease_search.json")
                .On("/api/song/lyric?", "netease_lyric_untimed.json");
            var r = await Chain(new NetEaseProvider(Http(h), "https://netease.test")).RunAsync(Q, true, CancellationToken.None);
            Assert.False(r.Best.HasSynced);
            Assert.Equal("Plain placeholder A\r\nPlain placeholder B", r.Best.PlainLyrics);
        }

        [Fact]
        public async Task NetEaseNoLyricFlagMeansInstrumental()
        {
            var h = new FixtureHandler()
                .On("/api/search/get?", "netease_search.json")
                .On("/api/song/lyric?", "netease_nolyric.json");
            var r = await Chain(new NetEaseProvider(Http(h), "https://netease.test")).RunAsync(Q, true, CancellationToken.None);
            Assert.True(r.Best.Instrumental);
            Assert.True(r.Best.IsDefinitive);
        }

        [Fact]
        public async Task NetEaseBlockCodeIsAFailureNotAMiss()
        {
            var h = new FixtureHandler().On("/api/search/get?", "netease_blocked.json");
            var p = new NetEaseProvider(Http(h), "https://netease.test");
            await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Q, CancellationToken.None));
        }

        [Fact]
        public async Task LyricsOvhReturnsPlainTextWithoutTheHeader()
        {
            var h = new FixtureHandler().On("/v1/", "ovh_lyrics.json");
            var r = await Chain(new LyricsOvhProvider(Http(h), "https://ovh.test")).RunAsync(Q, true, CancellationToken.None);
            Assert.Equal("Ovh placeholder line\nSecond placeholder line", r.Best.PlainLyrics);
            Assert.Equal("/v1/The Artist/Song", Uri.UnescapeDataString(h.Requests.Single().AbsolutePath));
        }

        [Fact]
        public async Task LyricsOvhNotFoundAndMissingArtist()
        {
            var h = new FixtureHandler().On("/v1/", "ovh_not_found.json", HttpStatusCode.NotFound);
            var p = new LyricsOvhProvider(Http(h), "https://ovh.test");
            Assert.Empty(await p.SearchAsync(Q, CancellationToken.None));
            Assert.Empty(await p.SearchAsync(new TrackQuery { Title = "Song" }, CancellationToken.None));
            Assert.Single(h.Requests); // no request without an artist
        }

        [Fact]
        public async Task LrclibPlainOnlyFallsThroughToNetEaseSynced()
        {
            var h = new FixtureHandler()
                .On("/api/get?", "get_plain_only.json")
                .On("/api/search?", "search_empty.json")
                .On("/api/search/get?", "netease_search.json")
                .On("/api/song/lyric?", "netease_lyric.json");
            var http = Http(h);
            var resolver = new LyricsResolver(new LyricsCache(_dir, TimeSpan.FromDays(7)),
                new ILyricsProvider[] { new LrclibProvider(new LrclibClient(http, "https://lrclib.test")), new NetEaseProvider(http, "https://netease.test") },
                new ProviderChainOptions());
            var track = new TrackInfo { Artist = "The Artist", Title = "Song (Remastered 2011)", Album = "Album", DurationMs = 200400 };

            var r = await resolver.ResolveAsync(track, null, CancellationToken.None);
            Assert.Equal("NetEase", r.Provider);
            Assert.True(LrcParser.Parse(r.Text).IsSynced);
        }
    }
}
