using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    /// <summary>Rate limiting, User-Agent, and behaviour with an unusable cache folder or after shutdown.</summary>
    public sealed class ReleaseHardeningTests : IDisposable
    {
        static readonly TrackQuery Q = new TrackQuery { Artist = "The Artist", Title = "Song", Album = "Album", DurationSec = 200 };
        static readonly TrackInfo Track = new TrackInfo { Artist = "The Artist", Title = "Song", Album = "Album", DurationMs = 200000 };

        readonly string _dir = Path.Combine(Path.GetTempPath(), "LyricsOverlayHardeningTests_" + Guid.NewGuid().ToString("N"));
        DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        /// <summary>Answers every request with 429 and the given Retry-After (seconds), counting requests.</summary>
        sealed class TooManyRequestsHandler : HttpMessageHandler
        {
            readonly int? _retryAfterSec;
            public int Requests;

            public TooManyRequestsHandler(int? retryAfterSec) => _retryAfterSec = retryAfterSec;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Requests);
                var resp = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("") };
                if (_retryAfterSec.HasValue) resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(_retryAfterSec.Value));
                return Task.FromResult(resp);
            }
        }

        [Fact]
        public async Task LrclibClientSurfaces429WithRetryAfter()
        {
            var client = new LrclibClient(new HttpClient(new TooManyRequestsHandler(120)), "https://lrclib.test");
            var ex = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync("The Artist", "Song", "Album", 200, CancellationToken.None));
            Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        }

        [Fact]
        public async Task MissingRetryAfterUsesDefault()
        {
            var client = new LrclibClient(new HttpClient(new TooManyRequestsHandler(null)), "https://lrclib.test");
            var ex = await Assert.ThrowsAsync<RateLimitedException>(() => client.SearchFreeTextAsync("x", CancellationToken.None));
            Assert.Equal(RateLimitedException.DefaultRetryAfter, ex.RetryAfter);
        }

        [Fact]
        public async Task RateLimitedProviderIsNotCalledAgainUntilRetryAfterHasPassed()
        {
            var handler = new TooManyRequestsHandler(600);
            var lrclib = new LrclibProvider(new LrclibClient(new HttpClient(handler), "https://lrclib.test"));
            var chain = new ProviderChain(new ILyricsProvider[] { lrclib }, new ProviderChainOptions { UtcNow = () => _now });

            var first = await chain.RunAsync(Q, hedge: false, CancellationToken.None);
            Assert.IsType<RateLimitedException>(first.FirstError);
            Assert.Equal(1, handler.Requests);
            Assert.True(chain.BreakerFor(LrclibProvider.ProviderName).IsRateLimited);

            // One failure is below the breaker threshold, and LRCLIB is the only provider, so without the
            // 429 handling the chain would retry at once (or probe it as "everything is backing off").
            _now = _now.AddSeconds(599);
            var second = await chain.RunAsync(Q, hedge: false, CancellationToken.None);
            Assert.Empty(second.Started);
            Assert.True(second.Incomplete);
            Assert.Equal(1, handler.Requests);

            _now = _now.AddSeconds(2);
            await chain.RunAsync(Q, hedge: false, CancellationToken.None);
            Assert.Equal(2, handler.Requests);
        }

        [Fact]
        public void UserAgentNamesPluginVersionAndProjectUrl()
        {
            string ua = ProductInfo.UserAgent("mb_LyricsOverlay");
            Assert.StartsWith("mb_LyricsOverlay/" + ProductInfo.Version.ToString(3) + " (+", ua);
            Assert.Contains(ProductInfo.ProjectUrl, ua);
            Assert.True(Uri.IsWellFormedUriString(ProductInfo.ProjectUrl, UriKind.Absolute), ProductInfo.ProjectUrl);
            // Must be accepted by HttpClient as-is (it throws on a malformed User-Agent).
            using (var http = LrclibClient.CreateHttpClient(ua, TimeSpan.FromSeconds(1)))
                Assert.Equal(ua, http.DefaultRequestHeaders.UserAgent.ToString());
        }

        [Fact]
        public async Task UnwritableCacheFolderStillReturnsLyrics()
        {
            // The "cache folder" is an existing file, so every disk write fails, like a read-only folder.
            Directory.CreateDirectory(_dir);
            var blocked = Path.Combine(_dir, "not-a-folder");
            File.WriteAllText(blocked, "");
            var resolver = new LyricsResolver(new LyricsCache(blocked, TimeSpan.FromDays(7), () => _now),
                new ILyricsProvider[] { FakeProvider.Synced("A") }, new ProviderChainOptions { UtcNow = () => _now });

            var r = await resolver.FetchAsync(Track, CancellationToken.None);
            Assert.True(r.Found);
            Assert.Equal("A", r.Provider);
        }

        [Fact]
        public async Task NoCacheFilesAreWrittenAfterShutdown()
        {
            var cacheDir = Path.Combine(_dir, "cache");
            var resolver = new LyricsResolver(new LyricsCache(cacheDir, TimeSpan.FromDays(7), () => _now),
                new ILyricsProvider[] { FakeProvider.Synced("A") }, new ProviderChainOptions { UtcNow = () => _now });

            resolver.Shutdown();
            var r = await resolver.FetchAsync(Track, CancellationToken.None);
            Assert.True(r.Found);
            Assert.False(Directory.Exists(cacheDir));
        }

        [Fact]
        public void PackageScriptShipsExactlyTheDocsUninstallRemoves()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var script = File.ReadAllText(Path.Combine(dir.FullName, "tools", "package.ps1"));
            var shipped = System.Text.RegularExpressions.Regex.Matches(script, @"'(mb_LyricsOverlay_[A-Z_]+\.txt)'")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            Assert.Equal(ProductInfo.ShippedDocs.OrderBy(x => x), shipped);
        }

        [Fact]
        public void FileLogCloseAndDeleteRemovesLogAndStopsWriting()
        {
            const string name = "mb_LyricsOverlay_test.log";
            FileLog.Init(name, _dir);
            FileLog.Info("hello");
            var path = FileLog.PathInUse;
            Assert.True(File.Exists(path));
            File.WriteAllText(path + ".old", "rolled over");

            FileLog.CloseAndDelete();
            FileLog.Info("after close");
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".old"));
            Assert.Null(FileLog.PathInUse);
        }
    }
}
