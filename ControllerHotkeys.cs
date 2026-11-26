using System;
using System.Collections.Generic;
using System.Text;

namespace PlayniteGameOverlay
{
    /// <summary>Button bit flags used for controller hotkeys (merged across XInput pads and the native Sony HID pad).</summary>
    [Flags]
    public enum PadButtons
    {
        None = 0,
        A = 1 << 0,        // Cross on Sony
        B = 1 << 1,        // Circle
        X = 1 << 2,        // Square
        Y = 1 << 3,        // Triangle (XInput pads only)
        LB = 1 << 4,
        RB = 1 << 5,
        LT = 1 << 6,
        RT = 1 << 7,
        Start = 1 << 8,
        Back = 1 << 9,
        Guide = 1 << 10,
        LS = 1 << 11,      // left stick click (XInput pads only)
        RS = 1 << 12,      // right stick click (XInput pads only)
        DUp = 1 << 13,
        DDown = 1 << 14,
        DLeft = 1 << 15,
        DRight = 1 << 16
    }

    /// <summary>Parsing / formatting of controller hotkey strings such as "Back+A" or "LB+RB+Y".</summary>
    public static class ControllerHotkeys
    {
        /// <summary>Buttons currently held, published by <see cref="XInputPoller"/> every poll.</summary>
        public static volatile int CurrentMask;

        /// <summary>True while the settings window is recording a combo: navigation and hotkeys are suspended.</summary>
        public static volatile bool CaptureActive;

        private static readonly string[] Names =
        {
            "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Start", "Back", "Guide", "LS", "RS",
            "DUp", "DDown", "DLeft", "DRight"
        };

        public static int Parse(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return 0;
            int mask = 0;
            foreach (var raw in combo.Split('+'))
            {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                int idx = -1;
                for (int i = 0; i < Names.Length; i++)
                    if (string.Equals(Names[i], n, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                if (idx < 0) return 0; // unknown token: treat the whole binding as unset
                mask |= 1 << idx;
            }
            return mask;
        }

        public static string Format(int mask)
        {
            if (mask == 0) return "";
            var sb = new StringBuilder();
            for (int k = 0; k < Names.Length; k++)
            {
                int i = HotkeysLayout.PadDisplayOrder[k]; // hotkeys-layout
                if ((mask & (1 << i)) == 0) continue;
                if (sb.Length > 0) sb.Append('+');
                sb.Append(Names[i]);
            }
            return sb.ToString();
        }

        public static int BitCount(int v)
        {
            int c = 0;
            while (v != 0) { c += v & 1; v >>= 1; }
            return c;
        }

        // XInput wButtons bits
        public static int MaskFromXInput(int w, byte lt, byte rt)
        {
            int m = 0;
            if ((w & 0x1000) != 0) m |= (int)PadButtons.A;
            if ((w & 0x2000) != 0) m |= (int)PadButtons.B;
            if ((w & 0x4000) != 0) m |= (int)PadButtons.X;
            if ((w & 0x8000) != 0) m |= (int)PadButtons.Y;
            if ((w & 0x0100) != 0) m |= (int)PadButtons.LB;
            if ((w & 0x0200) != 0) m |= (int)PadButtons.RB;
            if ((w & 0x0010) != 0) m |= (int)PadButtons.Start;
            if ((w & 0x0020) != 0) m |= (int)PadButtons.Back;
            if ((w & 0x0400) != 0) m |= (int)PadButtons.Guide;
            if ((w & 0x0040) != 0) m |= (int)PadButtons.LS;
            if ((w & 0x0080) != 0) m |= (int)PadButtons.RS;
            if ((w & 0x0001) != 0) m |= (int)PadButtons.DUp;
            if ((w & 0x0002) != 0) m |= (int)PadButtons.DDown;
            if ((w & 0x0004) != 0) m |= (int)PadButtons.DLeft;
            if ((w & 0x0008) != 0) m |= (int)PadButtons.DRight;
            if (lt >= 120) m |= (int)PadButtons.LT;
            if (rt >= 120) m |= (int)PadButtons.RT;
            return m;
        }

        public static int MaskFromSony(SonyHidGuidePoller.PadState s)
        {
            if (s == null) return 0;
            int m = 0;
            if (s.A) m |= (int)PadButtons.A;
            if (s.B) m |= (int)PadButtons.B;
            if (s.X) m |= (int)PadButtons.X;
            if (s.Lb) m |= (int)PadButtons.LB;
            if (s.Rb) m |= (int)PadButtons.RB;
            if (s.Start) m |= (int)PadButtons.Start;
            if (s.Back) m |= (int)PadButtons.Back;
            if (s.Guide) m |= (int)PadButtons.Guide;
            if (s.Up) m |= (int)PadButtons.DUp;
            if (s.Down) m |= (int)PadButtons.DDown;
            if (s.Left) m |= (int)PadButtons.DLeft;
            if (s.Right) m |= (int)PadButtons.DRight;
            if (s.Lt >= 120) m |= (int)PadButtons.LT;
            if (s.Rt >= 120) m |= (int)PadButtons.RT;
            return m;
        }
    }

    /// <summary>
    /// Edge-detects the user's controller hotkeys. Fires an action when every button of its combo
    /// becomes held; if several combos complete at once only the one with the most buttons fires.
    /// </summary>
    public sealed class ControllerHotkeyTracker
    {
        private sealed class Binding
        {
            public Func<string> Get;
            public Action Fire;
            public string CachedText;
            public int Mask;
        }

        private readonly List<Binding> _bindings = new List<Binding>();
        private int _prev;

        public void Add(Func<string> getCombo, Action fire)
        {
            _bindings.Add(new Binding { Get = getCombo, Fire = fire, CachedText = null, Mask = 0 });
        }

        public void Update(int mask)
        {
            int prev = _prev;
            _prev = mask;
            if (ControllerHotkeys.CaptureActive || mask == 0 || mask == prev) return;

            Binding best = null;
            int bestBits = 0;
            foreach (var b in _bindings)
            {
                string text = null;
                try { text = b.Get(); } catch { }
                if (!string.Equals(text, b.CachedText, StringComparison.Ordinal))
                {
                    b.CachedText = text;
                    b.Mask = ControllerHotkeys.Parse(text);
                }
                if (b.Mask == 0) continue;
                bool nowDown = (mask & b.Mask) == b.Mask;
                bool wasDown = (prev & b.Mask) == b.Mask;
                if (!nowDown || wasDown) continue;
                int bits = ControllerHotkeys.BitCount(b.Mask);
                if (bits > bestBits) { best = b; bestBits = bits; }
            }

            if (best != null)
            {
                try { best.Fire?.Invoke(); } catch { }
            }
        }
    }
}
