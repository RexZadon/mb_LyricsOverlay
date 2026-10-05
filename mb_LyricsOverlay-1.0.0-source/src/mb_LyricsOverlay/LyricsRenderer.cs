using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using LyricsOverlay.Core;

namespace MusicBeePlugin
{
    /// <summary>
    /// Draws lyric lines as outlined, shadowed text paths. Shared by the overlay and the settings preview
    /// (one instance each). Font family, brush, pen and each line's glyph outline are built once and
    /// reused, so an animation frame only changes transforms and colours and allocates nothing.
    /// Not thread-safe: use from the UI thread.
    /// </summary>
    public sealed class LyricsRenderer : IDisposable
    {
        const float ContextScale = SyncedLayout.ContextScale; // previous/next lines relative to the current line
        const float PlainScale = 0.8f;      // unsynced lyrics
        const float StatusScale = 0.6f;     // "No lyrics", "Searching..."
        const float LineSpacing = SyncedLayout.LineSpacing;
        const float Padding = 12f;
        const float RefEm = 100f;           // glyph outlines are cached at this size and scaled when drawn
        const int MaxCachedPaths = 400;

        sealed class TextPath
        {
            public GraphicsPath Path;
            public RectangleF Bounds;
        }

        readonly Dictionary<string, TextPath> _paths = new Dictionary<string, TextPath>();
        readonly List<FrameItem> _scratch = new List<FrameItem>();
        readonly SolidBrush _brush = new SolidBrush(Color.White);
        readonly Pen _pen = new Pen(Color.Black, 1f) { LineJoin = LineJoin.Round };
        OverlaySettings _for;
        string _familyName;
        FontFamily _family;
        FontStyle _style;
        Color _text, _outline, _shadow;

        /// <summary>Static layout: status text, unsynced block, or a synced window without animation.</summary>
        public void Draw(Graphics g, RectangleF area, OverlaySettings s, IList<DisplayLine> lines)
        {
            if (lines == null || lines.Count == 0) return;
            float total = 0;
            foreach (var l in lines) total += Scale(l.Role) * LineSpacing;

            _scratch.Clear();
            float y = -total / 2f;
            foreach (var l in lines)
            {
                float slot = Scale(l.Role) * LineSpacing;
                if (l.Text.Length > 0)
                    _scratch.Add(new FrameItem { Text = l.Text, Y = y + slot / 2f, Scale = Scale(l.Role), Alpha = Alpha(l) });
                y += slot;
            }
            DrawFrame(g, area, s, _scratch, total);
        }

