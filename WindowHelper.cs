using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PlayniteGameOverlay
{
    public static class WindowHelper
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_MAXIMIZE = 3;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;
        public const int SW_FORCEMINIMIZE = 11;

        private const uint WM_SYSCOMMAND = 0x0112;
        private const uint WM_CANCELMODE = 0x001F;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int SC_MINIMIZE = 0xF020;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        /// <summary>Reset display mode to registry defaults — clears exclusive-fullscreen black surfaces.</summary>
        [DllImport("user32.dll")]
        private static extern int ChangeDisplaySettings(IntPtr lpDevMode, int dwFlags);

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int ShowCursor(bool bShow);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint THREAD_SUSPEND_RESUME = 0x0002;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        /// <summary>
        /// Reliable foreground activation (AttachThreadInput + restore + bring-to-top).
        /// Needed so "Return to game" / "Return to Playnite" work on the first click.
        /// </summary>
        public static void ForceFocusWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            bool attachedFg = false;
            bool attachedTarget = false;
            uint currentThread = 0;
            uint foregroundThread = 0;
            uint targetThread = 0;

            try
            {
                ReleaseMouseLock(nudgeCursor: false);

                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                else
                    ShowWindow(hwnd, SW_SHOW);

                IntPtr foregroundWindow = GetForegroundWindow();
                foregroundThread = GetWindowThreadProcessId(foregroundWindow, out _);
                targetThread = GetWindowThreadProcessId(hwnd, out uint targetPid);
                currentThread = GetCurrentThreadId();

                // Allow us / target to take foreground
                try { AllowSetForegroundWindow(-1); } catch { }
                try { AllowSetForegroundWindow((int)targetPid); } catch { }

                // Prefer SafeRestore when target looks dead/unresponsive — AttachThreadInput
                // to a dying game thread can hang Playnite's UI until restart.
                if (targetPid != 0)
                {
                    try
                    {
                        using (var p = Process.GetProcessById((int)targetPid))
                        {
                            if (p.HasExited)
                            {
                                SafeRestoreAndFocus(hwnd);
                                return;
                            }
                        }
                    }
                    catch
                    {
                        SafeRestoreAndFocus(hwnd);
                        return;
                    }
                }

                if (foregroundThread != 0 && foregroundThread != currentThread)
                    attachedFg = AttachThreadInput(currentThread, foregroundThread, true);
                if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                    attachedTarget = AttachThreadInput(currentThread, targetThread, true);

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetForegroundWindow(hwnd);
            }
            catch
            {
                try { SetForegroundWindow(hwnd); } catch { }
            }
            finally
            {
                // Always detach — leaked AttachThreadInput is a known cause of
                // "Playnite taskbar icon does nothing" after quitting a game.
                try
                {
                    if (attachedTarget)
                        AttachThreadInput(currentThread, targetThread, false);
                }
                catch { }
                try
                {
                    if (attachedFg)
                        AttachThreadInput(currentThread, foregroundThread, false);
                }
                catch { }
            }
        }

        /// <summary>Find the main visible window for a process name (e.g. Playnite.DesktopApp).</summary>
        public static IntPtr FindMainWindowByProcessName(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var p in processes)
                {
                    try
                    {
                        var hwnd = FindMainWindow(p.Id);
                        if (hwnd != IntPtr.Zero)
                            return hwnd;
                        if (p.MainWindowHandle != IntPtr.Zero)
                            return p.MainWindowHandle;
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Finds a hidden, titled top-level window for a process. Used only as a tray fallback:
        /// the normal main-window lookup intentionally ignores hidden windows.
        /// </summary>
        public static IntPtr FindHiddenMainWindowByProcessName(string processName)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        int processId = process.Id;
                        IntPtr main = process.MainWindowHandle;
                        if (main != IntPtr.Zero && !IsWindowVisible(main))
                            return main;

                        IntPtr best = IntPtr.Zero;
                        int bestTitleLength = 0;
                        EnumWindows((hwnd, lParam) =>
                        {
                            try
                            {
                                GetWindowThreadProcessId(hwnd, out uint ownerPid);
                                if ((int)ownerPid != processId || IsWindowVisible(hwnd) ||
                                    GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero)
                                    return true;

                                int titleLength = GetWindowTextLength(hwnd);
                                if (titleLength == 0)
                                    return true;

                                int exStyle = GetWindowLongInt(hwnd, -20 /* GWL_EXSTYLE */);
                                bool toolWindow = (exStyle & 0x00000080) != 0 &&
                                                  (exStyle & 0x00040000 /* WS_EX_APPWINDOW */) == 0;
                                if (toolWindow)
                                    return true;

                                if (titleLength > bestTitleLength)
                                {
                                    best = hwnd;
                                    bestTitleLength = titleLength;
                                }
                            }
                            catch { }
                            return true;
                        }, IntPtr.Zero);

                        if (best != IntPtr.Zero)
                            return best;
                    }
                    catch { }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Releases relative-mouse / cursor lock that games often hold while focused
        /// (FPS-style center lock). Safe to call repeatedly.
        /// </summary>
        /// <param name="nudgeCursor">
        /// When true, moves the cursor slightly off center once (use on overlay open only).
        /// Leave false for periodic refreshes so we do not fight the user moving the mouse.
        /// </param>
        private const uint SPI_SETCURSORS = 0x0057;
        private const uint SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        public static void ReleaseMouseLock(
            bool nudgeCursor = false,
            bool restoreCursors = true,
            bool releaseCapture = true)
        {
            try
            {
                ClipCursor(IntPtr.Zero);
                // WPF controls rely on mouse capture between button-down and button-up.
                // The periodic overlay refresh must not release that capture, or clicks
                // can be cancelled before WPF raises Click.
                if (releaseCapture)
                    ReleaseCapture();

                // Restore default system cursors. CursorAutoHide replaces them with
                // blank icons via SetSystemCursor; ShowCursor alone cannot undo that.
                // restoreCursors=false is used by the 150 ms "keep the mouse free" timer:
                // reloading cursors that often is pointless.
                if (restoreCursors)
                {
                    try
                    {
                        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SpiBroadcastFlag());
                    }
                    catch { }
                }

                int count = ShowCursor(true);
                int guard = 0;
                while (count < 0 && guard++ < 32)
                    count = ShowCursor(true);

                if (nudgeCursor)
                {
                    int cx = GetSystemMetrics(SM_CXSCREEN) / 2;
                    int cy = GetSystemMetrics(SM_CYSCREEN) / 2;
                    SetCursorPos(cx + 40, cy + 40);
                }
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// SPIF_SENDCHANGE makes SystemParametersInfo broadcast WM_SETTINGCHANGE to every top-level
        /// window and wait for them. A window owned by an NtSuspended process cannot answer, so the
        /// caller (Playnite's UI thread) stalls — the overlay then never paints while the game is
        /// frozen. Never request the broadcast while anything is frozen.
        /// </summary>
        public static uint SpiBroadcastFlag()
        {
            if (IsAnyProcessFrozen) return 0u;
            // Right after NtResume the game's windows are not pumping messages yet, and the
            // broadcast blocks the caller (Playnite's UI thread) until every top-level window
            // answers or times out. Skip it for a moment after any resume.
            if (unchecked(Environment.TickCount - _noSpiBroadcastUntilTick) < 0) return 0u;
            return SPIF_SENDCHANGE;
        }

        private static int _noSpiBroadcastUntilTick = Environment.TickCount - 1;

        // PlayState-style: NtSuspendProcess / NtResumeProcess (not per-thread SuspendThread).
        // PROCESS_SUSPEND_RESUME = 0x0800
        private const uint PROCESS_SUSPEND_RESUME = 0x0800;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private delegate int NtSuspendProcessDelegate(IntPtr processHandle);
        private delegate int NtResumeProcessDelegate(IntPtr processHandle);

        private static NtSuspendProcessDelegate _ntSuspend;
        private static NtResumeProcessDelegate _ntResume;
        private static bool _ntResolved;

        // How many times WE suspended each PID. NtResumeProcess must be called exactly that many
        // times — never "a few extra just in case". Emulators (Xenia, ...) keep their own
        // per-thread suspend counts for guest threads; every surplus NtResumeProcess silently
        // releases those guest threads and corrupts the emulated machine.
        private static readonly Dictionary<int, int> _suspendDepth = new Dictionary<int, int>();
        private static readonly object _depthLock = new object();

        /// <summary>True while this plugin has any process NtSuspended.</summary>
        public static bool IsAnyProcessFrozen
        {
            get { lock (_depthLock) return _suspendDepth.Count > 0; }
        }

        private static readonly Playnite.SDK.ILogger _wlog = Playnite.SDK.LogManager.GetLogger("GameOverlay.WindowHelper");

        private static readonly List<int> _suspendedPids = new List<int>();
        private static int _suspendedProcessId; // primary pid (compat with older code)
        private static readonly List<IntPtr> _suspendedThreadHandles = new List<IntPtr>(); // unused legacy

        /// <summary>True when the user intentionally parked the game (suspend + minimize).</summary>
        public static bool IsParked { get; private set; }

        private static IntPtr _parkedWindowHwnd = IntPtr.Zero;
        private static int _parkedProcessId;
        public static int ParkedProcessId => _parkedProcessId;

        // PlayState-style: accumulate time spent suspended for playtime subtraction
        private static DateTime? _suspendSegmentStartedUtc;
        private static long _accumulatedSuspendedSeconds;

        /// <summary>True while the game is parked or paused by the overlay (time is counted as suspended).</summary>
        public static bool IsSuspendSegmentActive => _suspendSegmentStartedUtc != null;

        public static long ConsumeAccumulatedSuspendedSeconds()
        {
            FlushSuspendSegment();
            long s = _accumulatedSuspendedSeconds;
            _accumulatedSuspendedSeconds = 0;
            return s;
        }

        public static void ResetSuspendedTracking()
        {
            _suspendSegmentStartedUtc = null;
            _accumulatedSuspendedSeconds = 0;
        }

        /// <summary>
        /// Full cleanup for game start/stop: unfreeze any leftover NtSuspend targets and
        /// clear park flags. Does NOT restore/focus windows (that raced with game launch
        /// and could hang or destabilize Playnite).
        /// </summary>
        public static void ForceClearAllSuspendState()
        {
            try
            {
                // Resume frozen PIDs only — skip SafeRestoreAndFocus / ShowWindow during launch
                int pid = _parkedProcessId > 0 ? _parkedProcessId : _suspendedProcessId;
                try { ResumeGameProcess(); } catch { }
                if (pid > 0)
                {
                    try { NtResumePid(pid); } catch { }
                    try
                    {
                        foreach (var child in GetDirectChildPids(pid))
                            try { NtResumePid(child); } catch { }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                _parkedWindowHwnd = IntPtr.Zero;
                _parkedProcessId = 0;
                IsParked = false;
                _suspendedProcessId = 0;
                _suspendedPids.Clear();
                Interlocked.Exchange(ref _parkBusy, 0);
            }
            catch { }
            ResetSuspendedTracking();
        }

        private static void BeginSuspendSegment()
        {
            if (_suspendSegmentStartedUtc == null)
                _suspendSegmentStartedUtc = DateTime.UtcNow;
        }

        private static void FlushSuspendSegment()
        {
            if (_suspendSegmentStartedUtc == null) return;
            var sec = (long)Math.Max(0, (DateTime.UtcNow - _suspendSegmentStartedUtc.Value).TotalSeconds);
            _accumulatedSuspendedSeconds += sec;
            _suspendSegmentStartedUtc = null;
        }


        private static void EnsureNtApis()
        {
            if (_ntResolved) return;
            _ntResolved = true;
            try
            {
                IntPtr ntdll = GetModuleHandle("ntdll.dll");
                if (ntdll == IntPtr.Zero) return;
                IntPtr pSus = GetProcAddress(ntdll, "NtSuspendProcess");
                IntPtr pRes = GetProcAddress(ntdll, "NtResumeProcess");
                if (pSus != IntPtr.Zero)
                    _ntSuspend = (NtSuspendProcessDelegate)Marshal.GetDelegateForFunctionPointer(pSus, typeof(NtSuspendProcessDelegate));
                if (pRes != IntPtr.Zero)
                    _ntResume = (NtResumeProcessDelegate)Marshal.GetDelegateForFunctionPointer(pRes, typeof(NtResumeProcessDelegate));
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        /// <summary>
        /// Never suspend Playnite itself or other critical hosts — that freezes the whole UI
        /// ("Playnite (Not Responding)") and requires killing the process.
        /// </summary>
        public static bool IsProtectedProcess(int pid)
        {
            if (pid <= 0) return true;
            try
            {
                int self = Process.GetCurrentProcess().Id;
                if (pid == self) return true;
            }
            catch { return true; }

            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    string name = (p.ProcessName ?? "").ToLowerInvariant();
                    // Playnite hosts + common helpers that must stay alive
                    if (string.IsNullOrEmpty(name)) return true;
                    if (name.Contains("playnite")) return true;
                    if (name.Contains("fullscreenapp")) return true;
                    if (name == "explorer") return true;
                    if (name == "dwm") return true;
                    if (name == "csrss") return true;
                    if (name == "winlogon") return true;
                    if (name == "services") return true;
                    if (name == "lsass") return true;
                    if (name == "system") return true;
                    if (name == "svchost") return true;
                    if (name == "runtimebroker") return true;
                    if (name == "applicationframehost") return true;
                    if (name == "shellexperiencehost") return true;
                    if (name == "searchhost") return true;
                    if (name == "startmenuexperiencehost") return true;
                }
            }
            catch
            {
                // Cannot identify the process — refuse to suspend/kill (safer than guessing)
                return true;
            }
            return false;
        }

        private static bool NtSuspendPid(int pid)
        {
            if (pid <= 0) return false;
            if (IsProtectedProcess(pid))
                return false; // refuse — never freeze Playnite / system
            EnsureNtApis();
            if (_ntSuspend == null) return false;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                    h = OpenProcess(0x1F0FFF, false, pid); // PROCESS_ALL_ACCESS fallback
                if (h == IntPtr.Zero) return false;
                if (_ntSuspend(h) < 0) return false;
                lock (_depthLock)
                {
                    int d;
                    _suspendDepth.TryGetValue(pid, out d);
                    _suspendDepth[pid] = d + 1;
                }
                return true;
            }
            catch { return false; }
            finally
            {
                if (h != IntPtr.Zero) try { CloseHandle(h); } catch { }
            }
        }

        private static bool NtResumePid(int pid)
        {
            if (pid <= 0) return false;

            // Undo only what this class did. If we never suspended the PID there is nothing to
            // resume, and calling NtResumeProcess anyway would decrement suspend counts that
            // belong to the game itself.
            int owed;
            lock (_depthLock)
            {
                if (!_suspendDepth.TryGetValue(pid, out owed) || owed <= 0)
                {
                    _suspendDepth.Remove(pid);
                    return false;
                }
                _suspendDepth.Remove(pid);
            }

            EnsureNtApis();
            if (_ntResume == null) { RestoreDepth(pid, owed); return false; }

            IntPtr h = IntPtr.Zero;
            int remaining = owed;
            try
            {
                h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                    h = OpenProcess(0x1F0FFF, false, pid);
                if (h == IntPtr.Zero) return false;

                while (remaining > 0)
                {
                    if (_ntResume(h) >= 0) remaining--;
                    else break;
                }
                return remaining < owed;
            }
            catch { return false; }
            finally
            {
                if (h != IntPtr.Zero) try { CloseHandle(h); } catch { }
                // Resume failed part-way: keep the debt only if the process still exists,
                // otherwise a recycled PID would later get resumed by mistake.
                if (remaining > 0 && IsPidAlive(pid))
                    RestoreDepth(pid, remaining);
            }
        }

        private static void RestoreDepth(int pid, int count)
        {
            if (count <= 0) return;
            lock (_depthLock)
            {
                int d;
                _suspendDepth.TryGetValue(pid, out d);
                _suspendDepth[pid] = d + count;
            }
        }

        private static bool IsPidAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>
        /// Restore + focus without AttachThreadInput. Attaching to a thread that is still
        /// (or was just) NtSuspended can hang the UI thread → Playnite "Not Responding".
        /// Only SW_RESTORE when iconic — extra SW_SHOW/BringWindowToTop can upset DX12 titles.
        /// </summary>
        public static void SafeRestoreAndFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (IsIconic(hwnd))
                {
                    RequestRestoreAsync(hwnd);
                    WaitNotIconic(hwnd, 600);
                    // Still minimized (game was busy waking up): ask once more.
                    if (IsIconic(hwnd))
                    {
                        RequestRestoreAsync(hwnd);
                        WaitNotIconic(hwnd, 600);
                    }
                }
                else if (!IsWindowVisible(hwnd))
                    ShowWindowAsync(hwnd, SW_SHOW);

                try { AllowSetForegroundWindow(-1); } catch { }
                GetWindowThreadProcessId(hwnd, out uint targetPid);
                if (targetPid > 0)
                {
                    try { AllowSetForegroundWindow((int)targetPid); } catch { }
                }

                // Windows often refuses the first SetForegroundWindow (focus-stealing rules, a game
                // that is still waking up, borderless windows left behind). Try escalating methods
                // and stop as soon as the game really is in front. No AttachThreadInput here.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (IsInForeground(hwnd, targetPid)) return;

                    if (attempt == 0)
                    {
                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                    }
                    else if (attempt == 1)
                    {
                        // A synthetic Alt press/release lifts the foreground lock.
                        try
                        {
                            keybd_event(0x12, 0, 0, UIntPtr.Zero);
                            keybd_event(0x12, 0, 2, UIntPtr.Zero);
                        }
                        catch { }
                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                    }
                    else
                    {
                        try { SwitchToThisWindow(hwnd, true); } catch { }
                        try
                        {
                            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        }
                        catch { }
                        SetForegroundWindow(hwnd);
                    }

                    Thread.Sleep(60);
                }
            }
            catch
            {
                try { SetForegroundWindow(hwnd); } catch { }
            }
        }

        private static bool IsInForeground(IntPtr hwnd, uint targetPid)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                if (fg == hwnd) return true;
                GetWindowThreadProcessId(fg, out uint fgPid);
                return targetPid != 0 && fgPid == targetPid;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        /// <summary>
        /// Collect primary PID + direct children (launchers often leave the real game as a child).
        /// </summary>
        private static List<int> CollectProcessTree(int rootPid)
        {
            var result = new List<int>();
            if (rootPid <= 0) return result;
            result.Add(rootPid);
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == rootPid) continue;
                        int parent = 0;
                        try
                        {
                            // reuse toolhelp-style parent via Performance is heavy; use simple MainModule skip
                        }
                        catch { }
                        p.Dispose();
                    }
                    catch { }
                }
            }
            catch { }

            // Parent lookup via NtQueryInformationProcess is already used elsewhere in the plugin;
            // keep tree simple: root + any process whose parent is root.
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (GetParentProcessIdSafe(p.Id) == rootPid && !result.Contains(p.Id))
                            result.Add(p.Id);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return result;
        }

        private static int GetParentProcessIdSafe(int pid)
        {
            try
            {
                // Lightweight: Toolhelp
                return GetParentPidToolhelp(pid);
            }
            catch { return 0; }
        }

        private static int GetParentPidToolhelp(int pid)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snap, ref pe)) return 0;
                do
                {
                    if (pe.th32ProcessID == (uint)pid)
                        return (int)pe.th32ParentProcessID;
                } while (Process32Next(snap, ref pe));
            }
            finally { CloseHandle(snap); }
            return 0;
        }

        private const uint TH32CS_SNAPPROCESS = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        /// <summary>
        /// Soft-suspend while overlay is open (PlayState: NtSuspendProcess).
        /// </summary>
        public static void SuspendGameProcess(int processId)
        {
            ResumeGameProcess();
            if (processId <= 0) return;
            // Never soft-suspend Playnite / system even if a caller passed a bad PID
            if (IsProtectedProcess(processId))
                return;
            if (NtSuspendPid(processId))
                _suspendedPids.Add(processId);
            try
            {
                foreach (var child in GetDirectChildPids(processId))
                {
                    if (IsProtectedProcess(child))
                        continue;
                    if (NtSuspendPid(child) && !_suspendedPids.Contains(child))
                        _suspendedPids.Add(child);
                }
            }
            catch { }
            _suspendedProcessId = processId;
            if (_suspendedPids.Count > 0)
                BeginSuspendSegment();
        }

        /// <summary>
        /// Optional opt-out list: process-name fragments that must not be frozen automatically
        /// when the overlay opens. Empty by default. If an emulator (e.g. Xenia) still crashes
        /// after resume even with balanced suspend/resume, add "xenia" here. The explicit
        /// Suspend button (park) is not affected.
        /// </summary>
        private static readonly string[] SoftPauseBlockedNames = { }; // e.g. { "xenia" } to exempt an emulator

        private static bool LooksLikeUnrealEngine(string exePath, string procName)
        {
            try
            {
                if (!string.IsNullOrEmpty(procName) &&
                    procName.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.IsNullOrEmpty(exePath)) return false;
                string norm = exePath.Replace('/', '\\');
                if (norm.IndexOf("\\Binaries\\Win64\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    norm.IndexOf("\\Binaries\\WinGDK\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                // <Game>\<Name>\Binaries\Win64\x.exe or a root launcher next to an "Engine" folder
                string dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(dir) &&
                    Directory.Exists(Path.Combine(dir, "Engine", "Binaries")))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Easy Anti-Cheat games expect regular heartbeats from the client; freezing the process
        /// can get the player kicked. Looks for EAC files in the exe folder or up to 4 parents
        /// (UE games keep the exe in Game\\Binaries\\Win64 and EAC at the install root).
        /// </summary>
        private static bool LooksLikeEasyAntiCheat(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath)) return false;
                string dir = Path.GetDirectoryName(exePath);
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (Directory.Exists(Path.Combine(dir, "EasyAntiCheat")) ||
                        Directory.Exists(Path.Combine(dir, "EasyAntiCheat_EOS")) ||
                        File.Exists(Path.Combine(dir, "start_protected_game.exe")) ||
                        File.Exists(Path.Combine(dir, "EasyAntiCheat_EOS_Setup.exe")) ||
                        File.Exists(Path.Combine(dir, "EasyAntiCheat_Setup.exe")))
                        return true;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return false;
        }

        public static bool IsSoftPauseUnsafe(int processId)
        {
            try
            {
                using (var p = Process.GetProcessById(processId))
                {
                    string name = (p.ProcessName ?? "").ToLowerInvariant();
                    foreach (var frag in SoftPauseBlockedNames)
                        if (name.Contains(frag)) return true;

                    // Unreal Engine games crash after NtSuspend/Resume: never freeze them.
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (LooksLikeUnrealEngine(path, p.ProcessName))
                    {
                        _wlog.Info($"SoftPause skipped for '{p.ProcessName}': Unreal Engine game.");
                        return true;
                    }

                    // Easy Anti-Cheat games: a frozen client misses heartbeats and can be kicked.
                    if (LooksLikeEasyAntiCheat(path))
                    {
                        _wlog.Info($"SoftPause skipped for '{p.ProcessName}': Easy Anti-Cheat game.");
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>True when the foreground window belongs to this (Playnite) process.</summary>
        public static bool IsOwnProcessForeground()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                return (int)pid == Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        /// <summary>"pid:processname" of the current foreground window (diagnostics only).</summary>
        public static string DescribeForeground()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return "none";
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                using (var p = Process.GetProcessById((int)pid))
                    return pid + ":" + p.ProcessName;
            }
            catch { return "unknown"; }
        }

        /// <summary>Overlay HWND is visible and not minimized.</summary>
        public static bool IsWindowShownAndRestored(IntPtr hwnd)
        {
            try { return hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd); }
            catch { return false; }
        }

        private static bool _softPausedForOverlay;
        private static IntPtr _softPauseHwnd = IntPtr.Zero;
        private static int _softPausePid;

        /// <summary>
        /// Pause the game in place while the overlay is open — no minimize.
        /// Suspends only the main game PID (not children) to avoid leaving GPU helpers
        /// frozen. NtSuspend can still crash some DX12/UE5 titles — that is a driver
        /// limitation; disable "Pause game while overlay open" for those games.
        /// Suspend button still does full park (minimize + main + children).
        /// </summary>
        public static void SoftPauseForOverlay(int processId)
        {
            Breadcrumb.Write("WindowHelper.SoftPauseForOverlay pid=" + processId + "");
            if (processId <= 0 || IsProtectedProcess(processId)) return;
            if (IsParked) return; // park owns the freeze
            if (IsSoftPauseUnsafe(processId))
            {
                _wlog.Info($"SoftPause skipped for PID {processId} (process is on the never-auto-freeze list).");
                return;
            }

            try { SoftResumeFromOverlay(); } catch { }
            try { ResumeGameProcess(); } catch { }

            _softPausedForOverlay = false;
            _softPauseHwnd = IntPtr.Zero;
            _softPausePid = 0;

            IntPtr hwnd = FindMainWindow(processId);
            if (hwnd == IntPtr.Zero)
            {
                try
                {
                    using (var p = Process.GetProcessById(processId))
                        hwnd = p.MainWindowHandle;
                }
                catch { }
            }

            // A fullscreen game can retain mouse capture while it is frozen. Cancel its
            // mouse mode before suspending so pointer movement is delivered to the overlay.
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    SendMessageTimeout(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero,
                        SMTO_ABORTIFHUNG, 100, out _);
                }
                catch { }
            }
            try
            {
                // Clear clipping without releasing capture owned by WPF on this thread.
                ReleaseMouseLock(nudgeCursor: false, restoreCursors: false, releaseCapture: false);
            }
            catch { }

            // Main PID only — do not SuspendGameProcess (that freezes children too).
            if (NtSuspendPid(processId))
            {
                // A final clear catches a clip the game re-applied just before suspension.
                try { ClipCursor(IntPtr.Zero); } catch { }
                if (!_suspendedPids.Contains(processId))
                    _suspendedPids.Add(processId);
                _suspendedProcessId = processId;
                BeginSuspendSegment();
                _softPausedForOverlay = true;
                _softPauseHwnd = hwnd;
                _softPausePid = processId;
                _wlog.Info($"SoftPause: froze PID {processId}.");
            }
        }

        /// <summary>Unfreeze after overlay soft-pause. Resume is counted; call multiple times.</summary>
        public static void SoftResumeFromOverlay()
        {
            Breadcrumb.Write("WindowHelper.SoftResumeFromOverlay");
            int pid = _softPausePid > 0 ? _softPausePid : _suspendedProcessId;

            _softPausedForOverlay = false;
            _softPauseHwnd = IntPtr.Zero;
            _softPausePid = 0;

            // ResumeGameProcess settles the whole ledger (balanced, one resume per suspend).
            try { ResumeGameProcess(); } catch { }
            if (pid > 0)
            {
                try { NtResumePid(pid); } catch { } // no-op unless something is still owed
            }
        }

        public static bool IsSoftPausedForOverlay => _softPausedForOverlay;

        private static int _lastResumeTick;

        /// <summary>
        /// A game that was just NtResumed must get a moment to wake (and its GPU driver to settle)
        /// before its window is touched; restoring a DX12 swap chain in the same instant can make
        /// the game fail to come back. No-op when nothing was resumed recently.
        /// </summary>
        public static void SettleAfterResume(int settleMs = 220)
        {
            try
            {
                int last = _lastResumeTick;
                if (last == 0) return;
                int since = unchecked(Environment.TickCount - last);
                if (since >= 0 && since < settleMs)
                    Thread.Sleep(settleMs - since);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private const int SC_RESTORE_CMD = 0xF120;

        /// <summary>
        /// Ask a game window to restore WITHOUT blocking: a synchronous ShowWindow on a window whose
        /// thread is not pumping messages (just resumed, or hung) freezes the caller, which for
        /// Playnite's UI thread means "Not Responding".
        /// </summary>
        private static void RequestRestoreAsync(IntPtr hwnd)
        {
            try { PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_RESTORE_CMD, IntPtr.Zero); } catch { }
            try { ShowWindowAsync(hwnd, SW_RESTORE); } catch { }
        }

        private static void WaitNotIconic(IntPtr hwnd, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (!IsIconic(hwnd)) return; } catch { return; }
                Thread.Sleep(15);
                waited += 15;
            }
        }

        /// <summary>
        /// Resume any NtSuspendProcess targets.
        /// </summary>
        public static void ResumeGameProcess()
        {
            Breadcrumb.Write("WindowHelper.ResumeGameProcess");
            _noSpiBroadcastUntilTick = unchecked(Environment.TickCount + 3000);
            bool hadSuspended = _suspendedPids.Count > 0 || _suspendedThreadHandles.Count > 0;
            if (!hadSuspended)
            {
                try { lock (_depthLock) hadSuspended = _suspendDepth.Count > 0; } catch { }
            }
            if (hadSuspended)
                _lastResumeTick = Environment.TickCount;
            FlushSuspendSegment();
            foreach (var pid in _suspendedPids.ToList())
            {
                try { NtResumePid(pid); } catch { }
            }
            _suspendedPids.Clear();
            _suspendedProcessId = 0;

            // The ledger is authoritative: anything still owed gets resumed here, exactly once.
            List<int> owedPids;
            lock (_depthLock) owedPids = _suspendDepth.Keys.ToList();
            foreach (var pid in owedPids)
            {
                try { NtResumePid(pid); } catch { }
            }

            foreach (var h in _suspendedThreadHandles)
            {
                try
                {
                    int guard = 0;
                    while (ResumeThread(h) > 1 && guard++ < 64) { }
                }
                catch { }
                try { CloseHandle(h); } catch { }
            }
            _suspendedThreadHandles.Clear();
        }

        /// <summary>
        /// Park: minimize the game window, wait until it is iconic (or timeout),
        /// then NtSuspendProcess on the game PID and direct children.
        /// Waiting is required — exclusive-fullscreen / many engines only process
        /// SC_MINIMIZE on their message loop; suspending immediately freezes the
        /// window on-screen without minimizing.
        /// </summary>
        private static int _parkBusy; // 0 = idle, 1 = park/unpark in progress

        /// <summary>
        /// Park: minimize the game window, wait until it is iconic (or timeout),
        /// then NtSuspendProcess on the game PID and direct children.
        /// Protected against re-entry and never suspends Playnite itself.
        /// </summary>
        public static void SuspendAndMinimize(int processId, bool minimizeWindow = true, bool freezeProcess = true)
        {
            Breadcrumb.Write("WindowHelper.SuspendAndMinimize pid=" + processId + " minimize=" + minimizeWindow + " freeze=" + freezeProcess);
            if (processId <= 0) return;
            if (IsProtectedProcess(processId))
                return; // never park Playnite / system processes

            // Serialize park/unpark — multiple hotkey presses used to stack suspends
            // and leave IsParked out of sync (Playnite Not Responding).
            if (Interlocked.CompareExchange(ref _parkBusy, 1, 0) != 0)
                return;

            try
            {
                // Already parked on this PID → no-op (caller should Resume via toggle)
                if (IsParked && _parkedProcessId == processId)
                    return;

                // Unfreeze overlay soft-pause first (must not minimize a frozen process)
                if (_suspendedPids.Count > 0 || _suspendedThreadHandles.Count > 0)
                    ResumeGameProcess();
                IsParked = false;

                IntPtr hwnd = FindMainWindow(processId);
                if (hwnd == IntPtr.Zero)
                {
                    try
                    {
                        using (var p = Process.GetProcessById(processId))
                            hwnd = p.MainWindowHandle;
                    }
                    catch { }
                }

                // minimizeWindow=false: freeze only. Some Vulkan / emulator-based games crash when their
                // window is minimized (the swap chain is resized to 0x0), so they are frozen where they are.
                if (hwnd != IntPtr.Zero && minimizeWindow)
                {
                    // Exclusive-fullscreen (Forza, etc.): the game must process SC_MINIMIZE
                    // on its own message loop BEFORE we NtSuspend. Freezing too early leaves
                    // a black fullscreen surface. Prefer SW_MINIMIZE so Explorer keeps the icon.
                    try
                    {
                        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
                    }
                    catch { }

                    for (int i = 0; i < 30; i++) // up to ~450 ms, exit early when minimized
                    {
                        try
                        {
                            if (IsIconic(hwnd))
                                break;
                        }
                        catch { }
                        Thread.Sleep(15);
                    }

                    try
                    {
                        if (!IsIconic(hwnd))
                            ShowWindow(hwnd, SW_MINIMIZE);
                    }
                    catch { }

                    for (int i = 0; i < 20; i++) // up to ~300 ms more
                    {
                        try
                        {
                            if (IsIconic(hwnd))
                                break;
                        }
                        catch { }
                        Thread.Sleep(15);
                    }

                    // Last resort only — FORCEMINIMIZE often removes the taskbar icon
                    try
                    {
                        if (!IsIconic(hwnd))
                            ShowWindow(hwnd, SW_FORCEMINIMIZE);
                    }
                    catch { }

                    // Push the (minimized) game window to the bottom of the Z-order so a
                    // leftover exclusive-fullscreen surface cannot cover the desktop.
                    try
                    {
                        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                    catch { }

                    // Reveal the real desktop (Progman / tray) and reset display mode.
                    // Without this, Forza and similar titles leave a solid black screen
                    // even though IsIconic is true and the taskbar icon is present.
                    RevealDesktopAfterPark();
                }

                _suspendedPids.Clear();
                // freezeProcess=false: minimize only, the game keeps running in the background.
                if (freezeProcess)
                {
                    if (NtSuspendPid(processId))
                        _suspendedPids.Add(processId);

                    // Freeze the WHOLE process tree (launcher -> game core -> helpers), not just direct
                    // children. A launcher-style game with one process left running next to a frozen one
                    // usually dies on resume (broken pipe / heartbeat timeout).
                    try
                    {
                        foreach (var child in CollectProcessFamily(processId))
                        {
                            if (child == processId || IsProtectedProcess(child))
                                continue;
                            if (NtSuspendPid(child) && !_suspendedPids.Contains(child))
                                _suspendedPids.Add(child);
                        }
                    }
                    catch { }
                }
                try
                {
                    Breadcrumb.Write("Park froze " + _suspendedPids.Count + " process(es): " +
                        string.Join(", ", _suspendedPids.Select(p => { try { using (var pr = Process.GetProcessById(p)) return pr.ProcessName + "(" + p + ")"; } catch { return p.ToString(); } })));
                }
                catch { }

                _suspendedProcessId = processId;
                _parkedWindowHwnd = hwnd;
                _parkedProcessId = processId;
                // Mark parked even if NtSuspend partially failed — otherwise the window
                // can stay minimized with IsParked=false and resume never restores it.
                // ResumeAndRestore always re-attempts NtResume on the tracked PID/children.
                IsParked = true;
                if (_suspendedPids.Count > 0)
                    BeginSuspendSegment();

                try { ReleaseMouseLock(nudgeCursor: false); } catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _parkBusy, 0);
            }
        }

        /// <summary>
        /// After parking an exclusive-fullscreen game, bring the desktop compositor
        /// forward so the user is not left on a black swap-chain surface.
        /// Do NOT call ChangeDisplaySettings here — forcing a mode reset under a live
        /// DX12 title (Forza, etc.) corrupts the device and crashes on resume (e.g. FHE11).
        /// </summary>
        private static void RevealDesktopAfterPark()
        {
            try
            {
                IntPtr desktop = FindWindow("Progman", null);
                if (desktop == IntPtr.Zero)
                    desktop = FindWindow("WorkerW", null);

                IntPtr tray = FindWindow("Shell_TrayWnd", null);

                try { AllowSetForegroundWindow(-1); } catch { }

                if (desktop != IntPtr.Zero)
                {
                    try { ShowWindow(desktop, SW_SHOW); } catch { }
                    try { BringWindowToTop(desktop); } catch { }
                    try { SetForegroundWindow(desktop); } catch { }
                    try { SwitchToThisWindow(desktop, true); } catch { }
                }

                if (tray != IntPtr.Zero)
                {
                    try { SetForegroundWindow(tray); } catch { }
                }

                // Let DWM composite the desktop + let the game finish GPU work before freeze
                try { Thread.Sleep(150); } catch { }
            }
            catch { /* best-effort */ }
        }

        /// <summary>Fast: single snapshot, only PIDs whose parent is rootPid.</summary>
        private static List<int> GetDirectChildPids(int rootPid)
        {
            var list = new List<int>();
            if (rootPid <= 0) return list;
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
                return list;
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snap, ref pe))
                    return list;
                do
                {
                    if (pe.th32ParentProcessID == (uint)rootPid && pe.th32ProcessID != (uint)rootPid)
                        list.Add((int)pe.th32ProcessID);
                } while (Process32Next(snap, ref pe));
            }
            finally
            {
                try { CloseHandle(snap); } catch { }
            }
            return list;
        }

        /// <summary>
        /// Resume a parked game and optionally restore its window.
        /// </summary>
        public static void ResumeAndRestore(bool restoreWindow = true)
        {
            Breadcrumb.Write("WindowHelper.ResumeAndRestore");
            // Resume is the recovery path — try once without sleeping on the busy flag.
            if (Interlocked.CompareExchange(ref _parkBusy, 1, 0) != 0)
            {
                // Another park/resume in flight: still attempt resume of known PIDs (no wait).
                try
                {
                    int busyPid = _parkedProcessId > 0 ? _parkedProcessId : _suspendedProcessId;
                    ResumeGameProcess();
                    if (busyPid > 0)
                        try { NtResumePid(busyPid); } catch { }
                }
                catch { }
                return;
            }

            try
            {
                int pid = _parkedProcessId > 0 ? _parkedProcessId : _suspendedProcessId;
                IntPtr hwnd = _parkedWindowHwnd;

                // Always resume every tracked PID (and clear list) even if IsParked was wrong
                ResumeGameProcess(); // also FlushSuspendSegment

                // Extra safety: if we still have a parked PID, resume it + children explicitly
                if (pid > 0)
                {
                    try { NtResumePid(pid); } catch { }
                    try
                    {
                        foreach (var child in GetDirectChildPids(pid))
                            try { NtResumePid(child); } catch { }
                    }
                    catch { }
                    try
                    {
                        foreach (var child in GetDirectChildPids(pid))
                        {
                            foreach (var grand in GetDirectChildPids(child))
                                try { NtResumePid(grand); } catch { }
                        }
                    }
                    catch { }
                }

                // Let the process + GPU driver wake before we touch the window.
                // Restoring a DX12 swap chain the same instant as NtResume can trigger
                // Forza FHE11 / device-removed crashes.
                try { Thread.Sleep(200); } catch { }

                // Fullscreen games often recreate their window on mode changes: a stale handle
                // would silently leave the game minimized/black after resume.
                try { if (hwnd != IntPtr.Zero && !IsWindow(hwnd)) hwnd = IntPtr.Zero; } catch { }
                if (hwnd == IntPtr.Zero && pid > 0)
                    hwnd = FindMainWindow(pid);

                if (restoreWindow && hwnd != IntPtr.Zero)
                {
                    try { SafeRestoreAndFocus(hwnd); } catch { }

                    // If the window is still minimized, the window may have been replaced: look again.
                    try
                    {
                        if (IsIconic(hwnd) && pid > 0)
                        {
                            Thread.Sleep(250);
                            var again = FindMainWindow(pid);
                            if (again != IntPtr.Zero)
                                SafeRestoreAndFocus(again);
                        }
                    }
                    catch { }
                }

                try { ReleaseMouseLock(nudgeCursor: false); } catch { }

                _parkedWindowHwnd = IntPtr.Zero;
                _parkedProcessId = 0;
                IsParked = false;
            }
            finally
            {
                Interlocked.Exchange(ref _parkBusy, 0);
            }
        }

        /// <summary>
        /// Restore a minimized game window without activating it, then keep overlay on top.
        /// </summary>
        public static void RestoreWindowNoActivate(IntPtr gameHwnd)
        {
            if (gameHwnd == IntPtr.Zero)
                return;
            try
            {
                if (IsIconic(gameHwnd))
                {
                    // Restore size/position without forcing the game to the foreground
                    ShowWindow(gameHwnd, SW_RESTORE);
                    // If restore activated it, push it behind topmost overlay path below
                    ShowWindow(gameHwnd, SW_SHOWNOACTIVATE);
                }
            }
            catch { /* ignore */ }
        }

        // ---- Exclusive-fullscreen support -------------------------------------------------
        // A normal top-most window can never be drawn over a true exclusive-fullscreen D3D game:
        // the game owns the display. What Windows does reliably is take the game out of exclusive
        // mode (and usually minimize it) when another window takes the foreground, exactly like
        // Alt+Tab. So for those games the overlay is shown on the desktop in front of an opaque
        // backdrop and the game is restored when the overlay closes.

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

        /// <summary>
        /// True while the foreground app is a true exclusive-fullscreen Direct3D game
        /// (QUNS_RUNNING_D3D_FULL_SCREEN). Borderless / windowed-fullscreen and Windows
        /// "fullscreen optimizations" games report a different state and do not need this path.
        /// Must be called BEFORE the overlay takes the foreground.
        /// </summary>
        public static bool IsExclusiveFullscreenActive()
        {
            try
            {
                int state;
                return SHQueryUserNotificationState(out state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
            }
            catch { return false; }
        }

        /// <summary>
        /// The game has finished reacting to losing the foreground: its window is minimized or
        /// hidden. Freezing the process before this point leaves a frozen black fullscreen surface.
        /// </summary>
        public static bool HasGameYieldedDisplay(IntPtr gameHwnd)
        {
            try
            {
                if (gameHwnd == IntPtr.Zero) return true;
                return IsIconic(gameHwnd) || !IsWindowVisible(gameHwnd);
            }
            catch { return true; }
        }

        /// <summary>True when the process is still alive and the process is not Playnite / a system host.</summary>
        public static bool IsRestorableGameProcess(int pid)
        {
            if (pid <= 0 || IsProtectedProcess(pid)) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch { return false; }
        }

        // ---- DXVK / Vulkan fullscreen detection ----------------------------------------------
        // Vulkan (and DXVK = D3D9/10/11 on Vulkan) fullscreen swap chains are not reported as
        // "D3D fullscreen" by Windows and usually do not minimize on focus loss, so a plain
        // topmost window ends up hidden behind them. They are detected by the DLLs they load.

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule,
            uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule,
            StringBuilder lpFilename, uint nSize);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfoEx
        {
            public int cbSize;
            public WinRect rcMonitor;
            public WinRect rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        private static readonly Dictionary<int, bool> _vulkanFullscreenCache = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _openGlFullscreenCache = new Dictionary<int, bool>();

        /// <summary>The window is visible and covers its whole monitor (fullscreen / borderless).</summary>
        public static bool IsWindowCoveringMonitor(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;
                WinRect r;
                if (!GetWindowRect(hwnd, out r)) return false;
                IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                       r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
            catch { return false; }
        }

        /// <summary>
        /// True when the process renders through Vulkan: DXVK (non-system d3d9/d3d10core/d3d11/dxgi
        /// next to the game) or a native Vulkan game (vulkan-1.dll and no Direct3D runtime).
        /// Cached per PID. Uses EnumProcessModulesEx so 32-bit games are covered too.
        /// </summary>
        public static bool UsesVulkanRenderer(int pid)
        {
            if (pid <= 0) return false;
            lock (_vulkanFullscreenCache)
            {
                bool cached;
                if (_vulkanFullscreenCache.TryGetValue(pid, out cached)) return cached;
            }

            bool result = false;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0400 | 0x0010, false, pid); // QUERY_INFORMATION | VM_READ
                if (h != IntPtr.Zero)
                {
                    var mods = new IntPtr[2048];
                    uint needed;
                    if (EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out needed, 0x03 /* LIST_MODULES_ALL */))
                    {
                        int count = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)mods.Length);
                        string sysDir = (Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "").ToLowerInvariant();
                        bool vulkan = false, dxvk = false, systemD3d = false;
                        var sb = new StringBuilder(520);
                        for (int i = 0; i < count; i++)
                        {
                            sb.Clear();
                            if (GetModuleFileNameEx(h, mods[i], sb, 520) == 0) continue;
                            string path = sb.ToString().ToLowerInvariant();
                            string name = Path.GetFileName(path);
                            if (name == "vulkan-1.dll") { vulkan = true; continue; }
                            bool isD3dFamily = name == "d3d9.dll" || name == "d3d10core.dll" || name == "d3d11.dll" ||
                                               name == "dxgi.dll" || name == "d3d12.dll";
                            if (!isD3dFamily) continue;
                            bool inSystem = sysDir.Length > 0 && path.StartsWith(sysDir);
                            if (inSystem) { if (name != "dxgi.dll") systemD3d = true; }
                            else if (name != "d3d12.dll") dxvk = true;
                        }
                        result = dxvk || (vulkan && !systemD3d);
                    }
                }
            }
            catch { result = false; }
            finally { if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } } }

            lock (_vulkanFullscreenCache) _vulkanFullscreenCache[pid] = result;
            return result;
        }

        /// <summary>True when the process has the Windows OpenGL runtime loaded.</summary>
        public static bool UsesOpenGLRenderer(int pid)
        {
            if (pid <= 0) return false;
            lock (_openGlFullscreenCache)
            {
                bool cached;
                if (_openGlFullscreenCache.TryGetValue(pid, out cached)) return cached;
            }

            bool result = false;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0400 | 0x0010, false, pid); // QUERY_INFORMATION | VM_READ
                if (h != IntPtr.Zero)
                {
                    var mods = new IntPtr[2048];
                    uint needed;
                    if (EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out needed, 0x03 /* LIST_MODULES_ALL */))
                    {
                        int count = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)mods.Length);
                        var sb = new StringBuilder(520);
                        for (int i = 0; i < count; i++)
                        {
                            sb.Clear();
                            if (GetModuleFileNameEx(h, mods[i], sb, 520) == 0) continue;
                            if (string.Equals(Path.GetFileName(sb.ToString()), "opengl32.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                result = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch { result = false; }
            finally { if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } } }

            lock (_openGlFullscreenCache) _openGlFullscreenCache[pid] = result;
            return result;
        }

        /// <summary>
        /// Overlay must use the "step the game aside" path: true D3D exclusive fullscreen, or a
        /// Vulkan/DXVK/OpenGL game whose window covers the whole monitor. This avoids the
        /// fullscreen renderer covering the overlay when it does not yield to a topmost window.
        /// </summary>
        public static bool IsFullscreenGameNeedingFallback(IntPtr gameHwnd)
        {
            if (IsExclusiveFullscreenActive()) return true;
            try
            {
                if (gameHwnd == IntPtr.Zero || !IsWindowCoveringMonitor(gameHwnd)) return false;
                IntPtr fg = GetForegroundWindow();
                if (fg != gameHwnd)
                {
                    uint a, b;
                    GetWindowThreadProcessId(fg, out a);
                    GetWindowThreadProcessId(gameHwnd, out b);
                    if (a == 0 || a != b) return false;
                }
                uint pid;
                GetWindowThreadProcessId(gameHwnd, out pid);
                return UsesVulkanRenderer((int)pid) || UsesOpenGLRenderer((int)pid);
            }
            catch { return false; }
        }

        /// <summary>Ask the game to minimize without ever blocking on a busy game.</summary>
        public static void RequestGameMinimize(IntPtr gameHwnd)
        {
            if (gameHwnd == IntPtr.Zero) return;
            try { PostMessage(gameHwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero); } catch { }
            try { ShowWindowAsync(gameHwnd, SW_MINIMIZE); } catch { }
        }

        // ---- Borderless fullscreen for windowed games ---------------------------------------
        // Like Borderless Gaming: strip the title bar / frame of a windowed game and stretch it
        // over its monitor. Runs on a light background poll while the game is running.

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_CHILD = 0x40000000;
        private const int WS_EX_DLGMODALFRAME = 0x00000001;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_WINDOWEDGE = 0x00000100;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_STATICEDGE = 0x00020000;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLongInt(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        private static Timer _borderlessTimer;
        private static int _borderlessPid;
        private static int _borderlessBusy;
        private static int _borderlessTicks;
        private static readonly Dictionary<long, int> _borderlessApplied = new Dictionary<long, int>();
        private static Action<string> _borderlessLog;

        /// <summary>Begin watching the game's windows and make windowed ones borderless fullscreen.</summary>
        public static void StartBorderlessWatch(int pid, Action<string> log = null)
        {
            StopBorderlessWatch();
            if (pid <= 0 || IsProtectedProcess(pid)) return;
            _borderlessPid = pid;
            _borderlessLog = log;
            _borderlessTicks = 0;
            lock (_borderlessApplied) _borderlessApplied.Clear();
            _borderlessTimer = new Timer(BorderlessTick, null, 1500, 1500);
        }

        public static void StopBorderlessWatch()
        {
            var t = _borderlessTimer;
            _borderlessTimer = null;
            _borderlessPid = 0;
            if (t != null) { try { t.Dispose(); } catch { } }
        }

        private static void BorderlessTick(object state)
        {
            if (Interlocked.CompareExchange(ref _borderlessBusy, 1, 0) != 0) return;
            try
            {
                int pid = _borderlessPid;
                if (pid <= 0) return;

                // Slow the poll once the game has had time to create its window.
                _borderlessTicks++;
                if (_borderlessTicks == 60)
                {
                    var t = _borderlessTimer;
                    if (t != null) { try { t.Change(5000, 5000); } catch { } }
                }

                if (!IsRestorableGameProcess(pid) && CollectProcessFamily(pid).Count == 0)
                {
                    StopBorderlessWatch();
                    return;
                }

                IntPtr hwnd = FindMainWindow(pid);
                if (hwnd != IntPtr.Zero && MakeBorderless(hwnd))
                    _borderlessLog?.Invoke("Borderless fullscreen applied to game window.");
            }
            catch { /* best-effort */ }
            finally { Interlocked.Exchange(ref _borderlessBusy, 0); }
        }

        /// <summary>
        /// Strip frame styles and cover the monitor. Only touches normal captioned top-level windows;
        /// windows that are already borderless, minimized or exclusive-fullscreen are left alone.
        /// </summary>
        public static bool MakeBorderless(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;

                int style = GetWindowLongInt(hwnd, GWL_STYLE);
                int ex = GetWindowLongInt(hwnd, GWL_EXSTYLE);
                if ((style & WS_CHILD) != 0 || (ex & WS_EX_TOOLWINDOW) != 0) return false;
                if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0) return false; // already borderless

                // Avoid fighting a game that keeps restoring its frame.
                long key = hwnd.ToInt64();
                lock (_borderlessApplied)
                {
                    int n;
                    _borderlessApplied.TryGetValue(key, out n);
                    if (n >= 8) return false;
                    _borderlessApplied[key] = n + 1;
                }

                IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;

                if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);

                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
                ex &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
                SetWindowLongInt(hwnd, GWL_STYLE, style);
                SetWindowLongInt(hwnd, GWL_EXSTYLE, ex);

                int x = mi.rcMonitor.Left, y = mi.rcMonitor.Top;
                int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                return true;
            }
            catch { return false; }
        }

        // ---- "Fake fullscreen" (Windows fullscreen optimizations) -----------------------------
        // Windows can run Direct3D 10/11/12 games that ask for exclusive fullscreen as borderless
        // flip-model windows ("fake fullscreen"): no mode switch, instant alt-tab, overlays work.
        // That is controlled by HKCU\System\GameConfigStore. We switch it to "let Windows decide"
        // for the duration of the game and put the user's original values back afterwards.
        // Direct3D 9 / OpenGL / most Vulkan games ignore this and still go exclusive.

        private const string GameConfigStoreKey = @"System\GameConfigStore";
        private static readonly string[] FakeFullscreenValues =
        {
            "GameDVR_FSEBehaviorMode",
            "GameDVR_HonorUserFSEBehaviorMode",
            "GameDVR_DXGIHonorFSEWindowsCompatible"
        };

        public static void ApplyFakeFullscreen(string backupPath)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(GameConfigStoreKey))
                {
                    if (key == null) return;
                    // An existing backup holds the true originals (previous session crashed): keep it.
                    if (!string.IsNullOrEmpty(backupPath) && !File.Exists(backupPath))
                    {
                        var lines = new List<string>();
                        foreach (var name in FakeFullscreenValues)
                        {
                            object v = key.GetValue(name);
                            lines.Add(name + "=" + (v is int ? ((int)v).ToString() : "null"));
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                        File.WriteAllLines(backupPath, lines.ToArray());
                    }
                    foreach (var name in FakeFullscreenValues)
                        key.SetValue(name, 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { /* best-effort */ }
        }

        public static void RestoreFakeFullscreen(string backupPath)
        {
            try
            {
                if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return;
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(GameConfigStoreKey))
                {
                    if (key == null) return;
                    foreach (var line in File.ReadAllLines(backupPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string name = line.Substring(0, eq);
                        string val = line.Substring(eq + 1);
                        if (Array.IndexOf(FakeFullscreenValues, name) < 0) continue;
                        int n;
                        if (int.TryParse(val, out n))
                            key.SetValue(name, n, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                            key.DeleteValue(name, false);
                    }
                }
                File.Delete(backupPath);
            }
            catch { /* best-effort */ }
        }

        // ---- Prefer the discrete GPU (Windows "Graphics settings" per-app preference) ----------
        // Same switch as Settings > System > Display > Graphics > <app> > High performance.
        // Windows reads it when the program starts, so it affects the NEXT launch of that exe.

        private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>Returns true if the registry value was changed.</summary>
        public static bool SetHighPerformanceGpu(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
                string full = Path.GetFullPath(exePath);

                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "";
                if (win.Length > 0 && full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return false;
                try
                {
                    string self = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(self) && string.Equals(self, full, StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { }

                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UserGpuPreferencesKey))
                {
                    if (key == null) return false;
                    string current = key.GetValue(full) as string ?? "";
                    // Keep other per-app options (e.g. SwapEffectUpgradeEnable) and replace only GpuPreference.
                    var parts = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    parts.Insert(0, "GpuPreference=2");
                    string next = string.Join(";", parts) + ";";
                    if (string.Equals(current, next, StringComparison.OrdinalIgnoreCase)) return false;
                    key.SetValue(full, next, Microsoft.Win32.RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Remove the per-app GPU choice again (back to "let Windows decide").</summary>
        public static bool ClearGpuPreference(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath)) return false;
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey, true))
                {
                    if (key == null) return false;
                    string current = key.GetValue(exePath) as string;
                    if (current == null) return false;
                    var parts = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (parts.Count == 0) key.DeleteValue(exePath, false);
                    else key.SetValue(exePath, string.Join(";", parts) + ";", Microsoft.Win32.RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>Real-time key state (safe to call from a low-level keyboard hook).</summary>
        public static bool IsKeyDownAsync(int vk)
        {
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }
        }

        /// <summary>True if the process was started (directly or indirectly) by Playnite itself.</summary>
        public static bool IsDescendantOfCurrentProcess(int pid, int maxDepth = 8)
        {
            try
            {
                int self = Process.GetCurrentProcess().Id;
                int cur = pid;
                for (int i = 0; i < maxDepth && cur > 4; i++)
                {
                    int parent = GetParentProcessIdSafe(cur);
                    if (parent <= 0 || parent == cur) return false;
                    if (parent == self) return true;
                    cur = parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Visible Steam client windows (steam / steamwebhelper) of a usable size.</summary>
        public static List<IntPtr> GetSteamWindows()
        {
            var result = new List<IntPtr>();
            var isSteam = new Dictionary<uint, bool>();
            try
            {
                EnumWindows((h, l) =>
                {
                    try
                    {
                        if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        bool steam;
                        if (!isSteam.TryGetValue(pid, out steam))
                        {
                            steam = false;
                            try
                            {
                                using (var p = Process.GetProcessById((int)pid))
                                {
                                    var n = p.ProcessName;
                                    steam = n.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                                            n.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase);
                                }
                            }
                            catch { }
                            isSteam[pid] = steam;
                        }
                        if (!steam) return true;
                        WinRect r;
                        if (GetWindowRect(h, out r) && (r.Right - r.Left) > 120 && (r.Bottom - r.Top) > 120)
                            result.Add(h);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return result;
        }

        /// <summary>Let another program take the foreground from us (e.g. Steam opening a window).</summary>
        public static void AllowForegroundHandoff()
        {
            try { AllowSetForegroundWindow(-1); } catch { }
        }

        /// <summary>Show a window above the top-most overlay and give it the keyboard focus.</summary>
        public static bool BringAboveOverlay(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return false;
                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | 0x0040 /* SWP_SHOWWINDOW */);
                if (GetForegroundWindow() != hwnd) SetForegroundWindow(hwnd);
                if (GetForegroundWindow() != hwnd) ForceFocusWindow(hwnd);
                return GetForegroundWindow() == hwnd;
            }
            catch { return false; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>
        /// True when a normal application window (not the overlay, not the game, not the desktop / taskbar /
        /// Alt+Tab switcher) has the foreground, i.e. the user has moved on to another app.
        /// </summary>
        public static bool IsAnotherAppInForeground(IntPtr overlayHwnd, int gamePid)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == overlayHwnd) return false;
                if (!IsWindowVisible(fg) || GetWindowTextLength(fg) == 0) return false;

                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                if (gamePid > 0 && (int)pid == gamePid) return false;
                if (IsDescendantOfCurrentProcess((int)pid)) return false;      // the game or a helper Playnite started

                var cls = new StringBuilder(128);
                GetClassName(fg, cls, cls.Capacity);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                    c == "MultitaskingViewFrame" || c == "XamlExplorerHostIslandWindow" || c == "ForegroundStaging" ||
                    c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland")
                    return false;

                try
                {
                    using (var p = Process.GetProcessById((int)pid))
                        if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                            (c.StartsWith("Windows.UI") || c.StartsWith("Shell")))
                            return false;
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public static string GetProcessExePath(int pid)
        {
            try
            {
                if (pid <= 0 || IsProtectedProcess(pid)) return null;
                using (var p = Process.GetProcessById(pid))
                    return p.MainModule?.FileName;
            }
            catch { return null; }
        }

        public static int GetWindowOwnerPid(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return (int)pid;
            }
            catch { return 0; }
        }

        public static void EnsureTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;
            try
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Find the best visible main window for a process (MainWindowHandle is sometimes 0).
        /// </summary>
        public static IntPtr FindMainWindow(int processId)
        {
            if (processId <= 0)
                return IntPtr.Zero;

            // The real game window is often not the first window of the tracked process: launchers
            // hand off to a child process, and splash / launcher dialogs come first. Look at the
            // whole process family and take the largest real top-level window.
            try
            {
                var family = CollectProcessFamily(processId);
                IntPtr best = FindBestWindow(pid => family.Contains(pid));
                if (best != IntPtr.Zero)
                    return best;
            }
            catch { /* fall through */ }

            try
            {
                var proc = Process.GetProcessById(processId);
                if (proc.MainWindowHandle != IntPtr.Zero)
                    return proc.MainWindowHandle;
            }
            catch { /* ignore */ }

            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if ((int)pid != processId)
                        return true;
                    if (!IsWindowVisible(hWnd))
                        return true;
                    if (GetWindowTextLength(hWnd) == 0)
                        return true;
                    found = hWnd;
                    return false; // stop
                }, IntPtr.Zero);
            }
            catch { /* ignore */ }
            return found;
        }

        /// <summary>
        /// Main window of any process running from the given folder (the game's install directory).
        /// Used when the PID Playnite tracks is a launcher stub that no longer owns a window.
        /// </summary>
        public static IntPtr FindMainWindowInDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return IntPtr.Zero;
            try
            {
                string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";
                var cache = new Dictionary<int, bool>();
                return FindBestWindow(pid =>
                {
                    bool inside;
                    if (cache.TryGetValue(pid, out inside)) return inside;
                    inside = false;
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                        {
                            string exe = null;
                            try { exe = p.MainModule?.FileName; } catch { }
                            inside = !string.IsNullOrEmpty(exe) &&
                                     Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch { }
                    cache[pid] = inside;
                    return inside;
                });
            }
            catch { return IntPtr.Zero; }
        }

        private static HashSet<int> CollectProcessFamily(int rootPid)
        {
            var family = new HashSet<int> { rootPid };
            var parents = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return family;
            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32First(snap, ref pe))
                {
                    do { parents[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; }
                    while (Process32Next(snap, ref pe));
                }
            }
            finally { CloseHandle(snap); }

            for (int pass = 0; pass < 4; pass++)
            {
                bool added = false;
                foreach (var kv in parents)
                {
                    if (kv.Key > 4 && !family.Contains(kv.Key) && family.Contains(kv.Value))
                    {
                        family.Add(kv.Key);
                        added = true;
                    }
                }
                if (!added) break;
            }
            return family;
        }

        /// <summary>Largest visible, un-owned, non-tool top-level window among processes accepted by the filter.</summary>
        private static IntPtr FindBestWindow(Func<int, bool> acceptPid)
        {
            IntPtr best = IntPtr.Zero;
            double bestScore = -1;
            EnumWindows((h, l) =>
            {
                try
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (!acceptPid((int)pid)) return true;
                    if (!IsWindowVisible(h)) return true;
                    if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;

                    int ex = GetWindowLongInt(h, -20 /* GWL_EXSTYLE */);
                    bool toolWindow = (ex & 0x00000080) != 0 && (ex & 0x00040000 /* APPWINDOW */) == 0;
                    if (toolWindow) return true;

                    double w = 0, hgt = 0;
                    if (IsIconic(h))
                    {
                        var wp = new WinPlacement { length = Marshal.SizeOf(typeof(WinPlacement)) };
                        if (GetWindowPlacement(h, ref wp))
                        {
                            w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                            hgt = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                        }
                    }
                    else
                    {
                        WinRect r;
                        if (GetWindowRect(h, out r))
                        {
                            w = r.Right - r.Left;
                            hgt = r.Bottom - r.Top;
                        }
                    }

                    double score = (w < 64 || hgt < 64) ? 1 : w * hgt;
                    if (GetWindowTextLength(h) > 0) score += 1;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = h;
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPlacement
        {
            public int length, flags, showCmd;
            public int ptMinX, ptMinY, ptMaxX, ptMaxY;
            public WinRect rcNormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLongInt(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WinPlacement lpwndpl);

        public static bool IsFolderOpen(string folderPath)
        {
            return false;
        }
    }

    /// <summary>
    /// Tiny always-on trace (one line per important event) in the extension's data folder.
    /// Survives hard crashes, so the last lines show what the extension was doing.
    /// </summary>
    internal static class Breadcrumb
    {
        private static readonly object Gate = new object();
        public static string DirectoryPath;

        /// <summary>Turned off from the settings ("Write diagnostic log").</summary>
        public static volatile bool Enabled = false;

        public static void Write(string message)
        {
            try
            {
                if (!Enabled) return;
                string dir = DirectoryPath;
                if (string.IsNullOrEmpty(dir)) return;
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(dir);
                    string path = System.IO.Path.Combine(dir, "breadcrumbs.log");
                    try
                    {
                        var fi = new System.IO.FileInfo(path);
                        if (fi.Exists && fi.Length > 256 * 1024)
                        {
                            System.IO.File.Copy(path, path + ".old", true);
                            System.IO.File.Delete(path);
                        }
                    }
                    catch { }
                    System.IO.File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [t" +
                        System.Threading.Thread.CurrentThread.ManagedThreadId + "] " + message + "\r\n");
                }
            }
            catch { /* tracing must never throw */ }
        }
    }
}
