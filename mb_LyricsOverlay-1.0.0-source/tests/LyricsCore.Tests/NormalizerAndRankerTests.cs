using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    public class TitleNormalizerTests
    {
        [Theory]
        [InlineData("Song Title", "Song Title")]
        [InlineData("Song Title (feat. Someone Else)", "Song Title")]
        [InlineData("Song Title [ft. Someone]", "Song Title")]
        [InlineData("Song Title feat. Someone", "Song Title")]
        [InlineData("Song Title featuring Someone & Other", "Song Title")]
        [InlineData("Song Title (Remastered)", "Song Title")]
        [InlineData("Song Title (2011 Remaster)", "Song Title")]
        [InlineData("Song Title - Remastered 2009", "Song Title")]
        [InlineData("Song Title - 2011 Remaster", "Song Title")]
        [InlineData("Song Title - Digital Remaster", "Song Title")]
        [InlineData("Song Title - Live", "Song Title")]
        [InlineData("Song Title - Live at the Hall", "Song Title")]
        [InlineData("Song Title (Live)", "Song Title")]
        [InlineData("Song Title - Radio Edit", "Song Title")]
        [InlineData("Song Title - Single Version", "Song Title")]
        [InlineData("Song Title (Mono) [Remastered 2015]", "Song Title")]
        [InlineData("Song Title (Remastered) - Live feat. X", "Song Title")]
        [InlineData("Song Title (Remix)", "Song Title (Remix)")]          // a remix is a different recording
        [InlineData("Live and Let Die", "Live and Let Die")]              // "live" inside a real title stays
        [InlineData("Re-Make - Re-Model", "Re-Make - Re-Model")]          // dash that isn't a junk suffix
        [InlineData("(Live)", "(Live)")]                                  // never normalise to empty
        [InlineData("  spaced   out  ", "spaced out")]
        public void NormalizesTitles(string input, string expected)
        {
            Assert.Equal(expected, TitleNormalizer.NormalizeTitle(input));
        }

        [Theory]
        [InlineData("Artist feat. Guest", "Artist")]
        [InlineData("Artist (feat. Guest)", "Artist")]
        [InlineData("Artist ft. Guest", "Artist")]
        [InlineData("Duo & Partner", "Duo & Partner")]
        public void NormalizesArtists(string input, string expected)
        {
            Assert.Equal(expected, TitleNormalizer.NormalizeArtist(input));
        }

        [Fact]
        public void KeyIgnoresCaseAccentsAndPunctuation()
        {
            Assert.Equal(TitleNormalizer.Key("Beyoncé – Déjà Vu!"), TitleNormalizer.Key("beyonce deja vu"));
        }

        [Fact]
        public void DurationTextParsesMusicBeeFormats()
        {
            Assert.Equal(225000, DurationText.ParseToMs("3:45"));
            Assert.Equal(3723000, DurationText.ParseToMs("1:02:03"));
            Assert.Equal(0, DurationText.ParseToMs("n/a"));
            Assert.Equal(0, DurationText.ParseToMs(null));
        }
    }

    public class CandidateRankerTests
    {
        static LrclibTrack T(long id, string title, string artist, double? dur, bool synced = true, string album = "Album") =>
            new LrclibTrack
            {
                Id = id, TrackName = title, ArtistName = artist, AlbumName = album, Duration = dur,
                SyncedLyrics = synced ? "[00:01.00]placeholder" : null,
                PlainLyrics = "placeholder",
            };

        static readonly TrackQuery Q = new TrackQuery { Artist = "The Artist", Title = "Song (Remastered 2011)", Album = "Album", DurationSec = 200 };

        [Fact]
        public void PicksClosestDuration()
        {
            var best = CandidateRanker.PickBest(new[] { T(1, "Song", "The Artist", 206), T(2, "Song", "The Artist", 201), T(3, "Song", "The Artist", 195) }, Q);
            Assert.Equal(2, best.Id);
        }

        [Fact]
        public void RejectsWrongTitleArtistAndFarDurations()
        {
            var best = CandidateRanker.PickBest(new[]
            {
                T(1, "Other Song", "The Artist", 200),
                T(2, "Song", "Somebody Else", 200),
                T(3, "Song", "The Artist", 260),
            }, Q);
            Assert.Null(best);
        }

        [Fact]
        public void SyncedWinsNearTieButNotLargeGap()
        {
            Assert.Equal(2, CandidateRanker.PickBest(new[] { T(1, "Song", "The Artist", 200, synced: false), T(2, "Song", "The Artist", 202) }, Q).Id);
            Assert.Equal(1, CandidateRanker.PickBest(new[] { T(1, "Song", "The Artist", 200, synced: false), T(2, "Song", "The Artist", 208) }, Q).Id);
        }

        [Fact]
        public void MatchesNormalizedCandidateTitlesAndMultiArtistCredits()
        {
            var best = CandidateRanker.PickBest(new[] { T(7, "Song - Remastered", "The Artist, Guest", 199) }, Q);
            Assert.Equal(7, best.Id);
        }

        [Fact]
        public void SkipsEntriesWithoutLyricsButKeepsInstrumentals()
        {
            var empty = new LrclibTrack { Id = 1, TrackName = "Song", ArtistName = "The Artist", Duration = 200 };
            var instrumental = new LrclibTrack { Id = 2, TrackName = "Song", ArtistName = "The Artist", Duration = 203, Instrumental = true };
            Assert.Equal(2, CandidateRanker.PickBest(new[] { empty, instrumental }, Q).Id);
        }

        [Fact]
        public void UnknownDurationStillMatches()
        {
            var q = new TrackQuery { Artist = "The Artist", Title = "Song" };
            Assert.Equal(1, CandidateRanker.PickBest(new[] { T(1, "Song", "The Artist", 200) }, q).Id);
        }
    }
}
