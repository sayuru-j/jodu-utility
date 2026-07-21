using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Jodu.Desktop.Services;

/// <summary>
/// Stacked right-edge phone notification popups — Nothing OS inspired.
/// </summary>
public sealed class NotificationPopupService : IDisposable
{
    private readonly List<NotificationCardForm> _cards = new();
    private readonly object _gate = new();
    private readonly SynchronizationContext? _ui;
    private bool _disposed;

    /// <summary>Optional tone played each time a toast is shown.</summary>
    public Action? PlayTone { get; set; }

    private const int CardWidth = 356;
    private const int Margin = 16;
    private const int Gap = 10;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan HoldWithImage = TimeSpan.FromSeconds(12);

    public NotificationPopupService()
    {
        _ui = SynchronizationContext.Current;
    }

    public void ShowPhoneNotification(string appName, string? title, string? body, string? imageBase64 = null)
    {
        if (_disposed) return;
        var heading = string.IsNullOrWhiteSpace(appName) ? "Phone" : appName.Trim();
        var line1 = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var line2 = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        Image? thumb = TryDecodeImage(imageBase64);
        if (line1 is null && line2 is null && thumb is null) return;

        void Show()
        {
            lock (_gate)
            {
                while (_cards.Count >= 5)
                {
                    var oldest = _cards[0];
                    _cards.RemoveAt(0);
                    TryClose(oldest);
                }

                var card = new NotificationCardForm(heading, line1, line2, thumb);
                PlayTone?.Invoke();
                card.DismissRequested += () =>
                {
                    card.BeginFadeOut(() =>
                    {
                        TryClose(card);
                        Dismiss(card);
                    });
                };
                card.FormClosed += (_, _) => Dismiss(card);
                _cards.Add(card);
                LayoutCards();
                card.Show();
                card.BeginFadeIn();

                var hold = thumb is null ? Hold : HoldWithImage;
                var timer = new System.Windows.Forms.Timer { Interval = (int)hold.TotalMilliseconds };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    card.BeginFadeOut(() =>
                    {
                        TryClose(card);
                        Dismiss(card);
                    });
                };
                timer.Start();
            }
        }

        if (_ui is not null)
            _ui.Post(_ => Show(), null);
        else if (Application.OpenForms.Count > 0)
            Application.OpenForms[0]!.BeginInvoke(Show);
        else
            Show();
    }

    private static Image? TryDecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var raw = base64.Trim();
            var comma = raw.IndexOf(',');
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                raw = raw[(comma + 1)..];

            var bytes = Convert.FromBase64String(raw);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch
        {
            return null;
        }
    }

    private void Dismiss(NotificationCardForm? card)
    {
        lock (_gate)
        {
            if (card is not null)
                _cards.Remove(card);
            LayoutCards();
        }
    }

    private void LayoutCards()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1280, 800);

        var y = screen.Top + Margin;
        foreach (var card in _cards.ToArray())
        {
            if (card.IsDisposed) continue;
            var x = screen.Right - CardWidth - Margin;
            card.SetCardBounds(x, y, CardWidth, card.PreferredCardHeight);
            y += card.PreferredCardHeight + Gap;
        }
    }

    private static void TryClose(NotificationCardForm card)
    {
        try
        {
            if (!card.IsDisposed)
                card.Close();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (var card in _cards.ToArray())
                TryClose(card);
            _cards.Clear();
        }
    }
}

/// <summary>
/// Layered Win32 toast with per-pixel alpha so rounded corners are truly anti-aliased.
/// </summary>
internal sealed class NotificationCardForm : Form
{
    private static readonly Color Bg = Color.FromArgb(245, 10, 10, 10);
    private static readonly Color Fg = Color.FromArgb(242, 242, 242);
    private static readonly Color Muted = Color.FromArgb(122, 122, 122);
    private static readonly Color Line = Color.FromArgb(200, 55, 55, 55);

