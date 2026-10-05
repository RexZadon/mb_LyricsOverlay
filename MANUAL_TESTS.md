# Manual tests inside MusicBee

These can't be automated here because MusicBee isn't available. Install as described in README.md,
then go through the list. When something fails, check `mb_LyricsOverlay.log` (location in
README.md) first.

You'll need these test tracks:
- **A**: a track with synced lyrics available. Any popular song will usually be on LRCLIB.
- **B**: a track whose lyrics are only unsynced. Put plain text (no `[mm:ss]` stamps) in its
  lyrics tag, or in a sidecar `.lrc` file next to it.
- **C**: a track with no lyrics anywhere, e.g. a made-up title on a short audio file.
- **D**: optionally, a track with a sidecar `Name.lrc` that has timestamps.
- **E**: a playlist of 10 or more tracks you haven't played with this plugin yet, so they're not
  cached. A mix of well-known and obscure songs works best.
- For idle CPU, Task Manager's *Details* tab with the *CPU time* column, or Process Explorer, for
  `MusicBee.exe`.

## Clean install (do this first, on a MusicBee that has never had the plugin)

- [ ] Make sure `%APPDATA%\MusicBee\LyricsOverlay\` (portable: `<MusicBee>\AppData\LyricsOverlay\`)
      doesn't exist. Install `mb_LyricsOverlay_<version>.zip` with Preferences > Plugins > **Add
      Plugin…**. Only `mb_LyricsOverlay.dll` and the three `mb_LyricsOverlay_*.txt` files appear in
      the plugins folder.
- [ ] Preferences > Plugins shows *Lyrics Overlay*, the right version and the author name.
- [ ] Under Configure > Lyrics sources, LRCLIB and lyrics.ovh are ticked and NetEase is not.
- [ ] Play A with the network on. Lyrics appear. Nothing in the log mentions NetEase.

## Loading

- [ ] Enable the plugin in Preferences > Plugins. No error dialog appears, and the log has an
      `initialising` line.
- [ ] Tools menu has *Lyrics Overlay: Show or Hide* and *Lyrics Overlay: Lock or Unlock*.

## Transparency

- [ ] Play A. Only the text, its outline and its shadow are visible over the desktop or other
      windows. There's no black or white box, and no coloured fringe around the letters. Check over
      both light and dark backgrounds.
- [ ] Opacity at 30% in settings makes the whole overlay fade evenly.
- [ ] The overlay stays on top of other windows. It has no taskbar button and doesn't appear in
      Alt+Tab.

## Click-through toggle

- [ ] Locked: clicking or scrolling on the text reaches the window underneath. The overlay never
      takes focus.
- [ ] Unlocked: a faint dashed panel appears and the window can be grabbed.
- [ ] Lock and unlock via the Tools menu, via a hotkey bound in Preferences > Hotkeys (try it with
      *global* ticked while another app has focus), and via the settings dialog. All three work.

## Drag and resize (unlocked)

- [ ] Dragging from the middle moves the window.
- [ ] Dragging edges and corners resizes it. Text re-lays out at the new size, and long lines
      shrink to fit the width.
- [ ] Double-clicking does *not* maximise it.

## Settings and persistence

- [ ] Configure opens the dialog. Changing font, size, bold/italic, colours, outline, shadow,
      opacity, lines (try 1, 3, 5), alignment (left, center, right), line transition, transition
      length and easing updates the preview *and* the live overlay immediately. The preview steps
      to a new line every 1.8 s.
- [ ] The three *Lyrics sources* boxes, the transition and the easing survive OK and a restart.
- [ ] Cancel reverts the overlay to what it was before. OK keeps the changes.
- [ ] Reset to defaults restores the default look but keeps the position.
- [ ] Restart MusicBee. Position, size, lock state, visibility and every style setting are kept.
- [ ] Hide the overlay via the menu and restart. It stays hidden until shown again.

## Sync

- [ ] Play A. The highlighted line changes in time with the singing, previous and next lines show
      dimmer, and before the first line the center is empty with upcoming lines below.
- [ ] Seek forward and back with the progress bar. The correct line appears within about 0.1 s.
- [ ] Pause. The line holds. Resume. It continues correctly.
- [ ] Stop. The overlay clears.

## Animation smoothness

- [ ] Play A with *Slide*, 320 ms, *Ease out* (the defaults). On each line change the lines glide
      up without stutter. The new line grows and brightens while the old one shrinks and dims, and
      the top line fades out as a new one fades in at the bottom. There's no flicker, no blank
      frame and no flash of the edit panel.
- [ ] The motion starts when the singer starts the line, not a beat later. Compare with MusicBee's
      own lyrics panel.
- [ ] Try *Spring*, *Ease in-out* and *Linear*, then lengths of 100 ms and 1000 ms. The dialog
      preview shows each change, and so does the overlay right away.
- [ ] *Fade*: the old lines crossfade into the new ones in place, with no movement.
- [ ] *None (instant)*: lines switch at once.
- [ ] Seek with the progress bar, both a long jump and one of a few seconds. The overlay *snaps* to
      the right line. It never scrolls through the lines in between.
- [ ] A song with very quick lines (under 0.3 s apart) stays readable, and the overlay doesn't fall
      behind.
- [ ] Unlocked with the dashed panel showing: transitions look the same, and dragging or resizing
      during a transition works.
- [ ] 1 line and 9 lines shown: both animate correctly.
- [ ] On a second monitor with different DPI, if you have one: text stays sharp, and transitions look
      the same.

## Idle CPU

- [ ] Play A with the overlay visible. Between line changes MusicBee's CPU use is about what it is
      with the plugin disabled. During a transition it rises briefly, then drops back.
- [ ] Pause. After the current transition ends, the plugin adds essentially no CPU, apart from a
      tiny poll every 250 ms.
- [ ] Hide the overlay (Tools menu) while playing. The plugin adds no CPU.
- [ ] Play B (unsynced) and C (no lyrics). No CPU from the plugin.
- [ ] Leave A playing for 10 minutes. MusicBee's memory (private bytes) and GDI object count
      (Task Manager column *GDI objects*) stay flat.

## Rapid track skipping

- [ ] With E, press *Next* about 10 times as fast as you can, then stop on one track. Within a few
      seconds the overlay shows that track's lyrics (or *No lyrics*). It never shows lyrics from a
      track you skipped past, not even briefly after the right ones appeared. Repeat 3 times.
- [ ] Skip back and forth between two uncached tracks several times. The log shows lookups
      being cancelled or joining one in progress (`joining in-flight lookup`), and each track ends up
      with its own lyrics.
- [ ] Play a track of E for 5 s or more, then skip to the next one. Its lyrics appear at once, and
      the log shows `prefetch …` for it followed by a `cache hit` when it starts.
- [ ] Skip while a transition is running. The new track starts cleanly, with no half-finished
      animation carried over.

## Network off or LRCLIB down

- [ ] Turn the network off and play an uncached track from E. The overlay shows *No lyrics (lookup
      failed)* within about 12 s. MusicBee stays responsive throughout. Turn the network back on and
      replay the track: the lyrics are found, because failures aren't cached.
- [ ] Simulate LRCLIB being down: add `127.0.0.1 lrclib.net` to
      `C:\Windows\System32\drivers\etc\hosts` (as administrator), then run `ipconfig /flushdns`.
      Tick NetEase in the settings first. Play uncached tracks from E. Lyrics still arrive, from
      NetEase (or as plain text from lyrics.ovh), and the log says which provider. After 2 tracks the log shows `LRCLIB: skipped,
      circuit open`, and later tracks get lyrics without first waiting on LRCLIB.
      **Remove the hosts line afterwards** and run `ipconfig /flushdns` again.
- [ ] Simulate a slow LRCLIB, if you have a tool like Clumsy or NetLimiter: delay LRCLIB by 3 s.
      The log shows `hedging: LRCLIB slow, starting next provider`, and lyrics arrive in about
      1.5 s.
- [ ] With LRCLIB working, play a track that LRCLIB only has as plain text. The log shows
      `LRCLIB: plain lyrics only, trying other providers for synced`. If NetEase has it synced, the
      overlay shows synced lyrics.
- [ ] Untick each provider in the settings in turn and play an uncached track. The log never
      mentions a provider that's switched off. With all three unticked, uncached tracks show
      *No lyrics* and make no requests.
- [ ] Close MusicBee while NetEase or LRCLIB is mid-request (network slowed or off). It exits
      promptly and nothing lingers.

## Track changes

- [ ] Skip quickly through several tracks. The overlay ends on the right lyrics for the final
      track, never on a stale one from a previous track.
- [ ] Play A, then replay it. The log shows a `cache hit` for the second play, with the provider
      name (e.g. `cache hit (lyrics, LRCLIB)`), and `cache\*.json` has a `"provider"` field.

## Unsynced and missing lyrics

- [ ] Play B. Lines show as a static block in one style, and nothing advances with playback.
      When unlocked, click the overlay, then use the mouse wheel to scroll.
- [ ] Play C. The overlay shows a small dim *No lyrics*. The log shows each enabled provider asked once.
      Play C again: the log shows `cache hit (negative)` and no new request.
- [ ] Play D. The sidecar's synced lyrics are used even if LRCLIB has some. The log says `Sidecar`.
- [ ] Turn off the network and play an uncached track. The overlay shows *No lyrics (lookup
      failed)*, MusicBee stays responsive, and the next play retries, because failures aren't
      cached.

## Shutdown and uninstall

- [ ] Disable the plugin in Preferences > Plugins. The overlay disappears immediately and no ghost
      window remains.
- [ ] Close MusicBee while lyrics are showing and a lookup is in progress. It exits normally and
      `MusicBee.exe` doesn't linger in Task Manager.
- [ ] Make the settings folder read-only (or the `cache` folder a file) and play an uncached track.
      Lyrics still show, MusicBee stays responsive, and the log has `cache write failed`.
- [ ] Start a lookup on an uncached track with the network slowed, then click **Uninstall** on the
      plugin at once. The overlay disappears. Wait 15 s: neither the `LyricsOverlay` folder nor
      `mb_LyricsOverlay.log` (or `.log.old`) has come back, and only the provider plugin's files
      (if installed) remain.

## Optional provider DLL

- [ ] With `mb_LyricsOverlayProvider.dll` installed and enabled, *LRCLIB (Lyrics Overlay)* appears
      in MusicBee's lyrics provider list (Preferences > Tags (2)).
- [ ] With only that provider ticked, MusicBee's own lyrics panel shows lyrics for A.
- [ ] Untick "prefer synchronised lyrics" (if your MusicBee version has that option). The provider
      then returns plain text.
