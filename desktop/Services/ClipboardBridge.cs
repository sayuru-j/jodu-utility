namespace Jodu.Desktop.Services;

public sealed class ClipboardBridge
{
    private string _mobileClipboard = string.Empty;
    private bool _suppressEcho;

    public string MobileClipboard
    {
        get => _mobileClipboard;
        private set => _mobileClipboard = value ?? string.Empty;
    }

    public void SetFromPhone(string text)
    {
        MobileClipboard = text;
    }

    public void CopyMobileToWindows()
    {
        if (string.IsNullOrEmpty(MobileClipboard)) return;

        _suppressEcho = true;
        try
        {
            Clipboard.SetText(MobileClipboard);
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                _suppressEcho = false;
            });
        }
    }

    public void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _suppressEcho = true;
        try
        {
            Clipboard.SetText(text);
            MobileClipboard = text;
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                _suppressEcho = false;
            });
        }
    }

    public bool ShouldSuppressEcho => _suppressEcho;
}
