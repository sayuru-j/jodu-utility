using System.Runtime.InteropServices;
using System.Text.Json;
using Jodu.Desktop.Network;
using Jodu.Desktop.Protocol;
using Jodu.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Jodu.Desktop;

public sealed class MainForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray;
    private readonly DiscoveryService _discovery = new();
    private readonly WebSocketHub _hub = new();
    private readonly FileTransferServer _files = new();
    private readonly HotkeyService _hotkey = new();
    private readonly ToastService _toasts = new();
    private readonly ClipboardBridge _clipboard = new();
    private readonly UiBridge _bridge;

    private TelemetryPayload? _telemetry;
    private MediaStatePayload? _media;
    private DiscoveryPayload? _peer;
    private bool _connected;

    public MainForm()
    {
        Text = "JODU";
        Width = 960;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 520);
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(10, 10, 10);
        DoubleBuffered = true;
        Padding = new Padding(6);

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.FromArgb(10, 10, 10);
        Controls.Add(_webView);

        _tray = new NotifyIcon
        {
            Text = "JODU",
            Visible = true,
            Icon = SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.ContextMenuStrip = BuildTrayMenu();

        _bridge = new UiBridge(this);
        WireServices();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.TryEnableRoundedCorners(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WindowChrome.WmNchittest && WindowState == FormWindowState.Normal)
        {
            var x = (short)(m.LParam.ToInt32() & 0xFFFF);
            var y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
            var client = PointToClient(new Point(x, y));
            var hit = WindowChrome.HitTest(this, client);
            if (hit != WindowChrome.HtClient)
            {
                m.Result = hit;
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated && _webView.CoreWebView2 is not null)
            PushUiState();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open JODU", null, (_, _) => ShowFromTray());
        menu.Items.Add("Copy mobile clipboard", null, (_, _) => _clipboard.CopyMobileToWindows());
        menu.Items.Add("Ping phone", null, async (_, _) => await PingPhoneAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitApp());
        return menu;
    }

    private void WireServices()
    {
        _hotkey.HotkeyPressed += () =>
        {
            BeginInvoke(() =>
            {
                _clipboard.CopyMobileToWindows();
                _toasts.ShowInfo("JODU", "Mobile clipboard copied");
            });
        };

        _toasts.CopyOtpRequested += code =>
        {
            BeginInvoke(() => _clipboard.CopyText(code));
        };

        _discovery.PeerDiscovered += peer =>
        {
            _peer = peer;
            PushUiState();
        };

        _hub.ClientConnected += () =>
        {
            _connected = true;
            UpdateTrayStatus();
            PushUiState();
        };

        _hub.ClientDisconnected += () =>
        {
            _connected = _hub.HasClients;
            UpdateTrayStatus();
            PushUiState();
        };

        _hub.MessageReceived += OnMessage;
        _files.FileReceived += path =>
        {
            BeginInvoke(() =>
            {
                _toasts.ShowInfo("File received", Path.GetFileName(path));
                PushUiState();
            });
        };
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            _discovery.Start();
            _hub.Start();
            _files.Start();
            _hotkey.Register();

            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "jodu.local",
                GetUiDistPath(),
                CoreWebView2HostResourceAccessKind.Allow);

            // Host objects need COM visibility; UI primarily uses postMessage.
            try
            {
                _webView.CoreWebView2.AddHostObjectToScript("jodu", _bridge);
            }
            catch
            {
                // optional bridge
            }

            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

            var uiUrl = ResolveUiUrl();
            _webView.CoreWebView2.Navigate(uiUrl);
            UpdateTrayStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"JODU failed to start:\n\n{ex.Message}",
                "JODU",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string GetUiDistPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ui"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ui", "dist")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "ui", "dist"))
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
                return c;
        }

        var fallback = Path.Combine(baseDir, "ui");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string ResolveUiUrl()
    {
#if DEBUG
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
            var resp = client.GetAsync("http://localhost:5173").GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
                return "http://localhost:5173";
        }
        catch
        {
            // use packaged UI
        }
