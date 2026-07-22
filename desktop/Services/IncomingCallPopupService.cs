using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Jodu.Desktop.Services;

/// <summary>
/// Incoming call popup with Answer / Decline — Nothing OS inspired.
/// </summary>
public sealed class IncomingCallPopupService : IDisposable
{
    private readonly object _gate = new();
    private readonly SynchronizationContext? _ui;
    private IncomingCallCardForm? _card;
    private bool _disposed;

    public Action? PlayTone { get; set; }
    public Action? StopTone { get; set; }
    public Action? AnswerRequested { get; set; }
    public Action? DeclineRequested { get; set; }

    private const int CardWidth = 360;
    private const int Margin = 16;

    public IncomingCallPopupService()
    {
        _ui = SynchronizationContext.Current;
    }

    public void ShowIncoming(string? displayName, string? number)
    {
        if (_disposed) return;
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var num = string.IsNullOrWhiteSpace(number) ? null : number.Trim();
        if (name is null && num is null)
        {
            name = "Incoming call";
        }

        RunOnUi(() =>
        {
            lock (_gate)
            {
                if (_card is not null && !_card.IsDisposed)
                {
                    _card.UpdateCaller(name, num);
                    LayoutCard();
                    // Already ringing — keep looping tone; don't restart.
                    return;
                }

                var card = new IncomingCallCardForm(name, num);
                card.AnswerClicked += () =>
                {
                    StopTone?.Invoke();
                    AnswerRequested?.Invoke();
                };
                card.DeclineClicked += () =>
                {
                    StopTone?.Invoke();
                    DeclineRequested?.Invoke();
                    Dismiss();
                };
                card.FormClosed += (_, _) =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_card, card))
                            _card = null;
                    }
                    StopTone?.Invoke();
                };

                _card = card;
                LayoutCard();
                card.Show();
                card.BeginFadeIn();
                PlayTone?.Invoke();
            }
        });
    }

    public void Dismiss()
    {
        StopTone?.Invoke();
        RunOnUi(() =>
        {
            lock (_gate)
            {
                var card = _card;
                _card = null;
                if (card is null || card.IsDisposed) return;
                card.BeginFadeOut(() =>
                {
                    try
                    {
                        if (!card.IsDisposed) card.Close();
                    }
                    catch { /* ignore */ }
                });
            }
        });
    }

    private void LayoutCard()
    {
        if (_card is null || _card.IsDisposed) return;
        var screen = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1280, 800);
        var x = screen.Right - CardWidth - Margin;
        var y = screen.Top + Margin;
        _card.SetCardBounds(x, y, CardWidth, _card.PreferredCardHeight);
    }

    private void RunOnUi(Action action)
    {
        if (_ui is not null)
            _ui.Post(_ => action(), null);
        else if (Application.OpenForms.Count > 0)
            Application.OpenForms[0]!.BeginInvoke(action);
        else
            action();
    }

    public void Dispose()
    {
        _disposed = true;
        StopTone?.Invoke();
        lock (_gate)
        {
            try
            {
                if (_card is not null && !_card.IsDisposed)
                    _card.Close();
            }
            catch { /* ignore */ }
            _card = null;
        }
    }
}

internal sealed class IncomingCallCardForm : Form
{
    private static readonly Color SurfaceAlt = Color.FromArgb(255, 17, 17, 17);
    private static readonly Color Fg = Color.FromArgb(245, 245, 245);
    private static readonly Color Muted = Color.FromArgb(136, 136, 136);
    private static readonly Color Tertiary = Color.FromArgb(85, 85, 85);
    private static readonly Color Line = Color.FromArgb(255, 30, 30, 30);
    private static readonly Color Accent = Color.FromArgb(45, 212, 191);
    private static readonly Color Danger = Color.FromArgb(232, 17, 35);

    private const int CornerRadius = 6;
    private const int Pad = 16;
    private const int BtnH = 36;
    private const int BtnGap = 10;

