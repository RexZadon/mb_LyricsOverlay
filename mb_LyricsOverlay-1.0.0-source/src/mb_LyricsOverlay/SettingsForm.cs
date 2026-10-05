using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LyricsOverlay.Core;

namespace MusicBeePlugin
{
    /// <summary>
    /// Settings dialog built in code (no designer files). Every change updates the in-dialog preview
    /// and, through <see cref="Changed"/>, the real overlay. Cancel restores the original settings.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        // Made-up sample text; the preview steps through it so the line transition can be judged live.
        static readonly IReadOnlyList<LyricLine> SampleLines = new[]
        {
            new LyricLine(0, "The first sample line"),
            new LyricLine(1, "♪ A second line of sample text ♪"),
            new LyricLine(2, "The third line, a little longer than the rest"),
            new LyricLine(3, "Line number four"),
            new LyricLine(4, "And the fifth, before it starts again"),
        };

        readonly OverlaySettings _original;
        readonly OverlaySettings _s;
        readonly PreviewPanel _preview;
        readonly Label _fontLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        bool _loading;

        /// <summary>Raised with a fresh copy of the settings whenever the user changes something.</summary>
        public event Action<OverlaySettings> Changed;

        public OverlaySettings Result => _s.Clone();

        public SettingsForm(OverlaySettings current)
        {
            _original = current.Clone();
            _s = current.Clone();

            Text = "Lyrics Overlay Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(560, 800);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var fontButton = new Button { Text = "Choose…", AutoSize = true };
            fontButton.Click += (o, e) => PickFont();
            Row(grid, "Font", Flow(fontButton, _fontLabel));

            Row(grid, "Text colour", ColorButton(() => _s.TextColor, v => _s.TextColor = v));
            Row(grid, "Outline colour", ColorButton(() => _s.OutlineColor, v => _s.OutlineColor = v));
            Row(grid, "Outline thickness", Number(0, 10, 0.5m, 1, () => (decimal)_s.OutlineWidth, v => _s.OutlineWidth = (float)v));
            Row(grid, "Shadow colour", ColorButton(() => _s.ShadowColor, v => _s.ShadowColor = v));
            Row(grid, "Shadow distance", Number(0, 10, 0.5m, 1, () => (decimal)_s.ShadowDepth, v => _s.ShadowDepth = (float)v));

            var opacity = new TrackBar { Minimum = 10, Maximum = 100, TickFrequency = 10, Value = Clamp(_s.Opacity, 10, 100), Width = 250 };
            opacity.Tag = (Action)(() => opacity.Value = Clamp(_s.Opacity, 10, 100));
            opacity.ValueChanged += (o, e) => { _s.Opacity = opacity.Value; Notify(); };
            Row(grid, "Opacity", opacity);

            Row(grid, "Lines shown", Number(1, 9, 1, 0, () => _s.LinesShown, v => _s.LinesShown = (int)v));

            var align = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            align.Items.AddRange(new object[] { TextAlign.Left, TextAlign.Center, TextAlign.Right });
            align.SelectedItem = _s.Alignment;
            align.Tag = (Action)(() => align.SelectedItem = _s.Alignment);
            align.SelectedIndexChanged += (o, e) => { _s.Alignment = (TextAlign)align.SelectedItem; Notify(); };
            Row(grid, "Alignment", align);

            Row(grid, "Line transition", Combo(new object[] { AnimationStyle.Slide, AnimationStyle.Fade, AnimationStyle.None },
                v => v.Equals(AnimationStyle.None) ? "None (instant)" : v.ToString(), () => _s.Animation, v => _s.Animation = (AnimationStyle)v));
            Row(grid, "Transition length (ms)", Number(100, 1000, 10, 0, () => _s.AnimationMs, v => _s.AnimationMs = (int)v));
            Row(grid, "Easing", Combo(new object[] { EasingKind.EaseOutCubic, EasingKind.Spring, EasingKind.EaseInOutCubic, EasingKind.Linear },
                v => EasingName((EasingKind)v), () => _s.AnimationEasing, v => _s.AnimationEasing = (EasingKind)v));

            Row(grid, "", Check("Lock (click-through; drag/resize only when unlocked)", () => _s.Locked, v => _s.Locked = v));
            Row(grid, "", Check("Show overlay", () => _s.Visible, v => _s.Visible = v));

            Row(grid, "Lyrics sources", Check("LRCLIB (public API, tried first)", () => _s.ProviderLrclib, v => _s.ProviderLrclib = v));
            Row(grid, "", Check("NetEase Cloud Music (unofficial endpoint, synced; opt-in, see README)", () => _s.ProviderNetEase, v => _s.ProviderNetEase = v));
            Row(grid, "", Check("lyrics.ovh (public API, plain text only)", () => _s.ProviderLyricsOvh, v => _s.ProviderLyricsOvh = v));

            _preview = new PreviewPanel { Dock = DockStyle.Fill, Settings = _s, Lines = SampleLines };
            FormClosed += (o, e) => _preview.Stop();
            var previewBox = new GroupBox { Text = "Preview", Dock = DockStyle.Fill, Padding = new Padding(8) };
            previewBox.Controls.Add(_preview);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var reset = new Button { Text = "Reset to defaults", AutoSize = true };
            reset.Click += (o, e) => ResetDefaults();
            buttons.Controls.AddRange(new Control[] { cancel, ok, reset });
            AcceptButton = ok;
            CancelButton = cancel;

            var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            previewHost.Controls.Add(previewBox);
            Controls.Add(previewHost);
            Controls.Add(grid);
            Controls.Add(buttons);

            UpdateFontLabel();
        }

