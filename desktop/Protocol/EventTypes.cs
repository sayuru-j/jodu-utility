namespace Jodu.Desktop.Protocol;

public static class EventTypes
{
    public const string ClipboardUpdate = "CLIPBOARD_UPDATE";
    public const string Telemetry = "TELEMETRY";
    public const string OtpDetected = "OTP_DETECTED";
    public const string MediaControl = "MEDIA_CONTROL";
    public const string MediaState = "MEDIA_STATE";
    public const string PingDevice = "PING_DEVICE";
    public const string Discovery = "DISCOVERY";
    public const string PairRequest = "PAIR_REQUEST";
    public const string PairResponse = "PAIR_RESPONSE";
}

public static class MediaActions
{
    public const string Play = "PLAY";
    public const string Pause = "PAUSE";
    public const string Next = "NEXT";
    public const string Previous = "PREVIOUS";
    public const string VolumeUp = "VOLUME_UP";
    public const string VolumeDown = "VOLUME_DOWN";
}

public static class JoduPorts
{
    public const int Discovery = 19283;
    public const int WebSocket = 19284;
    public const int FileHttp = 19285;
}