        /// <summary>
        /// Draws positioned lines. <paramref name="heightEm"/> is the layout's full height in current-line
        /// font sizes; if it doesn't fit, everything shrinks uniformly (as the static layout always did).
        /// </summary>
        public void DrawFrame(Graphics g, RectangleF area, OverlaySettings s, IList<FrameItem> items, float heightEm)
        {
            if (items == null || items.Count == 0 || area.Width <= 2 * Padding || area.Height <= 0) return;
            Prepare(s);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float basePx = s.FontSize * g.DpiY / 72f;
            float fit = Math.Min(1f, (area.Height - Padding) / Math.Max(1f, heightEm * basePx));
            float unit = basePx * fit;
            float cy = area.Top + area.Height / 2f;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Alpha <= 0.004f || string.IsNullOrEmpty(it.Text)) continue;
                float em = unit * it.Scale;
                DrawLine(g, area, s, it.Text, em, cy + it.Y * unit - em / 2f, Math.Min(1f, it.Alpha));
            }
            g.ResetTransform();
        }

        /// <summary>Re-read colours and font on the next draw (for callers that edit one settings object in place).</summary>
        public void ResetSettingsCache() => _for = null;

        /// <summary>Drops cached glyph outlines (call when the lyrics change).</summary>
        public void ClearTextCache()
        {
            foreach (var p in _paths.Values) p.Path.Dispose();
            _paths.Clear();
        }

        void DrawLine(Graphics g, RectangleF area, OverlaySettings s, string text, float em, float top, float alpha)
        {
            var tp = PathFor(text);
            var b = tp.Bounds;
            if (b.Width <= 0) return;

            // Long lines are squeezed to fit the width instead of being cut off.
            float k = em / RefEm;
            float w = b.Width * k;
            float maxW = area.Width - 2 * Padding - 2 * s.OutlineWidth;
            float squeeze = w > maxW && maxW > 0 ? maxW / w : 1f;
            float scale = k * squeeze;
            w *= squeeze;
            float x = s.Alignment == TextAlign.Left ? area.Left + Padding
                    : s.Alignment == TextAlign.Right ? area.Right - Padding - w
                    : area.Left + (area.Width - w) / 2f;
            float y = top + (em - em * squeeze) / 2f;

            if (s.ShadowDepth > 0 && _shadow.A > 0)
            {
                Place(g, x + s.ShadowDepth, y + s.ShadowDepth, scale, b.Left);
                _brush.Color = WithAlpha(_shadow, alpha);
                g.FillPath(_brush, tp.Path);
                if (s.OutlineWidth > 0)
                {
                    _pen.Color = _brush.Color;
                    _pen.Width = s.OutlineWidth * 2 / scale;
                    g.DrawPath(_pen, tp.Path);
                }
            }
            Place(g, x, y, scale, b.Left);
            if (s.OutlineWidth > 0 && _outline.A > 0)
            {
                // The pen is centred on the glyph edge, so double it to get the visible thickness.
                _pen.Color = WithAlpha(_outline, alpha);
                _pen.Width = s.OutlineWidth * 2 / scale; // pen widths scale with the transform
                g.DrawPath(_pen, tp.Path);
            }
            _brush.Color = WithAlpha(_text, alpha);
            g.FillPath(_brush, tp.Path);
        }

        static void Place(Graphics g, float x, float y, float scale, float left)
        {
            g.ResetTransform();
            g.TranslateTransform(x, y);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-left, 0);
        }

        TextPath PathFor(string text)
        {
            if (_paths.TryGetValue(text, out var tp)) return tp;
            if (_paths.Count >= MaxCachedPaths) ClearTextCache();
            var path = new GraphicsPath();
            path.AddString(text, _family, (int)_style, RefEm, new PointF(0, 0), StringFormat.GenericTypographic);
            tp = new TextPath { Path = path, Bounds = path.GetBounds() };
            _paths[text] = tp;
            return tp;
        }

        /// <summary>Re-reads colours and font when the settings object changes (it is replaced on every edit).</summary>
        void Prepare(OverlaySettings s)
        {
            if (ReferenceEquals(s, _for) && _family != null) return;
            _for = s;
            _text = OverlaySettings.ParseColor(s.TextColor, Color.White);
            _outline = OverlaySettings.ParseColor(s.OutlineColor, Color.Black);
            _shadow = OverlaySettings.ParseColor(s.ShadowColor, Color.FromArgb(128, 0, 0, 0));

            if (_family == null || _familyName != s.FontFamily)
            {
                ClearTextCache();
                _family?.Dispose();
                _family = ResolveFamily(s.FontFamily);
                _familyName = s.FontFamily;
            }
            var style = ResolveStyle(_family, s);
            if (style != _style) ClearTextCache();
            _style = style;
        }

        public void Dispose()
        {
            ClearTextCache();
            _family?.Dispose();
            _family = null;
            _brush.Dispose();
            _pen.Dispose();
        }

        static float Scale(LineRole role) =>
            role == LineRole.Current ? 1f : role == LineRole.Plain ? PlainScale : role == LineRole.Status ? StatusScale : ContextScale;

        static float Alpha(DisplayLine line)
        {
            switch (line.Role)
            {
                case LineRole.Current: return 1f;
                case LineRole.Plain: return 0.9f;
                case LineRole.Status: return 0.45f;
                default: return Math.Max(0.25f, 0.6f - 0.15f * (line.Distance - 1));
            }
        }

        static Color WithAlpha(Color c, float factor) => Color.FromArgb((int)(c.A * factor), c.R, c.G, c.B);

        static FontFamily ResolveFamily(string name)
        {
            try { return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name); }
            catch (ArgumentException) { return new FontFamily(GenericFontFamilies.SansSerif); }
        }

        static FontStyle ResolveStyle(FontFamily family, OverlaySettings s)
        {
            var style = (s.Bold ? FontStyle.Bold : 0) | (s.Italic ? FontStyle.Italic : 0);
            if (family.IsStyleAvailable(style)) return style;
            if (family.IsStyleAvailable(FontStyle.Regular)) return FontStyle.Regular;
            return family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Italic;
        }
    }
}
