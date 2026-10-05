# Changelog

All notable changes to this project are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-05

First public release.

### Overlay
- Floating, transparent, always-on-top lyrics window with true per-pixel alpha.
- Synced (LRC) lyrics follow playback, pauses and seeks. Plain lyrics are shown as a scrollable block.
- Line transitions: Slide, Fade or None, with adjustable length (100–1000 ms) and easing.
- Lock mode (click-through) and unlocked mode (drag to move, drag edges to resize).
- Settings dialog with live preview: font, size, bold/italic, text/outline/shadow colours, outline
  width, shadow distance, opacity, lines shown and alignment.
- Tools-menu commands *Show or Hide* and *Lock or Unlock*, assignable to hotkeys.

### Lyrics lookup
- Lookup order: file tags, MusicBee's downloaded lyrics, sidecar `.lrc`, local cache, then online
  providers.
- Online providers, each switchable: LRCLIB (first), NetEase Cloud Music (off by default) and
  lyrics.ovh (plain text only).
- Strict matching on normalised title, artist and duration, so wrong-song lyrics are rejected.
- Hedged requests, per-provider timeouts and circuit breakers. HTTP 429 `Retry-After` is honoured.
- Background prefetch of the next track in Now Playing.
- Optional second plugin, `mb_LyricsOverlayProvider.dll`, adds LRCLIB to MusicBee's own lyrics
  providers.

### Housekeeping
- Uninstall removes settings, cache and logs.
- User-Agent identifies the plugin, its version and its project URL.
