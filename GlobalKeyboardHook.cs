using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class GlobalKeyboardHook : IDisposable
{
    private LowLevelKeyboardProc _proc;
    private IntPtr _hookID = IntPtr.Zero;
    private readonly object _sync = new object();
    private bool _disposed;

    /// <summary>
    /// Fired on key down. Return true to consume the key (prevent further processing), false to pass it on.
    /// Must return quickly — Windows silently drops WH_KEYBOARD_LL hooks that take too long.
    /// </summary>
    public event Func<Keys, bool, bool> KeyPressed;

    public GlobalKeyboardHook()
    {
        _proc = HookCallback;
        InstallHook();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            RemoveHook();
        }
    }

    /// <summary>
    /// Re-install the low-level hook. Call after sleep/hibernate or if hotkeys stop responding
    /// (Windows can silently remove WH_KEYBOARD_LL hooks after timeout or power transitions).
    /// </summary>
    public void Reinstall()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RemoveHook();
            InstallHook();
        }
    }

    public bool IsHooked
    {
        get { lock (_sync) return _hookID != IntPtr.Zero; }
    }

    private void InstallHook()
    {
        if (_hookID != IntPtr.Zero) return;
        _hookID = SetHook(_proc);
    }

    private void RemoveHook()
    {
        if (_hookID == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hookID); } catch { }
        _hookID = IntPtr.Zero;
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            // GetAsyncKeyState, not Control.ModifierKeys: GetKeyState is per-thread and goes stale
            // when Playnite is not the foreground app (e.g. an exclusive-fullscreen game just
            // stepped aside), which made Alt+` unable to close the overlay.
            bool altPressed = (GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0 ||
                              (Control.ModifierKeys & Keys.Alt) == Keys.Alt;

            bool handled = false;
            try
            {
                handled = KeyPressed?.Invoke((Keys)vkCode, altPressed) ?? false;
            }
            catch
            {
                // Never let exceptions escape the hook — Windows will remove it
            }

            if (handled)
            {
                // Consume the key so the target application never receives it
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
