using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Jodu.Desktop.Protocol;

namespace Jodu.Desktop.Network;

public sealed class WebSocketHub : IDisposable
{
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    public event Action<string>? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<JoduMessage>? MessageReceived;

    public bool HasClients => !_clients.IsEmpty;
    public string? LastClientIp { get; private set; }

    public void Start()
    {
        // Bind all interfaces — HttpListener URL prefixes often stick to the wrong NIC
        // (VPN/default route) so the phone's ws://{discovery-ip}:19284 connect fails.
        _listener = new TcpListener(IPAddress.Any, JoduPorts.WebSocket);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener ?? throw new InvalidOperationException("Listener not started.");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync().WaitAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (_cts.IsCancellationRequested) break;
            }
            catch
            {
                if (_cts.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        client.NoDelay = true;
        WebSocket? socket = null;
        var id = Guid.NewGuid();
        var announced = false;

        try
        {
            var stream = client.GetStream();
            if (!await TryCompleteHandshakeAsync(stream, _cts.Token))
                return;

            socket = WebSocket.CreateFromStream(
                stream,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(20));

            _clients[id] = socket;
            LastClientIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
            announced = true;
            ClientConnected?.Invoke(LastClientIp ?? string.Empty);
            await ReceiveLoopAsync(id, socket);
        }
        catch
        {
            // handshake or connect failed
        }
        finally
        {
            _clients.TryRemove(id, out _);
            try { socket?.Dispose(); } catch { /* ignore */ }
            try { client.Dispose(); } catch { /* ignore */ }
            if (announced)
                ClientDisconnected?.Invoke();
        }
    }

    private static async Task<bool> TryCompleteHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var request = await ReadHttpHeaderAsync(stream, ct);
        if (request is null) return false;

        if (!request.Contains("GET ", StringComparison.Ordinal) ||
            !request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, ct);
            return false;
        }

        const string keyHeader = "Sec-WebSocket-Key:";
        var keyLine = request
            .Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith(keyHeader, StringComparison.OrdinalIgnoreCase));
        if (keyLine is null) return false;

        var key = keyLine[keyHeader.Length..].Trim();
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        return true;
    }

    private static async Task<string?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder(1024);
        while (sb.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read <= 0) return null;
            sb.Append((char)buffer[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                return sb.ToString();
        }

        return null;
    }

    private async Task ReceiveLoopAsync(Guid id, WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();

        try
        {
            while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var json = builder.ToString();
                builder.Clear();

                var message = JoduMessage.FromJson(json);
                if (message is not null)
                    MessageReceived?.Invoke(message);
            }
        }
        catch
        {
            // connection dropped
        }
        finally
        {
            _clients.TryRemove(id, out _);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
        }
    }

    public async Task BroadcastAsync(JoduMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJson());
        foreach (var (id, socket) in _clients)
        {
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            }
            catch
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }

        foreach (var socket in _clients.Values)
        {
            try { socket.Dispose(); } catch { /* ignore */ }
        }

        _clients.Clear();
        _cts.Dispose();
    }
}
