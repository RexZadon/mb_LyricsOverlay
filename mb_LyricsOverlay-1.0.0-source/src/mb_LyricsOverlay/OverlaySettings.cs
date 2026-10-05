using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using LyricsOverlay.Core;

namespace MusicBeePlugin
{
    public enum TextAlign { Left = 0, Center = 1, Right = 2 }

    /// <summary>
    /// Everything the user can customise, saved as JSON in MusicBee's persistent storage folder.
    /// (DataContract rather than XmlSerializer: no runtime code generation inside MusicBee.)
    /// </summary>
    [DataContract]
    public sealed class OverlaySettings
    {
        [DataMember] public string FontFamily { get; set; }
        [DataMember] public float FontSize { get; set; }
        [DataMember] public bool Bold { get; set; }
        [DataMember] public bool Italic { get; set; }

        // Colours are "#AARRGGBB" strings so the file stays readable and hand-editable.
        [DataMember] public string TextColor { get; set; }
        [DataMember] public string OutlineColor { get; set; }
        [DataMember] public float OutlineWidth { get; set; }
        [DataMember] public string ShadowColor { get; set; }
        [DataMember] public float ShadowDepth { get; set; }

        /// <summary>Overall opacity, 10..100 percent.</summary>
        [DataMember] public int Opacity { get; set; }
        [DataMember] public int LinesShown { get; set; }
        [DataMember] public TextAlign Alignment { get; set; }

        /// <summary>Click-through mode: the window ignores the mouse and cannot be moved.</summary>
        [DataMember] public bool Locked { get; set; }
        [DataMember] public bool Visible { get; set; }

        /// <summary>How the synced lines move when the current line changes. None = instant switch.</summary>
        [DataMember] public AnimationStyle Animation { get; set; }
        /// <summary>Transition length in milliseconds (100..1000).</summary>
        [DataMember] public int AnimationMs { get; set; }
        [DataMember] public EasingKind AnimationEasing { get; set; }

        // Online lyrics providers, tried in this order. Each can be switched off.
        [DataMember] public bool ProviderLrclib { get; set; }
        [DataMember] public bool ProviderNetEase { get; set; }
        [DataMember] public bool ProviderLyricsOvh { get; set; }

        [DataMember] public int X { get; set; } // int.MinValue = default position
        [DataMember] public int Y { get; set; }
        [DataMember] public int Width { get; set; }
        [DataMember] public int Height { get; set; }

        public OverlaySettings() => SetDefaults();

        // DataContract skips constructors; this keeps defaults for keys missing from older files.
        [OnDeserializing]
        void OnDeserializing(StreamingContext _) => SetDefaults();

        void SetDefaults()
        {
            FontFamily = "Segoe UI";
            FontSize = 26f;
            Bold = true;
            Italic = false;
            TextColor = "#FFFFFFFF";
            OutlineColor = "#E0000000";
            OutlineWidth = 2.5f;
            ShadowColor = "#80000000";
            ShadowDepth = 2f;
            Opacity = 100;
            LinesShown = 3;
            Alignment = TextAlign.Center;
            Locked = false;
            Visible = true;
            Animation = AnimationStyle.Slide;
            AnimationMs = 320;
            AnimationEasing = EasingKind.EaseOutCubic;
            ProviderLrclib = true;
            // Off unless the user opts in: NetEase has no public API and its terms don't permit automated access.
            ProviderNetEase = false;
            ProviderLyricsOvh = true;
            X = int.MinValue;
            Y = 0;
            Width = 900;
            Height = 170;
        }

        public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();

        public bool IsProviderEnabled(string name)
        {
            switch (name)
            {
                case LrclibProvider.ProviderName: return ProviderLrclib;
                case NetEaseProvider.ProviderName: return ProviderNetEase;
                case LyricsOvhProvider.ProviderName: return ProviderLyricsOvh;
                default: return true;
            }
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            if (hex != null && hex.StartsWith("#") && hex.Length == 9
                && uint.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                return Color.FromArgb(unchecked((int)argb));
            return fallback;
        }

        public static string ToHex(Color c) => "#" + c.ToArgb().ToString("X8");

        public Rectangle GetBounds()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Max(200, Width), h = Math.Max(60, Height);
            var fallback = new Rectangle(area.Left + (area.Width - w) / 2, area.Bottom - h - 40, w, h);
            if (X == int.MinValue) return fallback;
            var r = new Rectangle(X, Y, w, h);
            // Pull the window back on screen if a monitor was disconnected since last time.
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(r)) return r;
            return fallback;
        }

        public static OverlaySettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                    using (var fs = File.OpenRead(path))
                        return (OverlaySettings)new DataContractJsonSerializer(typeof(OverlaySettings)).ReadObject(fs)
                               ?? new OverlaySettings(); // a file containing just "null"
            }
            catch (Exception ex)
            {
                FileLog.Error("loading settings, using defaults", ex);
            }
            return new OverlaySettings();
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                using (var fs = File.Create(tmp))
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, System.Text.Encoding.UTF8, false, true))
                    new DataContractJsonSerializer(typeof(OverlaySettings)).WriteObject(w, this);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                FileLog.Error("saving settings", ex);
            }
        }
    }
}
