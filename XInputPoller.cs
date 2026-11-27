using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Gamepad polling via XInput on the UI dispatcher.
    /// - Always polls Start+Back / Guide for overlay toggle (Desktop mode fallback).
    /// - When the overlay is open, also polls D-pad, left stick, A, and B for UI navigation.
    /// </summary>
    public sealed class XInputPoller : IDisposable
    {
        private const int ERROR_SUCCESS = 0;
        private const int XINPUT_GAMEPAD_DPAD_UP = 0x0001;
        private const int XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
        private const int XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
        private const int XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        private const int XINPUT_GAMEPAD_START = 0x0010;
        private const int XINPUT_GAMEPAD_BACK = 0x0020;
        private const int XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
        private const int XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const int XINPUT_GAMEPAD_A = 0x1000;
        private const int XINPUT_GAMEPAD_B = 0x2000;
        private const int XINPUT_GAMEPAD_X = 0x4000;
        private const int XINPUT_GAMEPAD_GUIDE = 0x0400;
        private const short STICK_DEADZONE = 22000;
        // Analog triggers (0-255): press above ON, re-arm only after dropping below OFF (hysteresis).
        private const byte TRIGGER_ON = 120;
        private const byte TRIGGER_OFF = 60;

        private readonly DispatcherTimer _timer;
        private readonly Action _onStartBack;
        private readonly Action _onGuide;
        private readonly Func<ControllerShortcut> _getShortcut;
        private readonly Func<bool> _isOverlayOpen;
        private readonly Action _onUp;
        private readonly Action _onDown;
        private readonly Action _onLeft;
        private readonly Action _onRight;
        private readonly Action _onA;
        private readonly Action _onB;
        private readonly Action _onLb;
        private readonly Action _onRb;
        private readonly Action _onLt;
        private readonly Action _onRt;
        private readonly Action _onLbDesktop;
        private readonly Action _onRbDesktop;
        private readonly Action _onLtDesktop;
        private readonly Action _onRtDesktop;
        private readonly Action _onAnyPadActivity;
        private readonly Action _onSquare;
        private readonly Action _onStart;

        /// <summary>Raised every poll with the buttons currently held (see <see cref="PadButtons"/>).</summary>
        public event Action<int> MaskPolled;

        private bool _prevStartBack;
        private bool _prevGuide;
        private bool _prevUp;
        private bool _prevDown;
        private bool _prevLeft;
        private bool _prevRight;
        private bool _prevA;
        private bool _prevB;
        private bool _prevSquare;
        private bool _prevStart;
        private bool _startComboUsed;   // Back was pressed while Start was held: that is the overlay shortcut, not a Start press
        private bool _prevLb;
        private bool _prevRb;
        private bool _prevLt;
        private bool _prevRt;
        private bool _started;
        private bool _sonySeen;

        private DateTime _upHeldSince = DateTime.MinValue;
        private DateTime _downHeldSince = DateTime.MinValue;
        private DateTime _leftHeldSince = DateTime.MinValue;
        private DateTime _rightHeldSince = DateTime.MinValue;
        private DateTime _lastUpRepeat = DateTime.MinValue;
        private DateTime _lastDownRepeat = DateTime.MinValue;
        private DateTime _lastLeftRepeat = DateTime.MinValue;
        private DateTime _lastRightRepeat = DateTime.MinValue;
        // Snappier for Desktop list browsing (still usable in overlay)
        private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(280);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(85);

        private static readonly GetStateDelegate GetStateFn;
        private static readonly GetStateDelegate GetStateExFn;

        private delegate int GetStateDelegate(int dwUserIndex, ref XINPUT_STATE pState);

        static XInputPoller()
        {
            GetStateFn = ResolveGetState("xinput1_4.dll")
                ?? ResolveGetState("xinput1_3.dll")
                ?? ResolveGetState("xinput9_1_0.dll");

            GetStateExFn = ResolveGetStateOrdinal("xinput1_4.dll", 100)
                ?? ResolveGetStateOrdinal("xinput1_3.dll", 100);
        }

        public XInputPoller(
            Func<ControllerShortcut> getShortcut,
            Action onStartBack,
            Action onGuide,
            Func<bool> isOverlayOpen = null,
            Action onUp = null,
            Action onDown = null,
            Action onLeft = null,
            Action onRight = null,
            Action onA = null,
            Action onB = null,
            Action onLb = null,
            Action onRb = null,
            Action onAnyPadActivity = null,
            int intervalMs = 33,
            Action onLt = null,
            Action onRt = null,
            Action onLbDesktop = null,
            Action onRbDesktop = null,
            Action onLtDesktop = null,
            Action onRtDesktop = null,
            Action onSquare = null,
            Action onStart = null)
        {
            _onSquare = onSquare;
            _onStart = onStart;
            _onLtDesktop = onLtDesktop;
            _onRtDesktop = onRtDesktop;
            _onLbDesktop = onLbDesktop;
            _onRbDesktop = onRbDesktop;
            _getShortcut = getShortcut ?? (() => ControllerShortcut.StartBack);
            _onStartBack = onStartBack;
            _onGuide = onGuide;
            _isOverlayOpen = isOverlayOpen ?? (() => false);
            _onUp = onUp;
            _onDown = onDown;
            _onLeft = onLeft;
            _onRight = onRight;
            _onA = onA;
            _onB = onB;
            _onLb = onLb;
            _onRb = onRb;
            _onLt = onLt;
            _onRt = onRt;
            _onAnyPadActivity = onAnyPadActivity;
            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(16, intervalMs))
            };
            _timer.Tick += (_, __) =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { PollOnce(); }
                catch { /* ignore */ }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms > 25) Breadcrumb.Throttled("poll-slow", 1000, "PadPoll: slow tick " + ms.ToString("0") + " ms (timer interval " + _timer.Interval.TotalMilliseconds + " ms)");
            };
        }

        public bool IsAvailable => GetStateFn != null || GetStateExFn != null;

        /// <summary>
        /// Optional native DualShock 4 / DualSense reader. Its state is OR-merged into the XInput state each
        /// poll, so a Sony pad without DS4Windows / Steam Input still navigates the overlay and Desktop UI.
        /// </summary>
        public SonyHidGuidePoller SonyPad { get; set; }

        /// <summary>
        /// True while at least one XInput pad answered the last poll. IsAvailable only says the XInput DLL
        /// loaded (always true on Windows), so SDL-only pads (DualSense, DS4, Switch Pro ...) must not be
        /// treated as "owned by XInput" just because the DLL exists.
        /// </summary>
        public bool AnyConnected { get; private set; }

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            ResetPrev();
            _timer.Start();
        }

        public void Stop()
        {
            _started = false;
            try { _timer.Stop(); } catch { }
        }

        public void Dispose() => Stop();

        private void ResetPrev()
        {
            _prevStartBack = false;
            _prevGuide = false;
            _prevUp = _prevDown = _prevLeft = _prevRight = false;
            _prevA = _prevB = _prevSquare = false;
            _prevStart = false; _startComboUsed = false;
            _prevLb = _prevRb = _prevLt = _prevRt = false;
            _upHeldSince = _downHeldSince = _leftHeldSince = _rightHeldSince = DateTime.MinValue;
            _lastUpRepeat = _lastDownRepeat = _lastLeftRepeat = _lastRightRepeat = DateTime.MinValue;
        }

        private void PollOnce()
        {
            if (GetStateFn == null && GetStateExFn == null)
                return;

            bool start = false, back = false, guide = false;
            bool up = false, down = false, left = false, right = false;
            bool a = false, b = false, lb = false, rb = false;
            bool lt = false, rt = false;
            bool square = false;

            int padMask = 0;   // every button held on any pad (controller hotkeys)
            bool anyConnected = false;
            bool xinputActive = false;   // a real XInput pad is being used right now
            bool sonyActive = false;     // the native DualShock / DualSense is being used right now
            for (int i = 0; i < 4; i++)
            {
                var state = new XINPUT_STATE();
                int result = -1;

                if (GetStateExFn != null)
                    result = GetStateExFn(i, ref state);
                if (result != ERROR_SUCCESS && GetStateFn != null)
                {
                    state = new XINPUT_STATE();
                    result = GetStateFn(i, ref state);
                }
                if (result != ERROR_SUCCESS)
                    continue;
                anyConnected = true;

                int buttons = state.Gamepad.wButtons;
                padMask |= ControllerHotkeys.MaskFromXInput(buttons, state.Gamepad.bLeftTrigger, state.Gamepad.bRightTrigger);
                if (buttons != 0 || state.Gamepad.bLeftTrigger >= TRIGGER_ON || state.Gamepad.bRightTrigger >= TRIGGER_ON ||
                    Math.Abs((int)state.Gamepad.sThumbLX) >= STICK_DEADZONE || Math.Abs((int)state.Gamepad.sThumbLY) >= STICK_DEADZONE)
                    xinputActive = true;
                if ((buttons & XINPUT_GAMEPAD_START) != 0) start = true;
                if ((buttons & XINPUT_GAMEPAD_BACK) != 0) back = true;
                if ((buttons & XINPUT_GAMEPAD_GUIDE) != 0) guide = true;
                if ((buttons & XINPUT_GAMEPAD_DPAD_UP) != 0) up = true;
                if ((buttons & XINPUT_GAMEPAD_DPAD_DOWN) != 0) down = true;
                if ((buttons & XINPUT_GAMEPAD_DPAD_LEFT) != 0) left = true;
                if ((buttons & XINPUT_GAMEPAD_DPAD_RIGHT) != 0) right = true;
                if ((buttons & XINPUT_GAMEPAD_A) != 0) a = true;
                if ((buttons & XINPUT_GAMEPAD_B) != 0) b = true;
                if ((buttons & XINPUT_GAMEPAD_X) != 0) square = true;
                if ((buttons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0) lb = true;
                if ((buttons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0) rb = true;
                if (state.Gamepad.bLeftTrigger >= (_prevLt ? TRIGGER_OFF : TRIGGER_ON)) lt = true;
                if (state.Gamepad.bRightTrigger >= (_prevRt ? TRIGGER_OFF : TRIGGER_ON)) rt = true;

                short lx = state.Gamepad.sThumbLX;
                short ly = state.Gamepad.sThumbLY;
                int ax = Math.Abs((int)lx);
                int ay = Math.Abs((int)ly);
                if (ax >= STICK_DEADZONE || ay >= STICK_DEADZONE)
                {
                    if (ay >= ax)
                    {
                        if (ly > 0) up = true;
                        else down = true;
                    }
                    else
                    {
                        if (lx < 0) left = true;
                        else right = true;
                    }
                }
            }

            // Native Sony pad (HID). Merged as plain button state: if DS4Windows / Steam Input also exposes the
            // same pad over XInput, both report the same press and the edge detection below fires once.
            var sony = SonyPad?.Current;
            if (sony != null)
            {
                anyConnected = true;
                padMask |= ControllerHotkeys.MaskFromSony(sony);
                if (sony.Up || sony.Down || sony.Left || sony.Right || sony.A || sony.B || sony.X || sony.Lb || sony.Rb ||
                    sony.Start || sony.Back || sony.Guide || sony.Lt >= TRIGGER_ON || sony.Rt >= TRIGGER_ON ||
                    Math.Abs((int)sony.LX) >= STICK_DEADZONE || Math.Abs((int)sony.LY) >= STICK_DEADZONE)
                    sonyActive = true;
                if (sony.Start) start = true;
                if (sony.Back) back = true;
                if (sony.Guide) guide = true;
                if (sony.Up) up = true;
                if (sony.Down) down = true;
                if (sony.Left) left = true;
                if (sony.Right) right = true;
                if (sony.A) a = true;
                if (sony.B) b = true;
                if (sony.X) square = true;
                if (sony.Lb) lb = true;
                if (sony.Rb) rb = true;
                if (sony.Lt >= (_prevLt ? TRIGGER_OFF : TRIGGER_ON)) lt = true;
                if (sony.Rt >= (_prevRt ? TRIGGER_OFF : TRIGGER_ON)) rt = true;

                int sax = Math.Abs((int)sony.LX);
                int say = Math.Abs((int)sony.LY);
                if (sax >= STICK_DEADZONE || say >= STICK_DEADZONE)
                {
                    if (say >= sax)
                    {
                        if (sony.LY > 0) up = true;
                        else down = true;
                    }
                    else
                    {
                        if (sony.LX < 0) left = true;
                        else right = true;
                    }
                }
            }

            AnyConnected = anyConnected;

            // Publish the raw button mask for controller hotkeys / the settings capture box.
            ControllerHotkeys.CurrentMask = padMask;
            try { MaskPolled?.Invoke(padMask); } catch { }

            // While the settings window records a combo, swallow navigation so the presses are not
            // also treated as A / B / D-pad input. Previous-state flags follow the pad so nothing
            // fires when recording ends.
            if (ControllerHotkeys.CaptureActive)
            {
                _prevA = a; _prevB = b; _prevSquare = square;
                _prevStart = start; _startComboUsed = false;
                _prevLb = lb; _prevRb = rb; _prevLt = lt; _prevRt = rt;
                _prevStartBack = start && back; _prevGuide = guide;
                _prevUp = up; _prevDown = down; _prevLeft = left; _prevRight = right;
                _upHeldSince = _downHeldSince = _leftHeldSince = _rightHeldSince = DateTime.MinValue;
                return;
            }

            // Which family of pad the player is using, for Auto button prompts. A Sony pad that is the only
            // pad present counts straight away; with both present, whichever was touched last wins.
            if (sony == null) PadPrompts.SonyDetected = false;
            else if (sonyActive) PadPrompts.SonyDetected = true;
            else if (xinputActive) PadPrompts.SonyDetected = false;
            else if (!_sonySeen) PadPrompts.SonyDetected = true;   // just connected, nothing pressed yet
            _sonySeen = sony != null;

            bool anyPad =
                start || back || guide || up || down || left || right || a || b || square || lb || rb || lt || rt;
            if (anyPad)
                _onAnyPadActivity?.Invoke();

            bool startBack = start && back;
            var shortcut = _getShortcut();

            // Button state is stored BEFORE any callback runs. A callback can open a modal dialog whose
            // nested message loop keeps this poller ticking; stale "previous" values would then re-fire
            // a held button inside the dialog or swallow the next real press.
            bool startBackPress = startBack && !_prevStartBack;
            bool guidePress = guide && !_prevGuide;
            bool aPress = a && !_prevA;
            bool bPress = b && !_prevB;
            bool squarePress = square && !_prevSquare;
            _prevSquare = square;
            // Start on its own acts when it is released, so Start + Back (the overlay shortcut) never also counts as Start.
            if (start && back) _startComboUsed = true;
            bool startAloneRelease = !start && _prevStart && !_startComboUsed;
            if (!start) _startComboUsed = false;
            _prevStart = start;
            bool lbPress = lb && !_prevLb;
            bool rbPress = rb && !_prevRb;
            bool ltPress = lt && !_prevLt;
            bool rtPress = rt && !_prevRt;
            _prevStartBack = startBack;
            _prevGuide = guide;
            _prevA = a;
            _prevB = b;
            _prevLb = lb;
            _prevRb = rb;
            _prevLt = lt;
            _prevRt = rt;

            if (shortcut == ControllerShortcut.StartBack)
            {
                if (startBackPress) Safe(_onStartBack);
            }
            else if (shortcut == ControllerShortcut.Guide)
            {
                if (guidePress) Safe(_onGuide);
            }

            // D-pad / stick / A / B always polled so Desktop nav works when overlay is closed.
            var now = DateTime.UtcNow;

            HandleDirection(up, ref _prevUp, ref _upHeldSince, ref _lastUpRepeat, now, _onUp);
            HandleDirection(down, ref _prevDown, ref _downHeldSince, ref _lastDownRepeat, now, _onDown);
            HandleDirection(left, ref _prevLeft, ref _leftHeldSince, ref _lastLeftRepeat, now, _onLeft);
            HandleDirection(right, ref _prevRight, ref _rightHeldSince, ref _lastRightRepeat, now, _onRight);

            if (aPress) Safe(_onA);
            if (bPress) Safe(_onB);
            if (squarePress) Safe(_onSquare);
            if (startAloneRelease) Safe(_onStart);

            bool overlayOpen = false;
            try { overlayOpen = _isOverlayOpen(); } catch { }

            if (overlayOpen)
            {
                if (lbPress) Safe(_onLb);
                if (rbPress) Safe(_onRb);
                if (ltPress) Safe(_onLt);
                if (rtPress) Safe(_onRt);
            }
            else
            {
                // Overlay closed: LB / RB drive the Playnite Desktop top bar (or a window's tabs / sections),
                // LT / RT jump between the top bar's icon groups (or a window's top / Save).
                if (lbPress) Safe(_onLbDesktop);
                if (rbPress) Safe(_onRbDesktop);
                if (ltPress) Safe(_onLtDesktop);
                if (rtPress) Safe(_onRtDesktop);
            }
        }

        /// <summary>One failing callback must never abort the rest of the poll (or leave state half-updated).</summary>
        private static void Safe(Action action)
        {
            try { action?.Invoke(); }
            catch { /* ignore */ }
        }

        private static void HandleDirection(
            bool pressed,
            ref bool prev,
            ref DateTime heldSince,
            ref DateTime lastRepeat,
            DateTime now,
            Action action)
        {
            if (!pressed)
            {
                prev = false;
                heldSince = DateTime.MinValue;
                return;
            }

            if (!prev)
            {
                prev = true;
                heldSince = now;
                lastRepeat = now;
                Safe(action);
                return;
            }

            if ((now - heldSince) >= RepeatDelay && (now - lastRepeat) >= RepeatInterval)
            {
                Safe(action);
                lastRepeat = DateTime.UtcNow;   // from the end of the step: a slow step must not be followed by an instant one
            }
        }

        private static GetStateDelegate ResolveGetState(string dll)
        {
            IntPtr h = LoadLibrary(dll);
            if (h == IntPtr.Zero)
                return null;
            IntPtr p = GetProcAddress(h, "XInputGetState");
            if (p == IntPtr.Zero)
                return null;
            return (GetStateDelegate)Marshal.GetDelegateForFunctionPointer(p, typeof(GetStateDelegate));
        }

        private static GetStateDelegate ResolveGetStateOrdinal(string dll, short ordinal)
        {
            IntPtr h = LoadLibrary(dll);
            if (h == IntPtr.Zero)
                return null;
            IntPtr p = GetProcAddress(h, new IntPtr(ordinal));
            if (p == IntPtr.Zero)
                return null;
            return (GetStateDelegate)Marshal.GetDelegateForFunctionPointer(p, typeof(GetStateDelegate));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr procOrdinal);
    }
}
