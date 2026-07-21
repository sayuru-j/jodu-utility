using System.Net;
using Jodu.Desktop.Protocol;

namespace Jodu.Desktop.Network;

public sealed class FileTransferServer : IDisposable
{
    private readonly HttpListener _listener = new();
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
        _listener.Prefixes.Add($"http://+:{JoduPorts.FileHttp}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{JoduPorts.FileHttp}/");
            _listener.Prefixes.Add($"http://localhost:{JoduPorts.FileHttp}/");
            _listener.Start();
        }

        _loop = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                _ = Task.Run(() => HandleRequestAsync(context));
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

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "OPTIONS")
            {
                AddCors(context.Response);
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod != "POST" ||
                !string.Equals(context.Request.Url?.AbsolutePath, "/upload", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var fileName = context.Request.Headers["X-Filename"];
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"jodu-{DateTime.Now:yyyyMMdd-HHmmss}.bin";

            fileName = Path.GetFileName(fileName);
            var dest = GetUniquePath(Path.Combine(_downloadDir, fileName));

            await using (var fs = File.Create(dest))
            {
                await context.Request.InputStream.CopyToAsync(fs, _cts.Token);
            }

            AddCors(context.Response);
            context.Response.StatusCode = 200;
            await using (var writer = new StreamWriter(context.Response.OutputStream))
            {
                await writer.WriteAsync($"{{\"ok\":true,\"path\":\"{dest.Replace("\\", "\\\\")}\"}}");
            }

            context.Response.Close();
            FileReceived?.Invoke(dest);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { /* ignore */ }
        }
    }

    private static void AddCors(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Filename";
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
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
        }
        catch { /* ignore */ }

        _listener.Close();
        _cts.Dispose();
    }
}
