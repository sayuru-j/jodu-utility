using System.Net;
using System.Net.NetworkInformation;
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
        // Listen on every local IPv4 so LAN peers can use the address they discovered
        // via UDP (default-route / VPN IP is often the wrong one).
        var all = GetAllLocalIpv4()
            .Select(ip => $"http://{ip}:{port}/")
            .Append($"http://127.0.0.1:{port}/")
            .Append($"http://localhost:{port}/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (all.Length > 0)
            yield return all;

        yield return [$"http://+:{port}/"];
    }

    public static string GetLocalIp()
    {
        var addresses = GetAllLocalIpv4().ToList();
        if (addresses.Count == 0)
            return IPAddress.Loopback.ToString();

        // Prefer RFC1918 LAN addresses over VPN/tunnel defaults.
        var lan = addresses.FirstOrDefault(IsPrivateLan);
        if (lan is not null)
            return lan;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                var routed = ep.Address.ToString();
                if (addresses.Contains(routed))
                    return routed;
            }
        }
        catch
        {
            // fall through
        }

        return addresses[0];
    }

    public static IEnumerable<string> GetAllLocalIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (IPAddress.IsLoopback(addr.Address)) continue;
                yield return ip;
            }
        }
    }

    private static bool IsPrivateLan(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        return false;
    }
}
