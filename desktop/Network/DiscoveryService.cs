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
    private Task? _listenTask;
    private Task? _announceTask;

    public event Action<DiscoveryPayload>? PeerDiscovered;

    public DiscoveryService(string? deviceName = null)
    {
        _deviceName = deviceName ?? Environment.MachineName;
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, JoduPorts.Discovery));
        _udp.EnableBroadcast = true;
    }

    public string DeviceId => _deviceId;

    public void Start()
    {
        _listenTask = Task.Run(ListenLoopAsync);
        _announceTask = Task.Run(AnnounceLoopAsync);
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
                if (msg?.Type != EventTypes.Discovery) continue;

                var peer = msg.GetPayload<DiscoveryPayload>();
                if (peer is null || peer.DeviceId == _deviceId) continue;
                if (!string.Equals(peer.Role, "android", StringComparison.OrdinalIgnoreCase)) continue;

                PeerDiscovered?.Invoke(peer);
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

    private static string GetLocalIp() => HttpListenerFactory.GetLocalIp();

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { /* ignore */ }
        _cts.Dispose();
        _udp.Dispose();
    }
}
