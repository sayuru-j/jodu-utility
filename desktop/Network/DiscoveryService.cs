using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Jodu.Desktop.Protocol;

namespace Jodu.Desktop.Network;

public sealed class DiscoveryService : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly ConcurrentDictionary<string, (DiscoveryPayload Peer, DateTime Seen)> _peers = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastReply = new();
    private string _lastPeerSignature = string.Empty;
    private Task? _listenTask;
    private Task? _announceTask;
    private Task? _pruneTask;

    public event Action<IReadOnlyList<DiscoveryPayload>>? PeersChanged;
    public event Action<PairPayload>? PairRequestReceived;
    public event Action<PairPayload>? PairResponseReceived;

    public DiscoveryService(string? deviceName = null)
    {
        _deviceId = LoadOrCreateDeviceId();
        _deviceName = deviceName ?? Environment.MachineName;
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, JoduPorts.Discovery));
        _udp.EnableBroadcast = true;
    }

    public string DeviceId => _deviceId;
    public string DeviceName => _deviceName;

    public IReadOnlyList<DiscoveryPayload> Peers =>
        _peers.Values.Select(v => v.Peer).OrderBy(p => p.DeviceName).ToList();

    public void Start()
    {
        _listenTask = Task.Run(ListenLoopAsync);
        _announceTask = Task.Run(AnnounceLoopAsync);
        _pruneTask = Task.Run(PruneLoopAsync);
    }

    public void RequestPair(DiscoveryPayload target)
    {
        var payload = LocalPairPayload(target.DeviceId);
        // Burst — UDP pair packets are easily dropped on some APs.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                SendTo(target.Ip, JoduMessage.Create(EventTypes.PairRequest, payload));
                Broadcast(JoduMessage.Create(EventTypes.PairRequest, payload));
                if (attempt < 3)
                {
                    try { await Task.Delay(350, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    public void RespondPair(PairPayload request, bool accepted)
    {
        var payload = LocalPairPayload(request.FromDeviceId);
        payload.Accepted = accepted;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                SendTo(request.FromIp, JoduMessage.Create(EventTypes.PairResponse, payload));
                Broadcast(JoduMessage.Create(EventTypes.PairResponse, payload));
                if (attempt < 3)
                {
                    try { await Task.Delay(350, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    private bool ShouldReply(string deviceId)
    {
        var now = DateTime.UtcNow;
        if (_lastReply.TryGetValue(deviceId, out var last) && now - last < TimeSpan.FromSeconds(2))
            return false;
        _lastReply[deviceId] = now;
        return true;
    }

    private DiscoveryPayload LocalDiscoveryPayload() => new()
    {
        DeviceId = _deviceId,
        DeviceName = _deviceName,
        Role = "desktop",
        Ip = GetLocalIp(),
        WsPort = JoduPorts.WebSocket,
        HttpPort = JoduPorts.FileHttp
    };

    private void ReplyDiscovery(IPAddress address)
    {
        SendTo(address.ToString(), JoduMessage.Create(EventTypes.Discovery, LocalDiscoveryPayload()));
    }

    private void Broadcast(JoduMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJson());
        foreach (var endpoint in BroadcastEndpoints())
        {
            try
            {
                _udp.Send(bytes, bytes.Length, endpoint);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static IEnumerable<IPEndPoint> BroadcastEndpoints()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, JoduPorts.Discovery);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var broadcast = GetIPv4Broadcast(addr.Address, addr.IPv4Mask);
                if (broadcast is not null)
                    yield return new IPEndPoint(broadcast, JoduPorts.Discovery);
            }
        }
    }

    private static IPAddress? GetIPv4Broadcast(IPAddress address, IPAddress? mask)
    {
        if (mask is null) return null;
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (ip.Length != 4 || m.Length != 4) return null;
        var b = new byte[4];
        for (var i = 0; i < 4; i++)
            b[i] = (byte)(ip[i] | ~m[i]);
        return new IPAddress(b);
    }

    private PairPayload LocalPairPayload(string targetDeviceId) => new()
    {
        FromDeviceId = _deviceId,
        FromDeviceName = _deviceName,
        FromRole = "desktop",
        FromIp = GetLocalIp(),
        WsPort = JoduPorts.WebSocket,
        HttpPort = JoduPorts.FileHttp,
        TargetDeviceId = targetDeviceId
    };

    private void SendTo(string ip, JoduMessage message)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var address)) return;
            var bytes = Encoding.UTF8.GetBytes(message.ToJson());
            _udp.Send(bytes, bytes.Length, new IPEndPoint(address, JoduPorts.Discovery));
        }
        catch
        {
            // ignore send failures
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var msg = JoduMessage.FromJson(json);
                if (msg is null) continue;

                switch (msg.Type)
                {
                    case EventTypes.Discovery:
                    {
                        var peer = msg.GetPayload<DiscoveryPayload>();
                        if (peer is null || peer.DeviceId == _deviceId) break;
                        if (!string.Equals(peer.Role, "android", StringComparison.OrdinalIgnoreCase)) break;

                        // Prefer the packet source IP — advertised IPs are often wrong on mobile.
                        peer.Ip = result.RemoteEndPoint.Address.ToString();

                        // Drop stale identities that share this LAN IP (deviceId rotates on restart).
                        foreach (var (id, entry) in _peers)
                        {
                            if (id != peer.DeviceId &&
                                string.Equals(entry.Peer.Ip, peer.Ip, StringComparison.OrdinalIgnoreCase))
                            {
                                _peers.TryRemove(id, out _);
                            }
                        }

                        _peers[peer.DeviceId] = (peer, DateTime.UtcNow);
                        EmitPeersIfChanged();

                        // Unicast our identity back — more reliable than phone receiving broadcasts.
                        // Rate-limit so announce↔reply does not amplify into a tight loop.
                        if (ShouldReply(peer.DeviceId))
                            ReplyDiscovery(result.RemoteEndPoint.Address);
                        break;
                    }
                    case EventTypes.PairRequest:
                    {
                        var req = msg.GetPayload<PairPayload>();
                        if (req is null) break;

                        // Prefer targeted match; also accept blank/"any" targets.
                        // Android clients sometimes cache a stale desktop deviceId after a
                        // restart — still surface phone→desktop pair prompts when the
                        // sender is clearly a phone on this LAN.
                        var targeted = string.IsNullOrWhiteSpace(req.TargetDeviceId)
                            || string.Equals(req.TargetDeviceId, _deviceId, StringComparison.OrdinalIgnoreCase);
                        var fromPhone = string.Equals(req.FromRole, "android", StringComparison.OrdinalIgnoreCase);
                        if (!targeted && !fromPhone) break;

                        req.FromIp = result.RemoteEndPoint.Address.ToString();
                        PairRequestReceived?.Invoke(req);
                        break;
                    }
                    case EventTypes.PairResponse:
                    {
                        var res = msg.GetPayload<PairPayload>();
                        if (res is null) break;
                        var forUs = string.IsNullOrWhiteSpace(res.TargetDeviceId)
                            || string.Equals(res.TargetDeviceId, _deviceId, StringComparison.OrdinalIgnoreCase);
                        if (!forUs) break;
                        res.FromIp = result.RemoteEndPoint.Address.ToString();
                        PairResponseReceived?.Invoke(res);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // ignore malformed packets
            }
        }
    }

    private async Task AnnounceLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var message = JoduMessage.Create(EventTypes.Discovery, LocalDiscoveryPayload());
                Broadcast(message);

                // Unicast to known phones so Android still lists this desktop when
                // broadcast/multicast is filtered by the AP or OS.
                foreach (var entry in _peers.Values)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Peer.Ip))
                        SendTo(entry.Peer.Ip, message);
                }
            }
            catch
            {
                // retry next interval
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PruneLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(10);
            var removed = false;
            foreach (var (id, entry) in _peers)
            {
                if (entry.Seen < cutoff && _peers.TryRemove(id, out _))
                    removed = true;
            }

            if (removed)
                EmitPeersIfChanged();
        }
    }

    private void EmitPeersIfChanged()
    {
        var signature = string.Join("|", Peers.Select(p => $"{p.DeviceId}:{p.Ip}:{p.WsPort}"));
        if (signature == _lastPeerSignature) return;
        _lastPeerSignature = signature;
        PeersChanged?.Invoke(Peers);
    }

    private static string GetLocalIp() => HttpListenerFactory.GetLocalIp();

    private static string LoadOrCreateDeviceId()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JODU");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "device-id.txt");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 8)
                    return existing.Length > 12 ? existing[..12] : existing;
            }

            var created = Guid.NewGuid().ToString("N")[..12];
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            return Guid.NewGuid().ToString("N")[..12];
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { /* ignore */ }
        _cts.Dispose();
        _udp.Dispose();
    }
}
