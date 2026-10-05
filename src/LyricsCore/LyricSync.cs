using System.Collections.Generic;

namespace LyricsOverlay.Core
{
    public enum LineRole { Current, Context, Plain, Status }

    public sealed class DisplayLine
    {
        public DisplayLine(string text, LineRole role, int distance = 0)
        {
            Text = text ?? "";
            Role = role;
            Distance = distance;
        }

        public string Text { get; }
        public LineRole Role { get; }
        /// <summary>How many lines away from the current line (Context lines only).</summary>
        public int Distance { get; }
    }

    public static class LyricSync
    {
        /// <summary>
        /// Index of the last line whose start time is &lt;= positionMs, or -1 before the first line.
        /// Lines must be sorted by time (LrcParser guarantees that).
        /// </summary>
        public static int FindLineIndex(IReadOnlyList<LyricLine> lines, int positionMs)
        {
            int lo = 0, hi = lines.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (lines[mid].TimeMs <= positionMs)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return found;
        }

        /// <summary>
        /// Builds the visible window of <paramref name="count"/> lines with the current line in the middle.
        /// Slots outside the lyrics are empty strings so the current line never jumps around.
        /// </summary>
        public static List<DisplayLine> BuildSyncedWindow(IReadOnlyList<LyricLine> lines, int currentIndex, int count)
        {
            if (count < 1) count = 1;
            int before = (count - 1) / 2;
            int after = count - 1 - before;
            var result = new List<DisplayLine>(count);
            for (int k = -before; k <= after; k++)
            {
                int i = currentIndex + k;
                string text = i >= 0 && i < lines.Count ? lines[i].Text : "";
                result.Add(k == 0 ? new DisplayLine(text, LineRole.Current) : new DisplayLine(text, LineRole.Context, k < 0 ? -k : k));
            }
            return result;
        }

        /// <summary>Static block of unsynced lines starting at <paramref name="firstLine"/>.</summary>
        public static List<DisplayLine> BuildUnsyncedWindow(IReadOnlyList<LyricLine> lines, int firstLine, int count)
        {
            var result = new List<DisplayLine>(count);
            for (int i = firstLine; i < lines.Count && result.Count < count; i++)
                result.Add(new DisplayLine(lines[i].Text, LineRole.Plain));
            return result;
        }
    }
}
