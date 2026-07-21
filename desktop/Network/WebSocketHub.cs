using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Jodu.Desktop.Protocol;

namespace Jodu.Desktop.Network;

public sealed class WebSocketHub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<JoduMessage>? MessageReceived;

    public bool HasClients => !_clients.IsEmpty;

    public void Start()
    {
        _listener.Prefixes.Add($"http://+:{JoduPorts.WebSocket}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{JoduPorts.WebSocket}/");
            _listener.Prefixes.Add($"http://localhost:{JoduPorts.WebSocket}/");
            _listener.Start();
        }

        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                _clients[id] = wsContext.WebSocket;
                ClientConnected?.Invoke();
                _ = Task.Run(() => ReceiveLoopAsync(id, wsContext.WebSocket));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested) break;
            }
        }
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
            try { socket.Dispose(); } catch { /* ignore */ }
            ClientDisconnected?.Invoke();
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
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
        }
        catch { /* ignore */ }

        foreach (var socket in _clients.Values)
        {
            try { socket.Dispose(); } catch { /* ignore */ }
        }

        _clients.Clear();
        _listener.Close();
        _cts.Dispose();
    }
}
