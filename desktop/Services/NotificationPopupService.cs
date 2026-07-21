using System.Drawing.Drawing2D;

namespace Jodu.Desktop.Services;

/// <summary>
/// Stacked right-edge phone notification popups (topmost, auto-dismiss).
/// </summary>
public sealed class NotificationPopupService : IDisposable
{
    private readonly List<NotificationCardForm> _cards = new();
    private readonly object _gate = new();
    private readonly SynchronizationContext? _ui;
    private bool _disposed;

    private const int CardWidth = 352;
    private const int Margin = 16;
    private const int Gap = 10;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8);

    public NotificationPopupService()
    {
        _ui = SynchronizationContext.Current;
    }

    public void ShowPhoneNotification(string appName, string? title, string? body)
    {
        if (_disposed) return;
        var heading = string.IsNullOrWhiteSpace(appName) ? "Phone" : appName.Trim();
        var line1 = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var line2 = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        if (line1 is null && line2 is null) return;

        void Show()
        {
            lock (_gate)
            {
                // Cap stack size
                while (_cards.Count >= 5)
                {
                    var oldest = _cards[0];
                    _cards.RemoveAt(0);
                    TryClose(oldest);
                }

                var card = new NotificationCardForm(heading, line1, line2);
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

                var timer = new System.Windows.Forms.Timer { Interval = (int)Hold.TotalMilliseconds };
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
            card.Bounds = new Rectangle(x, y, CardWidth, card.PreferredCardHeight);
            y += card.Height + Gap;
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

internal sealed class NotificationCardForm : Form
{
    private readonly Label _app;
    private readonly Label _title;
    private readonly Label _body;
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 16 };
    private float _opacityTarget = 1f;
    private Action? _onFadeDone;

    public event Action? DismissRequested;

    public int PreferredCardHeight { get; private set; } = 96;

    public NotificationCardForm(string app, string? title, string? body)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 26, 24);
        Opacity = 0;
        Padding = new Padding(14, 12, 14, 12);
        DoubleBuffered = true;

        _app = new Label
        {
            AutoSize = false,
            Height = 18,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
            ForeColor = Color.FromArgb(45, 212, 191),
            Text = app.ToUpperInvariant(),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _title = new Label
        {
            AutoSize = false,
            Height = title is null ? 0 : 22,
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(242, 245, 244),
            Text = title ?? string.Empty,
            Visible = title is not null,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _body = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(170, 186, 180),
            Text = body ?? string.Empty,
            Visible = body is not null,
            TextAlign = ContentAlignment.TopLeft
        };

        var close = new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            Width = 28,
            Height = 22,
            ForeColor = Color.FromArgb(122, 138, 134),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            TabStop = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => DismissRequested?.Invoke();

        PreferredCardHeight = 12 + 18 + 8
            + (title is null ? 0 : 22)
            + (body is null ? 4 : EstimateBodyHeight(body))
            + 12;
        Height = PreferredCardHeight;
        Width = 352;

        _app.SetBounds(14, 12, Width - 56, 18);
        _title.SetBounds(14, 34, Width - 28, title is null ? 0 : 22);
        var bodyTop = title is null ? 34 : 58;
        _body.SetBounds(14, bodyTop, Width - 28, Math.Max(0, Height - bodyTop - 12));
        close.SetBounds(Width - 40, 10, 28, 22);

        Controls.Add(close);
        Controls.Add(_body);
        Controls.Add(_title);
        Controls.Add(_app);

        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(45, 212, 191), 1);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawRectangle(pen, rect);
            using var accent = new SolidBrush(Color.FromArgb(45, 212, 191));
            e.Graphics.FillRectangle(accent, 0, 0, 3, Height);
        };

        _fadeTimer.Tick += (_, _) =>
        {
            var step = 0.08f;
            if (Math.Abs(Opacity - _opacityTarget) <= step)
            {
                Opacity = _opacityTarget;
                _fadeTimer.Stop();
                var done = _onFadeDone;
                _onFadeDone = null;
                done?.Invoke();
                return;
            }

            Opacity += Math.Sign(_opacityTarget - Opacity) * step;
        };
    }

    private static int EstimateBodyHeight(string text)
    {
        var lines = Math.Clamp((text.Length / 42) + 1, 1, 4);
        return 18 * lines + 8;
    }

    public void BeginFadeIn()
    {
        _opacityTarget = 0.96f;
        _fadeTimer.Start();
    }

    public void BeginFadeOut(Action? then = null)
    {
        _onFadeDone = then;
        _opacityTarget = 0f;
        _fadeTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoactivate = 0x08000000;
            const int WsExToolwindow = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoactivate | WsExToolwindow;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _fadeTimer.Dispose();
        base.Dispose(disposing);
    }
}
