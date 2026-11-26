using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace PlayniteGameOverlay
{
    // Hotkeys-Layout: controller volume combos (new settings + their save / cancel handling).
    // Part of the Hotkeys-Layout folder; integrate.py copies this file next to the other sources.
    public partial class OverlaySettings
    {
        private string _controllerHotkeyVolumeUp = "";
        private string _controllerHotkeyVolumeDown = "";
        private string _controllerHotkeyVolumeUpBackup = "";
        private string _controllerHotkeyVolumeDownBackup = "";

        /// <summary>Controller combo that raises the volume by the volume-hotkey step (empty = none).</summary>
        public string ControllerHotkeyVolumeUp
        {
            get => _controllerHotkeyVolumeUp;
            set => SetValue(ref _controllerHotkeyVolumeUp, value?.Trim() ?? "");
        }

        /// <summary>Controller combo that lowers the volume by the volume-hotkey step (empty = none).</summary>
        public string ControllerHotkeyVolumeDown
        {
            get => _controllerHotkeyVolumeDown;
            set => SetValue(ref _controllerHotkeyVolumeDown, value?.Trim() ?? "");
        }

        private void HotkeysLayout_Load(OverlaySettings saved)
        {
            if (saved == null) return;
            ControllerHotkeyVolumeUp = saved.ControllerHotkeyVolumeUp;
            ControllerHotkeyVolumeDown = saved.ControllerHotkeyVolumeDown;
        }

        private void HotkeysLayout_Backup()
        {
            _controllerHotkeyVolumeUpBackup = ControllerHotkeyVolumeUp;
            _controllerHotkeyVolumeDownBackup = ControllerHotkeyVolumeDown;
        }

        private void HotkeysLayout_Restore()
        {
            ControllerHotkeyVolumeUp = _controllerHotkeyVolumeUpBackup;
            ControllerHotkeyVolumeDown = _controllerHotkeyVolumeDownBackup;
        }
    }

    internal static class HotkeysLayout
    {
        /// <summary>
        /// Safeguard for the PrintScreen-remap box: while it is recording, also read F1-F24 straight from the
        /// keyboard state, so a function key is picked up even if the window never raises it as a WPF key event.
        /// </summary>
        public static void WatchFunctionKeys(Func<bool> active, Func<OverlaySettings> settings, Action done)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            int ticks = 0;
            timer.Tick += (_, __) =>
            {
                if (!active() || ++ticks > 400) { timer.Stop(); return; }
                for (int vk = 0x70; vk <= 0x87; vk++)   // F1..F24
                {
                    if (!WindowHelper.IsKeyDownAsync(vk)) continue;
                    var s = settings();
                    timer.Stop();
                    if (s == null) return;
                    bool ctrl = WindowHelper.IsKeyDownAsync(0x11);
                    bool alt = WindowHelper.IsKeyDownAsync(0x12);
                    bool shift = WindowHelper.IsKeyDownAsync(0x10);
                    s.PrintScreenRemapCombo = (ctrl ? "^" : "") + (alt ? "%" : "") + (shift ? "+" : "") + "{F" + (vk - 0x70 + 1) + "}";
                    done();
                    return;
                }
            };
            timer.Start();
        }

        /// <summary>
        /// Order controller buttons are written in a combo: Back / Start / Guide first, then the rest
        /// (so "Back + X" rather than "X + Back"). Values are indexes into ControllerHotkeys.Names.
        /// </summary>
        public static readonly int[] PadDisplayOrder =
        {
            9, 8, 10,                       // Back, Start, Guide
            0, 1, 2, 3, 4, 5, 6, 7,         // A B X Y LB RB LT RT
            11, 12, 13, 14, 15, 16          // LS RS DUp DDown DLeft DRight
        };

        /// <summary>Re-writes a saved combo in display order, so combos saved earlier ("X+Back") read "Back+X" too.</summary>
        public static string NormalizePad(string combo)
        {
            int mask = ControllerHotkeys.Parse(combo);
            if (mask == 0) return combo;
            var text = ControllerHotkeys.Format(mask);
            return string.IsNullOrEmpty(text) ? combo : text;
        }

        // ---- PrintScreen remap combo: assign by pressing keys ---------------------------------
        // The setting stays in SendKeys form (^ Ctrl, % Alt, + Shift, {F12}) because ButtonActions sends it.

        /// <summary>Turns a captured key + modifiers into the SendKeys combo. Null when the key is not supported.</summary>
        public static string ToSendKeys(Key key, bool alt, bool shift, bool ctrl)
        {
            string token = KeyToken(key);
            if (token == null) return null;
            return (ctrl ? "^" : "") + (alt ? "%" : "") + (shift ? "+" : "") + token;
        }

        /// <summary>Readable form of a SendKeys combo, e.g. ^+{F12} -> "Ctrl + Shift + F12". Unknown text is shown as typed.</summary>
        public static string FormatSendKeys(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return "(not set)";
            var parts = new List<string>();
            string key = null;
            int i = 0;
            while (i < combo.Length)
            {
                char c = combo[i];
                if (c == '^') { parts.Add("Ctrl"); i++; }
                else if (c == '%') { parts.Add("Alt"); i++; }
                else if (c == '+') { parts.Add("Shift"); i++; }
                else if (c == '{')
                {
                    int end = combo.IndexOf('}', i + 1);
                    if (end < 0) return combo;
                    key = combo.Substring(i + 1, end - i - 1).Trim();
                    i = end + 1;
                }
                else if (c == '(' || c == ')' || c == '~' || key != null) return combo;
                else { key = c.ToString().ToUpperInvariant(); i++; }
            }
            if (key == null) return combo;
            parts.Add(key.Length > 1 ? key.ToUpperInvariant() : key);
            return string.Join(" + ", parts);
        }

        private static string KeyToken(Key key)
        {
            if (key >= Key.F1 && key <= Key.F24) return "{" + key.ToString() + "}";
            if (key >= Key.A && key <= Key.Z) return key.ToString().ToLowerInvariant();
            if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
            switch (key)
            {
                case Key.Space: return " ";
                case Key.Escape: return "{ESC}";
                case Key.Return: return "{ENTER}";
                case Key.Tab: return "{TAB}";
                case Key.Back: return "{BACKSPACE}";
                case Key.Delete: return "{DELETE}";
                case Key.Insert: return "{INSERT}";
                case Key.Home: return "{HOME}";
                case Key.End: return "{END}";
                case Key.Prior: return "{PGUP}";
                case Key.Next: return "{PGDN}";
                case Key.Up: return "{UP}";
                case Key.Down: return "{DOWN}";
                case Key.Left: return "{LEFT}";
                case Key.Right: return "{RIGHT}";
                case Key.Snapshot: return "{PRTSC}";
                case Key.Scroll: return "{SCROLLLOCK}";
                case Key.Pause: return "{BREAK}";
                case Key.Add: return "{ADD}";
                case Key.Subtract: return "{SUBTRACT}";
                case Key.Multiply: return "{MULTIPLY}";
                case Key.Divide: return "{DIVIDE}";
            }
            return null;
        }

        /// <summary>
        /// Registers the controller volume combos on the tracker. They only act while a game is running
        /// (the plugin's AdjustVolume does nothing otherwise) and use the same step and volume mode as the
        /// keyboard volume hotkeys.
        /// </summary>
        public static void AddPadVolume(ControllerHotkeyTracker tracker, Func<OverlaySettings> settings,
            Action<int> adjustVolume, Action<Action> dispatch, Action<string> logError)
        {
            tracker.Add(() => settings()?.ControllerHotkeyVolumeUp, () =>
            {
                var s = settings();
                if (s == null) return;
                int step = s.VolumeHotkeyStep;
                dispatch(() =>
                {
                    try { adjustVolume(+step); }
                    catch (Exception ex) { logError("Controller volume-up hotkey failed: " + ex.Message); }
                });
            });
            tracker.Add(() => settings()?.ControllerHotkeyVolumeDown, () =>
            {
                var s = settings();
                if (s == null) return;
                int step = s.VolumeHotkeyStep;
                dispatch(() =>
                {
                    try { adjustVolume(-step); }
                    catch (Exception ex) { logError("Controller volume-down hotkey failed: " + ex.Message); }
                });
            });
        }
    }
}
