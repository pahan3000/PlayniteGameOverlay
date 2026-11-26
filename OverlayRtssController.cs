using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    public enum OverlayFpsSyncMode
    {
        Async = 0,
        FrontEdgeSync = 1,
        BackEdgeSync = 2,
        ReflexSync = 3,
        /// <summary>RTSS Scanline Sync — framerate limit is treated as target refresh (Hz); Limit written as 0.</summary>
        ScanlineSync = 4,
        ScanlineSyncHalf = 5
    }

    /// <summary>Which external framelimiter backend the overlay is driving.</summary>
    public enum OverlayFpsBackend
    {
        Rtss = 0,
        SpecialK = 1
    }

    /// <summary>
    /// Mirrors playnite-fps-limiter RtssLimiterBackend.ApplyLimit / ApplyProfileFileLimit:
    /// writes Framerate Limit, LimitDenominator, SyncLimiter, ReflexSetLatencyMarker, then
    /// calls RTSSHooks UpdateProfiles().
    /// </summary>
    public static class OverlayRtssController
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private const string GlobalProfileFileName = "Global";

        /// <summary>Optional path from OverlaySettings.RtssPath.</summary>
        public static string ConfiguredRtssPath { get; set; }

        /// <summary>RTSS SyncScanline0 used by the overlay fallback path. Default -1.</summary>
        public static int ScanlineSyncTarget { get; set; } = -1;

        public static string ResolveRtssPath()
        {
            if (!string.IsNullOrWhiteSpace(ConfiguredRtssPath) && File.Exists(ConfiguredRtssPath))
                return ConfiguredRtssPath;

            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return path;
                }
                catch { /* access denied */ }
            }

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "RivaTuner Statistics Server",
                "RTSS.exe");
            return File.Exists(defaultPath) ? defaultPath : null;
        }

        public static bool IsRtssAvailable() => !string.IsNullOrEmpty(ResolveRtssPath());

        public static void EnsureRtssRunning()
        {
            if (Process.GetProcessesByName("RTSS").Any())
                return;

            var path = ResolveRtssPath();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("RTSS.exe was not found. Install RivaTuner Statistics Server.");

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = true
            });
            Thread.Sleep(1000);
        }

        /// <summary>
        /// Same contract as FPSLimiter.RtssLimiterBackend.ApplyLimit.
        /// When executablePath is known, writes the per-exe profile (game.cfg) AND the Global
        /// profile so the cap sticks whether RTSS matched the process or not.
        /// frameLimit &lt;= 0 disables the cap.
        /// </summary>
        public static string Apply(
            string executablePath,
            double frameLimit,
            OverlayFpsSyncMode syncMode,
            bool reflexMarkersEnabled)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrEmpty(rtssPath))
                throw new InvalidOperationException("RTSS.exe was not found. Install RivaTuner Statistics Server.");

            EnsureRtssRunning();

            bool disableReflexMarkers = !reflexMarkersEnabled;
            var applied = new System.Collections.Generic.List<string>();

            // 1) Always write Global profile (FPS Limiter default during launch)
            ApplyProfileFileLimit(rtssPath, string.Empty, frameLimit, syncMode, disableReflexMarkers);
            applied.Add("Global");

            // 2) Also write per-executable profile when we know the game exe
            //    (same as forceGlobalProfile=false path in FPS Limiter)
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var profileName = Path.GetFileName(executablePath);
                if (!string.IsNullOrWhiteSpace(profileName))
                {
                    ApplyProfileFileLimit(rtssPath, profileName, frameLimit, syncMode, disableReflexMarkers);
                    applied.Add(profileName);
                }
            }

            RefreshProfiles(rtssPath);

            // Verify Global limit stuck
            var readback = ReadProfileFileLimit(rtssPath, string.Empty);
            if (frameLimit > 0 && readback.HasValue && Math.Abs(readback.Value - frameLimit) > 0.05)
            {
                throw new InvalidOperationException(
                    $"RTSS profile write did not stick (wanted {frameLimit}, read {readback.Value}). " +
                    "Check write permission on the RTSS Profiles folder.");
            }

            Logger.Info($"Overlay RTSS apply ok: profiles=[{string.Join(", ", applied)}], " +
                        $"limit={frameLimit}, sync={syncMode}, reflexMarkers={reflexMarkersEnabled}");

            return string.Join(", ", applied);
        }

        private static void ApplyProfileFileLimit(
            string rtssPath,
            string apiProfileName,
            double frameLimit,
            OverlayFpsSyncMode syncMode,
            bool disableReflexMarkers)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var profileText = File.Exists(path)
                ? File.ReadAllText(path, Encoding.Default)
                : ReadDefaultProfileText(rtssPath);

            // Match FPSLimiter.RtssLimiterBackend.ApplyProfileFileLimit exactly
            ToFraction(Math.Max(0, frameLimit), out int numerator, out int denominator);

            profileText = SetProfileValue(profileText, "Framerate", "Limit", numerator.ToString(CultureInfo.InvariantCulture));
            profileText = SetProfileValue(profileText, "Framerate", "LimitDenominator", denominator.ToString(CultureInfo.InvariantCulture));

            if (syncMode == OverlayFpsSyncMode.ScanlineSync || syncMode == OverlayFpsSyncMode.ScanlineSyncHalf)
            {
                // Extension: Scanline pairs with Front Edge Sync + SyncScanline0 + SyncFlush
                profileText = SetProfileValue(profileText, "Framerate", "SyncLimiter",
                    ((int)OverlayFpsSyncMode.FrontEdgeSync).ToString(CultureInfo.InvariantCulture));
                profileText = SetProfileValue(profileText, "Framerate", "SyncScanline0",
                    Math.Max(-500, Math.Min(500, ScanlineSyncTarget)).ToString(CultureInfo.InvariantCulture));
                profileText = SetProfileValue(profileText, "Framerate", "SyncFlush", "1");
                profileText = SetProfileValue(profileText, "Framerate", "SyncPeriods", syncMode == OverlayFpsSyncMode.ScanlineSyncHalf ? "1" : "0");
            }
            else
            {
                profileText = SetProfileValue(profileText, "Framerate", "SyncLimiter",
                    ((int)syncMode).ToString(CultureInfo.InvariantCulture));
                profileText = SetProfileValue(profileText, "Framerate", "SyncScanline0", "0");
                profileText = SetProfileValue(profileText, "Framerate", "SyncPeriods", "0");
            }

            profileText = SetProfileValue(profileText, "Hooking", "EnableHooking", "1");
            profileText = SetProfileValue(profileText, "Framerate", "ReflexSetLatencyMarker", disableReflexMarkers ? "0" : "1");

            File.WriteAllText(path, profileText, Encoding.Default);
            Logger.Debug($"Wrote RTSS profile {path}: Limit={numerator}/{denominator}, Sync={(int)syncMode}, Reflex={(!disableReflexMarkers ? 1 : 0)}");
        }

        /// <summary>
        /// Updates only RTSS SyncScanline0 in the Global and per-executable profiles.
        /// This is used after FPS Limiter's bridge call because its live-session tracker
        /// may not know about a game session started outside its own lifecycle hooks.
        /// </summary>
        public static string ApplyScanlineTarget(string executablePath, int target)
        {
            target = Math.Max(-500, Math.Min(500, target));
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrEmpty(rtssPath))
                throw new InvalidOperationException("RTSS.exe was not found. Install RivaTuner Statistics Server.");

            EnsureRtssRunning();
            var applied = new System.Collections.Generic.List<string>();

            ApplyProfileFileScanlineTarget(rtssPath, string.Empty, target);
            applied.Add("Global");

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var profileName = Path.GetFileName(executablePath);
                if (!string.IsNullOrWhiteSpace(profileName))
                {
                    ApplyProfileFileScanlineTarget(rtssPath, profileName, target);
                    applied.Add(profileName);
                }
            }

            RefreshProfiles(rtssPath);

            var globalReadback = GetProfileValue(
                File.ReadAllText(GetProfileFilePath(rtssPath, string.Empty), Encoding.Default),
                "Framerate", "SyncScanline0");
            if (!int.TryParse(globalReadback, NumberStyles.Integer, CultureInfo.InvariantCulture, out int verified) || verified != target)
                throw new InvalidOperationException($"RTSS Scanline target verification failed (wanted {target}, read '{globalReadback ?? "missing"}').");

            Logger.Info($"Overlay Scanline target applied: target={target}, profiles=[{string.Join(", ", applied)}]");
            return string.Join(", ", applied);
        }

        private static void ApplyProfileFileScanlineTarget(string rtssPath, string apiProfileName, int target)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var profileText = File.Exists(path)
                ? File.ReadAllText(path, Encoding.Default)
                : ReadDefaultProfileText(rtssPath);
            profileText = SetProfileValue(profileText, "Framerate", "SyncScanline0",
                target.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(path, profileText, Encoding.Default);
        }

        private static double? ReadProfileFileLimit(string rtssPath, string apiProfileName)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            if (!File.Exists(path))
                return null;

            var profileText = File.ReadAllText(path, Encoding.Default);
            var limitValue = GetProfileValue(profileText, "Framerate", "Limit");
            var denominatorValue = GetProfileValue(profileText, "Framerate", "LimitDenominator");

            if (!int.TryParse(limitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit))
                return null;

            if (string.IsNullOrWhiteSpace(denominatorValue) ||
                !int.TryParse(denominatorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int denominator) ||
                denominator <= 0)
            {
                denominator = 1;
            }

            return (double)limit / denominator;
        }

        private static void RefreshProfiles(string rtssPath)
        {
            try
            {
                using (var refresher = new RtssProfileRefresher(rtssPath))
                {
                    refresher.Initialize();
                    refresher.UpdateProfiles();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RTSS UpdateProfiles failed (profile files still written): {ex.Message}");
            }
        }

        private static void ToFraction(double frameLimit, out int numerator, out int denominator)
        {
            denominator = 1000;
            numerator = (int)Math.Round(frameLimit * denominator, MidpointRounding.AwayFromZero);
            var divisor = Gcd(numerator, denominator);
            if (divisor > 1)
            {
                numerator /= divisor;
                denominator /= divisor;
            }
            if (denominator <= 0) denominator = 1;
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                var t = b;
                b = a % b;
                a = t;
            }
            return a == 0 ? 1 : a;
        }

        private static string ReadDefaultProfileText(string rtssPath)
        {
            var globalPath = GetProfileFilePath(rtssPath, string.Empty);
            if (!File.Exists(globalPath))
            {
                // Minimal viable RTSS profile if Global is missing
                return "[Framerate]\r\nLimit=0\r\nLimitDenominator=1\r\nSyncLimiter=0\r\nReflexSetLatencyMarker=1\r\n[Hooking]\r\nEnableHooking=1\r\n";
            }
            return File.ReadAllText(globalPath, Encoding.Default);
        }

        private static string GetProfileFilePath(string rtssPath, string apiProfileName)
        {
            var dir = Path.Combine(Path.GetDirectoryName(rtssPath), "Profiles");
            var fileName = string.IsNullOrWhiteSpace(apiProfileName)
                ? GlobalProfileFileName
                : apiProfileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                    ? apiProfileName
                    : apiProfileName + ".cfg";
            return Path.Combine(dir, fileName);
        }

        private static string SetProfileValue(string text, string section, string key, string value)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n').ToList();
            var sectionHeader = "[" + section + "]";
            var inSection = false;
            var sectionFound = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (inSection)
                    {
                        lines.Insert(i, key + "=" + value);
                        return string.Join(newline, lines);
                    }
                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    sectionFound = sectionFound || inSection;
                    continue;
                }

                if (inSection && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    return string.Join(newline, lines);
                }
            }

            if (sectionFound)
            {
                lines.Add(key + "=" + value);
            }
            else
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                    lines.Add(string.Empty);
                lines.Add(sectionHeader);
                lines.Add(key + "=" + value);
            }

            return string.Join(newline, lines);
        }

        private static string GetProfileValue(string text, string section, string key)
        {
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var sectionHeader = "[" + section + "]";
            var inSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inSection && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(key.Length + 1).Trim();
            }

            return null;
        }

        private sealed class RtssProfileRefresher : IDisposable
        {
            private readonly string _rtssPath;
            private IntPtr _libraryHandle;
            private UpdateProfilesDelegate _updateProfiles;

            public RtssProfileRefresher(string rtssPath) { _rtssPath = rtssPath; }

            public void Initialize()
            {
                var rtssDirectory = Path.GetDirectoryName(_rtssPath);
                var hooksName = Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll";
                var hooksPath = Path.Combine(rtssDirectory, hooksName);
                if (!File.Exists(hooksPath) && Environment.Is64BitProcess)
                    hooksPath = Path.Combine(rtssDirectory, "RTSSHooks.dll");
                if (!File.Exists(hooksPath))
                    throw new FileNotFoundException("RTSS hooks DLL was not found.", hooksPath);

                _libraryHandle = LoadLibrary(hooksPath);
                if (_libraryHandle == IntPtr.Zero)
                    throw new InvalidOperationException($"Could not load {hooksPath}.");

                var pointer = GetProcAddress(_libraryHandle, "UpdateProfiles");
                if (pointer == IntPtr.Zero)
                    throw new MissingMethodException("RTSS hooks DLL does not export UpdateProfiles.");
                _updateProfiles = (UpdateProfilesDelegate)Marshal.GetDelegateForFunctionPointer(pointer, typeof(UpdateProfilesDelegate));
            }

            public void UpdateProfiles() => _updateProfiles?.Invoke();

            public void Dispose()
            {
                if (_libraryHandle != IntPtr.Zero)
                {
                    FreeLibrary(_libraryHandle);
                    _libraryHandle = IntPtr.Zero;
                }
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void UpdateProfilesDelegate();

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool FreeLibrary(IntPtr hModule);
        }
    }
}