#endif
        return "https://jodu.local/index.html";
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(json)) return;
            var msg = JsonSerializer.Deserialize<UiCommand>(json, JoduMessage.JsonOptions);
            if (msg is null) return;
            _ = HandleUiCommandAsync(msg);
        }
        catch
        {
            // ignore bad UI messages
        }
    }

    private async Task HandleUiCommandAsync(UiCommand cmd)
    {
        switch (cmd.Action?.ToUpperInvariant())
        {
            case "PING":
                await PingPhoneAsync();
                break;
            case "MEDIA":
                if (!string.IsNullOrWhiteSpace(cmd.Value))
                {
                    await _hub.BroadcastAsync(JoduMessage.Create(EventTypes.MediaControl,
                        new MediaControlPayload { Action = cmd.Value! }));
                }
                break;
            case "COPY_MOBILE_CLIPBOARD":
                _clipboard.CopyMobileToWindows();
                break;
            case "GET_STATE":
                PushUiState();
                break;
            case "WINDOW_MINIMIZE":
                WindowState = FormWindowState.Minimized;
                break;
            case "WINDOW_MAXIMIZE":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                PushUiState();
                break;
            case "WINDOW_CLOSE":
                Hide();
                break;
            case "WINDOW_DRAG":
                if (WindowState != FormWindowState.Maximized)
                    WindowChrome.BeginDrag(Handle);
                break;
        }
    }

    private async Task PingPhoneAsync()
    {
        await _hub.BroadcastAsync(JoduMessage.Create(EventTypes.PingDevice, new { }));
        _toasts.ShowInfo("JODU", "Ping sent to phone");
    }

    private void OnMessage(JoduMessage message)
    {
        BeginInvoke(() =>
        {
            switch (message.Type)
            {
                case EventTypes.Telemetry:
                    _telemetry = message.GetPayload<TelemetryPayload>();
                    PushUiState();
                    break;

                case EventTypes.ClipboardUpdate:
                {
                    var payload = message.GetPayload<ClipboardPayload>();
                    if (payload is not null)
                        _clipboard.SetFromPhone(payload.Text);
                    PushUiState();
                    break;
                }

                case EventTypes.OtpDetected:
                {
                    var otp = message.GetPayload<OtpPayload>();
                    if (otp is not null)
                    {
                        _clipboard.SetFromPhone(otp.Code);
                        _toasts.ShowOtp(otp.Code, otp.Sender);
                    }
                    PushUiState();
                    break;
                }

                case EventTypes.MediaState:
                    _media = message.GetPayload<MediaStatePayload>();
                    PushUiState();
                    break;
            }
        });
    }

    private void PushUiState()
    {
        if (_webView.CoreWebView2 is null) return;

        var state = new
        {
            connected = _connected,
            peer = _peer,
            telemetry = _telemetry,
            media = _media,
            clipboardPreview = Truncate(_clipboard.MobileClipboard, 80),
            httpPort = JoduPorts.FileHttp,
            wsPort = JoduPorts.WebSocket,
            maximized = WindowState == FormWindowState.Maximized
        };

        var json = JsonSerializer.Serialize(state, JoduMessage.JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= max ? value : value[..max] + "…";

    private void UpdateTrayStatus()
    {
        _tray.Text = _connected ? "JODU — connected" : "JODU — waiting";
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _discovery.Dispose();
        _hub.Dispose();
        _files.Dispose();
        _hotkey.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _discovery.Dispose();
            _hub.Dispose();
            _files.Dispose();
            _hotkey.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class UiCommand
    {
        public string? Action { get; set; }
        public string? Value { get; set; }
    }

    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComVisible(true)]
    public sealed class UiBridge
    {
        private readonly MainForm _form;

        public UiBridge(MainForm form) => _form = form;

        public void Ping() => _ = _form.PingPhoneAsync();

        public void Media(string action) =>
            _ = _form._hub.BroadcastAsync(JoduMessage.Create(EventTypes.MediaControl,
                new MediaControlPayload { Action = action }));

        public void CopyMobileClipboard() => _form._clipboard.CopyMobileToWindows();

        public string GetPeerHttpBase()
        {
            if (_form._peer is null || string.IsNullOrWhiteSpace(_form._peer.Ip))
                return string.Empty;
            return $"http://{_form._peer.Ip}:{_form._peer.HttpPort}";
        }
    }
}
