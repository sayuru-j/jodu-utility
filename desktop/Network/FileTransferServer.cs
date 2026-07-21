using System.Net;
using System.Net.Sockets;
using System.Text;
using Jodu.Desktop.Protocol;

namespace Jodu.Desktop.Network;

public sealed class FileTransferServer : IDisposable
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _downloadDir;
    private Task? _loop;

    public event Action<string>? FileReceived;

    public FileTransferServer(string? downloadDir = null)
    {
        _downloadDir = downloadDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(_downloadDir);
    }

    public void Start()
    {
        // TcpListener on all interfaces — avoids HttpListener URL ACL / admin requirements.
        _listener = new TcpListener(IPAddress.Any, JoduPorts.FileHttp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _loop = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
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
        try
        {
            await using var stream = client.GetStream();
            var parsedRequest = await ReadHeadersAsync(stream, _cts.Token);
            if (parsedRequest is null)
            {
                await WriteResponseAsync(stream, 400, "text/plain", "bad request");
                return;
            }

            var requestLine = parsedRequest.Value.RequestLine;
            var headers = parsedRequest.Value.Headers;
            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteResponseAsync(stream, 400, "text/plain", "bad request");
                return;
            }

            var method = parts[0].ToUpperInvariant();
            var path = parts[1];
            var pathOnly = path.Split('?', 2)[0];

            if (method == "OPTIONS")
            {
                await WriteResponseAsync(stream, 204, null, null);
                return;
            }

            if (method != "POST" || !string.Equals(pathOnly, "/upload", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 404, "text/plain", "not found");
                return;
            }

            headers.TryGetValue("x-filename", out var fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"jodu-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
            fileName = Path.GetFileName(fileName);
            var dest = GetUniquePath(Path.Combine(_downloadDir, fileName));

            long contentLength = 0;
            if (headers.TryGetValue("content-length", out var lenRaw) &&
                long.TryParse(lenRaw, out var parsed))
            {
                contentLength = parsed;
            }

            await using (var fs = File.Create(dest))
            {
                if (contentLength > 0)
                {
                    var remaining = contentLength;
                    var buffer = new byte[64 * 1024];
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), _cts.Token);
                        if (read <= 0) break;
                        await fs.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                        remaining -= read;
                    }
                }
                else
                {
                    // No Content-Length — read until the client closes (chunked/unknown).
                    await stream.CopyToAsync(fs, _cts.Token);
                }
            }

            var json = $"{{\"ok\":true,\"path\":\"{dest.Replace("\\", "\\\\")}\"}}";
            await WriteResponseAsync(stream, 200, "application/json", json);
            FileReceived?.Invoke(dest);
        }
        catch
        {
            // ignore per-connection failures
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private static async Task<(string RequestLine, Dictionary<string, string> Headers)?> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder(1024);
        while (sb.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read <= 0) return null;
            sb.Append((char)buffer[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                break;
        }

        var raw = sb.ToString();
        var lines = raw.Split(["\r\n"], StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return (lines[0], headers);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string? contentType,
        string? body)
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Error"
        };

        var payload = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type, X-Filename\r\n" +
            "Connection: close\r\n" +
            (contentType is null ? "" : $"Content-Type: {contentType}\r\n") +
            $"Content-Length: {payload.Length}\r\n" +
            "\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes);
        if (payload.Length > 0)
            await stream.WriteAsync(payload);
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({i++}){ext}");
        } while (File.Exists(candidate));
        return candidate;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
