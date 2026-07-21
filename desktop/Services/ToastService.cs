using Microsoft.Toolkit.Uwp.Notifications;

namespace Jodu.Desktop.Services;

public sealed class ToastService
{
    public const string CopyCodeAction = "copy_otp";
    public const string OtpArgKey = "otp";

    public event Action<string>? CopyOtpRequested;

    public ToastService()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += args =>
            {
                var parsed = ToastArguments.Parse(args.Argument);
                if (parsed.TryGetValue("action", out var action) &&
                    action == CopyCodeAction &&
                    parsed.TryGetValue(OtpArgKey, out var code))
                {
                    CopyOtpRequested?.Invoke(code);
                }
            };
        }
        catch
        {
            // Toast activation wiring is best-effort on unpackaged apps.
        }
    }

    public void ShowOtp(string code, string? sender)
    {
        var title = string.IsNullOrWhiteSpace(sender) ? "OTP detected" : $"OTP from {sender}";
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText($"Code: {code}")
                .AddButton(new ToastButton()
                    .SetContent("Copy Code")
                    .AddArgument("action", CopyCodeAction)
                    .AddArgument(OtpArgKey, code))
                .Show();
        }
        catch
        {
            FallbackBalloon?.Invoke(title, $"Code: {code} — press Ctrl+Shift+C to copy");
            CopyOtpRequested?.Invoke(code);
        }
    }

    public void ShowInfo(string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show();
        }
        catch
        {
            FallbackBalloon?.Invoke(title, body);
            System.Diagnostics.Debug.WriteLine($"[JODU] {title}: {body}");
        }
    }

    /// <summary>Optional tray balloon when WinRT toasts are unavailable.</summary>
    public Action<string, string>? FallbackBalloon { get; set; }
}
