using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jodu.Desktop.Protocol;

namespace Jodu.Desktop.Network;

public sealed class DiscoveryService : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _deviceId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _deviceName;
    private readonly ConcurrentDictionary<string, (DiscoveryPayload Peer, DateTime Seen)> _peers = new();
    private Task? _listenTask;
    private Task? _announceTask;
    private Task? _pruneTask;

    public event Action<IReadOnlyList<DiscoveryPayload>>? PeersChanged;
    public event Action<PairPayload>? PairRequestReceived;
    public event Action<PairPayload>? PairResponseReceived;

    public DiscoveryService(string? deviceName = null)
    {
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
        SendTo(target.Ip, JoduMessage.Create(EventTypes.PairRequest, payload));
    }

    public void RespondPair(PairPayload request, bool accepted)
    {
        var payload = LocalPairPayload(request.FromDeviceId);
        payload.Accepted = accepted;
        SendTo(request.FromIp, JoduMessage.Create(EventTypes.PairResponse, payload));
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
                        if (string.IsNullOrWhiteSpace(peer.Ip))
                            peer.Ip = result.RemoteEndPoint.Address.ToString();

                        _peers[peer.DeviceId] = (peer, DateTime.UtcNow);
                        PeersChanged?.Invoke(Peers);
                        break;
                    }
                    case EventTypes.PairRequest:
                    {
                        var req = msg.GetPayload<PairPayload>();
                        if (req is null || req.TargetDeviceId != _deviceId) break;
                        if (string.IsNullOrWhiteSpace(req.FromIp))
                            req.FromIp = result.RemoteEndPoint.Address.ToString();
                        PairRequestReceived?.Invoke(req);
                        break;
                    }
                    case EventTypes.PairResponse:
                    {
                        var res = msg.GetPayload<PairPayload>();
                        if (res is null || res.TargetDeviceId != _deviceId) break;
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
        var endpoint = new IPEndPoint(IPAddress.Broadcast, JoduPorts.Discovery);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var payload = new DiscoveryPayload
                {
                    DeviceId = _deviceId,
                    DeviceName = _deviceName,
                    Role = "desktop",
                    Ip = GetLocalIp(),
                    WsPort = JoduPorts.WebSocket,
                    HttpPort = JoduPorts.FileHttp
                };

                var bytes = Encoding.UTF8.GetBytes(JoduMessage.Create(EventTypes.Discovery, payload).ToJson());
                await _udp.SendAsync(bytes, bytes.Length, endpoint);
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

            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(8);
            var removed = false;
            foreach (var (id, entry) in _peers)
            {
                if (entry.Seen < cutoff && _peers.TryRemove(id, out _))
                    removed = true;
            }

            if (removed)
                PeersChanged?.Invoke(Peers);
        }
    }

    private static string GetLocalIp() => HttpListenerFactory.GetLocalIp();

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { /* ignore */ }
        _cts.Dispose();
        _udp.Dispose();
    }
}
