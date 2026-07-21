using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jodu.Desktop.Protocol;

public sealed class JoduMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonNode? Payload { get; set; }

    public static JoduMessage Create(string type, object? payload = null)
    {
        JsonNode? node = null;
        if (payload is not null)
        {
            node = JsonSerializer.SerializeToNode(payload, JsonOptions);
        }

        return new JoduMessage { Type = type, Payload = node };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static JoduMessage? FromJson(string json) =>
        JsonSerializer.Deserialize<JoduMessage>(json, JsonOptions);

    public T? GetPayload<T>()
    {
        if (Payload is null) return default;
        return Payload.Deserialize<T>(JsonOptions);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class TelemetryPayload
{
    public int BatteryPercent { get; set; }
    public bool IsCharging { get; set; }
    public string? WifiSsid { get; set; }
    public bool WifiConnected { get; set; }
    public int? WifiRssi { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class ClipboardPayload
{
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = "phone";
}

public sealed class OtpPayload
{
    public string Code { get; set; } = string.Empty;
    public string? Sender { get; set; }
    public string? Body { get; set; }
}

public sealed class MediaControlPayload
{
    public string Action { get; set; } = string.Empty;
}

public sealed class MediaStatePayload
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public bool IsPlaying { get; set; }
    public int Volume { get; set; }
}

public sealed class DiscoveryPayload
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Role { get; set; } = "desktop";
    public string Ip { get; set; } = string.Empty;
    public int WsPort { get; set; } = JoduPorts.WebSocket;
    public int HttpPort { get; set; } = JoduPorts.FileHttp;
}

public sealed class PairPayload
{
    public string FromDeviceId { get; set; } = string.Empty;
    public string FromDeviceName { get; set; } = string.Empty;
    public string FromRole { get; set; } = "desktop";
    public string FromIp { get; set; } = string.Empty;
    public int WsPort { get; set; } = JoduPorts.WebSocket;
    public int HttpPort { get; set; } = JoduPorts.FileHttp;
    public string TargetDeviceId { get; set; } = string.Empty;
    public bool? Accepted { get; set; }
}
