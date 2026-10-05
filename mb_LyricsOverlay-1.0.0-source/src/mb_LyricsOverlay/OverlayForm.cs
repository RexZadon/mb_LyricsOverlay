using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LyricsOverlay.Core;
using static MusicBeePlugin.NativeMethods;

namespace MusicBeePlugin
{
    /// <summary>
    /// Borderless, always-on-top layered window drawn with UpdateLayeredWindow (per-pixel alpha, no
    /// colour-key fringes). Locked: WS_EX_TRANSPARENT makes it click-through. Unlocked: a faint
    /// panel is drawn so every pixel is hit-testable, and WM_NCHITTEST turns it into a caption plus
    /// resize edges.
    /// </summary>
    public sealed class OverlayForm : Form
    {
        const int Grip = 8;
        OverlaySettings _settings;
        IList<DisplayLine> _lines = new List<DisplayLine>();
        IList<FrameItem> _frame;          // when set, drawn instead of _lines (animated synced lyrics)
        float _frameHeightEm;
        readonly LyricsRenderer _renderer = new LyricsRenderer();

        // Persistent drawing surface: a 32-bit premultiplied DIB section selected into a memory DC, with
        // a GDI+ Bitmap and Graphics over the same pixels. Recreated only when the size changes, so a
        // frame costs no allocations and no bitmap copy before UpdateLayeredWindow.
        IntPtr _memDc, _dib, _oldBitmap;
        Bitmap _surface;
        Graphics _g;
        Size _surfaceSize;

        // Edit-mode panel, cached per size.
        GraphicsPath _framePath;
        Size _framePathSize;
        readonly SolidBrush _frameFill = new SolidBrush(Color.FromArgb(70, 20, 20, 24));
        readonly Pen _framePen = new Pen(Color.FromArgb(140, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };

        /// <summary>User finished dragging or resizing.</summary>
        public event EventHandler BoundsCommitted;
        /// <summary>Mouse wheel while unlocked (used to scroll unsynced lyrics). Arg: +1 down, -1 up.</summary>
        public event Action<int> ScrollRequested;

        public OverlayForm(OverlaySettings settings)
        {
            _settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            Text = "Lyrics Overlay";
            MinimumSize = new Size(200, 60);
            Bounds = settings.GetBounds();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                if (_settings != null && _settings.Locked) cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ApplySettings(OverlaySettings settings)
        {
            bool lockChanged = settings.Locked != _settings.Locked;
            _settings = settings;
            if (lockChanged && IsHandleCreated)
            {
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                ex = settings.Locked ? ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
                SetWindowLong(Handle, GWL_EXSTYLE, ex);
            }
            Redraw();
        }

        public void SetLines(IList<DisplayLine> lines)
        {
            _lines = lines ?? new List<DisplayLine>();
            _frame = null;
            Redraw();
        }

        /// <summary>Draws positioned lines (see <see cref="SyncedLayout"/>). The list is read, not copied.</summary>
        public void SetFrame(IList<FrameItem> items, float heightEm)
        {
            _frame = items;
            _frameHeightEm = heightEm;
            Redraw();
        }

        /// <summary>New lyrics: drop cached glyph outlines of the old ones.</summary>
        public void ClearTextCache() => _renderer.ClearTextCache();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Redraw();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Redraw();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_settings.Locked) ScrollRequested?.Invoke(e.Delta > 0 ? -1 : 1);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCHITTEST:
                    m.Result = (IntPtr)HitTest(m.LParam);
                    return;
                case WM_NCLBUTTONDBLCLK:
                    return; // a caption double-click would otherwise maximise the window
                case WM_MOUSEACTIVATE when _settings.Locked:
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
                case WM_EXITSIZEMOVE:
                    base.WndProc(ref m);
                    BoundsCommitted?.Invoke(this, EventArgs.Empty);
                    return;
            }
            base.WndProc(ref m);
        }

        int HitTest(IntPtr lParam)
        {
            if (_settings.Locked) return HTTRANSPARENT;
            int v = unchecked((int)lParam.ToInt64());
            var p = PointToClient(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF)));
            bool l = p.X < Grip, r = p.X >= Width - Grip, t = p.Y < Grip, b = p.Y >= Height - Grip;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (b && l) return HTBOTTOMLEFT;
            if (b && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (b) return HTBOTTOM;
            return HTCAPTION;
        }

        /// <summary>Renders into the persistent premultiplied surface and pushes it to the layered window.</summary>
        public void Redraw()
        {
            if (!IsHandleCreated || IsDisposed || Width <= 0 || Height <= 0) return;
            if (!EnsureSurface()) return;

            _g.ResetTransform();
            _g.Clear(Color.Transparent);
            if (!_settings.Locked) DrawEditFrame(_g);
            var area = new RectangleF(0, 0, Width, Height);
            if (_frame != null) _renderer.DrawFrame(_g, area, _settings, _frame, _frameHeightEm);
            else _renderer.Draw(_g, area, _settings, _lines);
            _g.Flush(FlushIntention.Sync);
            GdiFlush();
            Push();
        }

        bool EnsureSurface()
        {
            if (_g != null && _surfaceSize == Size) return true;
            FreeSurface();
            int w = Width, h = Height;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = w,
                    biHeight = -h, // top-down rows, matching GDI+'s layout
                    biPlanes = 1,
                    biBitCount = 32,
                };
                _dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero) return false;
                _memDc = CreateCompatibleDC(screenDc);
                _oldBitmap = SelectObject(_memDc, _dib);
                _surface = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                _g = Graphics.FromImage(_surface);
                _surfaceSize = new Size(w, h);
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        void FreeSurface()
        {
            _g?.Dispose();
            _g = null;
            _surface?.Dispose();
            _surface = null;
            if (_memDc != IntPtr.Zero)
            {
                SelectObject(_memDc, _oldBitmap);
                DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_dib != IntPtr.Zero)
            {
                DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }
            _surfaceSize = Size.Empty;
        }

        void DrawEditFrame(Graphics g)
        {
            // Visible only while unlocked so the window can be found, grabbed and resized.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_framePath == null || _framePathSize != Size)
            {
                _framePath?.Dispose();
                _framePath = RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10);
                _framePathSize = Size;
            }
            g.FillPath(_frameFill, _framePath);
            g.DrawPath(_framePen, _framePath);
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Push()
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                // The DIB holds premultiplied 32-bit pixels, as ULW_ALPHA expects.
                var size = new SIZE(_surfaceSize.Width, _surfaceSize.Height);
                var src = new POINT(0, 0);
                var dst = new POINT(Left, Top);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    SourceConstantAlpha = (byte)(Math.Max(10, Math.Min(100, _settings.Opacity)) * 255 / 100),
                    AlphaFormat = AC_SRC_ALPHA,
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _renderer.Dispose();
                _framePath?.Dispose();
                _frameFill.Dispose();
                _framePen.Dispose();
                FreeSurface();
            }
            base.Dispose(disposing);
        }
    }
}
