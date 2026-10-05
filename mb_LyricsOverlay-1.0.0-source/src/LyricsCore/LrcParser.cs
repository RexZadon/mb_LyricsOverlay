using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LyricsOverlay.Core
{
    public sealed class LyricLine
    {
        public LyricLine(int timeMs, string text)
        {
            TimeMs = timeMs;
            Text = text;
        }

        /// <summary>Start time in milliseconds. 0 for unsynced lyrics.</summary>
        public int TimeMs { get; }
        public string Text { get; }

        public override string ToString() => $"{TimeMs}: {Text}";
    }

    public sealed class ParsedLyrics
    {
        public static readonly ParsedLyrics Empty = new ParsedLyrics(false, new LyricLine[0]);

        public ParsedLyrics(bool isSynced, IReadOnlyList<LyricLine> lines)
        {
            IsSynced = isSynced;
            Lines = lines;
        }

        public bool IsSynced { get; }
        public IReadOnlyList<LyricLine> Lines { get; }
        public bool IsEmpty => Lines.Count == 0;

        /// <summary>Plain text without timestamps, one line per entry.</summary>
        public string ToPlainText() => string.Join("\r\n", Lines.Select(l => l.Text));
    }

    /// <summary>
    /// Parses LRC text. Supports [mm:ss], [mm:ss.x], [mm:ss.xx], [mm:ss.xxx] (also ':' as the fraction
    /// separator), several timestamps on one line, the [offset:±ms] tag, ID tags such as [ar:...],
    /// and strips enhanced-LRC word tags like &lt;00:12.34&gt;. Text without any timestamps is
    /// returned as unsynced lyrics.
    /// </summary>
    public static class LrcParser
    {
        static readonly Regex TimeTag = new Regex(@"\G\s*\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        static readonly Regex IdTag = new Regex(@"^\[([A-Za-z#]+)\s*:(.*)\]$", RegexOptions.Compiled);
        static readonly Regex WordTag = new Regex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s{2,}", RegexOptions.Compiled);

        public static ParsedLyrics Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ParsedLyrics.Empty;

            var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var timed = new List<LyricLine>();
            var plain = new List<string>();
            int offsetMs = 0;

            foreach (var raw in rawLines)
            {
                var line = raw.Trim();
                var times = new List<int>();
                int pos = 0;
                Match m;
                while ((m = TimeTag.Match(line, pos)).Success)
                {
                    times.Add(ToMs(m));
                    pos = m.Index + m.Length;
                }

                if (times.Count == 0)
                {
                    var id = IdTag.Match(line);
                    if (id.Success)
                    {
                        if (id.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(id.Groups[2].Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var off))
                            offsetMs = off;
                        continue;
                    }
                    plain.Add(CleanText(line));
                    continue;
                }

                var lyric = CleanText(line.Substring(pos));
                foreach (var t in times)
                    timed.Add(new LyricLine(t, lyric));
            }

            if (timed.Count > 0)
            {
                // A positive offset makes lyrics appear sooner (LRC convention).
                var lines = timed
                    .Select(l => new LyricLine(Math.Max(0, l.TimeMs - offsetMs), l.Text))
                    .OrderBy(l => l.TimeMs) // stable sort keeps file order for equal times
                    .ToList();
                return new ParsedLyrics(true, lines);
            }

            return BuildUnsynced(plain);
        }

        static ParsedLyrics BuildUnsynced(List<string> plain)
        {
            var result = new List<LyricLine>();
            bool lastBlank = true; // drops leading blanks
            foreach (var l in plain)
            {
                bool blank = l.Length == 0;
                if (blank && lastBlank) continue; // collapse runs of blank lines
                result.Add(new LyricLine(0, l));
                lastBlank = blank;
            }
            while (result.Count > 0 && result[result.Count - 1].Text.Length == 0)
                result.RemoveAt(result.Count - 1);
            return result.Count == 0 ? ParsedLyrics.Empty : new ParsedLyrics(false, result);
        }

        static int ToMs(Match m)
        {
            int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int ms = 0;
            if (m.Groups[3].Success)
            {
                // ".5" = 500 ms, ".05" = 50 ms, ".005" = 5 ms
                ms = int.Parse(m.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
            }
            return (min * 60 + sec) * 1000 + ms;
        }

        static string CleanText(string s) => Spaces.Replace(WordTag.Replace(s, ""), " ").Trim();
    }
}
