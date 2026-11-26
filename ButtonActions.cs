using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PlayniteGameOverlay
{
    public static class ButtonActions
    {
        public static void FocusOrLaunchDiscord()
        {

            bool processFound = false;
            try
            {
                Process[] processes = Process.GetProcessesByName("Discord");
                if (processes.Length > 0)
                {

                    // Try to bring the window to the foreground
                    foreach (var process in processes)
                    {
                        try
                        {

                            Debug.WriteLine($"Try to show {process.ProcessName}");
                            // Get the main window handle
                            IntPtr handle = process.MainWindowHandle;

                            if (handle != IntPtr.Zero)
                            {
                                // Restore window if minimized
                                var shown = ShowWindow(handle, SW_MAXIMIZE);

                                // Bring to foreground
                                var fg = SetForegroundWindow(handle);

                                if (fg && shown)
                                {
                                    Debug.WriteLine($"Shown");
                                    processFound = true;
                                    break;
                                }
                                Debug.WriteLine($"Show failed {shown}, {fg}");



                            }
                        }
                        catch
                        {
                            // Continue to the next process if this one fails
                            continue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for existing process: {ex.Message}");
            }

            // If no running process was found or focused, start discord via URI
            if (!processFound)
            {
                Debug.WriteLine($"Launching discord via URI");
                Process.Start(new ProcessStartInfo
                {
                    FileName = @"discord://-/",
                    UseShellExecute = true
                });
            }
        }

        public static void ExecuteKbdShortcut(string keystroke)
        {
            SendKeys.SendWait(keystroke);
        }

        /// <summary>
        /// Used by the overlay buttons (RTSS OSD, recording, streaming...). Runs in the background:
        /// waits until the game is resumed and in the foreground (up to ~3 s), then sends the
        /// SendKeys-style combo (e.g. "^+{F10}", "%{F9}") as real scancode input with a short hold
        /// so polling hotkey handlers (RTSS, OBS, Steam...) actually see it.
        /// Falls back to SendKeys if the string can't be parsed.
        /// </summary>
        public static void ExecuteKbdShortcutWhenGameReady(string keystroke, int gamePid)
        {
            if (string.IsNullOrWhiteSpace(keystroke)) return;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    // 1) wait for game to be the foreground window (it was just unfrozen)
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 3000)
                    {
                        if (gamePid <= 0) { System.Threading.Thread.Sleep(350); break; }
                        IntPtr fg = GetForegroundWindow();
                        uint fgPid;
                        GetWindowThreadProcessId(fg, out fgPid);
                        if (fg != IntPtr.Zero && IsSameProcessTree((int)fgPid, gamePid))
                        {
                            System.Threading.Thread.Sleep(300); // let it wake / swap chain settle
                            break;
                        }
                        System.Threading.Thread.Sleep(50);
                    }

                    // 2) send keys
                    if (!TrySendCombo(keystroke))
                        SendKeys.SendWait(keystroke);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ExecuteKbdShortcutWhenGameReady failed: " + ex.Message);
                    try { SendKeys.SendWait(keystroke); } catch { }
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }

        /// <summary>Send a SendKeys-style combo right now (background thread, scancode input with a short hold).</summary>
        public static void SendComboAsync(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    // Let the physical PrintScreen key come up first so it can't combine with the combo.
                    System.Threading.Thread.Sleep(40);
                    if (!TrySendCombo(combo))
                        SendKeys.SendWait(combo);
                }
                catch (Exception ex) { Debug.WriteLine("SendComboAsync failed: " + ex.Message); }
            });
            t.IsBackground = true;
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }

        private static bool IsSameProcessTree(int pid, int gamePid)
        {
            if (pid == gamePid) return true;
            try
            {
                // Launcher-style games: the focused window may belong to a child/sibling process.
                using (var fgp = Process.GetProcessById(pid))
                using (var gp = Process.GetProcessById(gamePid))
                {
                    if (fgp.ProcessName.Equals(gp.ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        // ---- scancode SendInput ------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type; public INPUTUNION u;
        }

        private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        private const uint KEYEVENTF_KEYUP = 0x2;
        private const uint KEYEVENTF_SCANCODE = 0x8;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static void Key(ushort vk, bool up)
        {
            ushort scan = (ushort)MapVirtualKey(vk, 0 /*MAPVK_VK_TO_VSC*/);
            bool ext = vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 || vk == 0x25 || vk == 0x26 ||
                       vk == 0x27 || vk == 0x28 || vk == 0x2D || vk == 0x2E || vk == 0x6F || vk == 0xA3 || vk == 0xA5;
            var inp = new INPUT { type = 1 };
            inp.u.ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = KEYEVENTF_SCANCODE | (ext ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };
            SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>Parses SendKeys syntax: ^ ctrl, + shift, % alt, {F1}..{F24}, {ESC}, {HOME}..., single chars.</summary>
        private static bool TrySendCombo(string combo)
        {
            var mods = new System.Collections.Generic.List<ushort>();
            var keys = new System.Collections.Generic.List<ushort>();
            int i = 0;
            while (i < combo.Length)
            {
                char c = combo[i];
                if (c == '^') { mods.Add(0xA2); i++; }          // LCtrl
                else if (c == '+') { mods.Add(0xA0); i++; }     // LShift
                else if (c == '%') { mods.Add(0xA4); i++; }     // LAlt
                else if (c == '{')
                {
                    int end = combo.IndexOf('}', i + 1);
                    if (end < 0) return false;
                    string name = combo.Substring(i + 1, end - i - 1).Trim().ToUpperInvariant();
                    ushort vk = NamedKey(name);
                    if (vk == 0) return false;
                    keys.Add(vk);
                    i = end + 1;
                }
                else if (c == '(' || c == ')' || c == '~') return false; // groups/ENTER: use SendKeys fallback
                else
                {
                    short v = VkKeyScan(c);
                    if (v == -1) return false;
                    keys.Add((ushort)(v & 0xFF));
                    if ((v & 0x100) != 0 && !mods.Contains(0xA0)) mods.Add(0xA0);
                    i++;
                }
            }
            if (keys.Count == 0) return false;

            foreach (var m in mods) { Key(m, false); System.Threading.Thread.Sleep(15); }
            foreach (var k in keys)
            {
                Key(k, false);
                System.Threading.Thread.Sleep(90);   // hold long enough for polling hotkey handlers
                Key(k, true);
                System.Threading.Thread.Sleep(15);
            }
            for (int m = mods.Count - 1; m >= 0; m--) { Key(mods[m], true); System.Threading.Thread.Sleep(10); }
            return true;
        }

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        private static ushort NamedKey(string n)
        {
            if (n.Length >= 2 && n[0] == 'F')
            {
                int f;
                if (int.TryParse(n.Substring(1), out f) && f >= 1 && f <= 24) return (ushort)(0x70 + f - 1);
            }
            switch (n)
            {
                case "ESC": case "ESCAPE": return 0x1B;
                case "ENTER": return 0x0D;
                case "TAB": return 0x09;
                case "BACKSPACE": case "BS": case "BKSP": return 0x08;
                case "DELETE": case "DEL": return 0x2E;
                case "INSERT": case "INS": return 0x2D;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PGUP": return 0x21;
                case "PGDN": return 0x22;
                case "UP": return 0x26;
                case "DOWN": return 0x28;
                case "LEFT": return 0x25;
                case "RIGHT": return 0x27;
                case "PRTSC": case "PRINTSCREEN": return 0x2C;
                case "SCROLLLOCK": return 0x91;
                case "BREAK": return 0x13;
                case "ADD": return 0x6B;
                case "SUBTRACT": return 0x6D;
                case "MULTIPLY": return 0x6A;
                case "DIVIDE": return 0x6F;
            }
            return 0;
        }

        public static void FocusOrLaunch(string path)
        {
            bool processFound = false;
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                Process[] processes = Process.GetProcessesByName(fileName);
                if (processes.Length > 0)
                {
                    processFound = true;

                    // Try to bring the window to the foreground
                    foreach (var process in processes)
                    {
                        try
                        {
                            // Get the main window handle
                            IntPtr handle = process.MainWindowHandle;

                            if (handle != IntPtr.Zero)
                            {
                                // Restore window if minimized
                                ShowWindow(handle, SW_MAXIMIZE);

                                // Bring to foreground
                                SetForegroundWindow(handle);

                                break; // Exit after focusing the first valid window
                            }
                        }
                        catch
                        {
                            // Continue to the next process if this one fails
                            continue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for existing process: {ex.Message}");
            }

            // If no running process was found or focused, start a new one
            if (!processFound)
            {
                Process.Start(path);
            }
        }

        // Win32 API Declarations
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MAXIMIZE = 3;

    }
}
