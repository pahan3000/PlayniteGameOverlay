using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Per-app volume via Windows Core Audio (same sessions as sndvol).
    /// Interop uses IntPtr outs + explicit QI to avoid RCW cast failures.
    /// </summary>
    public static class GameAudioSession
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static string _lastError;
        public static string LastError => _lastError;

        // All hand-rolled COM (raw vtable calls + manual Marshal.Release) goes through this one gate.
        // At game start several callers (ThreadPool volume apply, the 2 s unmute timer on the UI
        // thread, the 4 s retry) used to run it concurrently; interleaved raw AddRef/Release on the
        // same COM objects can corrupt the heap, which the CLR reports as a fail-fast (0xC0000409).
        private static readonly object ComGate = new object();

        public static List<AudioDeviceInfo> ListPlaybackDevices()
        {
            lock (ComGate) { return ListPlaybackDevicesCore(); }
        }

        public static bool TryGetMasterVolume(out float volume, out bool muted)
        {
            lock (ComGate) { return TryGetMasterVolumeCore(out volume, out muted); }
        }

        public static bool TrySetMasterVolume(float volume)
        {
            lock (ComGate) { return TrySetMasterVolumeCore(volume); }
        }

        public static bool TrySetMasterMute(bool muted)
        {
            lock (ComGate) { return TrySetMasterMuteCore(muted); }
        }

        public static bool SetDefaultPlaybackDevice(string deviceId)
        {
            lock (ComGate) { return SetDefaultPlaybackDeviceCore(deviceId); }
        }

        public static bool TryGetVolume(int processId, string exePath, out float volume, out bool muted)
        {
            lock (ComGate) { return TryGetVolumeCore(processId, exePath, out volume, out muted); }
        }

        public static bool TrySetVolume(int processId, string exePath, float volume)
        {
            lock (ComGate) { return TrySetVolumeCore(processId, exePath, volume); }
        }

        public static bool TrySetMute(int processId, string exePath, bool mute)
        {
            lock (ComGate) { return TrySetMuteCore(processId, exePath, mute); }
        }

        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static readonly Guid IID_IAudioSessionEnumerator = new Guid("E2F5BB11-0570-40CA-ACDD-3AA01232CE87");
        private static readonly Guid IID_IAudioSessionControl2 = new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
        private static readonly Guid IID_ISimpleAudioVolume = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");
        private static readonly Guid IID_IMMDeviceCollection = new Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E");
        private static readonly Guid IID_IMMDevice = new Guid("D666063F-1587-4E43-81F1-B948E807363F");
        private static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        private static readonly Guid PKEY_Device_FriendlyName = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        // PolicyConfig for SetDefaultEndpoint
        private static readonly Guid CLSID_PolicyConfigClient = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        private static readonly Guid IID_IPolicyConfig = new Guid("F8679F50-850A-41CF-9C72-430F290290C8");

        public sealed class AudioDeviceInfo : System.ComponentModel.INotifyPropertyChanged
        {
            public string Id { get; set; }
            public string Name { get; set; }

            private bool _isDefault;
            public bool IsDefault
            {
                get => _isDefault;
                set
                {
                    if (_isDefault == value) return;
                    _isDefault = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDefault)));
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            public override string ToString() => Name ?? Id ?? "";
        }

        /// <summary>List active render (playback) endpoints.</summary>
        private static List<AudioDeviceInfo> ListPlaybackDevicesCore()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                object enumObj = Activator.CreateInstance(t);
                IntPtr pEnumUnk = Marshal.GetIUnknownForObject(enumObj);
                Guid iidEnum = IID_IMMDeviceEnumerator;
                Marshal.QueryInterface(pEnumUnk, ref iidEnum, out IntPtr pEnumerator);
                Marshal.Release(pEnumUnk);
                if (pEnumerator == IntPtr.Zero) return list;

                string defaultId = null;
                try
                {
                    var immEnum = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnumerator);
                    // eRender=0, eConsole=0
                    if (immEnum.GetDefaultAudioEndpoint(0, 0, out IntPtr pDef) >= 0 && pDef != IntPtr.Zero)
                    {
                        try { defaultId = GetDeviceId(pDef); }
                        finally { Marshal.Release(pDef); }
                    }

                    // DEVICE_STATE_ACTIVE = 1
                    if (immEnum.EnumAudioEndpoints(0, 1, out IntPtr pCol) >= 0 && pCol != IntPtr.Zero)
                    {
                        try
                        {
                            int count = CollectionGetCount(pCol);
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr pDev = CollectionItem(pCol, i);
                                if (pDev == IntPtr.Zero) continue;
                                try
                                {
                                    string id = GetDeviceId(pDev);
                                    string name = GetDeviceFriendlyName(pDev) ?? id;
                                    list.Add(new AudioDeviceInfo
                                    {
                                        Id = id,
                                        Name = name,
                                        IsDefault = !string.IsNullOrEmpty(defaultId) &&
                                                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                                    });
                                }
                                finally { Marshal.Release(pDev); }
                            }
                        }
                        finally { Marshal.Release(pCol); }
                    }
                }
                finally
                {
                    Marshal.Release(pEnumerator);
                    try { if (Marshal.IsComObject(enumObj)) Marshal.ReleaseComObject(enumObj); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ListPlaybackDevices: {ex.Message}");
            }
            return list;
        }

        // System-wide MASTER mixer for the default playback endpoint, not a per-game session.
        private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        private static bool TryGetMasterVolumeCore(out float volume, out bool muted)
        {
            volume = 1f;
            muted = false;
            try
            {
                using (var endpoint = new MasterEndpoint())
                {
                    if (endpoint.Volume == null) return false;
                    int hr = endpoint.Volume.GetMasterVolumeLevelScalar(out volume);
                    if (hr < 0) return false;
                    return endpoint.Volume.GetMute(out muted) >= 0;
                }
            }
            catch (Exception ex) { _lastError = ex.Message; return false; }
        }

        private static bool TrySetMasterVolumeCore(float volume)
        {
            try
            {
                using (var endpoint = new MasterEndpoint())
                {
                    if (endpoint.Volume == null) return false;
                    Guid ctx = Guid.Empty;
                    int hr = endpoint.Volume.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, volume)), ref ctx);
                    if (hr >= 0) return true;
                    _lastError = "Master volume error 0x" + hr.ToString("X8");
                    return false;
                }
            }
            catch (Exception ex) { _lastError = ex.Message; return false; }
        }

        private static bool TrySetMasterMuteCore(bool muted)
        {
            try
            {
                using (var endpoint = new MasterEndpoint())
                {
                    if (endpoint.Volume == null) return false;
                    Guid ctx = Guid.Empty;
                    return endpoint.Volume.SetMute(muted, ref ctx) >= 0;
                }
            }
            catch (Exception ex) { _lastError = ex.Message; return false; }
        }

        private sealed class MasterEndpoint : IDisposable
        {
            private object enumerator;
            private object device;
            public IAudioEndpointVolume Volume { get; private set; }
            public MasterEndpoint()
            {
                enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                var en = (IMMDeviceEnumerator)enumerator;
                if (en.GetDefaultAudioEndpoint(0, 0, out IntPtr ptr) < 0 || ptr == IntPtr.Zero) return;
                try { device = Marshal.GetObjectForIUnknown(ptr); }
                finally { Marshal.Release(ptr); }
                var dev = (IMMDevice)device;
                Guid iid = IID_IAudioEndpointVolume;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out IntPtr pv) < 0 || pv == IntPtr.Zero) return;
                try { Volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(pv); }
                finally { Marshal.Release(pv); }
            }
            public void Dispose()
            {
                if (Volume != null) Marshal.ReleaseComObject(Volume);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int GetChannelCount(out uint pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
            [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
            [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }

        // --- COM types for setting the default endpoint (undocumented but widely used) ---

        private enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class PolicyConfigClientCom { }

        [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
        private class PolicyConfigVistaClientCom { }

        // Win7+ IPolicyConfig — method order must match the real vtable
        [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int ResetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            [PreserveSig] int SetEndpointVisibility();
        }

        // Vista-style IPolicyConfig (slightly different layout on some builds)
        [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfigVista
        {
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            [PreserveSig] int SetEndpointVisibility();
        }

        /// <summary>Set Windows default playback device (Console + Multimedia + Communications).</summary>
        private static bool SetDefaultPlaybackDeviceCore(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                _lastError = "Empty device id";
                return false;
            }

            _lastError = null;
            Exception lastEx = null;

            // Try modern PolicyConfigClient + IPolicyConfig first
            try
            {
                var client = new PolicyConfigClientCom();
                var cfg = (IPolicyConfig)client;
                int hr0 = cfg.SetDefaultEndpoint(deviceId, ERole.eConsole);
                int hr1 = cfg.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                int hr2 = cfg.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                try { Marshal.ReleaseComObject(client); } catch { }

                if (hr0 >= 0 || hr1 >= 0 || hr2 >= 0)
                {
                    Logger.Info($"SetDefaultPlaybackDevice OK (IPolicyConfig) id={deviceId} hrs={hr0:X8}/{hr1:X8}/{hr2:X8}");
                    return true;
                }
                _lastError = $"IPolicyConfig hrs={hr0:X8}/{hr1:X8}/{hr2:X8}";
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Logger.Debug($"IPolicyConfig path: {ex.Message}");
            }

            // Fallback: Vista client
            try
            {
                var client = new PolicyConfigVistaClientCom();
                var cfg = (IPolicyConfigVista)client;
                int hr0 = cfg.SetDefaultEndpoint(deviceId, ERole.eConsole);
                int hr1 = cfg.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                int hr2 = cfg.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                try { Marshal.ReleaseComObject(client); } catch { }

                if (hr0 >= 0 || hr1 >= 0 || hr2 >= 0)
                {
                    Logger.Info($"SetDefaultPlaybackDevice OK (IPolicyConfigVista) id={deviceId} hrs={hr0:X8}/{hr1:X8}/{hr2:X8}");
                    return true;
                }
                _lastError = $"IPolicyConfigVista hrs={hr0:X8}/{hr1:X8}/{hr2:X8}";
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Logger.Debug($"IPolicyConfigVista path: {ex.Message}");
            }

            // Last resort: raw Activator + QI + vtable (older approach)
            try
            {
                if (TrySetDefaultViaVtable(deviceId))
                    return true;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }

            if (string.IsNullOrEmpty(_lastError))
                _lastError = lastEx?.Message ?? "SetDefaultEndpoint failed";
            Logger.Error($"SetDefaultPlaybackDevice failed: {_lastError}");
            return false;
        }

        private static bool TrySetDefaultViaVtable(string deviceId)
        {
            Type t = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (t == null) return false;
            object client = Activator.CreateInstance(t);
            if (client == null) return false;

            IntPtr pCfg = IntPtr.Zero;
            foreach (var iidTry in new[]
            {
                new Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
                new Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")
            })
            {
                IntPtr pUnk = Marshal.GetIUnknownForObject(client);
                try
                {
                    Guid g = iidTry;
                    if (Marshal.QueryInterface(pUnk, ref g, out pCfg) >= 0 && pCfg != IntPtr.Zero)
                        break;
                    pCfg = IntPtr.Zero;
                }
                finally { Marshal.Release(pUnk); }
            }

            if (pCfg == IntPtr.Zero)
            {
                try { if (Marshal.IsComObject(client)) Marshal.ReleaseComObject(client); } catch { }
                return false;
            }

            try
            {
                // SetDefaultEndpoint is typically slot 13 (IUnknown 3 + 10 methods)
                int[] slots = { 13, 12, 10, 11, 14, 9 };
                foreach (int slot in slots)
                {
                    try
                    {
                        IntPtr vtable = Marshal.ReadIntPtr(pCfg);
                        IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
                        if (fn == IntPtr.Zero) continue;
                        var del = Marshal.GetDelegateForFunctionPointer<SetDefaultEndpointDelegate>(fn);
                        int hr0 = del(pCfg, deviceId, 0);
                        int hr1 = del(pCfg, deviceId, 1);
                        int hr2 = del(pCfg, deviceId, 2);
                        if (hr0 >= 0 || hr1 >= 0 || hr2 >= 0)
                        {
                            Logger.Info($"SetDefaultPlaybackDevice OK (vtable slot {slot}) id={deviceId}");
                            return true;
                        }
                        _lastError = $"vtable slot {slot} hrs={hr0:X8}/{hr1:X8}/{hr2:X8}";
                    }
                    catch { }
                }
                return false;
            }
            finally
            {
                Marshal.Release(pCfg);
                try { if (Marshal.IsComObject(client)) Marshal.ReleaseComObject(client); } catch { }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetDefaultEndpointDelegate(IntPtr thisPtr, [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        private static string GetDeviceId(IntPtr pDevice)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pDevice);
            // IMMDevice::GetId is slot 5 (IUnknown 3 + Activate + OpenPropertyStore + GetId)
            IntPtr fn = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var del = Marshal.GetDelegateForFunctionPointer<GetIdDelegate>(fn);
            int hr = del(pDevice, out IntPtr pStr);
            if (hr < 0 || pStr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(pStr); }
            finally { Marshal.FreeCoTaskMem(pStr); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetIdDelegate(IntPtr thisPtr, out IntPtr ppstrId);

        private static string GetDeviceFriendlyName(IntPtr pDevice)
        {
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(pDevice);
                // OpenPropertyStore slot 4, STGM_READ=0
                IntPtr fnOpen = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
                var delOpen = Marshal.GetDelegateForFunctionPointer<OpenPropertyStoreDelegate>(fnOpen);
                int hr = delOpen(pDevice, 0, out IntPtr pStore);
                if (hr < 0 || pStore == IntPtr.Zero) return null;
                try
                {
                    // PROPERTYKEY: fmtid + pid (2 = FriendlyName for PKEY_Device_FriendlyName)
                    var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 14 };
                    IntPtr vtableStore = Marshal.ReadIntPtr(pStore);
                    IntPtr fnGet = Marshal.ReadIntPtr(vtableStore, 5 * IntPtr.Size); // GetValue slot 5
                    var delGet = Marshal.GetDelegateForFunctionPointer<GetValueDelegate>(fnGet);
                    hr = delGet(pStore, ref key, out PROPVARIANT pv);
                    if (hr < 0) return null;
                    try
                    {
                        if (pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero)
                            return Marshal.PtrToStringUni(pv.p);
                        if (pv.vt == 8 /* VT_BSTR */ && pv.p != IntPtr.Zero)
                            return Marshal.PtrToStringBSTR(pv.p);
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }
                finally { Marshal.Release(pStore); }
            }
            catch { }
            return null;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OpenPropertyStoreDelegate(IntPtr thisPtr, int access, out IntPtr store);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetValueDelegate(IntPtr thisPtr, ref PROPERTYKEY key, out PROPVARIANT pv);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort w1, w2, w3;
            public IntPtr p;
            public IntPtr p2;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        private static int CollectionGetCount(IntPtr pCol)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pCol);
            IntPtr fn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var del = Marshal.GetDelegateForFunctionPointer<GetCountColDelegate>(fn);
            del(pCol, out uint count);
            return (int)count;
        }

        private static IntPtr CollectionItem(IntPtr pCol, int index)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pCol);
            IntPtr fn = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
            var del = Marshal.GetDelegateForFunctionPointer<ItemColDelegate>(fn);
            int hr = del(pCol, (uint)index, out IntPtr pDev);
            return hr >= 0 ? pDev : IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCountColDelegate(IntPtr thisPtr, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ItemColDelegate(IntPtr thisPtr, uint index, out IntPtr device);

        private static bool TryGetVolumeCore(int processId, string exePath, out float volume, out bool muted)
        {
            volume = 1f;
            muted = false;
            _lastError = null;
            try
            {
                var pids = BuildTargetPids(processId, exePath);
                bool ok = false;
                float v = 1f;
                bool m = false;
                EnumSessions(pids, (simple) =>
                {
                    int hr = simple.GetMasterVolume(out v);
                    if (hr < 0) return true;
                    hr = simple.GetMute(out int mi);
                    if (hr < 0) return true;
                    m = mi != 0;
                    ok = true;
                    return false;
                });
                if (!ok)
                {
                    _lastError = _lastError ?? "No audio session (play sound in-game first)";
                    return false;
                }
                volume = v;
                muted = m;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Logger.Error($"TryGetVolume: {ex}");
                return false;
            }
        }

        private static bool TrySetVolumeCore(int processId, string exePath, float volume)
        {
            volume = Math.Max(0f, Math.Min(1f, volume));
            _lastError = null;
            try
            {
                var pids = BuildTargetPids(processId, exePath);
                if (pids.Count == 0)
                {
                    _lastError = "No process id";
                    return false;
                }

                bool ok = false;
                for (int attempt = 0; attempt < 6 && !ok; attempt++)
                {
                    int applied = 0;
                    EnumSessions(pids, (simple) =>
                    {
                        Guid ctx = Guid.Empty;
                        int hr = simple.SetMasterVolume(volume, ref ctx);
                        if (hr >= 0)
                        {
                            applied++;
                            ok = true;
                        }
                        else _lastError = $"SetMasterVolume 0x{hr:X8}";
                        return true;
                    });
                    if (applied == 0)
                        _lastError = _lastError ?? "No audio session (play sound in-game first)";
                    if (!ok && attempt < 5)
                        System.Threading.Thread.Sleep(40);
                }
                return ok;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Logger.Error($"TrySetVolume: {ex}");
                return false;
            }
        }

        private static bool TrySetMuteCore(int processId, string exePath, bool mute)
        {
            _lastError = null;
            try
            {
                var pids = BuildTargetPids(processId, exePath);
                if (pids.Count == 0)
                {
                    _lastError = "No process id";
                    return false;
                }

                bool ok = false;
                for (int attempt = 0; attempt < 6 && !ok; attempt++)
                {
                    int applied = 0;
                    EnumSessions(pids, (simple) =>
                    {
                        Guid ctx = Guid.Empty;
                        // Mute + volume 0 / restore path handled by caller; force mute flag here
                        int hr = simple.SetMute(mute ? 1 : 0, ref ctx);
                        if (hr >= 0)
                        {
                            applied++;
                            // verify
                            if (simple.GetMute(out int mi) >= 0)
                            {
                                bool isMuted = mi != 0;
                                if (isMuted == mute) ok = true;
                            }
                            else ok = true;
                        }
                        else _lastError = $"SetMute 0x{hr:X8}";
                        return true; // all sessions
                    });
                    if (applied == 0)
                        _lastError = _lastError ?? "No audio session (play sound in-game first)";
                    if (!ok && attempt < 5)
                        System.Threading.Thread.Sleep(40);
                }
                return ok;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Logger.Error($"TrySetMute: {ex}");
                return false;
            }
        }

        private static HashSet<int> BuildTargetPids(int processId, string exePath)
        {
            var set = new HashSet<int>();
            if (processId > 0) set.Add(processId);

            string name = null;
            try { if (processId > 0) name = Process.GetProcessById(processId).ProcessName; }
            catch { }

            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(exePath))
            {
                try { name = System.IO.Path.GetFileNameWithoutExtension(exePath); }
                catch { }
            }

            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { set.Add(p.Id); } catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }
            return set;
        }

        /// <summary>callback: return true to continue, false to stop.
        /// Scans ALL active render endpoints (not only the default device).
        /// </summary>
        private static void EnumSessions(HashSet<int> targetPids, Func<ISimpleAudioVolume, bool> callback)
        {
            if (targetPids == null || targetPids.Count == 0)
            {
                _lastError = "No process id";
                return;
            }

            Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            object enumObj = Activator.CreateInstance(t);
            IntPtr pEnumUnk = Marshal.GetIUnknownForObject(enumObj);
            Guid iidEnum = IID_IMMDeviceEnumerator;
            Marshal.QueryInterface(pEnumUnk, ref iidEnum, out IntPtr pEnumerator);
            Marshal.Release(pEnumUnk);
            if (pEnumerator == IntPtr.Zero)
                throw new InvalidOperationException("IMMDeviceEnumerator QI failed");

            try
            {
                var immEnum = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnumerator);
                // DEVICE_STATE_ACTIVE = 1, eRender = 0
                if (immEnum.EnumAudioEndpoints(0, 1, out IntPtr pCol) < 0 || pCol == IntPtr.Zero)
                {
                    // Fallback: default endpoint only
                    if (immEnum.GetDefaultAudioEndpoint(0, 1, out IntPtr pDef) >= 0 && pDef != IntPtr.Zero)
                    {
                        try { EnumSessionsOnDevice(pDef, targetPids, callback); }
                        finally { Marshal.Release(pDef); }
                    }
                    return;
                }

                try
                {
                    int n = CollectionGetCount(pCol);
                    Logger.Debug($"Audio endpoints={n}, targets={string.Join(",", targetPids)}");
                    for (int d = 0; d < n; d++)
                    {
                        IntPtr pDev = CollectionItem(pCol, d);
                        if (pDev == IntPtr.Zero) continue;
                        try
                        {
                            if (!EnumSessionsOnDevice(pDev, targetPids, callback))
                                return; // callback asked to stop
                        }
                        finally { Marshal.Release(pDev); }
                    }
                }
                finally { Marshal.Release(pCol); }
            }
            finally
            {
                if (pEnumerator != IntPtr.Zero) Marshal.Release(pEnumerator);
                try
                {
                    if (Marshal.IsComObject(enumObj))
                        Marshal.ReleaseComObject(enumObj);
                }
                catch { }
            }
        }

        /// <returns>false if callback requested stop</returns>
        private static bool EnumSessionsOnDevice(IntPtr pDevice, HashSet<int> targetPids, Func<ISimpleAudioVolume, bool> callback)
        {
            IntPtr pManager = IntPtr.Zero;
            IntPtr pSessionEnum = IntPtr.Zero;
            IMMDevice immDevice = null;
            try
            {
                Guid iidMgr = IID_IAudioSessionManager2;
                immDevice = (IMMDevice)Marshal.GetObjectForIUnknown(pDevice);
                int hr = immDevice.Activate(ref iidMgr, 0x17, IntPtr.Zero, out pManager);
                if (hr < 0 || pManager == IntPtr.Zero)
                    return true;

                pSessionEnum = VTable.GetSessionEnumerator(pManager);
                if (pSessionEnum == IntPtr.Zero)
                    return true;

                int count = VTable.SessionEnumGetCount(pSessionEnum);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pCtl = VTable.SessionEnumGetSession(pSessionEnum, i);
                    if (pCtl == IntPtr.Zero) continue;
                    try
                    {
                        Guid iidCtl2 = IID_IAudioSessionControl2;
                        hr = Marshal.QueryInterface(pCtl, ref iidCtl2, out IntPtr pCtl2);
                        if (hr < 0 || pCtl2 == IntPtr.Zero) continue;
                        try
                        {
                            uint pid = VTable.SessionControl2GetProcessId(pCtl2);
                            if (!targetPids.Contains((int)pid))
                                continue;

                            Guid iidVol = IID_ISimpleAudioVolume;
                            hr = Marshal.QueryInterface(pCtl, ref iidVol, out IntPtr pVol);
                            if (hr < 0 || pVol == IntPtr.Zero)
                                continue;
                            try
                            {
                                var simple = (ISimpleAudioVolume)Marshal.GetObjectForIUnknown(pVol);
                                bool cont;
                                // Release the RCW now; leaving it to the finalizer thread is a classic
                                // source of crashes when Playnite shuts down.
                                try { cont = callback(simple); }
                                finally { try { Marshal.ReleaseComObject(simple); } catch { } }
                                if (!cont) return false;
                            }
                            finally { Marshal.Release(pVol); }
                        }
                        finally { Marshal.Release(pCtl2); }
                    }
                    finally { Marshal.Release(pCtl); }
                }
            }
            finally
            {
                if (pSessionEnum != IntPtr.Zero) Marshal.Release(pSessionEnum);
                if (pManager != IntPtr.Zero) Marshal.Release(pManager);
                if (immDevice != null) { try { Marshal.ReleaseComObject(immDevice); } catch { } }
            }
            return true;
        }

        /// <summary>Raw vtable calls for session manager/enumerator (avoids RCW IID cast bugs).</summary>
        private static class VTable
        {
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int GetSessionEnumeratorDelegate(IntPtr thisPtr, out IntPtr sessionEnum);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int GetCountDelegate(IntPtr thisPtr, out int count);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int GetSessionDelegate(IntPtr thisPtr, int index, out IntPtr session);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int GetProcessIdDelegate(IntPtr thisPtr, out uint pid);

            // IAudioSessionManager2: IUnknown(3) + IAudioSessionManager(2) + GetSessionEnumerator = slot 5
            public static IntPtr GetSessionEnumerator(IntPtr pManager)
            {
                IntPtr vtable = Marshal.ReadIntPtr(pManager);
                IntPtr fn = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
                var del = Marshal.GetDelegateForFunctionPointer<GetSessionEnumeratorDelegate>(fn);
                int hr = del(pManager, out IntPtr pEnum);
                if (hr < 0)
                    throw new COMException("GetSessionEnumerator", hr);
                return pEnum;
            }

            // IAudioSessionEnumerator: IUnknown(3) + GetCount = slot 3
            public static int SessionEnumGetCount(IntPtr pEnum)
            {
                IntPtr vtable = Marshal.ReadIntPtr(pEnum);
                IntPtr fn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                var del = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(fn);
                int hr = del(pEnum, out int count);
                if (hr < 0) throw new COMException("GetCount", hr);
                return count;
            }

            // GetSession = slot 4
            public static IntPtr SessionEnumGetSession(IntPtr pEnum, int index)
            {
                IntPtr vtable = Marshal.ReadIntPtr(pEnum);
                IntPtr fn = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
                var del = Marshal.GetDelegateForFunctionPointer<GetSessionDelegate>(fn);
                int hr = del(pEnum, index, out IntPtr pSession);
                if (hr < 0) return IntPtr.Zero;
                return pSession;
            }

            // IAudioSessionControl2: IUnknown(3) + IAudioSessionControl(9) + GetSessionIdentifier + GetSessionInstanceIdentifier + GetProcessId
            // slots: 3+9+2 = 14
            public static uint SessionControl2GetProcessId(IntPtr pCtl2)
            {
                IntPtr vtable = Marshal.ReadIntPtr(pCtl2);
                IntPtr fn = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
                var del = Marshal.GetDelegateForFunctionPointer<GetProcessIdDelegate>(fn);
                int hr = del(pCtl2, out uint pid);
                if (hr < 0) return 0;
                return pid;
            }
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr ppInterface);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
            [PreserveSig] int GetMasterVolume(out float pfLevel);
            [PreserveSig] int SetMute(int bMute, ref Guid EventContext);
            [PreserveSig] int GetMute(out int pbMute);
        }
    }
}
