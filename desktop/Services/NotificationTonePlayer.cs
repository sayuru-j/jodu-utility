using Microsoft.Web.WebView2.Core;

namespace Jodu.Desktop.Services;

/// <summary>
/// Plays <c>notification.ogg</c> for phone notification toasts (via WebView2 audio).
/// </summary>
public sealed class NotificationTonePlayer
{
    private readonly Func<CoreWebView2?> _webView;
    private readonly Func<string> _uiBaseUrl;

    public NotificationTonePlayer(Func<CoreWebView2?> webView, Func<string> uiBaseUrl)
    {
        _webView = webView;
        _uiBaseUrl = uiBaseUrl;
    }

    public void Play()
    {
        try
        {
            var core = _webView();
            if (core is null) return;

            var url = ResolveToneUrl();
            var jsonUrl = System.Text.Json.JsonSerializer.Serialize(url);
            // Chromium plays OGG; keep a short-lived Audio element so GC doesn't cut it off.
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

    private string ResolveToneUrl()
    {
        var baseUrl = _uiBaseUrl().TrimEnd('/');
        if (baseUrl.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/index.html".Length];

        // Dev: Vite serves public/ at root. Packaged: virtual host maps ui/dist (includes public assets).
        return $"{baseUrl}/notification.ogg";
    }

    /// <summary>Resolves on-disk tone for docs / packaging checks.</summary>
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
