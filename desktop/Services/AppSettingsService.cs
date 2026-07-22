using System.Text.Json;
using Microsoft.Win32;

namespace Jodu.Desktop.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool StartWithWindows { get; set; }
    public bool AutoConnectLastDevice { get; set; }
    public string? LastPeerDeviceId { get; set; }
    public string? LastPeerDeviceName { get; set; }

    public static AppSettingsService Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path)) return new AppSettingsService();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettingsService>(json, JsonOptions) ?? new AppSettingsService();
        }
        catch
        {
            return new AppSettingsService();
        }
    }

    public void Save()
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // best-effort persistence
        }
    }

    public void ApplyStartWithWindows(bool enabled)
    {
        StartWithWindows = enabled;
        Save();
        SetAutoStart(enabled);
    }

    public void ApplyAutoConnect(bool enabled)
    {
        AutoConnectLastDevice = enabled;
        Save();
    }

    public void RememberPeer(string? deviceId, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        LastPeerDeviceId = deviceId.Trim();
        LastPeerDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
        Save();
    }

    private static string SettingsPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JODU");
        return Path.Combine(dir, "settings.json");
    }

    private static void SetAutoStart(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(runKey);
        if (key is null) return;

        const string valueName = "JODU";
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Jodu.Desktop.exe");
            key.SetValue(valueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
