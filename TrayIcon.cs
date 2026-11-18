using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BatteryTray;

// Own Shell_NotifyIcon wrapper: WinForms NotifyIcon ids change between runs, so Windows forgets icons the user
// dragged onto the taskbar. A GUID derived from exe path + device name keeps each device's identity stable.
sealed class TrayIcon : IDisposable
{
    const int WM_TRAY = 0x8001, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_GUID = 0x20;
    const int NIIF_WARNING = 0x02;

    static readonly List<TrayIcon> All = new();
    static MessageWindow? window;
    public static ContextMenuStrip? Menu;
    public static Action? LeftClick;

    readonly Guid id;
    IntPtr hIcon;
    string text = "";

    public TrayIcon(string key)
    {
        window ??= new MessageWindow();
        id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(Application.ExecutablePath.ToLowerInvariant() + "|" + key)));
        All.Add(this);
    }

    // Takes ownership of the HICON.
    public void Update(IntPtr newIcon, string tooltip)
    {
        var old = hIcon;
        hIcon = newIcon;
        text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (!Send(NIM_MODIFY)) Send(NIM_ADD);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    // Shown by Windows 10/11 as a toast notification coming from this icon.
    public void Warn(string title, string message) => Send(NIM_MODIFY, title, message);

    public void Dispose()
    {
        Send(NIM_DELETE);
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        hIcon = IntPtr.Zero;
        All.Remove(this);
    }

    bool Send(int message, string? infoTitle = null, string? info = null)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = window!.Handle,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | (info is null ? 0 : NIF_INFO),
            uCallbackMessage = WM_TRAY,
            hIcon = hIcon,
            szTip = text,
            szInfo = info ?? "",
            szInfoTitle = infoTitle ?? "",
            dwInfoFlags = info is null ? 0 : NIIF_WARNING,
            guidItem = id,
        };
        return Shell_NotifyIconW(message, ref data);
    }

    // Windows 11 keeps per-icon pins in the registry; pin our new icons once and respect the user's later choice.
    public static void PromoteOnWindows11()
    {
        const string root = @"Control Panel\NotifyIconSettings";
        using var settings = Registry.CurrentUser.OpenSubKey(root);
        if (settings is null) return; // Windows 10
        var exe = "\\" + Path.GetFileName(Application.ExecutablePath);
        foreach (var name in settings.GetSubKeyNames())
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{root}\{name}", writable: true);
            if (key?.GetValue("ExecutablePath") is string path && path.EndsWith(exe, StringComparison.OrdinalIgnoreCase) && key.GetValue("IsPromoted") is null)
                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
        }
    }

    sealed class MessageWindow : NativeWindow
    {
        static readonly int TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // Hidden top-level window (not message-only) so it also receives the TaskbarCreated broadcast.
        public MessageWindow() => CreateHandle(new CreateParams());

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TRAY && (int)m.LParam == WM_LBUTTONUP)
            {
                SetForegroundWindow(Handle); // allows the flyout to take focus
                LeftClick?.Invoke();
            }
            else if (m.Msg == WM_TRAY && (int)m.LParam == WM_RBUTTONUP && Menu is not null)
            {
                SetForegroundWindow(Handle); // lets the menu close when clicking elsewhere
                Menu.Show(Cursor.Position);
            }
            else if (m.Msg == TaskbarCreated)
            {
                foreach (var icon in All) icon.Send(NIM_ADD); // Explorer restarted
            }
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
