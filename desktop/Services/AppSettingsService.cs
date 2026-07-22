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
    /// <summary>Absolute path to a user-selected notification tone, or null for default.</summary>
    public string? CustomNotificationTonePath { get; set; }

    public bool HasCustomNotificationTone =>
        !string.IsNullOrWhiteSpace(CustomNotificationTonePath) &&
        File.Exists(CustomNotificationTonePath);

    public string NotificationToneLabel =>
        HasCustomNotificationTone
            ? Path.GetFileName(CustomNotificationTonePath!)
            : "default";

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

    /// <summary>Copies the selected audio file into LocalAppData and saves the path.</summary>
    public bool ApplyCustomNotificationTone(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return false;

        try
        {
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".ogg";

            var tonesDir = Path.Combine(AppDataDir(), "tones");
            Directory.CreateDirectory(tonesDir);
            var dest = Path.Combine(tonesDir, "custom" + ext.ToLowerInvariant());
            File.Copy(sourcePath, dest, overwrite: true);
            CustomNotificationTonePath = dest;
            Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ResetNotificationTone()
    {
        var previous = CustomNotificationTonePath;
        CustomNotificationTonePath = null;
        Save();

        if (string.IsNullOrWhiteSpace(previous)) return;
        try
        {
            var tonesDir = Path.Combine(AppDataDir(), "tones");
            if (previous.StartsWith(tonesDir, StringComparison.OrdinalIgnoreCase) && File.Exists(previous))
                File.Delete(previous);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private static string AppDataDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JODU");

    private static string SettingsPath() => Path.Combine(AppDataDir(), "settings.json");

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
