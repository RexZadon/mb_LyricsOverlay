# Lyrics Overlay for MusicBee

Shows the current lyric line in a floating, transparent, always-on-top window over your desktop.
Lines scroll or fade smoothly as the song plays. If a track has no lyrics, the plugin looks them up
online, preferring time-synced lyrics from [LRCLIB](https://lrclib.net).

- Synced (LRC) lyrics follow playback, seeks and pauses. Plain lyrics are shown as a static block.
- Lock the overlay to make it click-through, or unlock it to drag and resize it.
- Font, colours, outline, shadow, opacity, number of lines, alignment and line transition are all
  adjustable, with a live preview.
- Uses lyrics already in your files first. Fetched lyrics are only shown and cached, never written
  into your music files.

Requirements: Windows with .NET Framework 4.8 (included with Windows 10 and 11) and MusicBee 3.x.

## Install

**With the Add Plugin button (recommended)**

1. Download `mb_LyricsOverlay_<version>.zip`.
2. In MusicBee open **Edit > Preferences > Plugins**, click **Add Plugin…** and select the zip.
3. Enable **Lyrics Overlay** in the plugin list if it isn't already, then click **Save**. Restart
   MusicBee if it asks you to.

**By hand**

1. Close MusicBee.
2. Unzip and copy `mb_LyricsOverlay.dll` into MusicBee's plugins folder. That is
   `%APPDATA%\MusicBee\Plugins` for the installed version (create the folder if needed) or
   `<MusicBee folder>\Plugins`. For the portable version it is `<MusicBee folder>\Plugins`.
3. Start MusicBee and enable the plugin under **Edit > Preferences > Plugins**.

The DLL is self-contained. It needs no other files.

**Optional: LRCLIB in MusicBee's own lyrics panel.** `mb_LyricsOverlayProvider_<version>.zip`
contains a second, small plugin that adds *LRCLIB (Lyrics Overlay)* to MusicBee's list of lyrics
providers (**Preferences > Tags (2) > Lyrics**). You don't need it for the overlay. Install it the
same way, enable it, then tick the provider in that list.

## Use

- The overlay appears near the bottom of the main screen when a track plays.
- **Unlocked** (the default on first run): a faint dashed panel shows behind the text. Drag it to
  move it, and drag the edges or corners to resize it. For plain (unsynced) lyrics, click the
  overlay and use the mouse wheel to scroll.
- **Locked**: only the text is drawn and mouse clicks pass through to the windows underneath.
- **Tools menu**: *Lyrics Overlay: Show or Hide* and *Lyrics Overlay: Lock or Unlock*. Both can be
  bound to keys under **Preferences > Hotkeys**. Tick *global* there to use them outside MusicBee.

| The overlay shows | When |
|---|---|
| Current line large, neighbours smaller and dimmer | The track has synced lyrics |
| A static block of lines | Only plain lyrics exist |
| `♪ Instrumental ♪` | LRCLIB or NetEase marks the track as instrumental |
| `Searching for lyrics…` | A lookup is running |
| `No lyrics` | Nothing was found |
| `No lyrics (lookup failed)` | The lookup failed, e.g. no network. It is retried the next time the track plays. |
| Nothing | The player is stopped |

## Settings

Open **Preferences > Plugins > Lyrics Overlay > Configure**. Changes show at once in the preview
and on the overlay. **Cancel** undoes them and **Reset to defaults** restores the default look
while keeping the window position.

| Setting | What it does | Default |
|---|---|---|
| Font, size, bold, italic | Text font | Segoe UI, 26, bold |
| Text, outline and shadow colour | Colours. Transparency (alpha) can only be set in `settings.json`. | White text, black outline and shadow |
| Outline thickness, shadow distance | Outline width and shadow offset | 2.5, 2 |
| Opacity | Transparency of the whole overlay, 10–100 % | 100 % |
| Lines shown | Number of lines, the current one in the middle | 3 |
| Alignment | Left, center or right | Center |
| Line transition | **Slide**: lines scroll up, the new line grows and brightens. **Fade**: old and new lines crossfade in place. **None**: instant switch. | Slide |
| Transition length | 100–1000 ms | 320 ms |
| Easing | Ease out, Spring, Ease in-out or Linear | Ease out |
| Lock | Click-through mode | Off |
| Show overlay | Same as Tools > Show or Hide | On |
| Lyrics sources | Which online providers may be used (see below) | LRCLIB and lyrics.ovh on, NetEase off |

Position and size are saved when you finish moving or resizing the overlay.

## Where lyrics come from

The plugin checks these in order and stops at the first hit:

1. Lyrics in the file's tags, then lyrics MusicBee itself has already downloaded.
2. A sidecar file next to the track with the same name, e.g. `Song.flac` with `Song.lrc`.
3. The plugin's own cache (see *Privacy*).
4. The enabled online providers, which you can switch on or off under **Lyrics sources**:

| Provider | What it returns | Notes and limits |
|---|---|---|
| [LRCLIB](https://lrclib.net) | Synced and plain lyrics | Free, open, documented API with no key. Tried first. When LRCLIB says it is busy (HTTP 429), the plugin waits as long as LRCLIB asks before trying it again. |
| NetEase Cloud Music | Synced lyrics | **Off by default.** NetEase has no public API. This uses unofficial endpoints, and NetEase's terms don't grant automated access, so turning it on is at your own discretion. It can stop working without notice or ask for a captcha. In that case the plugin backs off. |
| [lyrics.ovh](https://lyrics.ovh) | Plain lyrics only | Free API with no key. A small hobby service that is sometimes slow or offline. It doesn't say where its texts come from. It is used only when no synced lyrics were found. |

A wrong song's lyrics are worse than none, so matches are strict. The title must match after
removing tags like `(Remastered 2011)`, `feat. …` or `- Live`. The artist must match loosely, and
the length must be within 10 seconds when known. A provider that fails or is slow doesn't hold up
the others. After repeated failures it is skipped for a while.

A few seconds into each track, the next track in Now Playing is looked up in the background, so its
lyrics are ready when it starts.

## Privacy

**What is sent, and to whom.** Only when a track has no lyrics in its tags, in a sidecar `.lrc` or
in the cache, and only to the providers you have enabled:

| Service | Data sent |
|---|---|
| LRCLIB (`lrclib.net`) | Artist, title, album and duration of the track |
| NetEase (`music.163.com`), only if enabled | Artist and title, then NetEase's ID of the matching song |
| lyrics.ovh (`api.lyrics.ovh`) | Artist and title |

This happens for the playing track and, a few seconds in, for the next track in Now Playing.
Every request carries a User-Agent naming the plugin, its version and its project page. Like any
web request, it also reveals your IP address to that service. Nothing else is sent: no file
paths, no account details, no lyrics from your files, and no usage statistics. The plugin
contacts no other servers.

The optional provider plugin sends the same LRCLIB data whenever MusicBee asks it for lyrics.

**What is stored on your disk.** Nothing leaves your computer except the requests above.

| What | Where | Contents |
|---|---|---|
| Settings | `%APPDATA%\MusicBee\LyricsOverlay\settings.json` (portable MusicBee: `<MusicBee folder>\AppData\LyricsOverlay\`) | Your settings and the overlay position |
| Cache | `…\LyricsOverlay\cache\*.json`, one file per track, named by a hash | Artist, title, the fetched lyrics, which provider supplied them, and when. Found lyrics are kept. "Not found" results expire after 7 days. |
| Log | `mb_LyricsOverlay.log` next to the plugin DLL, or in the settings folder if that isn't writable. The provider plugin writes `mb_LyricsOverlayProvider.log`. | Timestamps, the artist and title of looked-up tracks, which source answered, and error messages. Error messages can include local file and folder paths. Each log rolls over at 1 MB (one `.old` copy is kept). |

All of this stays on your computer. Delete the files at any time. The plugin recreates what it
needs.

## Troubleshooting

- **The overlay doesn't appear.** Check that the plugin is enabled in Preferences > Plugins, then
  use Tools > *Lyrics Overlay: Show or Hide*. Nothing is shown while the player is stopped.
- **I can't move the overlay.** It is locked. Unlock it from the Tools menu or in Configure.
- **Always "No lyrics".** Check that at least one provider is ticked under Lyrics sources and that
  the track's artist and title tags are right. "Not found" is cached for 7 days. To retry sooner,
  delete the `cache` folder.
- **"No lyrics (lookup failed)".** The network or the provider failed. The track is retried the
  next time it plays.
- **Wrong lyrics.** Delete the cache folder and check the tags. You can always override with a
  sidecar `.lrc` file or lyrics in the file's tags.
- **Anything else.** Look at `mb_LyricsOverlay.log` (see *Privacy* for where it is). Include the
  relevant lines when you report a problem, after checking them for anything you'd rather not share.

## Uninstall

In **Preferences > Plugins**, click **Uninstall** next to Lyrics Overlay. This removes the plugin
and deletes its settings, cache, log and the `mb_LyricsOverlay_*.txt` files that came in the zip. If you also installed the provider plugin, uninstall it
the same way.

If you removed the DLL by hand instead, also delete the `LyricsOverlay` folder in MusicBee's
AppData folder (Help > Support > Open AppData Folder, if your MusicBee version has it) and
`mb_LyricsOverlay.log` / `.log.old` from the plugins folder.

## License

See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Building from source
is described in [DEVELOPING.md](DEVELOPING.md).
