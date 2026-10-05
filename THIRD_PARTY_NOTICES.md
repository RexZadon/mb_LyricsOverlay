# Third-party notices

## Code included in the plugin DLLs

| Component | Used for | License |
|---|---|---|
| `MusicBeeInterface.cs`, MusicBee plugin API definitions by Steven Mayall ([getmusicbee.com/help/api](https://getmusicbee.com/help/api/)) | Compiled into both DLLs so they can talk to MusicBee | Published by the MusicBee author for building MusicBee plugins. No separate license text is supplied with it. |

The plugin DLLs reference only assemblies that ship with the .NET Framework 4.8. No third-party
DLLs are bundled.

## Build and test tools (not shipped)

| Component | Version | License |
|---|---|---|
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 | MIT ([license](https://github.com/Microsoft/dotnet/blob/master/LICENSE)) |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |

## Online services

The plugin can request lyrics from the services below. It doesn't include any of their code or
data. Lyrics remain the property of their respective rights holders. The plugin only displays them
to the user and keeps a local cache, and never redistributes them.

| Service | Terms / documentation |
|---|---|
| LRCLIB | <https://lrclib.net/docs>, a free, open API that asks clients to identify themselves in the User-Agent and to honour `Retry-After` |
| lyrics.ovh | <https://lyricsovh.docs.apiary.io> and <https://github.com/NTag/lyrics.ovh> (site source, MIT) |
| NetEase Cloud Music | No public API or developer terms. Unofficial endpoints, off by default. See README. |
