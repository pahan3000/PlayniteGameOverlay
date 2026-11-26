using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    // Windows Game Mode integration: launcher hand-offs and emulator actions are resolved
    // without installing a separate Playnite extension.
    internal static class OverlayGameMode
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private static readonly string[] LauncherNames = { "steam.exe", "epicgameslauncher.exe", "gog galaxy.exe", "galaxyclient.exe", "upc.exe", "ubisoftconnect.exe", "eadesktop.exe", "ealauncher.exe", "playnite.desktopapp.exe", "playnite.fullscreenapp.exe", "cmd.exe", "powershell.exe", "explorer.exe", "start.exe" };
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder buffer, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

        private static string ProcessPath(int pid)
        {
            if (pid <= 0) return null;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try { var b = new StringBuilder(2048); uint n = (uint)b.Capacity; return QueryFullProcessImageName(h, 0, b, ref n) ? b.ToString(0, (int)n) : null; }
            catch { return null; }
            finally { CloseHandle(h); }
        }
        private static bool IsLauncher(string path) => string.IsNullOrWhiteSpace(path) || LauncherNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        private static void Register(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsLauncher(path) || !File.Exists(path)) return;
            try { WgcRegistrar.AddGameIfNeeded(path); }
            catch (Exception ex) { Log.Warn($"Game Mode registration failed for {path}: {ex.Message}"); }
        }
        // The original extension exposes explicit registration for selected games and
        // an entire library. Keep that workflow for games launched through URI actions.
        // A URI without a resolved executable cannot be registered until it runs.
        internal static bool RegisterManually(IPlayniteAPI api, Game game)
        {
            if (game == null || api == null) return false;
            try
            {
                var action = game.GameActions?.FirstOrDefault(a => a.IsPlayAction && a.Type == GameActionType.File);
                if (action == null) return false;
                var expanded = api.ExpandGameVariables(game, action);
                var path = expanded?.Path;
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                    path = Path.Combine(game.InstallDirectory, path);
                path = Path.GetFullPath(path);
                if (IsLauncher(path) || !File.Exists(path)) return false;
                return WgcRegistrar.AddGameIfNeeded(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Game Mode manual registration failed for {game.Name}: {ex.Message}");
                return false;
            }
        }

        internal static void OnStarting(IPlayniteAPI api, Game game)
        {
            if (game == null || api == null) return;
            try
            {
                var action = game.GameActions?.FirstOrDefault(a => a.IsPlayAction && a.Type == GameActionType.File);
                if (action == null) return;
                var expanded = api.ExpandGameVariables(game, action);
                var path = expanded?.Path;
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(game.InstallDirectory)) path = Path.Combine(game.InstallDirectory, path);
                Register(Path.GetFullPath(path)); // emulator actions register emulator exe, not ROM
            }
            catch (Exception ex) { Log.Debug($"Game Mode prelaunch resolution: {ex.Message}"); }
        }
        internal static void OnStarted(Game game, int startedPid)
        {
            if (game == null) return;
            // An emulator can reside outside the game's install folder; honor its actual PID.
            Register(ProcessPath(startedPid));
            var install = game.InstallDirectory;
            if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) return;
            string root;
            try { root = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch { return; }
            Task.Run(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Game process may appear after Steam/Epic/GOG hand-off; never block Playnite.
                for (int i = 0; i < 24; i++)
                {
                    try
                    {
                        foreach (var proc in Process.GetProcesses())
                        {
                            using (proc)
                            {
                                var path = ProcessPath(proc.Id);
                                if (path == null || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(path)) continue;
                                Register(path);
                            }
                        }
                    }
                    catch (Exception ex) { Log.Debug($"Game Mode process check: {ex.Message}"); }
                    Thread.Sleep(2000);
                }
            });
        }
    }
}
