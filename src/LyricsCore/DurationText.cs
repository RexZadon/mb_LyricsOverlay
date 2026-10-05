using System.Globalization;

namespace LyricsOverlay.Core
{
    public static class DurationText
    {
        /// <summary>
        /// Parses MusicBee's displayed duration ("3:45", "1:02:03", "225") into milliseconds; 0 if unknown.
        /// </summary>
        public static int ParseToMs(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var parts = text.Trim().Split(':');
            if (parts.Length > 3) return 0;
            double total = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return 0;
                total = total * 60 + v;
            }
            return (int)(total * 1000);
        }
    }
}
