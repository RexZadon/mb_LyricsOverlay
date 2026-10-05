using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LyricsOverlay.Core
{
    /// <summary>Cleans track metadata before searching and produces comparison keys.</summary>
    public static class TitleNormalizer
    {
        const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        // "(feat. X)", "[Remastered 2011]", "(Live at Wembley)", "(Radio Edit)", "(Mono)", ...
        static readonly Regex BracketedJunk = new Regex(
            @"\s*[\(\[][^\)\]]*\b(feat\.?|ft\.?|featuring|with|remaster(ed)?|live|version|edit|mono|stereo|deluxe|bonus|demo|explicit|clean|mix)\b[^\)\]]*[\)\]]",
            Opts);

        // " - Remastered 2009", " - 2011 Remaster", " - Live", " - Live at X", " - Radio Edit", " - Mono Version"
        static readonly Regex DashSuffix = new Regex(
            @"\s+[-–—]\s+((\d{4}\s+)?(digital(ly)?\s+)?remaster(ed)?\b.*|live(\s+(at|from|in|on)\b.*)?|.*\b(version|edit|mix|mono|stereo)|demo|acoustic|bonus\s+track|explicit)\s*$",
            Opts);

        // Unbracketed " feat. X" / " ft. X" / " featuring X" to the end
        static readonly Regex FeatSuffix = new Regex(@"\s+(feat\.?|ft\.|featuring)\s+.*$", Opts);

        static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        public static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string s = title.Trim();
            string prev;
            do
            {
                prev = s;
                s = BracketedJunk.Replace(s, "");
                s = DashSuffix.Replace(s, "");
                s = FeatSuffix.Replace(s, "");
                s = Whitespace.Replace(s, " ").Trim();
            } while (s != prev && s.Length > 0);
            return s.Length == 0 ? title.Trim() : s;
        }

        public static string NormalizeArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return "";
            string s = BracketedJunk.Replace(artist.Trim(), "");
            s = FeatSuffix.Replace(s, "");
            s = Whitespace.Replace(s, " ").Trim();
            return s.Length == 0 ? artist.Trim() : s;
        }

        /// <summary>Lowercase, accents removed, letters and digits only. For equality checks.</summary>
        public static string Key(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
