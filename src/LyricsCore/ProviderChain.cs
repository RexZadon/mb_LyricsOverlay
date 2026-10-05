using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LyricsOverlay.Core
{
    public enum BreakerState { Closed, Open, HalfOpen }

    /// <summary>
    /// Per-provider circuit breaker. After <c>threshold</c> consecutive failures the provider is skipped
    /// for a backoff period that doubles on every further trip (capped). When the period is over one
    /// trial request is let through: success closes the breaker, failure re-opens it for longer.
    /// "Not found" is a success: the provider answered.
    /// </summary>
    public sealed class CircuitBreaker
    {
        readonly object _gate = new object();
        readonly int _threshold;
        readonly TimeSpan _baseBackoff, _maxBackoff;
        readonly Func<DateTime> _utcNow;
        int _failures;
        int _trips;
        DateTime _openUntil;
        DateTime _rateLimitedUntil;
        bool _trialInFlight;

        public CircuitBreaker(int threshold, TimeSpan baseBackoff, TimeSpan maxBackoff, Func<DateTime> utcNow = null)
        {
            _threshold = Math.Max(1, threshold);
            _baseBackoff = baseBackoff;
            _maxBackoff = maxBackoff;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public BreakerState State
        {
            get
            {
                lock (_gate)
                {
                    if (_trips == 0) return BreakerState.Closed;
                    return _utcNow() < _openUntil ? BreakerState.Open : BreakerState.HalfOpen;
                }
            }
        }

        public DateTime OpenUntilUtc { get { lock (_gate) return _openUntil; } }

        /// <summary>The service itself asked us to wait (429) and that time isn't over: never probe it early.</summary>
        public bool IsRateLimited { get { lock (_gate) return _utcNow() < _rateLimitedUntil; } }

        /// <summary>Backoff used after the <paramref name="trip"/>-th consecutive trip (1-based).</summary>
        public TimeSpan BackoffFor(int trip)
        {
            double ms = _baseBackoff.TotalMilliseconds * Math.Pow(2, Math.Max(0, Math.Min(30, trip - 1)));
            return TimeSpan.FromMilliseconds(Math.Min(ms, _maxBackoff.TotalMilliseconds));
        }

        /// <summary>True if a request may go out now. In half-open state only one trial at a time is allowed.</summary>
        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (_trips == 0) return true;
                if (_utcNow() < _openUntil || _trialInFlight) return false;
                _trialInFlight = true;
                return true;
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _failures = 0;
                _trips = 0;
                _trialInFlight = false;
            }
        }

        public void RecordFailure()
        {
            lock (_gate)
            {
                _failures++;
                bool trial = _trialInFlight;
                _trialInFlight = false;
                if (trial || _failures >= _threshold)
                {
                    _trips++;
                    _openUntil = _utcNow() + BackoffFor(_trips);
                }
            }
        }

        /// <summary>
        /// The service asked us to slow down (HTTP 429): open at once, for at least <paramref name="retryAfter"/>
        /// and at least the normal backoff.
        /// </summary>
        public void RecordRateLimited(TimeSpan retryAfter)
        {
            lock (_gate)
            {
                _failures++;
                _trialInFlight = false;
                _trips++;
                var backoff = BackoffFor(_trips);
                _openUntil = _utcNow() + (retryAfter > backoff ? retryAfter : backoff);
                _rateLimitedUntil = _utcNow() + retryAfter;
            }
        }

        /// <summary>The request was abandoned by us (lost a hedge race, track changed): no verdict.</summary>
        public void RecordAbandoned()
        {
            lock (_gate) _trialInFlight = false;
        }
    }

    public sealed class ProviderChainOptions
    {
        /// <summary>Start the next provider in parallel if the running ones haven't answered by then.</summary>
        public TimeSpan HedgeDelay { get; set; } = TimeSpan.FromMilliseconds(1200);
        /// <summary>Upper bound for one provider's whole lookup (search plus fetch, several requests).</summary>
        public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(12);
        public int BreakerThreshold { get; set; } = 2;
        public TimeSpan BreakerBaseBackoff { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan BreakerMaxBackoff { get; set; } = TimeSpan.FromMinutes(15);
        /// <summary>Read on every lookup, so toggling a provider in the settings applies immediately.</summary>
        public Func<string, bool> IsEnabled { get; set; } = _ => true;
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    }

    public sealed class ChainResult
    {
        /// <summary>Best lyrics found (synced or instrumental if any provider had them, else plain), or null.</summary>
        public LyricsCandidate Best { get; set; }
        /// <summary>First real failure (network error, timeout, server error), if any provider failed.</summary>
        public Exception FirstError { get; set; }
        /// <summary>A provider was skipped (open circuit) or failed, so "nothing found" isn't conclusive.</summary>
        public bool Incomplete { get; set; }
        /// <summary>Names of providers that were actually started, in start order (for logs and tests).</summary>
        public List<string> Started { get; } = new List<string>();
    }

    /// <summary>
    /// Runs online providers in priority order and stops at the first synced (or instrumental) result.
    /// With hedging on, the next provider starts in parallel when the running ones are slow; the first
    /// good answer wins and the others are cancelled. A provider that fails, times out, finds nothing,
    /// or finds only plain lyrics makes the next one start at once. Plain lyrics are kept as a fallback;
    /// plain-only providers are skipped once plain lyrics are in hand.
    /// </summary>
    public sealed class ProviderChain
    {
        readonly IReadOnlyList<ILyricsProvider> _providers;
        readonly ProviderChainOptions _o;
        readonly Action<string> _log;
        readonly Dictionary<string, CircuitBreaker> _breakers = new Dictionary<string, CircuitBreaker>();

        public ProviderChain(IEnumerable<ILyricsProvider> providers, ProviderChainOptions options = null, Action<string> log = null)
        {
            _providers = providers.ToList();
            _o = options ?? new ProviderChainOptions();
            _log = log ?? (_ => { });
            foreach (var p in _providers)
                _breakers[p.Name] = new CircuitBreaker(_o.BreakerThreshold, _o.BreakerBaseBackoff, _o.BreakerMaxBackoff, _o.UtcNow);
        }

        public IReadOnlyList<ILyricsProvider> Providers => _providers;
        public CircuitBreaker BreakerFor(string providerName) => _breakers[providerName];

        sealed class Running
        {
            public ILyricsProvider Provider;
            public int Rank;
            public CancellationTokenSource Cts;
            public Task<LyricsCandidate> Task;
        }

        public async Task<ChainResult> RunAsync(TrackQuery q, bool hedge, CancellationToken ct)
        {
            var result = new ChainResult();
            var queue = new Queue<KeyValuePair<int, ILyricsProvider>>();
            for (int i = 0; i < _providers.Count; i++)
                if (_o.IsEnabled(_providers[i].Name)) queue.Enqueue(new KeyValuePair<int, ILyricsProvider>(i, _providers[i]));

            var running = new List<Running>();
            var clock = Stopwatch.StartNew();
            TimeSpan lastStart = TimeSpan.Zero;
            int plainRank = int.MaxValue;
            var callerCancelled = new TaskCompletionSource<bool>();
            KeyValuePair<int, ILyricsProvider>? firstSkipped = null;

            void Start(KeyValuePair<int, ILyricsProvider> next)
            {
                var p = next.Value;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_o.ProviderTimeout);
                var run = new Running { Provider = p, Rank = next.Key, Cts = cts };
                run.Task = Task.Run(() => LookupAsync(p, q, cts.Token), cts.Token);
                // Losers are abandoned: observe their exceptions so they never go unobserved, and
                // release the timeout timer whenever the task ends.
                run.Task.ContinueWith(t => { _ = t.Exception; cts.Dispose(); }, TaskScheduler.Default);
                running.Add(run);
                result.Started.Add(p.Name);
                lastStart = clock.Elapsed;
            }

            bool StartNext()
            {
                while (queue.Count > 0)
                {
                    var next = queue.Dequeue();
                    var p = next.Value;
                    if (result.Best != null && !p.SupportsSynced) continue; // can't beat the plain lyrics we have
                    if (!_breakers[p.Name].TryAcquire())
                    {
                        _log($"{p.Name}: skipped, circuit open until {_breakers[p.Name].OpenUntilUtc:HH:mm:ss} UTC");
                        result.Incomplete = true;
                        if (!_breakers[p.Name].IsRateLimited) firstSkipped = firstSkipped ?? next;
                        continue;
                    }
                    Start(next);
                    return true;
                }
                return false;
            }

            void CancelAll()
            {
                foreach (var r in running)
                {
                    _breakers[r.Provider.Name].RecordAbandoned();
                    try { r.Cts.Cancel(); } catch (ObjectDisposedException) { }
                }
                running.Clear();
            }

            using (ct.Register(() => callerCancelled.TrySetResult(true)))
            {
                if (!StartNext() && firstSkipped.HasValue)
                {
                    // Every enabled provider is backing off: usually the network itself was down. Probe
                    // the top one anyway, so lyrics come back as soon as the network does instead of
                    // after the longest backoff. Costs one request per track while everything is down.
                    _log($"all providers backing off; probing {firstSkipped.Value.Value.Name}");
                    Start(firstSkipped.Value);
                }
                while (running.Count > 0)
                {
                    var wait = new List<Task>(running.Count + 2) { callerCancelled.Task };
                    wait.AddRange(running.Select(r => r.Task));
                    Task hedgeTimer = null;
                    Task done;
                    using (var timerCts = new CancellationTokenSource())
                    {
                        if (hedge && queue.Count > 0)
                        {
                            var due = lastStart + _o.HedgeDelay - clock.Elapsed;
                            hedgeTimer = due <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(due, timerCts.Token);
                            wait.Add(hedgeTimer);
                        }
                        done = await Task.WhenAny(wait).ConfigureAwait(false);
                        timerCts.Cancel(); // don't leave a pending timer behind each loop
                    }
                    if (done == callerCancelled.Task || ct.IsCancellationRequested)
                    {
                        CancelAll();
                        ct.ThrowIfCancellationRequested();
                    }
                    if (done == hedgeTimer)
                    {
                        if (queue.Count > 0) _log($"hedging: {string.Join(", ", running.Select(r => r.Provider.Name))} slow, starting next provider");
                        if (!StartNext()) hedge = false;
                        continue;
                    }

                    var run = running.First(r => r.Task == done);
                    running.Remove(run);
                    var name = run.Provider.Name;
                    var breaker = _breakers[name];

                    if (run.Task.Status == TaskStatus.RanToCompletion)
                    {
                        breaker.RecordSuccess();
                        var c = run.Task.Result;
                        if (c != null && c.IsDefinitive)
                        {
                            _log($"{name}: {(c.HasSynced ? "synced" : "instrumental")} lyrics, {(running.Count > 0 ? "cancelling " + string.Join(", ", running.Select(r => r.Provider.Name)) : "done")}");
                            CancelAll();
                            result.Best = c;
                            return result;
                        }
                        if (c != null && c.HasAnyLyrics)
                        {
                            _log($"{name}: plain lyrics only, trying other providers for synced");
                            if (run.Rank < plainRank)
                            {
                                result.Best = c;
                                plainRank = run.Rank;
                            }
                        }
                        else
                        {
                            _log($"{name}: no match");
                        }
                    }
                    else
                    {
                        var ex = run.Task.IsFaulted
                            ? run.Task.Exception.GetBaseException()
                            : new TimeoutException($"{name} did not answer within {_o.ProviderTimeout.TotalSeconds:0.#} s");
                        if (ex is RateLimitedException rl) breaker.RecordRateLimited(rl.RetryAfter);
                        else breaker.RecordFailure();
                        result.Incomplete = true;
                        result.FirstError = result.FirstError ?? ex;
                        _log($"{name}: failed ({ex.GetType().Name}: {ex.Message}); breaker {breaker.State}");
                    }

                    if (running.Count == 0) StartNext();
                }
            }
            return result;
        }

        /// <summary>Search one provider, rank its candidates, load the best (falling back to the runner-up).</summary>
        static async Task<LyricsCandidate> LookupAsync(ILyricsProvider p, TrackQuery q, CancellationToken ct)
        {
            var remaining = (await p.SearchAsync(q, ct).ConfigureAwait(false) ?? new LyricsCandidate[0])
                .Where(c => c != null).ToList();
            for (int attempt = 0; attempt < 2 && remaining.Count > 0; attempt++)
            {
                var best = remaining.FirstOrDefault(c => c.ExactMatch && c.IsUsable) ?? CandidateRanker.PickBest(remaining, q);
                if (best == null) return null;
                remaining.Remove(best);
                var loaded = best.LyricsLoaded ? best : await p.FetchAsync(best, ct).ConfigureAwait(false);
                if (loaded != null && loaded.Provider == null) loaded.Provider = p.Name;
                if (LyricsSanity.IsPlausible(loaded, q)) return loaded;
            }
            return null;
        }
    }
}
