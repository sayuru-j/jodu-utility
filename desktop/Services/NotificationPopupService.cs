using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Jodu.Desktop.Services;

/// <summary>
/// Stacked right-edge phone notification popups — one bubble per app, updates in place.
/// </summary>
public sealed class NotificationPopupService : IDisposable
{
    private readonly List<CardEntry> _cards = new();
    private readonly Dictionary<string, CardEntry> _byStackKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly SynchronizationContext? _ui;
    private bool _disposed;

    /// <summary>Optional tone played when a toast first appears or stacks.</summary>
    public Action? PlayTone { get; set; }

    private const int MaxVisible = 5;
    private const int CardWidth = 356;
    private const int Margin = 16;
    private const int Gap = 10;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan HoldWithImage = TimeSpan.FromSeconds(12);

    private sealed class CardEntry
    {
        public required string StackKey { get; init; }
        public required NotificationCardForm Card { get; init; }
        public System.Windows.Forms.Timer? DismissTimer { get; set; }
        public int StackCount { get; set; } = 1;
        public bool HoverPaused { get; set; }
    }

    public NotificationPopupService()
    {
        _ui = SynchronizationContext.Current;
    }

    public void ShowPhoneNotification(
        string appName,
        string? title,
        string? body,
        string? imageBase64 = null,
        string? stackKey = null)
    {
        if (_disposed) return;
        var heading = string.IsNullOrWhiteSpace(appName) ? "Phone" : appName.Trim();
        var line1 = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var line2 = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        if (line1 is null && line2 is null && string.IsNullOrWhiteSpace(imageBase64)) return;

        var key = string.IsNullOrWhiteSpace(stackKey) ? heading : stackKey.Trim();
        RunOnUi(() => PresentOrStack(key, heading, line1, line2, imageBase64));
    }

    private void PresentOrStack(
        string stackKey,
        string appLabel,
        string? title,
        string? body,
        string? imageBase64)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_byStackKey.TryGetValue(stackKey, out var existing))
            {
                existing.StackCount++;
                var thumb = TryDecodeImage(imageBase64);
                existing.Card.PushMessage(title, body, thumb);
                _cards.Remove(existing);
                _cards.Insert(0, existing);
                LayoutCards();
                if (!existing.HoverPaused)
                    ResetDismissTimer(existing);
                PlayTone?.Invoke();
                return;
            }

            while (_cards.Count >= MaxVisible)
                RemoveEntry(_cards[^1]);

            var image = TryDecodeImage(imageBase64);
            var card = new NotificationCardForm(appLabel, title, body, image);
            var entry = new CardEntry { StackKey = stackKey, Card = card, StackCount = 1 };
            _byStackKey[stackKey] = entry;

            card.SizeChangedRequested += () =>
            {
                lock (_gate) LayoutCards();
            };
            card.HoverChanged += hovering =>
            {
                lock (_gate)
                {
                    if (!_byStackKey.TryGetValue(stackKey, out var current) ||
                        !ReferenceEquals(current.Card, card))
                        return;

                    current.HoverPaused = hovering;
                    if (hovering)
                    {
                        current.DismissTimer?.Stop();
                    }
                    else
                    {
                        ResetDismissTimer(current);
                    }
                    LayoutCards();
                }
            };
            card.DismissRequested += () =>
            {
                card.BeginFadeOut(() =>
                {
                    lock (_gate)
                    {
                        if (_byStackKey.TryGetValue(stackKey, out var current) && ReferenceEquals(current.Card, card))
                            RemoveEntry(current);
                    }
                });
            };
            card.FormClosed += (_, _) =>
            {
                lock (_gate)
                {
                    if (_byStackKey.TryGetValue(stackKey, out var current) && ReferenceEquals(current.Card, card))
                        RemoveEntry(current);
                }
            };

            _cards.Insert(0, entry);
            LayoutCards();
            card.Show();
            card.BeginFadeIn();
            ResetDismissTimer(entry);
            PlayTone?.Invoke();
        }
    }

    private void ResetDismissTimer(CardEntry entry)
    {
        entry.DismissTimer?.Stop();
        entry.DismissTimer?.Dispose();
        entry.DismissTimer = null;
        if (entry.HoverPaused) return;

        var hold = entry.Card.HasThumbnail ? HoldWithImage : Hold;
        var timer = new System.Windows.Forms.Timer { Interval = (int)hold.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (entry.HoverPaused) return;
            entry.Card.BeginFadeOut(() =>
            {
                lock (_gate)
                {
                    if (_byStackKey.TryGetValue(entry.StackKey, out var current) &&
                        ReferenceEquals(current.Card, entry.Card))
                    {
                        RemoveEntry(current);
                    }
                }
            });
        };
        timer.Start();
        entry.DismissTimer = timer;
    }

    private void RemoveEntry(CardEntry entry)
    {
        entry.DismissTimer?.Stop();
        entry.DismissTimer?.Dispose();
        entry.DismissTimer = null;
        _byStackKey.Remove(entry.StackKey);
        _cards.Remove(entry);
        TryClose(entry.Card);
        LayoutCards();
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

    private void LayoutCards()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1280, 800);

        var y = screen.Top + Margin;
        foreach (var entry in _cards.ToArray())
        {
            var card = entry.Card;
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
            foreach (var entry in _cards.ToArray())
                RemoveEntry(entry);
        }
    }
}

