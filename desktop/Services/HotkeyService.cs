using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jodu.Desktop.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4A4F; // "JO"
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int VkC = 0x43;
    private const int WmHotkey = 0x0312;

    private readonly NativeWindow _window;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyService()
    {
        _window = new HotkeyWindow(msg =>
        {
            if (msg.Msg == WmHotkey && (int)msg.WParam == HotkeyId)
                HotkeyPressed?.Invoke();
        });
    }

    public bool Register()
    {
        _registered = RegisterHotKey(_window.Handle, HotkeyId, ModControl | ModShift, VkC);
        return _registered;
    }

    public void Dispose()
    {
        if (_registered)
            UnregisterHotKey(_window.Handle, HotkeyId);
        _window.DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action<Message> _onMessage;

        public HotkeyWindow(Action<Message> onMessage)
        {
            _onMessage = onMessage;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            _onMessage(m);
            base.WndProc(ref m);
        }
    }
}
