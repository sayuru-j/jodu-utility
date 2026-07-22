using Microsoft.Web.WebView2.Core;

namespace Jodu.Desktop.Services;

/// <summary>
/// Plays UI tones via WebView2 audio — packaged default OGG, or a custom file as a data URL.
/// </summary>
public sealed class NotificationTonePlayer
{
    private readonly Func<CoreWebView2?> _webView;
    private readonly Func<string> _uiBaseUrl;
    private readonly Func<string?> _customTonePath;
    private readonly string _defaultAssetFileName;
    private readonly string _jsSlot;

    public NotificationTonePlayer(
        Func<CoreWebView2?> webView,
        Func<string> uiBaseUrl,
        Func<string?> customTonePath,
        string defaultAssetFileName = "notification.ogg",
        string jsSlot = "__joduTone")
    {
        _webView = webView;
        _uiBaseUrl = uiBaseUrl;
        _customTonePath = customTonePath;
        _defaultAssetFileName = string.IsNullOrWhiteSpace(defaultAssetFileName)
            ? "notification.ogg"
            : defaultAssetFileName.Trim();
        _jsSlot = string.IsNullOrWhiteSpace(jsSlot) ? "__joduTone" : jsSlot.Trim();
    }

    public void Play(bool loop = false)
    {
        try
        {
            var core = _webView();
            if (core is null) return;

            var url = ResolvePlayUrl();
            if (string.IsNullOrWhiteSpace(url)) return;

            var jsonUrl = System.Text.Json.JsonSerializer.Serialize(url);
            var slot = System.Text.Json.JsonSerializer.Serialize(_jsSlot);
            var loopJs = loop ? "true" : "false";
            // Chromium plays common audio formats; keep a short-lived Audio element so GC doesn't cut it off.
            var script =
                "(function(){" +
                "try{" +
                $"var slot={slot};" +
                "var prev=window[slot];" +
                "if(prev){try{prev.pause();}catch(e){} try{prev.src='';}catch(e){}}" +
                $"var a=new Audio({jsonUrl});" +
                "a.volume=0.78;" +
                $"a.loop={loopJs};" +
                "window[slot]=a;" +
                "var done=function(){" +
                "if(window[slot]===a)window[slot]=null;" +
                "try{window.dispatchEvent(new CustomEvent('jodu-tone-ended',{detail:slot}));}catch(e){}" +
                "};" +
                "if(!a.loop)a.addEventListener('ended',done);" +
                "a.addEventListener('error',done);" +
                "a.play().catch(done);" +
                "}catch(e){}" +
                "})();";
            _ = core.ExecuteScriptAsync(script);
        }
        catch
        {
            // tone is best-effort
        }
    }

    public void Stop()
    {
        try
        {
            var core = _webView();
            if (core is null) return;

            var slot = System.Text.Json.JsonSerializer.Serialize(_jsSlot);
            var script =
                "(function(){" +
                "try{" +
                $"var a=window[{slot}];" +
                "if(a){try{a.pause();}catch(e){} try{a.currentTime=0;}catch(e){} try{a.src='';}catch(e){}" +
                $"window[{slot}]=null;" +
                "}" +
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
        return $"{baseUrl}/{_defaultAssetFileName}";
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
}
