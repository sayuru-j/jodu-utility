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
    private readonly Icon _appIcon;
    private readonly DiscoveryService _discovery = new();
    private readonly WebSocketHub _hub = new();
    private readonly FileTransferServer _files = new();
    private readonly HotkeyService _hotkey = new();
    private readonly ToastService _toasts = new();
    private readonly NotificationPopupService _phoneToasts = new();
    private NotificationTonePlayer? _notificationTone;
    private readonly ClipboardBridge _clipboard = new();
    private readonly AppSettingsService _settings = AppSettingsService.Load();
    private readonly UiBridge _bridge;

    private TelemetryPayload? _telemetry;
    private MediaStatePayload? _media;
    private DiscoveryPayload? _peer;
    private IReadOnlyList<DiscoveryPayload> _lanPeers = Array.Empty<DiscoveryPayload>();
    private PairPayload? _incomingPair;
    private string? _outgoingPairDeviceId;
    private string _pairStatus = "idle";
    private bool _connected;
    private FileTransferPayload? _transfer;
    private const int AndroidFileHttpPort = 19286;
    private readonly bool _startMinimized;
    private bool _allowVisible = true;
    private DateTime _lastAutoConnectAttempt = DateTime.MinValue;

    public MainForm(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        if (startMinimized)
            _allowVisible = false;
        Text = "JODU";
        Width = 960;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 420);
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(10, 10, 10);
        DoubleBuffered = true;
        Padding = new Padding(6);

        _appIcon = AppIcons.Load();
        Icon = _appIcon;

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.FromArgb(10, 10, 10);
        Controls.Add(_webView);

        _tray = new NotifyIcon
        {
            Text = "JODU",
            Visible = true,
            Icon = (Icon)_appIcon.Clone()
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.ContextMenuStrip = BuildTrayMenu();

        _toasts.FallbackBalloon = (title, body) =>
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = body;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(8000);
            }
            catch { /* ignore */ }
        };

        _bridge = new UiBridge(this);
        _notificationTone = new NotificationTonePlayer(
            () => _webView.CoreWebView2,
            ResolveUiUrl);
        _phoneToasts.PlayTone = () => _notificationTone.Play();
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

        _discovery.PeersChanged += peers =>
        {
            BeginInvoke(() =>
            {
                _lanPeers = peers;
                if (_peer is not null)
                {
                    var fresh = peers.FirstOrDefault(p =>
                        string.Equals(p.Ip, _peer.Ip, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.DeviceId, _peer.DeviceId, StringComparison.OrdinalIgnoreCase));
                    if (fresh is not null)
                        _peer = NormalizePhonePeer(fresh);
                }
                else if (_connected)
                {
                    EnsurePeerFromLan();
                }
                TryAutoConnect();
                PushUiState();
            });
        };

        _discovery.PairRequestReceived += req =>
        {
            BeginInvoke(() =>
            {
                if (_connected) return;

                // UDP bursts retry — keep showing the same incoming request (no modal).
                var samePhone = _pairStatus == "incoming" &&
                    string.Equals(_incomingPair?.FromDeviceId, req.FromDeviceId, StringComparison.OrdinalIgnoreCase);
                if (samePhone)
                {
                    _incomingPair = req;
                    return;
                }

                _incomingPair = req;
                _pairStatus = "incoming";
                ShowFromTray();
                _toasts.ShowInfo("Pair request", $"{req.FromDeviceName} wants to pair");
                try
                {
                    _tray.BalloonTipTitle = "JODU — pair request";
                    _tray.BalloonTipText = $"{req.FromDeviceName} ({req.FromIp}) wants to pair";
                    _tray.BalloonTipIcon = ToolTipIcon.Info;
                    _tray.ShowBalloonTip(12000);
                }
                catch { /* ignore */ }
                PushUiState();
            });
        };

        _discovery.PairResponseReceived += res =>
        {
            BeginInvoke(() =>
            {
                if (_outgoingPairDeviceId is null || res.FromDeviceId != _outgoingPairDeviceId)
                    return;

                if (res.Accepted == true)
                {
                    _peer = NormalizePhonePeer(new DiscoveryPayload
                    {
                        DeviceId = res.FromDeviceId,
                        DeviceName = res.FromDeviceName,
                        Role = res.FromRole,
                        Ip = res.FromIp,
                        WsPort = res.WsPort > 0 ? res.WsPort : JoduPorts.WebSocket,
                        HttpPort = res.HttpPort
                    });
                    _pairStatus = "accepted";
                    _outgoingPairDeviceId = null;
                    _toasts.ShowInfo("JODU", $"Paired with {res.FromDeviceName}");
                }
                else
                {
                    _pairStatus = "rejected";
                    _outgoingPairDeviceId = null;
                    _toasts.ShowInfo("JODU", "Pair request declined");
                }

                PushUiState();
            });
        };

        _hub.ClientConnected += remoteIp =>
        {
            BeginInvoke(() =>
            {
                _connected = true;
                _pairStatus = "linked";
                _incomingPair = null;
                _outgoingPairDeviceId = null;
                if (_peer is not null)
                    _settings.RememberPeer(_peer.DeviceId, _peer.DeviceName);
                if (_peer is null || string.IsNullOrWhiteSpace(_peer.Ip))
                {
                    EnsurePeerFromLan(remoteIp);
                }
                else
                {
                    _peer = NormalizePhonePeer(_peer);
                    if (!string.IsNullOrWhiteSpace(remoteIp) &&
                        !string.Equals(_peer.Ip, remoteIp, StringComparison.OrdinalIgnoreCase))
                    {
                        _peer.Ip = remoteIp;
                    }
                }
                UpdateTrayStatus();
                _toasts.ShowInfo("JODU", $"Paired with {_peer?.DeviceName ?? "phone"}");
                PushUiState();
            });
        };

        _hub.ClientDisconnected += () =>
        {
            BeginInvoke(() =>
            {
                _connected = _hub.HasClients;
                if (!_connected && _pairStatus == "linked")
                    _pairStatus = _peer is not null ? "accepted" : "idle";
                UpdateTrayStatus();
                PushUiState();
            });
        };

        _hub.MessageReceived += OnMessage;
        _files.FileReceiveProgress += (fileName, transferred, total) =>
        {
            BeginInvoke(() =>
            {
                var percent = total > 0 ? (int)Math.Clamp(transferred * 100 / total, 0, 100) : 0;
                _transfer = new FileTransferPayload
                {
                    FileName = fileName,
                    Direction = "receive",
                    BytesTransferred = transferred,
                    TotalBytes = total,
                    Percent = percent,
                    Status = "progress"
                };
                _ = BroadcastTransferAsync(_transfer);
                PushUiState();
            });
        };
        _files.FileReceived += path =>
        {
            BeginInvoke(() =>
            {
                var name = Path.GetFileName(path);
                _transfer = new FileTransferPayload
                {
                    FileName = name,
                    Direction = "receive",
                    Percent = 100,
                    Status = "done"
                };
                _ = BroadcastTransferAsync(_transfer);
                _toasts.ShowInfo("File received", name);
                PushUiState();
            });
        };
    }

    private async Task BroadcastTransferAsync(FileTransferPayload payload)
    {
        try
        {
            await _hub.BroadcastAsync(JoduMessage.Create(EventTypes.FileTransfer, payload));
        }
        catch
        {
            // best-effort progress mirror
        }
    }

    private void EnsurePeerFromLan(string? preferredIp = null)
    {
        DiscoveryPayload? match = null;
        if (!string.IsNullOrWhiteSpace(preferredIp))
        {
            match = _lanPeers.FirstOrDefault(p =>
                string.Equals(p.Ip, preferredIp, StringComparison.OrdinalIgnoreCase));
        }

        match ??= _lanPeers.FirstOrDefault(p =>
            string.Equals(p.Role, "android", StringComparison.OrdinalIgnoreCase));

        if (match is null && !string.IsNullOrWhiteSpace(preferredIp))
        {
            match = new DiscoveryPayload
            {
                DeviceId = "phone",
                DeviceName = "phone",
                Role = "android",
                Ip = preferredIp!,
                WsPort = JoduPorts.WebSocket,
                HttpPort = AndroidFileHttpPort
            };
        }

        if (match is not null)
            _peer = NormalizePhonePeer(match);
    }

    private static DiscoveryPayload NormalizePhonePeer(DiscoveryPayload peer)
    {
        peer.HttpPort = peer.HttpPort > 0 && peer.HttpPort != JoduPorts.FileHttp
            ? peer.HttpPort
            : AndroidFileHttpPort;
        return peer;
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
            _settings.ApplyStartWithWindows(_settings.StartWithWindows);

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
            _webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                if (IsHandleCreated)
                    BeginInvoke(PushUiState);
            };

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
            case "OPEN_DOCS":
                OpenDocsFolder();
                break;
            case "PAIR_REQUEST":
                if (!string.IsNullOrWhiteSpace(cmd.Value))
                    RequestPair(cmd.Value!);
                break;
            case "PAIR_ACCEPT":
                AcceptIncomingPair();
                break;
            case "PAIR_REJECT":
                RejectIncomingPair();
                break;
            case "SET_START_WITH_WINDOWS":
                _settings.ApplyStartWithWindows(IsTruthy(cmd.Value));
                PushUiState();
                break;
            case "SET_AUTO_CONNECT":
                _settings.ApplyAutoConnect(IsTruthy(cmd.Value));
                PushUiState();
                break;
        }
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private void RequestPair(string deviceId)
    {
        if (_connected) return;
        var target = _lanPeers.FirstOrDefault(p => p.DeviceId == deviceId);
        if (target is null) return;
        _outgoingPairDeviceId = deviceId;
        _pairStatus = "outgoing";
        _discovery.RequestPair(target);
        PushUiState();
    }

    private void AcceptIncomingPair()
    {
        if (_incomingPair is null) return;
        var req = _incomingPair;
        _peer = NormalizePhonePeer(new DiscoveryPayload
        {
            DeviceId = req.FromDeviceId,
            DeviceName = req.FromDeviceName,
            Role = req.FromRole,
            Ip = req.FromIp,
            WsPort = req.WsPort > 0 ? req.WsPort : JoduPorts.WebSocket,
            HttpPort = req.HttpPort
        });
        _discovery.RespondPair(req, accepted: true);
        _incomingPair = null;
        _pairStatus = "accepted";
        PushUiState();
    }

    private void RejectIncomingPair()
    {
        if (_incomingPair is null) return;
        _discovery.RespondPair(_incomingPair, accepted: false);
        _incomingPair = null;
        _pairStatus = "idle";
        PushUiState();
    }

    private static void OpenDocsFolder()
    {
        var docs = FindDocsDirectory();
        if (docs is null)
        {
            MessageBox.Show(
                "Could not locate the docs folder.",
                "JODU",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = docs,
            UseShellExecute = true
        });
    }

    private static string? FindDocsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "README.md")))
                return candidate;
            dir = dir.Parent;
        }

        return null;
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

                case EventTypes.Notification:
                {
                    var note = message.GetPayload<NotificationPayload>();
                    if (note is not null)
                    {
                        var app = string.IsNullOrWhiteSpace(note.AppName)
                            ? note.PackageName
                            : note.AppName!;
                        var stackKey = string.IsNullOrWhiteSpace(note.PackageName) ? app : note.PackageName;
                        _phoneToasts.ShowPhoneNotification(
                            app,
                            note.Title,
                            note.Text,
                            note.ImageBase64,
                            stackKey);
                    }
                    break;
                }

                case EventTypes.MediaState:
                    _media = message.GetPayload<MediaStatePayload>();
                    PushUiState();
                    break;

                case EventTypes.FileTransfer:
                {
                    var transfer = message.GetPayload<FileTransferPayload>();
                    if (transfer is not null)
                    {
                        // Phone reports its direction; flip for desktop wording.
                        if (string.Equals(transfer.Direction, "send", StringComparison.OrdinalIgnoreCase))
                            transfer.Direction = "receive";
                        else if (string.Equals(transfer.Direction, "receive", StringComparison.OrdinalIgnoreCase))
                            transfer.Direction = "send";
                        _transfer = transfer;
                        PushUiState();
                    }
                    break;
                }
            }
        });
    }

    private void PushUiState()
    {
        if (_webView.CoreWebView2 is null) return;

        if (_connected && (_peer is null || string.IsNullOrWhiteSpace(_peer.Ip)))
            EnsurePeerFromLan(_hub.LastClientIp);

        var peer = _peer is null ? null : new
        {
            deviceId = _peer.DeviceId,
            deviceName = _peer.DeviceName,
            role = _peer.Role,
            ip = _peer.Ip,
            wsPort = _peer.WsPort > 0 ? _peer.WsPort : JoduPorts.WebSocket,
            httpPort = _peer.HttpPort > 0 && _peer.HttpPort != JoduPorts.FileHttp
                ? _peer.HttpPort
                : AndroidFileHttpPort
        };

        var state = new
        {
            connected = _connected,
            peer,
            peers = _lanPeers,
            incomingPair = _incomingPair is null ? null : new
            {
                deviceId = _incomingPair.FromDeviceId,
                deviceName = _incomingPair.FromDeviceName,
                ip = _incomingPair.FromIp,
                role = _incomingPair.FromRole
            },
            outgoingPairDeviceId = _outgoingPairDeviceId,
            pairStatus = _pairStatus,
            telemetry = _telemetry,
            media = _media,
            transfer = _transfer is null ? null : new
            {
                fileName = _transfer.FileName,
                direction = _transfer.Direction,
                bytesTransferred = _transfer.BytesTransferred,
                totalBytes = _transfer.TotalBytes,
                percent = _transfer.Percent,
                status = _transfer.Status,
                error = _transfer.Error
            },
            clipboardPreview = Truncate(_clipboard.MobileClipboard, 80),
            httpPort = JoduPorts.FileHttp,
            wsPort = JoduPorts.WebSocket,
            maximized = WindowState == FormWindowState.Maximized,
            startWithWindows = _settings.StartWithWindows,
            autoConnectLastDevice = _settings.AutoConnectLastDevice,
            lastPeerDeviceName = _settings.LastPeerDeviceName
        };

        var json = JsonSerializer.Serialize(state, JoduMessage.JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= max ? value : value[..max] + "…";

    private void UpdateTrayStatus()
    {
        _tray.Text = _connected
            ? $"JODU — paired with {_peer?.DeviceName ?? "phone"}"
            : "JODU — waiting";
    }

    private void ShowFromTray()
    {
        _allowVisible = true;
        Show();
        WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
        BringToFront();
        TopMost = false;
        WindowChrome.Flash(Handle);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowVisible && value)
        {
            if (!IsHandleCreated)
                CreateHandle();
            value = false;
        }

        base.SetVisibleCore(value);
    }

    private void TryAutoConnect()
    {
        if (!_settings.AutoConnectLastDevice) return;
        if (_connected) return;
        if (_pairStatus is "outgoing" or "incoming") return;
        if (string.IsNullOrWhiteSpace(_settings.LastPeerDeviceId)) return;

        var now = DateTime.UtcNow;
        if (now - _lastAutoConnectAttempt < TimeSpan.FromSeconds(8))
            return;

        var target = _lanPeers.FirstOrDefault(p =>
            string.Equals(p.DeviceId, _settings.LastPeerDeviceId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        _lastAutoConnectAttempt = now;
        RequestPair(target.DeviceId);
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
        _phoneToasts.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            if (!ReferenceEquals(_appIcon, SystemIcons.Application))
                _appIcon.Dispose();
            _discovery.Dispose();
            _hub.Dispose();
            _files.Dispose();
            _hotkey.Dispose();
            _phoneToasts.Dispose();
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