        /// <summary>The settings to restore if the user cancels.</summary>
        public OverlaySettings Original => _original.Clone();

        void Notify()
        {
            if (_loading) return;
            UpdateFontLabel();
            _preview.SettingsChanged();
            Changed?.Invoke(_s.Clone());
        }

        void UpdateFontLabel() =>
            _fontLabel.Text = $"{_s.FontFamily}, {_s.FontSize:0.#} pt{(_s.Bold ? ", bold" : "")}{(_s.Italic ? ", italic" : "")}";

        void PickFont()
        {
            var style = (_s.Bold ? FontStyle.Bold : 0) | (_s.Italic ? FontStyle.Italic : 0);
            Font initial;
            try { initial = new Font(_s.FontFamily, _s.FontSize, style); }
            catch (ArgumentException) { initial = new Font(FontFamily.GenericSansSerif, _s.FontSize, style); }
            using (initial)
            using (var dlg = new FontDialog { Font = initial, ShowEffects = false, FontMustExist = true, MaxSize = 96, MinSize = 8 })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _s.FontFamily = dlg.Font.FontFamily.Name;
                _s.FontSize = dlg.Font.SizeInPoints;
                _s.Bold = dlg.Font.Bold;
                _s.Italic = dlg.Font.Italic;
                Notify();
            }
        }

        void ResetDefaults()
        {
            var d = new OverlaySettings();
            // Keep the window where it is and keep it visible; reset only the look.
            d.Visible = _s.Visible;
            d.X = _s.X; d.Y = _s.Y; d.Width = _s.Width; d.Height = _s.Height;
            _loading = true;
            foreach (var p in typeof(OverlaySettings).GetProperties())
                if (p.CanWrite) p.SetValue(_s, p.GetValue(d));
            foreach (Control c in Controls) RefreshControls(c);
            _loading = false;
            Notify();
        }

        static void RefreshControls(Control c)
        {
            (c.Tag as Action)?.Invoke();
            foreach (Control child in c.Controls) RefreshControls(child);
        }

        // ---------------------------------------------------------------- small control factories
        // Each control stores a "reload from settings" action in Tag, used by Reset to defaults.

        static void Row(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
            grid.Controls.Add(control);
        }

        static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            f.Controls.AddRange(controls);
            return f;
        }

        Control ColorButton(Func<string> get, Action<string> set)
        {
            var b = new Button { Width = 90, Height = 26, FlatStyle = FlatStyle.Flat };
            Action load = () => b.BackColor = Opaque(OverlaySettings.ParseColor(get(), Color.White));
            b.Tag = load;
            load();
            b.Click += (o, e) =>
            {
                var current = OverlaySettings.ParseColor(get(), Color.White);
                using (var dlg = new ColorDialog { Color = Opaque(current), FullOpen = true })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    // ColorDialog has no alpha; keep the colour's existing transparency.
                    set(OverlaySettings.ToHex(Color.FromArgb(current.A == 0 ? 255 : current.A, dlg.Color)));
                    load();
                    Notify();
                }
            };
            return b;
        }

        Control Number(decimal min, decimal max, decimal step, int decimals, Func<decimal> get, Action<decimal> set)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = 80 };
            Action load = () => n.Value = Math.Max(min, Math.Min(max, get()));
            n.Tag = load;
            load();
            n.ValueChanged += (o, e) => { set(n.Value); Notify(); };
            return n;
        }

        Control Combo(object[] items, Func<object, string> name, Func<object> get, Action<object> set)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, FormattingEnabled = true };
            c.Format += (o, e) => e.Value = name(e.ListItem);
            c.Items.AddRange(items);
            Action load = () => c.SelectedItem = get();
            c.Tag = load;
            load();
            c.SelectedIndexChanged += (o, e) => { if (c.SelectedItem != null) { set(c.SelectedItem); Notify(); } };
            return c;
        }

        static string EasingName(EasingKind k)
        {
            switch (k)
            {
                case EasingKind.Spring: return "Spring (critically damped)";
                case EasingKind.EaseInOutCubic: return "Ease in-out";
                case EasingKind.Linear: return "Linear";
                default: return "Ease out";
            }
        }

        Control Check(string text, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true };
            Action load = () => c.Checked = get();
            c.Tag = load;
            load();
            c.CheckedChanged += (o, e) => { set(c.Checked); Notify(); };
            return c;
        }

        static Color Opaque(Color c) => Color.FromArgb(255, c);
        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        /// <summary>
        /// Draws the sample lines over a checkerboard so transparency is visible, and steps to the next
        /// line every couple of seconds using the chosen transition. Frames run only while it moves.
        /// </summary>
        sealed class PreviewPanel : Panel
        {
            const int StepMs = 1800;
            public OverlaySettings Settings;
            public IReadOnlyList<LyricLine> Lines;

            readonly LyricsRenderer _renderer = new LyricsRenderer();
            readonly ScrollAnimator _anim = new ScrollAnimator();
            readonly List<FrameItem> _items = new List<FrameItem>();
            readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            readonly Timer _step = new Timer { Interval = StepMs };
            readonly Timer _frame = new Timer { Interval = 15 };
            Bitmap _layer;
            int _index = 1;

            public PreviewPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                _anim.SnapTo(_index);
                _step.Tick += (o, e) => Guard(Step);
                _frame.Tick += (o, e) => Guard(() =>
                {
                    if (!_anim.IsAnimating(_clock.Elapsed.TotalMilliseconds)) _frame.Stop();
                    Invalidate();
                });
                _step.Start();
            }

            public void SettingsChanged()
            {
                _renderer.ResetSettingsCache();
                if (Settings.Animation == AnimationStyle.None) _anim.SnapTo(_index);
                Invalidate();
            }

            public void Stop()
            {
                _step.Stop();
                _frame.Stop();
            }

            void Step()
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                _anim.DurationMs = Math.Max(50, Settings.AnimationMs);
                _anim.Easing = Settings.AnimationEasing;
                int next = (_index + 1) % Lines.Count;
                bool animate = LineTransition.ShouldAnimate(Settings.Animation, _index, next, seek: false);
                _index = next;
                if (!animate) _anim.SnapTo(next); // also the wrap-around back to the first line
                else if (Settings.Animation == AnimationStyle.Fade) _anim.CrossTo(next, now);
                else _anim.AnimateTo(next, now);
                if (animate) _frame.Start();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var checker = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(70, 70, 76), Color.FromArgb(110, 110, 118)))
                    e.Graphics.FillRectangle(checker, ClientRectangle);

                double now = _clock.Elapsed.TotalMilliseconds;
                if (Settings.Animation == AnimationStyle.Fade && _anim.IsAnimating(now))
                    SyncedLayout.Fade(Lines, (int)_anim.From, (int)_anim.Target, _anim.ProgressAt(now), Settings.LinesShown, _items);
                else
                    SyncedLayout.Slide(Lines, Settings.Animation == AnimationStyle.Fade ? _anim.Target : _anim.ValueAt(now), Settings.LinesShown, _items);

                // Render to an ARGB layer first so overall opacity applies exactly as in the overlay.
                int w = Math.Max(1, Width), h = Math.Max(1, Height);
                if (_layer == null || _layer.Width != w || _layer.Height != h)
                {
                    _layer?.Dispose();
                    _layer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                }
                using (var g = Graphics.FromImage(_layer))
                {
                    g.Clear(Color.Transparent);
                    _renderer.DrawFrame(g, new RectangleF(0, 0, w, h), Settings, _items, SyncedLayout.WindowHeight(Settings.LinesShown));
                }
                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                {
                    var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Max(10, Settings.Opacity) / 100f };
                    attrs.SetColorMatrix(cm);
                    e.Graphics.DrawImage(_layer, ClientRectangle, 0, 0, w, h, GraphicsUnit.Pixel, attrs);
                }
            }

            void Guard(Action a)
            {
                try { a(); }
                catch (Exception ex) { Stop(); FileLog.Error("settings preview", ex); }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Stop();
                    _step.Dispose();
                    _frame.Dispose();
                    _renderer.Dispose();
                    _layer?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
