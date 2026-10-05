using System.Linq;
using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    public class LrcParserTests
    {
        [Theory]
        [InlineData("[01:02.50]x", 62500)]   // [mm:ss.xx]
        [InlineData("[01:02.505]x", 62505)]  // [mm:ss.xxx]
        [InlineData("[01:02.5]x", 62500)]    // [mm:ss.x]
        [InlineData("[01:02]x", 62000)]      // [mm:ss]
        [InlineData("[01:02:50]x", 62500)]   // colon as fraction separator
        [InlineData("[100:00.00]x", 6000000)] // three-digit minutes
        public void ParsesTimestampFormats(string lrc, int expectedMs)
        {
            var p = LrcParser.Parse(lrc);
            Assert.True(p.IsSynced);
            Assert.Equal(expectedMs, p.Lines.Single().TimeMs);
            Assert.Equal("x", p.Lines[0].Text);
        }

        [Fact]
        public void SeveralTimestampsOnOneLineProduceOneEntryEach()
        {
            var p = LrcParser.Parse("[00:10.00][00:30.00] [00:50.00]Repeated line\n[00:20.00]Other");
            Assert.Equal(new[] { 10000, 20000, 30000, 50000 }, p.Lines.Select(l => l.TimeMs));
            Assert.Equal(new[] { "Repeated line", "Other", "Repeated line", "Repeated line" }, p.Lines.Select(l => l.Text));
        }

        [Fact]
        public void OutOfOrderInputIsSortedStably()
        {
            var p = LrcParser.Parse("[00:30.00]c\n[00:10.00]a\n[00:20.00]b1\n[00:20.00]b2");
            Assert.Equal(new[] { "a", "b1", "b2", "c" }, p.Lines.Select(l => l.Text));
        }

        [Fact]
        public void PositiveOffsetShowsLinesEarlier()
        {
            var p = LrcParser.Parse("[offset:+500]\n[00:10.00]a\n[00:00.20]b");
            Assert.Equal(new[] { 0, 9500 }, p.Lines.Select(l => l.TimeMs)); // clamped at 0
        }

        [Fact]
        public void NegativeOffsetShowsLinesLater()
        {
            var p = LrcParser.Parse("[00:10.00]a\n[offset: -250]");
            Assert.Equal(10250, p.Lines.Single().TimeMs);
        }

        [Fact]
        public void IdTagsAreIgnoredAndBlankTimedLinesAreKeptAsGaps()
        {
            var p = LrcParser.Parse("[ar:Someone]\n[ti:Something]\n[length: 03:00]\n\n[00:01.00]a\n\n[00:05.00]\n[00:09.00]b\n");
            Assert.True(p.IsSynced);
            Assert.Equal(new[] { "a", "", "b" }, p.Lines.Select(l => l.Text));
        }

        [Fact]
        public void CrLfAndWordLevelTagsAreHandled()
        {
            var p = LrcParser.Parse("[00:01.00]<00:01.00>one <00:01.50>two\r\n[00:02.00]three\r\n");
            Assert.Equal(new[] { "one two", "three" }, p.Lines.Select(l => l.Text));
        }

        [Fact]
        public void TextWithoutTimestampsIsUnsynced()
        {
            var p = LrcParser.Parse("\n\nfirst\n\n\n\nsecond\n[Chorus]\nthird\n\n");
            Assert.False(p.IsSynced);
            Assert.Equal(new[] { "first", "", "second", "[Chorus]", "third" }, p.Lines.Select(l => l.Text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n\n ")]
        [InlineData("[ar:Only tags]\n[ti:No lines]")]
        public void EmptyInputGivesEmptyLyrics(string text)
        {
            Assert.True(LrcParser.Parse(text).IsEmpty);
        }

        [Fact]
        public void ToPlainTextDropsTimestamps()
        {
            Assert.Equal("a\r\nb", LrcParser.Parse("[00:01.00]a\n[00:02.00]b").ToPlainText());
        }
    }

    public class LyricSyncTests
    {
        static readonly LyricLine[] Lines =
        {
            new LyricLine(1000, "a"), new LyricLine(2000, "b"), new LyricLine(2000, "b2"), new LyricLine(5000, "c"),
        };

        [Theory]
        [InlineData(0, -1)]
        [InlineData(999, -1)]
        [InlineData(1000, 0)]
        [InlineData(1999, 0)]
        [InlineData(2000, 2)]   // equal timestamps: the last one wins
        [InlineData(4999, 2)]
        [InlineData(5000, 3)]
        [InlineData(999999, 3)]
        public void FindsCurrentLine(int pos, int expected)
        {
            Assert.Equal(expected, LyricSync.FindLineIndex(Lines, pos));
        }

        [Fact]
        public void EmptyListGivesMinusOne()
        {
            Assert.Equal(-1, LyricSync.FindLineIndex(new LyricLine[0], 1234));
        }

        [Fact]
        public void SeekingBackwardsIsJustAnotherLookup()
        {
            Assert.Equal(3, LyricSync.FindLineIndex(Lines, 6000));
            Assert.Equal(0, LyricSync.FindLineIndex(Lines, 1500));
        }

        [Fact]
        public void WindowKeepsCurrentLineCentred()
        {
            var w = LyricSync.BuildSyncedWindow(Lines, 0, 3);
            Assert.Equal(new[] { "", "a", "b" }, w.Select(l => l.Text));
            Assert.Equal(new[] { LineRole.Context, LineRole.Current, LineRole.Context }, w.Select(l => l.Role));

            var before = LyricSync.BuildSyncedWindow(Lines, -1, 5);
            Assert.Equal(new[] { "", "", "", "a", "b" }, before.Select(l => l.Text));
            Assert.Equal(new[] { 2, 1, 0, 1, 2 }, before.Select(l => l.Distance));
        }

        [Fact]
        public void EvenLineCountShowsMoreUpcomingLines()
        {
            var w = LyricSync.BuildSyncedWindow(Lines, 1, 4);
            Assert.Equal(new[] { "a", "b", "b2", "c" }, w.Select(l => l.Text));
            Assert.Equal(LineRole.Current, w[1].Role);
        }

        [Fact]
        public void UnsyncedWindowIsStaticBlock()
        {
            var w = LyricSync.BuildUnsyncedWindow(Lines, 2, 5);
            Assert.Equal(new[] { "b2", "c" }, w.Select(l => l.Text));
            Assert.All(w, l => Assert.Equal(LineRole.Plain, l.Role));
        }
    }
}
