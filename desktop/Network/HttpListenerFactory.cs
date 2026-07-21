using System.Net;
using System.Net.Sockets;

namespace Jodu.Desktop.Network;

internal static class HttpListenerFactory
{
    public static HttpListener Start(int port)
    {
        var prefixes = BuildPrefixes(port);
        Exception? last = null;

        foreach (var batch in prefixes)
        {
            var listener = new HttpListener();
            try
            {
                foreach (var prefix in batch)
                    listener.Prefixes.Add(prefix);

                listener.Start();
                return listener;
            }
            catch (Exception ex)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            $"Unable to start HTTP listener on port {port}. " +
            "Try running once as admin to reserve the URL, or free the port.",
            last);
    }

    private static IEnumerable<string[]> BuildPrefixes(int port)
    {
        var lan = GetLocalIp();
        if (!string.IsNullOrWhiteSpace(lan) && lan != "127.0.0.1")
            yield return [$"http://{lan}:{port}/"];

        // Wildcard often needs URL ACL; try after specific IP.
        yield return [$"http://+:{port}/"];

        yield return
        [
            $"http://127.0.0.1:{port}/",
            $"http://localhost:{port}/"
        ];
    }

    public static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch
        {
            // fall through
        }

        return IPAddress.Loopback.ToString();
    }
}