/// <summary>
/// Layered Win32 toast with per-pixel alpha so rounded corners are truly anti-aliased.
/// </summary>
internal sealed class NotificationCardForm : Form
{
    private static readonly Color SurfaceAlt = Color.FromArgb(255, 17, 17, 17);
    private static readonly Color Fg = Color.FromArgb(245, 245, 245);
    private static readonly Color Muted = Color.FromArgb(136, 136, 136);
    private static readonly Color Tertiary = Color.FromArgb(85, 85, 85);
    private static readonly Color Line = Color.FromArgb(255, 30, 30, 30);

    private const int CornerRadius = 6;
    private const int Pad = 14;
    private const int ThumbSize = 52;
    private const int ThumbLeft = 14;
    private const int CloseHit = 28;
    private const int MaxHistory = 20;
    private const int MaxExpandedItems = 8;

    private readonly string _app;
    private readonly List<HistoryItem> _history = new();
    private Image? _ownedImage;
    private Bitmap? _circleImage;
    private readonly Font _mono;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _closeFont;
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 16 };
    private Rectangle _closeRect;

    private byte _alpha;
    private byte _alphaTarget = 247;
    private Action? _onFadeDone;
    private bool _closeHover;
    private bool _expanded;
    private bool _pointerInside;
    private Bitmap? _frame;

    private sealed record HistoryItem(string? Title, string? Body);

    public event Action? DismissRequested;
    public event Action? SizeChangedRequested;
    public event Action<bool>? HoverChanged;

    public int PreferredCardHeight { get; private set; }

    public bool HasThumbnail => _circleImage is not null;

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
        _history.Add(new HistoryItem(title, body));
        _ownedImage = thumbnail;

        _mono = TryFont(new[] { "IBM Plex Mono", "Cascadia Mono", "Consolas" }, 7.5f, FontStyle.Regular);
        _titleFont = TryFont(new[] { "Segoe UI Semibold", "Segoe UI" }, 10.5f, FontStyle.Bold);
        _bodyFont = TryFont(new[] { "Segoe UI", "Arial" }, 8.75f, FontStyle.Regular);
        _closeFont = TryFont(new[] { "Segoe UI", "Arial" }, 11f, FontStyle.Regular);

        Width = 356;
        ApplyThumbnail(thumbnail);
        RecalculateHeight();
        _closeRect = new Rectangle(Width - CloseHit - 6, 6, CloseHit, CloseHit);

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

        MouseEnter += (_, _) => SetPointerInside(true);
        MouseLeave += (_, _) =>
        {
            if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
            _closeHover = false;
            Cursor = Cursors.Default;
            SetPointerInside(false);
        };

        MouseMove += (_, e) =>
        {
            SetPointerInside(true);
            var hover = _closeRect.Contains(e.Location);
            if (hover == _closeHover) return;
            _closeHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
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

    public void PushMessage(string? title, string? body, Image? thumbnail)
    {
        _history.Insert(0, new HistoryItem(title, body));
        while (_history.Count > MaxHistory)
            _history.RemoveAt(_history.Count - 1);

        ApplyThumbnail(thumbnail);

        var shouldExpand = _pointerInside && _history.Count > 1;
        if (_expanded != shouldExpand)
            _expanded = shouldExpand;

        RecalculateHeight();
        RebuildFrame();
        Present();
        SizeChangedRequested?.Invoke();
    }

    private void SetPointerInside(bool inside)
    {
        if (_pointerInside == inside) return;
        _pointerInside = inside;

        var shouldExpand = inside && _history.Count > 1;
        if (_expanded != shouldExpand)
        {
            _expanded = shouldExpand;
            RecalculateHeight();
            RebuildFrame();
            Present();
            SizeChangedRequested?.Invoke();
        }

        HoverChanged?.Invoke(inside);
    }

    private void ApplyThumbnail(Image? thumbnail)
    {
        _circleImage?.Dispose();
        _circleImage = null;
        if (!ReferenceEquals(_ownedImage, thumbnail))
        {
            _ownedImage?.Dispose();
            _ownedImage = thumbnail;
        }

        if (thumbnail is not null)
            _circleImage = MakeCircleCover(thumbnail, ThumbSize);
    }

    private void RecalculateHeight()
    {
        if (_expanded && _history.Count > 1)
        {
            var count = Math.Min(_history.Count, MaxExpandedItems);
            var height = Pad + 18; // header
            height += 14; // "history" label
            for (var i = 0; i < count; i++)
            {
                var item = _history[i];
                height += 8; // separator gap
                if (!string.IsNullOrEmpty(item.Title)) height += 18;
                if (!string.IsNullOrEmpty(item.Body)) height += EstimateBodyHeight(item.Body, 2);
                else if (string.IsNullOrEmpty(item.Title)) height += 16;
            }

            if (_history.Count > MaxExpandedItems)
                height += 16;

            PreferredCardHeight = height + Pad;
        }
        else
        {
            var latest = _history[0];
            var hasThumb = _circleImage is not null && !_expanded;
            var bodyHeight = latest.Body is null ? 0 : EstimateBodyHeight(latest.Body);
            var textBlock = 14 + (latest.Title is null ? 0 : 20) + (latest.Body is null ? 0 : bodyHeight + 2);
            if (_history.Count > 1)
                textBlock += 14;
            var contentHeight = Math.Max(textBlock, hasThumb ? ThumbSize : 0);
            PreferredCardHeight = Pad + contentHeight + Pad;
            if (hasThumb)
                PreferredCardHeight = Math.Max(PreferredCardHeight, Pad + ThumbSize + Pad);
        }

        Height = PreferredCardHeight;
        _closeRect = new Rectangle(Width - CloseHit - 6, 6, CloseHit, CloseHit);
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

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        if (!_expanded && _history.Count > 1)
        {
            var layers = Math.Min(_history.Count - 1, 2);
            for (var layer = layers; layer >= 1; layer--)
            {
                var offset = layer * 4f;
                var ghost = new RectangleF(
                    bounds.X + offset,
                    bounds.Y + offset,
                    bounds.Width,
                    bounds.Height);
                using var path = RoundedRect(ghost, CornerRadius);
                using var fill = new SolidBrush(Color.FromArgb(210, 14, 14, 14));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(220, 42, 42, 42), 1f);
                g.DrawPath(border, path);
            }
        }

        using (var path = RoundedRect(bounds, CornerRadius))
        {
            using var fill = new SolidBrush(SurfaceAlt);
            g.FillPath(fill, path);
            using var border = new Pen(Line, 1f);
            g.DrawPath(border, path);
        }

        using var mutedBrush = new SolidBrush(Tertiary);
        using var secondaryBrush = new SolidBrush(Muted);
        using var fgBrush = new SolidBrush(Fg);
        using var dotBrush = new SolidBrush(Color.FromArgb(74, 222, 128));

        if (_expanded && _history.Count > 1)
            DrawExpanded(g, mutedBrush, secondaryBrush, fgBrush, dotBrush);
        else
            DrawCollapsed(g, mutedBrush, secondaryBrush, fgBrush, dotBrush);

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

    private void DrawCollapsed(
        Graphics g,
        SolidBrush mutedBrush,
        SolidBrush secondaryBrush,
        SolidBrush fgBrush,
        SolidBrush dotBrush)
    {
        var latest = _history[0];
        var thumbTop = (Height - ThumbSize) / 2;
        var textLeft = _circleImage is null ? Pad : ThumbLeft + ThumbSize + 12;
        var textWidth = Width - textLeft - Pad - CloseHit;

        if (_circleImage is not null)
        {
            g.DrawImage(_circleImage, ThumbLeft, thumbTop, ThumbSize, ThumbSize);
            using var ring = new Pen(Color.FromArgb(220, 220, 220), 1.25f);
            g.DrawEllipse(ring, ThumbLeft + 0.5f, thumbTop + 0.5f, ThumbSize - 1.5f, ThumbSize - 1.5f);
        }

        g.FillRectangle(dotBrush, textLeft, Pad + 3f, 6f, 6f);

        var appLabel = _history.Count > 1 ? $"{_app} · {_history.Count}" : _app;
        g.DrawString(appLabel, _mono, mutedBrush, new RectangleF(textLeft + 14f, Pad, Math.Max(40, textWidth - 14f), 14),
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

        var y = Pad + 16f;
        if (_history.Count > 1)
        {
            g.DrawString("hover for history", _mono, mutedBrush, new RectangleF(textLeft, y, textWidth, 12),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            y += 14f;
        }

        if (!string.IsNullOrEmpty(latest.Title))
        {
            g.DrawString(latest.Title, _titleFont, fgBrush, new RectangleF(textLeft, y, textWidth, 20),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            y += 22;
        }

        if (!string.IsNullOrEmpty(latest.Body))
        {
            var bodyHeight = EstimateBodyHeight(latest.Body);
            g.DrawString(latest.Body, _bodyFont, secondaryBrush, new RectangleF(textLeft, y, textWidth, bodyHeight),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
        }
    }

    private void DrawExpanded(
        Graphics g,
        SolidBrush mutedBrush,
        SolidBrush secondaryBrush,
        SolidBrush fgBrush,
        SolidBrush dotBrush)
    {
        var textLeft = Pad;
        var textWidth = Width - Pad - Pad - CloseHit;

        g.FillRectangle(dotBrush, textLeft, Pad + 3f, 6f, 6f);
        g.DrawString($"{_app} · {_history.Count}", _mono, mutedBrush,
            new RectangleF(textLeft + 14f, Pad, Math.Max(40, textWidth - 14f), 14),
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

        var y = Pad + 18f;
        g.DrawString("history · newest first", _mono, mutedBrush,
            new RectangleF(textLeft, y, textWidth, 12),
            new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        y += 16f;

        var count = Math.Min(_history.Count, MaxExpandedItems);
        for (var i = 0; i < count; i++)
        {
            var item = _history[i];
            using (var sep = new Pen(Line, 1f))
                g.DrawLine(sep, textLeft, y, textLeft + textWidth, y);
            y += 8f;

            if (!string.IsNullOrEmpty(item.Title))
            {
                g.DrawString(item.Title, _titleFont, fgBrush, new RectangleF(textLeft, y, textWidth, 18),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                y += 18f;
            }

            if (!string.IsNullOrEmpty(item.Body))
            {
                var bodyHeight = EstimateBodyHeight(item.Body, 2);
                g.DrawString(item.Body, _bodyFont, secondaryBrush, new RectangleF(textLeft, y, textWidth, bodyHeight),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
                y += bodyHeight;
            }
            else if (string.IsNullOrEmpty(item.Title))
            {
                g.DrawString("(empty)", _bodyFont, mutedBrush, new RectangleF(textLeft, y, textWidth, 16));
                y += 16f;
            }
        }

        if (_history.Count > MaxExpandedItems)
        {
            y += 6f;
            g.DrawString($"+{_history.Count - MaxExpandedItems} more", _mono, mutedBrush,
                new RectangleF(textLeft, y, textWidth, 14));
        }
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

    private static int EstimateBodyHeight(string text, int maxLines = 3)
    {
        var lines = Math.Clamp((text.Length / 40) + 1, 1, maxLines);
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
