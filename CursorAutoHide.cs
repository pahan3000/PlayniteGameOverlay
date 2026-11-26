using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Hides the system mouse cursor when gamepad input is detected,
    /// and shows it again when the mouse is moved (console-style).
    /// Uses SetSystemCursor with a blank cursor (ShowCursor alone is unreliable on modern Windows).
    /// </summary>
    public sealed class CursorAutoHide : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetSystemCursor(IntPtr hcur, uint id);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private const uint SPI_SETCURSORS = 0x0057;
        private const uint SPIF_SENDCHANGE = 0x02;

        // System cursor IDs
        private const uint OCR_NORMAL = 32512;
        private const uint OCR_IBEAM = 32513;
        private const uint OCR_WAIT = 32514;
        private const uint OCR_CROSS = 32515;
        private const uint OCR_UP = 32516;
        private const uint OCR_SIZENWSE = 32642;
        private const uint OCR_SIZENESW = 32643;
        private const uint OCR_SIZEWE = 32644;
        private const uint OCR_SIZENS = 32645;
        private const uint OCR_SIZEALL = 32646;
        private const uint OCR_NO = 32648;
        private const uint OCR_HAND = 32649;
        private const uint OCR_APPSTARTING = 32650;

        private static readonly uint[] CursorIds =
        {
            OCR_NORMAL, OCR_IBEAM, OCR_WAIT, OCR_CROSS, OCR_UP,
            OCR_SIZENWSE, OCR_SIZENESW, OCR_SIZEWE, OCR_SIZENS, OCR_SIZEALL,
            OCR_NO, OCR_HAND, OCR_APPSTARTING
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        private readonly DispatcherTimer _timer;
        private readonly Func<bool> _isEnabled;
        private bool _hidden;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;
        private bool _started;

        private const int MoveThresholdPx = 2;

        public CursorAutoHide(Func<bool> isEnabled, int pollMs = 33)
        {
            _isEnabled = isEnabled ?? (() => true);
            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(16, pollMs))
            };
            _timer.Tick += (_, __) => Tick();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            CapturePos();
            _timer.Start();
        }

        public void Stop()
        {
            _started = false;
            try { _timer.Stop(); } catch { }
            ForceShow();
        }

        public void Dispose() => Stop();

        public void NotifyGamepadActivity()
        {
            if (!_isEnabled()) return;
            Hide();
        }

        public void ForceShow()
        {
            if (!_hidden) return;
            try
            {
                // Restore all system cursors from defaults
                // No WM_SETTINGCHANGE broadcast while a game is NtSuspended: the broadcast waits
                // for every top-level window and would stall the UI thread on the frozen one.
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, WindowHelper.SpiBroadcastFlag());

                int c = ShowCursor(true);
                int guard = 0;
                while (c < 0 && guard++ < 32)
                    c = ShowCursor(true);
            }
            catch { }
            _hidden = false;
            CapturePos();
        }

        private void Hide()
        {
            if (_hidden) return;
            try
            {
                // Replace every system cursor with a fully transparent one
                foreach (uint id in CursorIds)
                {
                    IntPtr blank = CreateBlankCursor();
                    if (blank == IntPtr.Zero) continue;
                    // SetSystemCursor takes ownership of the handle
                    if (!SetSystemCursor(blank, id))
                        DestroyIcon(blank);
                }

                int c = ShowCursor(false);
                int guard = 0;
                while (c >= 0 && guard++ < 32)
                    c = ShowCursor(false);
            }
            catch { }
            _hidden = true;
            CapturePos();
        }

        private static IntPtr CreateBlankCursor()
        {
            // 32x32 fully transparent cursor
            const int size = 32;
            int andMaskBytes = size * size / 8;
            byte[] andMask = new byte[andMaskBytes];
            for (int i = 0; i < andMask.Length; i++)
                andMask[i] = 0xFF; // all transparent in AND mask
            byte[] xorMask = new byte[andMaskBytes]; // all zero

            IntPtr andPtr = Marshal.AllocHGlobal(andMask.Length);
            IntPtr xorPtr = Marshal.AllocHGlobal(xorMask.Length);
            try
            {
                Marshal.Copy(andMask, 0, andPtr, andMask.Length);
                Marshal.Copy(xorMask, 0, xorPtr, xorMask.Length);

                IntPtr hAnd = CreateBitmap(size, size, 1, 1, andPtr);
                IntPtr hXor = CreateBitmap(size, size, 1, 1, xorPtr);
                if (hAnd == IntPtr.Zero || hXor == IntPtr.Zero)
                {
                    if (hAnd != IntPtr.Zero) DeleteObject(hAnd);
                    if (hXor != IntPtr.Zero) DeleteObject(hXor);
                    return IntPtr.Zero;
                }

                var ii = new ICONINFO
                {
                    fIcon = false,
                    xHotspot = 0,
                    yHotspot = 0,
                    hbmMask = hAnd,
                    hbmColor = hXor
                };
                IntPtr cursor = CreateIconIndirect(ref ii);
                DeleteObject(hAnd);
                DeleteObject(hXor);
                return cursor;
            }
            finally
            {
                Marshal.FreeHGlobal(andPtr);
                Marshal.FreeHGlobal(xorPtr);
            }
        }

        private void Tick()
        {
            if (!_isEnabled())
            {
                if (_hidden) ForceShow();
                return;
            }

            if (!_hidden) return;

            if (!GetCursorPos(out POINT p)) return;
            if (_lastX == int.MinValue)
            {
                _lastX = p.X;
                _lastY = p.Y;
                return;
            }

            int dx = Math.Abs(p.X - _lastX);
            int dy = Math.Abs(p.Y - _lastY);
            // Pad sticks / games often warp cursor slightly — only unhide on real movement
            if (dx >= MoveThresholdPx || dy >= MoveThresholdPx)
                ForceShow();
            else
            {
                _lastX = p.X;
                _lastY = p.Y;
            }
        }

        private void CapturePos()
        {
            if (GetCursorPos(out POINT p))
            {
                _lastX = p.X;
                _lastY = p.Y;
            }
        }
    }
}