    private string? _name;
    private string? _number;
    private readonly Font _mono;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _btnFont;
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 16 };
    private Rectangle _answerRect;
    private Rectangle _declineRect;
    private string _hoverBtn = "";

    private byte _alpha;
    private byte _alphaTarget = 247;
    private Action? _onFadeDone;
    private Bitmap? _frame;

    public event Action? AnswerClicked;
    public event Action? DeclineClicked;

    public int PreferredCardHeight { get; private set; }

    public IncomingCallCardForm(string? displayName, string? number)
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

        _name = displayName;
        _number = number;

        _mono = TryFont(new[] { "IBM Plex Mono", "Cascadia Mono", "Consolas" }, 7.5f, FontStyle.Regular);
        _titleFont = TryFont(new[] { "Segoe UI Semibold", "Segoe UI" }, 14f, FontStyle.Bold);
        _bodyFont = TryFont(new[] { "Segoe UI", "Arial" }, 9.5f, FontStyle.Regular);
        _btnFont = TryFont(new[] { "IBM Plex Mono", "Consolas" }, 8.5f, FontStyle.Regular);

        Width = 360;
        RecalculateLayout();

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
            var hover = _answerRect.Contains(e.Location) ? "answer"
                : _declineRect.Contains(e.Location) ? "decline"
                : "";
            Cursor = hover.Length > 0 ? Cursors.Hand : Cursors.Default;
            if (hover == _hoverBtn) return;
            _hoverBtn = hover;
            RebuildFrame();
            Present();
        };

        MouseLeave += (_, _) =>
        {
            if (_hoverBtn.Length == 0) return;
            _hoverBtn = "";
            Cursor = Cursors.Default;
            RebuildFrame();
            Present();
        };

        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_answerRect.Contains(e.Location)) AnswerClicked?.Invoke();
            else if (_declineRect.Contains(e.Location)) DeclineClicked?.Invoke();
        };

        HandleCreated += (_, _) =>
        {
            RebuildFrame();
            Present();
        };
        LocationChanged += (_, _) => Present();
    }

    public void UpdateCaller(string? displayName, string? number)
    {
        _name = displayName;
        _number = number;
        RecalculateLayout();
        RebuildFrame();
        Present();
    }

    private void RecalculateLayout()
    {
        var hasSecondary = !string.IsNullOrWhiteSpace(_number) &&
            !string.Equals(_number, _name, StringComparison.OrdinalIgnoreCase);
        PreferredCardHeight = Pad + 18 + 28 + (hasSecondary ? 20 : 4) + 12 + BtnH + Pad;
        Height = PreferredCardHeight;
        var btnW = (Width - Pad * 2 - BtnGap) / 2;
        var btnY = Height - Pad - BtnH;
        _declineRect = new Rectangle(Pad, btnY, btnW, BtnH);
        _answerRect = new Rectangle(Pad + btnW + BtnGap, btnY, btnW, BtnH);
    }

    public void SetCardBounds(int x, int y, int width, int height)
    {
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

        // Inset slightly so anti-aliased edge pixels aren't clipped by the layered window.
        var bounds = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
        using (var path = RoundedRect(bounds, CornerRadius))
        {
            using var fill = new SolidBrush(SurfaceAlt);
            g.FillPath(fill, path);
            using var border = new Pen(Accent, 1f) { Alignment = PenAlignment.Inset };
            g.DrawPath(border, path);
        }

        using var mutedBrush = new SolidBrush(Tertiary);
        using var secondaryBrush = new SolidBrush(Muted);
        using var fgBrush = new SolidBrush(Fg);
        using var accentBrush = new SolidBrush(Accent);

        g.FillRectangle(accentBrush, Pad, Pad + 3f, 6f, 6f);
        g.DrawString("INCOMING CALL", _mono, mutedBrush,
            new RectangleF(Pad + 14f, Pad, Width - Pad * 2 - 14f, 14),
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

        var title = !string.IsNullOrWhiteSpace(_name) ? _name!
            : !string.IsNullOrWhiteSpace(_number) ? _number!
            : "Unknown caller";
        g.DrawString(title, _titleFont, fgBrush,
            new RectangleF(Pad, Pad + 20f, Width - Pad * 2, 26),
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

        if (!string.IsNullOrWhiteSpace(_number) &&
            !string.Equals(_number, _name, StringComparison.OrdinalIgnoreCase))
        {
            g.DrawString(_number, _bodyFont, secondaryBrush,
                new RectangleF(Pad, Pad + 48f, Width - Pad * 2, 18),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }

        DrawButton(g, _declineRect, "DECLINE", Danger, _hoverBtn == "decline");
        DrawButton(g, _answerRect, "ANSWER", Accent, _hoverBtn == "answer", filled: true);
    }

    private void DrawButton(Graphics g, Rectangle rect, string label, Color color, bool hover, bool filled = false)
    {
        var bounds = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1f, rect.Height - 1f);
        using var path = RoundedRect(bounds, 4f);
        if (filled)
        {
            var fillColor = hover ? ControlPaint.Light(color) : color;
            using var brush = new SolidBrush(fillColor);
            g.FillPath(brush, path);
            using var textBrush = new SolidBrush(Color.FromArgb(10, 10, 10));
            DrawCentered(g, label, rect, textBrush);
        }
        else
        {
            if (hover)
            {
                using var brush = new SolidBrush(Color.FromArgb(40, color));
                g.FillPath(brush, path);
            }
            using var pen = new Pen(color, 1f) { Alignment = PenAlignment.Center };
            g.DrawPath(pen, path);
            using var textBrush = new SolidBrush(color);
            DrawCentered(g, label, rect, textBrush);
        }
    }

    private void DrawCentered(Graphics g, string text, Rectangle rect, Brush brush)
    {
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, _btnFont, brush, rect, format);
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
            UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Font TryFont(string[] families, float size, FontStyle style)
    {
        foreach (var family in families)
        {
            try { return new Font(family, size, style, GraphicsUnit.Point); }
            catch { /* next */ }
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

    private static GraphicsPath RoundedRect(Rectangle bounds, float radius) =>
        RoundedRect(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

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
            _mono.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _btnFont.Dispose();
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
        IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

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
