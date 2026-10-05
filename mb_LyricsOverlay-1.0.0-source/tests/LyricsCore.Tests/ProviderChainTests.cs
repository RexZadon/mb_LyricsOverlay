using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    /// <summary>Scriptable in-memory provider: no network, records calls and cancellations.</summary>
    sealed class FakeProvider : ILyricsProvider
    {
        readonly Func<CancellationToken, Task<IReadOnlyList<LyricsCandidate>>> _search;
        int _calls, _cancelled;
        public readonly TaskCompletionSource<bool> CancelledSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProvider(string name, Func<CancellationToken, Task<IReadOnlyList<LyricsCandidate>>> search, bool supportsSynced = true)
        {
            Name = name;
            _search = search;
            SupportsSynced = supportsSynced;
        }

        public string Name { get; }
        public bool SupportsSynced { get; }
        public int Calls => Volatile.Read(ref _calls);
        public int Cancelled => Volatile.Read(ref _cancelled);

        public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                return await _search(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                CancelledSignal.TrySetResult(true);
                throw;
            }
        }

        public Task<LyricsCandidate> FetchAsync(LyricsCandidate candidate, CancellationToken ct) => Task.FromResult(candidate);

        // ---- canned behaviours

        public static LyricsCandidate Candidate(string text, bool synced, string title = "Song", double? duration = 200) => new LyricsCandidate
        {
            TrackName = title, ArtistName = "The Artist", AlbumName = "Album", Duration = duration,
            SyncedLyrics = synced ? "[00:01.00]" + text : null,
            PlainLyrics = synced ? null : text,
        };

        public static FakeProvider Synced(string name, int delayMs = 0) => Returns(name, delayMs, Candidate(name + " synced", true));
        public static FakeProvider Plain(string name, int delayMs = 0, bool supportsSynced = true) =>
            Returns(name, delayMs, supportsSynced, Candidate(name + " plain", false));
        public static FakeProvider Nothing(string name, int delayMs = 0) => Returns(name, delayMs);
        public static FakeProvider Fails(string name) => new FakeProvider(name, ct => throw new HttpRequestException(name + " is down"));
        public static FakeProvider Hangs(string name) => new FakeProvider(name, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });

        public static FakeProvider Returns(string name, int delayMs, params LyricsCandidate[] result) => Returns(name, delayMs, true, result);

        static FakeProvider Returns(string name, int delayMs, bool supportsSynced, params LyricsCandidate[] result) =>
            new FakeProvider(name, async ct =>
            {
                if (delayMs > 0) await Task.Delay(delayMs, ct);
                return result;
            }, supportsSynced);
    }

    public sealed class ProviderChainTests : IDisposable
    {
        static readonly TrackQuery Q = new TrackQuery { Artist = "The Artist", Title = "Song", Album = "Album", DurationSec = 200 };
        static readonly TrackInfo Track = new TrackInfo { Artist = "The Artist", Title = "Song", Album = "Album", DurationMs = 200000 };

        readonly string _dir = Path.Combine(Path.GetTempPath(), "LyricsOverlayChainTests_" + Guid.NewGuid().ToString("N"));
        DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        ProviderChainOptions Options(int hedgeMs = 10000, int timeoutMs = 10000) => new ProviderChainOptions
        {
            HedgeDelay = TimeSpan.FromMilliseconds(hedgeMs),
            ProviderTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            BreakerThreshold = 2,
            BreakerBaseBackoff = TimeSpan.FromSeconds(30),
            BreakerMaxBackoff = TimeSpan.FromMinutes(10),
            UtcNow = () => _now,
        };

        ProviderChain Chain(ProviderChainOptions o, params ILyricsProvider[] providers) => new ProviderChain(providers, o);

        LyricsResolver Resolver(ProviderChainOptions o, params ILyricsProvider[] providers) =>
            new LyricsResolver(new LyricsCache(_dir, TimeSpan.FromDays(7), () => _now), providers, o);

        // ------------------------------------------------------------------ fallback

        [Fact]
        public async Task FallsBackWhenPrimaryFails()
        {
            var a = FakeProvider.Fails("A");
            var b = FakeProvider.Synced("B");
            var r = await Chain(Options(), a, b).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("B", r.Best.Provider);
            Assert.True(r.Best.HasSynced);
            Assert.Equal(new[] { "A", "B" }, r.Started);
            Assert.IsType<HttpRequestException>(r.FirstError);
        }

        [Fact]
        public async Task FallsBackWhenPrimaryTimesOut()
        {
            var a = FakeProvider.Hangs("A");
            var b = FakeProvider.Synced("B");
            var sw = Stopwatch.StartNew();
            var r = await Chain(Options(timeoutMs: 150), a, b).RunAsync(Q, hedge: false, CancellationToken.None);
            Assert.Equal("B", r.Best.Provider);
            Assert.True(sw.ElapsedMilliseconds < 3000);
            Assert.IsType<TimeoutException>(r.FirstError);
            Assert.Equal(1, a.Cancelled); // the timed-out request was actually cancelled
        }

        [Fact]
        public async Task FallsBackImmediatelyOnEmptyResultWithoutWaitingForHedge()
        {
            var a = FakeProvider.Nothing("A");
            var b = FakeProvider.Synced("B");
            var sw = Stopwatch.StartNew();
            var r = await Chain(Options(hedgeMs: 10000), a, b).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("B", r.Best.Provider);
            Assert.True(sw.ElapsedMilliseconds < 3000);
            Assert.Null(r.FirstError);
        }

        [Fact]
        public async Task UnsyncedResultMakesTheChainTryAnotherProviderForSynced()
        {
            var a = FakeProvider.Plain("A");
            var b = FakeProvider.Synced("B");
            var r = await Chain(Options(), a, b).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("B", r.Best.Provider);
            Assert.True(r.Best.HasSynced);
        }

        [Fact]
        public async Task UnsyncedResultIsKeptWhenNobodyHasSynced()
        {
            var a = FakeProvider.Plain("A");
            var b = FakeProvider.Nothing("B");
            var c = FakeProvider.Plain("C", supportsSynced: false); // plain-only source can't improve on plain
            var r = await Chain(Options(), a, b, c).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("A", r.Best.Provider);
            Assert.False(r.Best.HasSynced);
            Assert.Equal(0, c.Calls);
        }

        [Fact]
        public async Task PlainOnlyProviderIsUsedWhenSyncedProvidersHaveNothing()
        {
            var r = await Chain(Options(), FakeProvider.Nothing("A"), FakeProvider.Plain("C", supportsSynced: false))
                .RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("C", r.Best.Provider);
        }

        [Fact]
        public async Task DisabledProviderIsNeverCalled()
        {
            var a = FakeProvider.Synced("A");
            var b = FakeProvider.Synced("B");
            var o = Options();
            o.IsEnabled = n => n != "A";
            var r = await Chain(o, a, b).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("B", r.Best.Provider);
            Assert.Equal(0, a.Calls);
        }

        [Fact]
        public async Task CandidatesWithWrongTitleOrDurationAreRejected()
        {
            var a = FakeProvider.Returns("A", 0,
                FakeProvider.Candidate("other song", true, title: "Other Song"),
                FakeProvider.Candidate("far duration", true, duration: 260));
            // Synced lyrics that run far past the end of a 200 s track belong to another recording.
            var b = FakeProvider.Returns("B", 0, new LyricsCandidate
            {
                TrackName = "Song", ArtistName = "The Artist", Duration = null, SyncedLyrics = "[00:01.00]a\n[05:00.00]b",
            });
            var c = FakeProvider.Synced("C");
            var r = await Chain(Options(), a, b, c).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal("C", r.Best.Provider);
        }

        // ------------------------------------------------------------------ hedging

        [Fact]
        public async Task SlowPrimaryGetsAHedgedParallelStartAndTheLoserIsCancelled()
        {
            var a = FakeProvider.Synced("A", delayMs: 5000);
            var b = FakeProvider.Synced("B", delayMs: 20);
            var sw = Stopwatch.StartNew();
            var r = await Chain(Options(hedgeMs: 100), a, b).RunAsync(Q, hedge: true, CancellationToken.None);

            Assert.Equal("B", r.Best.Provider);
            Assert.True(sw.ElapsedMilliseconds < 3000, "took " + sw.ElapsedMilliseconds);
            Assert.Equal(new[] { "A", "B" }, r.Started);
            Assert.True(await Task.WhenAny(a.CancelledSignal.Task, Task.Delay(3000)) == a.CancelledSignal.Task, "loser A not cancelled");
        }

        [Fact]
        public async Task PrimaryWinningTheRaceCancelsTheHedgedProvider()
        {
            var a = FakeProvider.Synced("A", delayMs: 250);
            var b = FakeProvider.Hangs("B");
            var chain = Chain(Options(hedgeMs: 50), a, b);
            var r = await chain.RunAsync(Q, hedge: true, CancellationToken.None);

            Assert.Equal("A", r.Best.Provider);
            Assert.Equal(new[] { "A", "B" }, r.Started);
            Assert.True(await Task.WhenAny(b.CancelledSignal.Task, Task.Delay(3000)) == b.CancelledSignal.Task, "loser B not cancelled");
            Assert.Equal(BreakerState.Closed, chain.BreakerFor("B").State); // being cancelled by us is not a failure
        }

        [Fact]
        public async Task FastPrimaryNeverStartsTheNextProvider()
        {
            var a = FakeProvider.Synced("A");
            var b = FakeProvider.Synced("B");
            var r = await Chain(Options(hedgeMs: 1000), a, b).RunAsync(Q, hedge: true, CancellationToken.None);
            Assert.Equal(new[] { "A" }, r.Started);
            Assert.Equal(0, b.Calls);
        }

        [Fact]
        public async Task WithoutHedgingProvidersRunOneAtATime()
        {
            var a = FakeProvider.Nothing("A", delayMs: 300);
            var b = FakeProvider.Synced("B");
            var chain = Chain(Options(hedgeMs: 10), a, b);
            var run = chain.RunAsync(Q, hedge: false, CancellationToken.None);
            await Task.Delay(150);
            Assert.Equal(0, b.Calls); // prefetch mode: no parallel start
            Assert.Equal("B", (await run).Best.Provider);
        }

        [Fact]
        public async Task CallerCancellationCancelsRunningProviders()
        {
            var a = FakeProvider.Hangs("A");
            var b = FakeProvider.Hangs("B");
            using (var cts = new CancellationTokenSource())
            {
                var run = Chain(Options(hedgeMs: 20), a, b).RunAsync(Q, hedge: true, cts.Token);
                await Task.Delay(150);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            Assert.True(await Task.WhenAny(a.CancelledSignal.Task, Task.Delay(3000)) == a.CancelledSignal.Task);
            Assert.True(await Task.WhenAny(b.CancelledSignal.Task, Task.Delay(3000)) == b.CancelledSignal.Task);
        }

        // ------------------------------------------------------------------ circuit breaker

        [Fact]
        public void BreakerOpensAfterThresholdAndBacksOffExponentially()
        {
            var b = new CircuitBreaker(2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), () => _now);
            Assert.True(b.TryAcquire());
            b.RecordFailure();
            Assert.Equal(BreakerState.Closed, b.State); // one failure isn't enough
            b.RecordFailure();
            Assert.Equal(BreakerState.Open, b.State);
            Assert.False(b.TryAcquire());

            _now = _now.AddSeconds(31);
            Assert.Equal(BreakerState.HalfOpen, b.State);
            Assert.True(b.TryAcquire());   // one trial request
            Assert.False(b.TryAcquire());  // ...and only one
            b.RecordFailure();             // trial failed: open again, twice as long
            Assert.Equal(_now.AddSeconds(60), b.OpenUntilUtc);

            _now = _now.AddSeconds(61);
            Assert.True(b.TryAcquire());
            b.RecordFailure();
            Assert.Equal(_now.AddMinutes(2), b.OpenUntilUtc); // 120 s, the cap
            _now = _now.AddMinutes(3);
            Assert.True(b.TryAcquire());
            b.RecordFailure();
            Assert.Equal(_now.AddMinutes(2), b.OpenUntilUtc); // stays capped

            _now = _now.AddMinutes(3);
            Assert.True(b.TryAcquire());
            b.RecordSuccess();
            Assert.Equal(BreakerState.Closed, b.State);
            Assert.True(b.TryAcquire());
        }

        [Fact]
        public void AbandonedTrialFreesTheHalfOpenSlot()
        {
            var b = new CircuitBreaker(1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), () => _now);
            b.RecordFailure();
            _now = _now.AddSeconds(11);
            Assert.True(b.TryAcquire());
            b.RecordAbandoned();
            Assert.True(b.TryAcquire());
        }

        [Fact]
        public async Task DeadProviderIsSkippedWhileItsCircuitIsOpenThenRetried()
        {
            var a = FakeProvider.Fails("A");
            var b = FakeProvider.Synced("B");
            var chain = Chain(Options(), a, b);

            await chain.RunAsync(Q, true, CancellationToken.None);
            await chain.RunAsync(Q, true, CancellationToken.None);
            Assert.Equal(2, a.Calls);
            Assert.Equal(BreakerState.Open, chain.BreakerFor("A").State);

            var sw = Stopwatch.StartNew();
            var r = await chain.RunAsync(Q, true, CancellationToken.None);
            Assert.Equal(new[] { "B" }, r.Started); // no waiting on the dead provider
            Assert.Equal(2, a.Calls);
            Assert.True(r.Incomplete);

            _now = _now.AddSeconds(31);
            r = await chain.RunAsync(Q, true, CancellationToken.None);
            Assert.Equal(new[] { "A", "B" }, r.Started); // half-open trial
            Assert.Equal(3, a.Calls);
            Assert.Equal(BreakerState.Open, chain.BreakerFor("A").State);
            Assert.Equal(_now.AddSeconds(60), chain.BreakerFor("A").OpenUntilUtc);
        }

        [Fact]
        public async Task WhenEveryProviderIsBackingOffTheFirstIsProbedAnyway()
        {
            bool networkUp = false;
            var a = new FakeProvider("A", ct => networkUp
                ? Task.FromResult<IReadOnlyList<LyricsCandidate>>(new[] { FakeProvider.Candidate("A synced", true) })
                : throw new HttpRequestException("network down"));
            var b = FakeProvider.Fails("B");
            var chain = Chain(Options(), a, b);
            for (int i = 0; i < 2; i++) await chain.RunAsync(Q, true, CancellationToken.None);
            Assert.Equal(BreakerState.Open, chain.BreakerFor("A").State);
            Assert.Equal(BreakerState.Open, chain.BreakerFor("B").State);

            networkUp = true; // back online, long before the backoff ends
            var r = await chain.RunAsync(Q, true, CancellationToken.None);
            Assert.Equal(new[] { "A" }, r.Started);
            Assert.Equal("A", r.Best.Provider);
            Assert.Equal(BreakerState.Closed, chain.BreakerFor("A").State);
            Assert.Equal(BreakerState.Open, chain.BreakerFor("B").State);
        }

        [Fact]
        public async Task NotFoundCountsAsSuccessForTheBreaker()
        {
            var a = FakeProvider.Nothing("A");
            var chain = Chain(Options(), a);
            for (int i = 0; i < 5; i++) await chain.RunAsync(Q, true, CancellationToken.None);
            Assert.Equal(BreakerState.Closed, chain.BreakerFor("A").State);
            Assert.Equal(5, a.Calls);
        }

        // ------------------------------------------------------------------ resolver: caching, coalescing

        [Fact]
        public async Task NegativeResultIsCachedWithExpiry()
        {
            var a = FakeProvider.Nothing("A");
            var b = FakeProvider.Nothing("B");
            var resolver = Resolver(Options(), a, b);

            Assert.False((await resolver.FetchAsync(Track, CancellationToken.None)).Found);
            Assert.False((await resolver.FetchAsync(Track, CancellationToken.None)).Found);
            Assert.Equal(1, a.Calls);
            Assert.Equal(1, b.Calls);

            _now = _now.AddDays(8);
            await resolver.FetchAsync(Track, CancellationToken.None);
            Assert.Equal(2, a.Calls);
        }

        [Fact]
        public async Task FailureIsNotCachedAsNotFound()
        {
            var a = FakeProvider.Fails("A");
            var b = FakeProvider.Nothing("B");
            var resolver = Resolver(Options(), a, b);
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FetchAsync(Track, CancellationToken.None));
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FetchAsync(Track, CancellationToken.None));
            Assert.Equal(2, b.Calls); // asked again: nothing was cached
        }

        [Fact]
        public async Task NothingFoundWhileAProviderIsSkippedIsNotCached()
        {
            var a = FakeProvider.Fails("A");
            var b = FakeProvider.Nothing("B");
            var o = Options();
            var resolver = Resolver(o, a, b);
            for (int i = 0; i < 2; i++)
                await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FetchAsync(Track, CancellationToken.None));
            Assert.Equal(BreakerState.Open, resolver.Chain.BreakerFor("A").State);

            Assert.False((await resolver.FetchAsync(Track, CancellationToken.None)).Found); // A skipped, B: nothing
            Assert.False((await resolver.FetchAsync(Track, CancellationToken.None)).Found);
            Assert.Equal(4, b.Calls); // not negatively cached while A couldn't be asked
        }

        [Fact]
        public async Task ProviderIsRecordedWithTheCachedResult()
        {
            var resolver = Resolver(Options(), FakeProvider.Nothing("A"), FakeProvider.Synced("B"));
            var first = await resolver.FetchAsync(Track, CancellationToken.None);
            Assert.Equal(LyricsSource.Online, first.Source);
            Assert.Equal("B", first.Provider);

            var second = await resolver.FetchAsync(Track, CancellationToken.None);
            Assert.Equal(LyricsSource.Cache, second.Source);
            Assert.Equal("B", second.Provider);

            // ...and survives a restart (fresh memory cache, same disk folder).
            var restarted = Resolver(Options(), FakeProvider.Nothing("A"));
            var third = await restarted.FetchAsync(Track, CancellationToken.None);
            Assert.Equal(LyricsSource.Cache, third.Source);
            Assert.Equal("B", third.Provider);
        }

        [Fact]
        public async Task DuplicateRequestsForTheSameTrackAreCoalesced()
        {
            var a = FakeProvider.Synced("A", delayMs: 200);
            var resolver = Resolver(Options(), a);
            var r1 = resolver.FetchAsync(Track, CancellationToken.None);
            var r2 = resolver.FetchAsync(Track, CancellationToken.None);
            var r3 = resolver.PrefetchAsync(Track, CancellationToken.None);
            await Task.WhenAll(r1, r2, r3);
            Assert.Equal(1, a.Calls);
            Assert.Equal((await r1).Text, (await r2).Text);
        }

        [Fact]
        public async Task CoalescedRequestSurvivesOneCallerCancellingButStopsWhenAllDo()
        {
            var a = FakeProvider.Synced("A", delayMs: 300);
            var resolver = Resolver(Options(), a);
            using (var cts1 = new CancellationTokenSource())
            {
                var r1 = resolver.FetchAsync(Track, cts1.Token);
                var r2 = resolver.FetchAsync(Track, CancellationToken.None);
                cts1.Cancel(); // e.g. the track changed for one caller
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r1);
                Assert.True((await r2).Found);
            }
            Assert.Equal(1, a.Calls);

            var hang = FakeProvider.Hangs("H");
            var other = Resolver(Options(), hang);
            var uncached = new TrackInfo { Artist = "The Artist", Title = "Another Song", DurationMs = 180000 };
            using (var c1 = new CancellationTokenSource())
            using (var c2 = new CancellationTokenSource())
            {
                var t1 = other.FetchAsync(uncached, c1.Token);
                var t2 = other.FetchAsync(uncached, c2.Token);
                await Task.Delay(100);
                Assert.Equal(1, other.InFlightCount);
                c1.Cancel();
                c2.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
            }
            Assert.True(await Task.WhenAny(hang.CancelledSignal.Task, Task.Delay(3000)) == hang.CancelledSignal.Task, "request not cancelled");
            Assert.Equal(0, other.InFlightCount);
        }

        [Fact]
        public async Task CancelAllStopsPendingRequests()
        {
            var hang = FakeProvider.Hangs("H");
            var resolver = Resolver(Options(), hang);
            var t = resolver.FetchAsync(Track, CancellationToken.None);
            await Task.Delay(100);
            resolver.CancelAll();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
            Assert.True(await Task.WhenAny(hang.CancelledSignal.Task, Task.Delay(3000)) == hang.CancelledSignal.Task);
        }

        [Fact]
        public async Task PrefetchFillsTheCacheForTheLaterLookup()
        {
            var a = FakeProvider.Synced("A");
            var resolver = Resolver(Options(), a);
            // Prefetch sees MusicBee's "m:ss" duration (199 s), the live lookup the exact 199.6 s.
            await resolver.PrefetchAsync(new TrackInfo { Artist = "The Artist", Title = "Song", Album = "Album", DurationMs = 199000 }, CancellationToken.None);
            var r = await resolver.FetchAsync(new TrackInfo { Artist = "The Artist", Title = "Song", Album = "Album", DurationMs = 199600 }, CancellationToken.None);
            Assert.Equal(LyricsSource.Cache, r.Source);
            Assert.Equal(1, a.Calls);
        }

        [Fact]
        public async Task PrefetchSwallowsFailures()
        {
            var resolver = Resolver(Options(), FakeProvider.Fails("A"));
            await resolver.PrefetchAsync(Track, CancellationToken.None); // no throw
        }
    }
}
