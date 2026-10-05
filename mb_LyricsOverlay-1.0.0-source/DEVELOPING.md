# Developing Lyrics Overlay

## Build and test

You need the .NET SDK 8 or later (tested with 8.0.417) on Windows. Visual Studio isn't required. The
projects target .NET Framework 4.8, and the reference assemblies come from NuGet.

```
dotnet build -c Release     # -> dist\mb_LyricsOverlay.dll and dist\mb_LyricsOverlayProvider.dll
dotnet test -c Release      # unit tests, no network needed
```

Release builds treat warnings as errors and land in `dist\`. To try a build, close MusicBee, copy
`dist\mb_LyricsOverlay.dll` into its plugins folder and start it again. See `MANUAL_TESTS.md` for
what to check inside MusicBee.

## Making a release

1. Bump `<Version>` in `Directory.Build.props`. It is the only place the version lives. The
   assembly version, the version MusicBee shows in Preferences > Plugins and the User-Agent
   (`mb_LyricsOverlay/1.0.0 (+<ProjectUrl>)`) all derive from it.
2. Add a section to `CHANGELOG.md`.
3. Run `powershell -ExecutionPolicy Bypass -File tools\package.ps1`. It cleans, builds, tests,
   writes the zips and `SHA256SUMS.txt` to `release\`, then unzips each plugin zip into an empty
   temp folder and checks that the DLL loads and resolves all its dependencies there.
4. Go through the key steps in `MANUAL_TESTS.md` in a real MusicBee.

Example bump to 1.0.1:

```xml
<!-- Directory.Build.props -->
<Version>1.0.1</Version>
```

Author, copyright and the project URL sent in the User-Agent are also set in `Directory.Build.props`.

## Layout

```
Directory.Build.props          version, author, project URL, build settings shared by every project
MusicBeeInterface.cs           MusicBee plugin API (from the MusicBee API kit; compiled into both DLLs)
src/LyricsCore/                plain classes, no MusicBee or UI references:
                                 LrcParser, LyricSync (binary search + line window), TitleNormalizer,
                                 CandidateRanker + LrclibClient (Lrclib.cs), LyricsCache,
                                 LyricsProviders (ILyricsProvider: LRCLIB, NetEase, lyrics.ovh; 429 handling),
                                 ProviderChain (hedging, circuit breaker), LyricsResolver (lookup order,
                                 coalescing, prefetch), Animation (easing, PlaybackClock, ScrollAnimator,
                                 SyncedLayout), FileLog, DurationText, ProductInfo (version/User-Agent)
src/mb_LyricsOverlay/          the overlay plugin: Plugin.cs (entry point), OverlayController (timers,
                                 events, threading), OverlayForm (layered window), LyricsRenderer,
                                 SettingsForm, OverlaySettings
src/mb_LyricsOverlayProvider/  optional LyricsRetrieval plugin (LRCLIB only)
tests/LyricsCore.Tests/        xUnit tests with fake providers and HTTP handlers; Fixtures/ holds
                                 LRCLIB-, NetEase- and lyrics.ovh-shaped JSON with made-up text
tools/package.ps1              release packaging and install check
```

The core sources are compiled into each plugin DLL, not referenced, so each plugin is a single
self-contained file that depends only on the .NET Framework. The tests reference `LyricsCore` as a
normal project.

**Why two DLLs.** A MusicBee plugin has exactly one `PluginType`. The overlay must be `General` to
receive player events, and adding a provider to MusicBee's lyrics settings needs `LyricsRetrieval`.

## How it works

- **Window.** `UpdateLayeredWindow` with one persistent 32-bit premultiplied DIB section gives true
  per-pixel alpha with no colour-key fringes. The section is recreated only on resize, so no bitmap
  is allocated per frame. Locked mode adds `WS_EX_TRANSPARENT` for click-through. Unlocked,
  `WM_NCHITTEST` makes the window a caption with resize edges. The renderer caches the font family,
  brushes, pens and each line's glyph outline. A frame changes only transforms and colours.
- **Timing.** A WinForms timer on MusicBee's UI thread polls `Player_GetPosition` every 60 ms (250 ms
  while paused). Between polls the position is interpolated with a stopwatch and re-anchored on
  every poll, and small backward jitter is absorbed. A disagreement of more than 350 ms counts as a
  seek and snaps, as do jumps of more than two lines, backward moves and new lyrics.
- **Frames.** A second timer at about 60 fps runs only during a transition or just before the next
  line is due. Identical frames are skipped. No timer runs while hidden, stopped, or showing
  unsynced or no lyrics.
- **Threads.** Tag reads, file I/O and HTTP run in `Task.Run`. Results are applied only if the track
  hasn't changed meanwhile (a generation counter). Every MusicBee callback, timer tick and UI action
  is wrapped in try/catch that logs to the file, so no exception reaches MusicBee.
- **Lookup order.** Tags, then MusicBee's downloaded lyrics, then a sidecar `.lrc`, then the cache
  (memory, then disk), then online providers.
- **Provider chain.** Providers run in priority order and the chain stops at the first synced or
  instrumental result. Plain lyrics are kept as a fallback. If the running provider hasn't answered
  within 1.2 s, the next one starts in parallel (hedging), and losers are cancelled. Timeouts are
  8 s per HTTP request and 12 s per provider. Prefetch runs one provider at a time with no hedging.
- **Circuit breaker.** After 2 consecutive failures a provider is skipped for 30 s, doubling on each
  further failed trial up to 15 min. Any answer, including "not found", resets it. If every
  provider is backing off, the top one is probed anyway, unless it answered 429. An HTTP 429 opens
  the breaker at once for at least the server's `Retry-After` (default 60 s), and a rate-limited
  provider is never probed early.
- **Coalescing.** Two lookups of the same song (prefetch plus the real one) share one request. It is
  cancelled only when every caller has given up.
- **Caching.** Positive results are kept indefinitely. Negative results expire after 7 days and are
  only written when every enabled provider answered. Failures are never cached. Writes stop on
  shutdown, so a late result can't recreate the folder after an uninstall.
- **Shutdown.** `Close` disposes the controller on the UI thread: timers stop, lookups are
  cancelled, the window is destroyed and the `HttpClient` is disposed. `Uninstall` also stops the
  log and deletes the settings folder (settings and cache) and the log files.

## Network etiquette

- **User-Agent.** `mb_LyricsOverlay/<version> (+<ProjectUrl>)` (the provider DLL uses
  `mb_LyricsOverlayProvider/...`), as [LRCLIB's API docs](https://lrclib.net/docs) require.
- **Retry-After.** LRCLIB's `Retry-After` is honoured (see *Circuit breaker*).
- **LRCLIB requests.** At most three sequential requests per track (`/api/get`, then
  `/api/search`, then a free-text search), and only when nothing local matched.
- **NetEase.** Off by default because it has no public API. See README.

## Tests

`dotnet test` runs everything offline. Coverage includes LRC parsing, title normalisation and
ranking, provider response parsing from fixtures, the provider chain (fallback, hedging, timeouts,
breaker, 429), the resolver (cache, coalescing, cancellation, unwritable cache, no writes after
shutdown), animation maths, User-Agent format and log cleanup. The WinForms and MusicBee glue in
`src/mb_LyricsOverlay` has no automated tests. Use `MANUAL_TESTS.md`.

Fixtures and test strings must stay synthetic. Never paste real lyrics into the repo.
