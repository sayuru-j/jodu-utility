using Microsoft.Web.WebView2.Core;

namespace Jodu.Desktop.Services;

/// <summary>
/// Plays notification tones for phone toasts (via WebView2 audio).
/// Uses the packaged default OGG, or a user-selected custom file as a data URL.
/// </summary>
public sealed class NotificationTonePlayer
{
    private readonly Func<CoreWebView2?> _webView;
    private readonly Func<string> _uiBaseUrl;
    private readonly Func<string?> _customTonePath;

    public NotificationTonePlayer(
        Func<CoreWebView2?> webView,
        Func<string> uiBaseUrl,
        Func<string?> customTonePath)
    {
        _webView = webView;
        _uiBaseUrl = uiBaseUrl;
        _customTonePath = customTonePath;
    }

    public void Play()
    {
        try
        {
            var core = _webView();
            if (core is null) return;

            var url = ResolvePlayUrl();
            if (string.IsNullOrWhiteSpace(url)) return;

            var jsonUrl = System.Text.Json.JsonSerializer.Serialize(url);
            // Chromium plays common audio formats; keep a short-lived Audio element so GC doesn't cut it off.
            var script =
                "(function(){" +
                "try{" +
                $"var a=new Audio({jsonUrl});" +
                "a.volume=0.78;" +
                "window.__joduTone=a;" +
                "a.play().catch(function(){});" +
                "}catch(e){}" +
                "})();";
            _ = core.ExecuteScriptAsync(script);
        }
        catch
        {
            // tone is best-effort
        }
    }

    private string? ResolvePlayUrl()
    {
        var custom = _customTonePath();
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            var dataUrl = TryBuildDataUrl(custom);
            if (dataUrl is not null)
                return dataUrl;
        }

        return ResolveDefaultToneUrl();
    }

    private string ResolveDefaultToneUrl()
    {
        var baseUrl = _uiBaseUrl().TrimEnd('/');
        if (baseUrl.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/index.html".Length];

        // Dev: Vite serves public/ at root. Packaged: virtual host maps ui/dist (includes public assets).
        return $"{baseUrl}/notification.ogg";
    }

    private static string? TryBuildDataUrl(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024)
                return null;

            var mime = MimeFromExtension(Path.GetExtension(path));
            var b64 = Convert.ToBase64String(bytes);
            return $"data:{mime};base64,{b64}";
        }
        catch
        {
            return null;
        }
    }

    private static string MimeFromExtension(string? ext) =>
        (ext ?? string.Empty).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".webm" => "audio/webm",
            _ => "audio/ogg"
        };

    /// <summary>Resolves on-disk default tone for docs / packaging checks.</summary>
    public static string? FindToneFile()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ui", "notification.ogg"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ui", "public", "notification.ogg")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "ui", "public", "notification.ogg")),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
