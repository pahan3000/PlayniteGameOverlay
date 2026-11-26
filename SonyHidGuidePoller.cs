using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Background poller for Sony DualShock 4 / DualSense PS (home) button via HID.
    /// XInput / Steam Input often never expose the PS button as XINPUT_GAMEPAD_GUIDE.
    /// </summary>
    public sealed class SonyHidGuidePoller : IDisposable
    {
        private readonly Action _onGuide;
        private readonly Action _onStartBack;
        private readonly List<DeviceSlot> _devices = new List<DeviceSlot>();
        private readonly object _lock = new object();
        private Thread _thread;
        private volatile bool _run;
        private bool _prevGuide;
        private bool _prevStartBack;
        private DateTime _lastEnumUtc = DateTime.MinValue;

        /// <summary>Snapshot of the merged DualShock / DualSense state, laid out like an XInput pad.</summary>
        public sealed class PadState
        {
            public bool Up, Down, Left, Right;
            public bool A, B, X, Lb, Rb;   // X = square
            public bool Start, Back, Guide;
            public byte Lt, Rt;          // analog 0-255 (digital press reads as 255)
            public short LX, LY;         // XInput-style: +Y is up
        }

        private volatile PadState _current;

        /// <summary>Latest merged state of all connected Sony pads, or null when none is readable.</summary>
        public PadState Current => _current;

        private sealed class DeviceSlot
        {
            public string Path;
            public IntPtr Handle;
            public volatile byte ReportId;
            public int ReportLength;
            public bool DualSense;
            public bool UseRead;                 // stream with ReadFile instead of HidD_GetInputReport
            public int GetFailures;
            public volatile bool Dead;
            public Thread Reader;
            public readonly object Gate = new object();
            public byte[] Latest;                // last streamed report (read mode)
        }

        /// <summary>Optional diagnostics sink (device found / lost / read mode).</summary>
        public Action<string> Log { get; set; }

        private void Say(string msg)
        {
            try { Log?.Invoke(msg); } catch { }
        }

        // Sony gamepads only (VID 054C): DS4 v1/v2/dongle, DualSense, DualSense Edge.
        private static readonly string[] Ds4Pids = { "PID_05C4", "PID_09CC", "PID_0BA0" };
        private static readonly string[] DualSensePids = { "PID_0CE6", "PID_0DF2" };

        public SonyHidGuidePoller(Action onGuide = null, Action onStartBack = null)
        {
            _onGuide = onGuide;
            _onStartBack = onStartBack;
        }

        public void Start()
        {
            if (_thread != null) return;
            _run = true;
            _prevGuide = false;
            _prevStartBack = false;
            _thread = new Thread(Worker) { IsBackground = true, Name = "SonyHidGuidePoller" };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            try { _thread?.Join(500); } catch { }
            _thread = null;
            _current = null;
            CloseDevices();
        }

        public void Dispose() => Stop();

        private void Worker()
        {
            while (_run)
            {
                try
                {
                    if ((DateTime.UtcNow - _lastEnumUtc).TotalSeconds > 4)
                        EnumerateDevices();

                    bool guide = false, startBack = false;
                    PadState merged = null;

                    List<DeviceSlot> snapshot;
                    lock (_lock) snapshot = new List<DeviceSlot>(_devices);

                    foreach (var dev in snapshot)
                    {
                        if (dev.Handle == IntPtr.Zero || dev.Dead) continue;
                        byte[] buf;
                        if (dev.UseRead)
                        {
                            lock (dev.Gate) buf = dev.Latest == null ? null : (byte[])dev.Latest.Clone();
                            if (buf == null) continue;
                        }
                        else
                        {
                            buf = new byte[dev.ReportLength];
                            buf[0] = dev.ReportId;
                            if (!HidD_GetInputReport(dev.Handle, buf, buf.Length))
                            {
                                // Windows refuses GET_REPORT on some pads / transports: stream with ReadFile instead.
                                if (++dev.GetFailures >= 40) StartReader(dev);
                                continue;
                            }
                            dev.GetFailures = 0;
                        }
                        bool ps, share, options;
                        if (!Parse(dev, buf, out ps, out share, out options)) continue;
                        if (ps) guide = true;
                        if (share && options) startBack = true;

                        var pad = ParseFull(dev, buf, ps, share, options);
                        if (pad == null) continue;
                        if (merged == null) merged = pad;
                        else Merge(merged, pad);
                    }
                    _current = merged;

                    if (guide && !_prevGuide) Raise(_onGuide);
                    if (startBack && !_prevStartBack) Raise(_onStartBack);
                    _prevGuide = guide;
                    _prevStartBack = startBack;
                }
                catch { _current = null; }

                Thread.Sleep(25);
            }
        }

        private static void Raise(Action action)
        {
            if (action == null) return;
            try
            {
                var disp = Application.Current?.Dispatcher;
                if (disp != null)
                    disp.BeginInvoke(DispatcherPriority.Input, action);
                else
                    action();
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        /// <summary>
        /// Byte offsets (buffer[0] = report id).
        /// DS4 USB / simple BT (id 1): buttons[6] share=0x10 options=0x20, PS=[7] bit0
        /// DS4 BT full (id 0x11):      buttons[8], PS=[9]
        /// DualSense USB (id 1, 64 B): buttons[9] create=0x10 options=0x20, PS=[10] bit0
        /// DualSense BT (id 0x31):     buttons[10], PS=[11]
        /// </summary>
        private static bool Parse(DeviceSlot d, byte[] r, out bool ps, out bool share, out bool options)
        {
            ps = share = options = false;
            int btn, psIdx;
            if (d.ReportId == 0x11) { btn = 8; psIdx = 9; }
            else if (d.ReportId == 0x31) { btn = 10; psIdx = 11; }
            else if (d.DualSense && d.ReportLength <= 64) { btn = 9; psIdx = 10; }
            else { btn = 6; psIdx = 7; }
            if (r == null || r.Length <= psIdx) return false;
            share = (r[btn] & 0x10) != 0;
            options = (r[btn] & 0x20) != 0;
            ps = (r[psIdx] & 0x01) != 0;
            return true;
        }

        /// <summary>
        /// Full pad state using the same report layouts as Parse. Relative to the buttons byte (btn):
        /// face + hat byte = btn-1; DS4 sticks start at btn-5, DualSense at btn-8; DS4 L2/R2 follow the
        /// PS byte, DualSense L2/R2 sit at btn-4 / btn-3.
        /// Hat (low nibble): 0=N 1=NE 2=E 3=SE 4=S 5=SW 6=W 7=NW 8=released. Faces: square 0x10, cross 0x20,
        /// circle 0x40, triangle 0x80. Shoulders: L1 0x01, R1 0x02, L2 0x04, R2 0x08 (in the btn byte).
        /// </summary>
        private static PadState ParseFull(DeviceSlot d, byte[] r, bool ps, bool share, bool options)
        {
            int btn, psIdx, stickIdx, trigIdx;
            if (d.ReportId == 0x11) { btn = 8; psIdx = 9; stickIdx = 3; trigIdx = 10; }
            else if (d.ReportId == 0x31) { btn = 10; psIdx = 11; stickIdx = 2; trigIdx = 6; }
            else if (d.DualSense && d.ReportLength <= 64) { btn = 9; psIdx = 10; stickIdx = 1; trigIdx = 5; }
            else { btn = 6; psIdx = 7; stickIdx = 1; trigIdx = 8; }
            if (r == null || r.Length <= psIdx) return null;

            int face = r[btn - 1];
            int hat = face & 0x0F;
            var p = new PadState
            {
                Up = hat == 0 || hat == 1 || hat == 7,
                Right = hat == 1 || hat == 2 || hat == 3,
                Down = hat == 3 || hat == 4 || hat == 5,
                Left = hat == 5 || hat == 6 || hat == 7,
                A = (face & 0x20) != 0,   // cross
                B = (face & 0x40) != 0,   // circle
                X = (face & 0x10) != 0,   // square
                Lb = (r[btn] & 0x01) != 0,
                Rb = (r[btn] & 0x02) != 0,
                Start = options,
                Back = share,
                Guide = ps,
                LX = (short)((r[stickIdx] - 128) * 256),
                LY = (short)((128 - r[stickIdx + 1]) * 256)
            };

            byte lt = trigIdx < r.Length ? r[trigIdx] : (byte)0;
            byte rt = trigIdx + 1 < r.Length ? r[trigIdx + 1] : (byte)0;
            p.Lt = (r[btn] & 0x04) != 0 ? (byte)255 : lt;
            p.Rt = (r[btn] & 0x08) != 0 ? (byte)255 : rt;
            return p;
        }

        /// <summary>Folds a second pad into the first (any pad can navigate, like the XInput loop does).</summary>
        private static void Merge(PadState into, PadState other)
        {
            into.Up |= other.Up; into.Down |= other.Down; into.Left |= other.Left; into.Right |= other.Right;
            into.A |= other.A; into.B |= other.B; into.X |= other.X; into.Lb |= other.Lb; into.Rb |= other.Rb;
            into.Start |= other.Start; into.Back |= other.Back; into.Guide |= other.Guide;
            if (other.Lt > into.Lt) into.Lt = other.Lt;
            if (other.Rt > into.Rt) into.Rt = other.Rt;
            if (Math.Abs((int)other.LX) + Math.Abs((int)other.LY) > Math.Abs((int)into.LX) + Math.Abs((int)into.LY))
            {
                into.LX = other.LX;
                into.LY = other.LY;
            }
        }

        private readonly HashSet<string> _openFailLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void StartReader(DeviceSlot dev)
        {
            lock (dev.Gate)
            {
                if (dev.UseRead) return;
                dev.UseRead = true;
            }
            Say("Sony HID: streaming reports with ReadFile (GET_REPORT unavailable) - " + dev.Path);
            dev.Reader = new Thread(() => ReaderLoop(dev)) { IsBackground = true, Name = "SonyHidReader" };
            dev.Reader.Start();
        }

        private void ReaderLoop(DeviceSlot dev)
        {
            byte[] buf = new byte[Math.Max(dev.ReportLength, 64)];
            while (_run && !dev.Dead)
            {
                uint read;
                if (!ReadFile(dev.Handle, buf, (uint)buf.Length, out read, IntPtr.Zero) || read == 0)
                {
                    dev.Dead = true;
                    lock (dev.Gate) dev.Latest = null;
                    break;
                }
                byte id = buf[0];
                if (id != 0x01 && id != 0x11 && id != 0x31) continue;   // not an input report we understand
                dev.ReportId = id;
                var copy = new byte[buf.Length];
                Buffer.BlockCopy(buf, 0, copy, 0, (int)Math.Min(read, (uint)buf.Length));
                lock (dev.Gate) dev.Latest = copy;
            }
        }

        private void EnumerateDevices()
        {
            _lastEnumUtc = DateTime.UtcNow;

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr infoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
                return;

            try
            {
                var iface = new SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(iface);

                for (uint index = 0; SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref iface); index++)
                {
                    uint needed = 0;
                    SetupDiGetDeviceInterfaceDetail(infoSet, ref iface, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                    if (needed == 0) continue;

                    IntPtr detailPtr = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detailPtr, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(infoSet, ref iface, detailPtr, needed, ref needed, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detailPtr, 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        if (path.IndexOf("VID_054C", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        bool isDs4 = Array.Exists(Ds4Pids, x => path.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                        bool isDs5 = Array.Exists(DualSensePids, x => path.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!isDs4 && !isDs5) continue;
                        // No interface filter: a USB DS4 / DualSense is a composite device (audio + HID), so its gamepad
                        // lives on MI_03 (seen in Playnite's own log), not MI_00 / MI_01. The collection's usage is
                        // checked after opening instead.

                        bool known;
                        lock (_lock) known = _devices.Exists(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
                        if (known) { found.Add(path); continue; }

                        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == IntPtr.Zero || h == new IntPtr(-1))
                        {
                            h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        }
                        if (h == IntPtr.Zero || h == new IntPtr(-1))
                        {
                            if (_openFailLogged.Add(path))
                                Say("Sony HID: cannot open " + path + " (error " + Marshal.GetLastWin32Error() +
                                    "). Another tool (HidHide / DS4Windows exclusive mode) may be hiding the pad.");
                            continue;
                        }

                        ushort usagePage, usage;
                        int len = GetInputReportLength(h, out usagePage, out usage);
                        // Only the gamepad collection (Generic Desktop: Joystick 4 / Game Pad 5), not audio / vendor ones.
                        if (usagePage != 0x01 || (usage != 0x04 && usage != 0x05))
                        {
                            CloseHandle(h);
                            continue;
                        }
                        if (len < 10) { CloseHandle(h); Say("Sony HID: skipped " + path + " (input report length " + len + ")"); continue; }

                        found.Add(path);
                        var slot = new DeviceSlot { Path = path, Handle = h, ReportLength = len, DualSense = isDs5 };

                        byte[] reportIds = { 0x01, 0x11, 0x31 };
                        byte[] probe = new byte[len];
                        foreach (var id in reportIds)
                        {
                            Array.Clear(probe, 0, probe.Length);
                            probe[0] = id;
                            if (HidD_GetInputReport(h, probe, probe.Length)) { slot.ReportId = id; break; }
                        }

                        lock (_lock) _devices.Add(slot);
                        Say("Sony HID: found " + (isDs5 ? "DualSense" : "DualShock 4") + " (" + path + ", report length " + len +
                            (slot.ReportId == 0 ? ", GET_REPORT failed" : ", report id 0x" + slot.ReportId.ToString("X2")) + ")");
                        if (slot.ReportId == 0) StartReader(slot);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailPtr);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }

            // Drop pads that were unplugged (or whose reader died); they are re-opened on the next pass if still present.
            List<DeviceSlot> gone;
            lock (_lock)
            {
                gone = _devices.FindAll(d => d.Dead || !found.Contains(d.Path));
                foreach (var d in gone) _devices.Remove(d);
            }
            foreach (var d in gone)
            {
                Say("Sony HID: lost " + d.Path);
                CloseSlot(d);
            }
        }

        private static void CloseSlot(DeviceSlot d)
        {
            try
            {
                d.Dead = true;
                if (d.Handle != IntPtr.Zero && d.Handle != new IntPtr(-1))
                {
                    try { CancelIoEx(d.Handle, IntPtr.Zero); } catch { }
                    try { d.Reader?.Join(300); } catch { }
                    CloseHandle(d.Handle);
                }
            }
            catch { }
        }

        private static int GetInputReportLength(IntPtr h, out ushort usagePage, out ushort usage)
        {
            usagePage = 0;
            usage = 0;
            IntPtr pre = IntPtr.Zero;
            try
            {
                if (!HidD_GetPreparsedData(h, out pre) || pre == IntPtr.Zero) return 0;
                HIDP_CAPS caps;
                if (HidP_GetCaps(pre, out caps) != 0x110000) return 0; // HIDP_STATUS_SUCCESS
                usagePage = caps.UsagePage;
                usage = caps.Usage;
                return caps.InputReportByteLength;
            }
            catch { return 0; }
            finally { if (pre != IntPtr.Zero) { try { HidD_FreePreparsedData(pre); } catch { } } }
        }

        private void CloseDevices()
        {
            List<DeviceSlot> all;
            lock (_lock)
            {
                all = new List<DeviceSlot>(_devices);
                _devices.Clear();
            }
            foreach (var d in all) CloseSlot(d);
        }

        private const uint DIGCF_PRESENT = 0x2;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern uint HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
    }
}