    private const int CornerRadius = 18;
    private const int Pad = 14;
    private const int ThumbSize = 52;
    private const int ThumbLeft = 14;
    private const int CloseHit = 28;

    private readonly string _app;
    private readonly string? _title;
    private readonly string? _body;
    private readonly Image? _ownedImage;
    private readonly Bitmap? _circleImage;
    private readonly Font _mono;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _closeFont;
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 16 };
    private readonly Rectangle _closeRect;

    private byte _alpha;
    private byte _alphaTarget = 247;
    private Action? _onFadeDone;
    private bool _closeHover;
    private Bitmap? _frame;

    public event Action? DismissRequested;

    public int PreferredCardHeight { get; }

    public NotificationCardForm(string app, string? title, string? body, Image? thumbnail)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ShowIcon = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;

        _app = app.ToUpperInvariant();
        _title = title;
        _body = body;
        _ownedImage = thumbnail;

        _mono = TryFont(new[] { "Cascadia Mono", "Consolas", "Courier New" }, 7.5f, FontStyle.Regular);
        _titleFont = TryFont(new[] { "Segoe UI Semibold", "Segoe UI" }, 10.5f, FontStyle.Bold);
        _bodyFont = TryFont(new[] { "Segoe UI", "Arial" }, 8.75f, FontStyle.Regular);
        _closeFont = TryFont(new[] { "Segoe UI", "Arial" }, 11f, FontStyle.Regular);

        var hasThumb = thumbnail is not null;
        var bodyHeight = body is null ? 0 : EstimateBodyHeight(body);
        var textBlock = 14 + (title is null ? 0 : 20) + (body is null ? 0 : bodyHeight + 2);
        var contentHeight = Math.Max(textBlock, hasThumb ? ThumbSize : 0);
        PreferredCardHeight = Pad + contentHeight + Pad;
        if (hasThumb)
            PreferredCardHeight = Math.Max(PreferredCardHeight, Pad + ThumbSize + Pad);

        Width = 356;
        Height = PreferredCardHeight;
        _closeRect = new Rectangle(Width - CloseHit - 6, 6, CloseHit, CloseHit);

        if (hasThumb && thumbnail is not null)
            _circleImage = MakeCircleCover(thumbnail, ThumbSize);

        _fadeTimer.Tick += (_, _) =>
        {
            var step = (byte)12;
            if (Math.Abs(_alpha - _alphaTarget) <= step)
            {
                _alpha = _alphaTarget;
                _fadeTimer.Stop();
                Present();
                var done = _onFadeDone;
                _onFadeDone = null;
                done?.Invoke();
                return;
            }

            _alpha = (byte)(_alpha + Math.Sign(_alphaTarget - _alpha) * step);
            Present();
        };

        MouseMove += (_, e) =>
        {
            var hover = _closeRect.Contains(e.Location);
            if (hover == _closeHover) return;
            _closeHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            RebuildFrame();
            Present();
        };

        MouseLeave += (_, _) =>
        {
            if (!_closeHover) return;
            _closeHover = false;
            Cursor = Cursors.Default;
            RebuildFrame();
            Present();
        };

        MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _closeRect.Contains(e.Location))
                DismissRequested?.Invoke();
        };

        HandleCreated += (_, _) =>
        {
            RebuildFrame();
            Present();
        };

        LocationChanged += (_, _) => Present();
    }

    public void SetCardBounds(int x, int y, int width, int height)
    {
        // Width/height are fixed for a card; reposition and re-blit layered buffer.
        SetBounds(x, y, Width, Height);
        Present();
    }

    public void BeginFadeIn()
    {
        _alpha = 0;
        _alphaTarget = 247;
        Present();
        _fadeTimer.Start();
    }

    public void BeginFadeOut(Action? then = null)
    {
        _onFadeDone = then;
        _alphaTarget = 0;
        _fadeTimer.Start();
    }

    private void RebuildFrame()
    {
        _frame?.Dispose();
        _frame = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(_frame);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = RoundedRect(bounds, CornerRadius))
        {
            using var fill = new SolidBrush(Bg);
            g.FillPath(fill, path);
            using var border = new Pen(Line, 1f);
            g.DrawPath(border, path);
        }

        var thumbTop = (Height - ThumbSize) / 2;
        var textLeft = _circleImage is null ? Pad : ThumbLeft + ThumbSize + 12;
        var textWidth = Width - textLeft - Pad - CloseHit;

        if (_circleImage is not null)
        {
            g.DrawImage(_circleImage, ThumbLeft, thumbTop, ThumbSize, ThumbSize);
            using var ring = new Pen(Color.FromArgb(220, 220, 220), 1.25f);
            g.DrawEllipse(ring, ThumbLeft + 0.5f, thumbTop + 0.5f, ThumbSize - 1.5f, ThumbSize - 1.5f);
        }

        using var mutedBrush = new SolidBrush(Muted);
        using var fgBrush = new SolidBrush(Fg);

        g.DrawString(_app, _mono, mutedBrush, new RectangleF(textLeft, Pad, Math.Max(40, textWidth), 14),
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

        var y = Pad + 16f;
        if (!string.IsNullOrEmpty(_title))
        {
            g.DrawString(_title, _titleFont, fgBrush, new RectangleF(textLeft, y, textWidth, 20),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            y += 22;
        }

        if (!string.IsNullOrEmpty(_body))
        {
            var bodyHeight = EstimateBodyHeight(_body);
            g.DrawString(_body, _bodyFont, mutedBrush, new RectangleF(textLeft, y, textWidth, bodyHeight),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
        }

        if (_closeHover)
        {
            using var hover = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
            g.FillEllipse(hover, _closeRect);
        }

        var closeFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("×", _closeFont, mutedBrush, _closeRect, closeFormat);
    }

    private void Present()
    {
        if (!IsHandleCreated || _frame is null || IsDisposed) return;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = _frame.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new Size(_frame.Width, _frame.Height);
            var pointSource = new Point(0, 0);
            var topPos = new Point(Left, Top);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = _alpha,
                AlphaFormat = AcSrcAlpha
            };

            UpdateLayeredWindow(
                Handle,
                screenDc,
                ref topPos,
                ref size,
                memDc,
                ref pointSource,
                0,
                ref blend,
                UlwAlpha);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Bitmap MakeCircleCover(Image src, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        using (var path = new GraphicsPath())
        {
            path.AddEllipse(0.5f, 0.5f, size - 1.5f, size - 1.5f);
            g.SetClip(path);
        }

        var scale = Math.Max((float)size / src.Width, (float)size / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        var x = (size - w) / 2f;
        var y = (size - h) / 2f;
        g.DrawImage(src, x, y, w, h);
        return bmp;
    }

    private static Font TryFont(string[] families, float size, FontStyle style)
    {
        foreach (var family in families)
        {
            try
            {
                return new Font(family, size, style, GraphicsUnit.Point);
            }
            catch
            {
                // try next
            }
        }

        return new Font(SystemFonts.MessageBoxFont!.FontFamily, size, style, GraphicsUnit.Point);
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static int EstimateBodyHeight(string text)
    {
        var lines = Math.Clamp((text.Length / 40) + 1, 1, 3);
        return 16 * lines + 2;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoactivate = 0x08000000;
            const int WsExToolwindow = 0x00000080;
            const int WsExLayered = 0x00080000;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoactivate | WsExToolwindow | WsExLayered;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Dispose();
            _frame?.Dispose();
            _circleImage?.Dispose();
            _ownedImage?.Dispose();
            _mono.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _closeFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref Point pptDst,
        ref Size psize,
        IntPtr hdcSrc,
        ref Point pptSrc,
        int crKey,
        ref BlendFunction pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
