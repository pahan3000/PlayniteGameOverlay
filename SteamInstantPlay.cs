using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlayniteGameOverlay
{
    internal enum InstantStore { None, Steam, Gog, Epic, Ubisoft, Xbox }

    /// <summary>What to start for an Instant Play launch.</summary>
    internal sealed class InstantLaunch
    {
        public string Exe;
        public string Root;
        public string Arguments;
        public string WorkingDir;
    }

    /// <summary>
    /// SKIF-style helper: resolve Steam install + game exe and launch directly,
    /// skipping the platform launch pipeline (including cloud save sync).
    /// </summary>
    internal static class SteamInstantPlay
    {
        public static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        private static readonly string[] ExeExcludeHints =
        {
            "unitycrashhandler", "crashhandler", "crashreport", "unins", "uninstall",
            "redist", "vcredist", "directx", "dotnet", "setup", "installer",
            "easyae", "eac_", "easyanti", "battleye", "beclient", "beservice",
            "notification_helper", "steamerrorreporter", "cef", "chrome", "subprocess"
        };

        public static readonly Guid GogPluginId = Guid.Parse("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");
        public static readonly Guid EpicPluginId = Guid.Parse("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
        public static readonly Guid UbisoftPluginId = Guid.Parse("c2f038e5-8b92-4877-91f1-da9094155fc5");
        public static readonly Guid XboxPluginId = Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");

        private static bool SourceIs(Game game, params string[] names)
        {
            try
            {
                var n = game?.Source?.Name;
                if (string.IsNullOrEmpty(n)) return false;
                return names.Any(x => string.Equals(n, x, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public static InstantStore GetStore(Game game)
        {
            if (game == null) return InstantStore.None;
            if (IsSteamGame(game)) return InstantStore.Steam;
            if (game.PluginId == GogPluginId || SourceIs(game, "GOG", "GOG.com")) return InstantStore.Gog;
            if (game.PluginId == EpicPluginId || SourceIs(game, "Epic", "Epic Games", "Epic Games Store")) return InstantStore.Epic;
            if (game.PluginId == UbisoftPluginId || SourceIs(game, "Ubisoft Connect", "Ubisoft", "Uplay")) return InstantStore.Ubisoft;
            if (game.PluginId == XboxPluginId || SourceIs(game, "Xbox", "Xbox Game Pass", "Microsoft Store")) return InstantStore.Xbox;
            return InstantStore.None;
        }

        public static string StoreName(InstantStore s)
        {
            switch (s)
            {
                case InstantStore.Steam: return "Steam";
                case InstantStore.Gog: return "GOG";
                case InstantStore.Epic: return "Epic";
                case InstantStore.Ubisoft: return "Ubisoft";
                case InstantStore.Xbox: return "Xbox";
                default: return "";
            }
        }

        // ---- GOG: goggame-<id>.info lists the primary play task ----
        internal class GogPlayTask
        {
            public bool isPrimary { get; set; }
            public string category { get; set; }
            public string type { get; set; }
            public string path { get; set; }
            public string arguments { get; set; }
            public string workingDir { get; set; }
        }

        internal class GogInfoFile
        {
            public List<GogPlayTask> playTasks { get; set; }
        }

        private static InstantLaunch ResolveGog(Game game)
        {
            string root = game.InstallDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            string info = null;
            try
            {
                string byId = Path.Combine(root, "goggame-" + game.GameId + ".info");
                info = File.Exists(byId) ? byId : Directory.GetFiles(root, "goggame-*.info").FirstOrDefault();
            }
            catch { }

            if (info != null)
            {
                try
                {
                    var data = Serialization.FromJson<GogInfoFile>(File.ReadAllText(info));
                    var tasks = data?.playTasks ?? new List<GogPlayTask>();
                    var task =
                        tasks.FirstOrDefault(t => t != null && t.isPrimary && !string.IsNullOrWhiteSpace(t.path) &&
                                                  (string.IsNullOrEmpty(t.type) || string.Equals(t.type, "FileTask", StringComparison.OrdinalIgnoreCase))) ??
                        tasks.FirstOrDefault(t => t != null && !string.IsNullOrWhiteSpace(t.path) &&
                                                  string.Equals(t.category, "game", StringComparison.OrdinalIgnoreCase));
                    if (task != null)
                    {
                        string exe = Path.IsPathRooted(task.path) ? task.path : Path.Combine(root, task.path);
                        if (File.Exists(exe))
                        {
                            string wd = null;
                            if (!string.IsNullOrWhiteSpace(task.workingDir))
                                wd = Path.IsPathRooted(task.workingDir) ? task.workingDir : Path.Combine(root, task.workingDir);
                            return new InstantLaunch { Exe = Path.GetFullPath(exe), Root = root, Arguments = task.arguments, WorkingDir = wd };
                        }
                    }
                }
                catch { }
            }

            var best = FindBestExe(root, game.Name);
            return best == null ? null : new InstantLaunch { Exe = best, Root = root };
        }

        // ---- Epic: launcher manifests (*.item) hold the launch executable and command ----
        internal class EpicManifestItem
        {
            public string AppName { get; set; }
            public string InstallLocation { get; set; }
            public string LaunchExecutable { get; set; }
            public string LaunchCommand { get; set; }
        }

        private static List<EpicManifestItem> _epicManifests;
        private static int _epicManifestsTick;

        private static List<EpicManifestItem> GetEpicManifests()
        {
            int now = Environment.TickCount;
            if (_epicManifests != null && unchecked(now - _epicManifestsTick) < 10000)
                return _epicManifests;

            var list = new List<EpicManifestItem>();
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.item"))
                    {
                        try
                        {
                            var item = Serialization.FromJson<EpicManifestItem>(File.ReadAllText(f));
                            if (item != null && !string.IsNullOrEmpty(item.AppName)) list.Add(item);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _epicManifests = list;
            _epicManifestsTick = now;
            return list;
        }

        private static InstantLaunch ResolveEpic(Game game)
        {
            if (string.IsNullOrEmpty(game.GameId)) return null;
            foreach (var item in GetEpicManifests())
            {
                if (!string.Equals(item.AppName, game.GameId, StringComparison.OrdinalIgnoreCase)) continue;
                string root = item.InstallLocation;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
                if (string.IsNullOrWhiteSpace(item.LaunchExecutable)) return null;
                string exe = Path.Combine(root, item.LaunchExecutable);
                if (!File.Exists(exe)) return null;
                return new InstantLaunch { Exe = Path.GetFullPath(exe), Root = root, Arguments = item.LaunchCommand };
            }
            return null;
        }

        // ---- Xbox (GDK / Game Pass PC games under <drive>:\XboxGames) ----
        // Games in the protected WindowsApps folder cannot be started directly; GDK titles can.
        private static InstantLaunch ResolveXbox(Game game)
        {
            string root = game.InstallDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            if (root.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            string exeName = null;
            try
            {
                // MicrosoftGame.config lives in the Content folder (or the install folder itself).
                string cfg = new[] { root, Path.Combine(root, "Content") }
                    .Select(d => Path.Combine(d, "MicrosoftGame.config")).FirstOrDefault(File.Exists);
                if (cfg != null)
                {
                    var doc = new System.Xml.XmlDocument();
                    doc.Load(cfg);
                    var nodes = doc.SelectNodes("//*[local-name()='Executable']");
                    string first = null;
                    if (nodes != null)
                    {
                        foreach (System.Xml.XmlNode n in nodes)
                        {
                            string name = n.Attributes?["Name"]?.Value;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (first == null) first = name;
                            string fam = n.Attributes?["TargetDeviceFamily"]?.Value;
                            if (string.IsNullOrEmpty(fam) || string.Equals(fam, "PC", StringComparison.OrdinalIgnoreCase))
                            {
                                exeName = name;
                                break;
                            }
                        }
                        if (exeName == null) exeName = first;
                    }
                    if (exeName != null)
                    {
                        string baseDir = Path.GetDirectoryName(cfg);
                        string exe = Path.Combine(baseDir, exeName);
                        if (File.Exists(exe))
                            return new InstantLaunch { Exe = Path.GetFullPath(exe), Root = root };
                    }
                }
            }
            catch { }

            var best = FindBestExe(root, game.Name);
            return best == null ? null : new InstantLaunch { Exe = best, Root = root };
        }

        /// <summary>Resolve how to start the game directly, or null when it cannot be done safely.</summary>
        public static InstantLaunch ResolveLaunch(Game game)
        {
            switch (GetStore(game))
            {
                case InstantStore.Steam:
                    {
                        var root = ResolveInstallRoot(game);
                        var exe = ResolveGameExecutable(game);
                        return string.IsNullOrEmpty(exe) ? null : new InstantLaunch { Exe = exe, Root = root };
                    }
                case InstantStore.Gog:
                    return ResolveGog(game);
                case InstantStore.Epic:
                    return ResolveEpic(game);
                case InstantStore.Xbox:
                    return ResolveXbox(game);
                case InstantStore.Ubisoft:
                    {
                        var root = game.InstallDirectory;
                        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                        var exe = FindBestExe(root, game.Name);
                        return exe == null ? null : new InstantLaunch { Exe = exe, Root = root };
                    }
                default:
                    return null;
            }
        }

        public static bool IsSteamGame(Game game)
        {
            if (game == null) return false;
            if (game.PluginId == SteamPluginId) return true;
            if (game.Source != null &&
                string.Equals(game.Source.Name, "Steam", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(game.GameId) &&
                game.GameId.All(char.IsDigit) &&
                !string.IsNullOrEmpty(game.InstallDirectory) &&
                game.InstallDirectory.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        public static string GetSteamPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var p = k?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "steam.exe")))
                        return p.TrimEnd('\\', '/');
                }
            }
            catch { }

            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    var p = k?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "steam.exe")))
                        return p.TrimEnd('\\', '/');
                }
            }
            catch { }

            foreach (var c in new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam"
            })
            {
                if (File.Exists(Path.Combine(c, "steam.exe")))
                    return c;
            }
            return null;
        }

        public static List<string> GetLibrarySteamAppsFolders(string steamPath)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(steamPath)) return list;

            var defaultApps = Path.Combine(steamPath, "steamapps");
            if (Directory.Exists(defaultApps))
                list.Add(defaultApps);

            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return list;

            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var path = m.Groups[1].Value.Replace(@"\\", @"\");
                    var apps = Path.Combine(path, "steamapps");
                    if (Directory.Exists(apps) && !list.Contains(apps, StringComparer.OrdinalIgnoreCase))
                        list.Add(apps);
                }
            }
            catch { }

            return list;
        }

        public static string FindInstallRoot(Game game, string steamPath, string appId)
        {
            if (game != null &&
                !string.IsNullOrEmpty(game.InstallDirectory) &&
                Directory.Exists(game.InstallDirectory))
            {
                return game.InstallDirectory;
            }

            if (string.IsNullOrEmpty(steamPath) || string.IsNullOrEmpty(appId))
                return null;

            foreach (var apps in GetLibrarySteamAppsFolders(steamPath))
            {
                var acf = Path.Combine(apps, "appmanifest_" + appId + ".acf");
                if (!File.Exists(acf)) continue;

                try
                {
                    var text = File.ReadAllText(acf);
                    var m = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
                    if (!m.Success) continue;
                    var full = Path.Combine(apps, "common", m.Groups[1].Value);
                    if (Directory.Exists(full))
                        return full;
                }
                catch { }
            }
            return null;
        }

        public static string FindBestExe(string root, string gameName)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return null;

            List<FileInfo> exes;
            try
            {
                exes = new DirectoryInfo(root)
                    .EnumerateFiles("*.exe", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var n = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                        return !ExeExcludeHints.Any(h => n.Contains(h));
                    })
                    .ToList();
            }
            catch
            {
                return null;
            }

            if (exes.Count == 0) return null;
            if (exes.Count == 1) return exes[0].FullName;

            string Norm(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                return Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
            }

            var folderHint = Norm(Path.GetFileName(root.TrimEnd('\\', '/')));
            var nameHint = Norm(gameName);

            FileInfo best = null;
            int bestScore = int.MinValue;
            foreach (var exe in exes)
            {
                var baseName = Norm(Path.GetFileNameWithoutExtension(exe.Name));
                int score = 0;
                if (!string.IsNullOrEmpty(folderHint))
                {
                    if (baseName == folderHint) score += 100;
                    else if (baseName.Contains(folderHint)) score += 40;
                    else if (folderHint.Contains(baseName) && baseName.Length > 3) score += 30;
                }
                if (!string.IsNullOrEmpty(nameHint) && baseName.Contains(nameHint))
                    score += 20;

                try
                {
                    var rel = exe.FullName.Substring(root.Length).TrimStart('\\', '/');
                    var depth = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    score += Math.Max(0, 15 - depth);
                }
                catch { }

                try { score += (int)Math.Min(10, exe.Length / (5L * 1024 * 1024)); }
                catch { }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = exe;
                }
            }

            return best?.FullName;
        }

        public static string ResolveGameExecutable(Game game)
        {
            if (game == null || string.IsNullOrEmpty(game.GameId) || !game.GameId.All(char.IsDigit))
                return null;

            var steam = GetSteamPath();
            var root = FindInstallRoot(game, steam, game.GameId);
            if (root == null) return null;
            return FindBestExe(root, game.Name);
        }

        public static string ResolveInstallRoot(Game game)
        {
            if (game == null || string.IsNullOrEmpty(game.GameId)) return null;
            return FindInstallRoot(game, GetSteamPath(), game.GameId);
        }

        public static Process LaunchDirect(Game game, string exePath, string arguments = null, string workingDir = null)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return null;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
                    ? workingDir : (Path.GetDirectoryName(exePath) ?? ""),
                UseShellExecute = false
            };
            if (!string.IsNullOrWhiteSpace(arguments))
                psi.Arguments = arguments;

            if (game != null && IsSteamGame(game) && !string.IsNullOrEmpty(game.GameId) && game.GameId.All(char.IsDigit))
            {
                psi.Environment["SteamAppId"] = game.GameId;
                psi.Environment["SteamGameId"] = game.GameId;
            }

            return Process.Start(psi);
        }
    }

    /// <summary>
    /// PlayController that launches the Steam game exe directly and keeps Playnite
    /// tracking the session until the process (or any process in the install dir) exits.
    /// </summary>
    public class SteamInstantPlayController : PlayController
    {
        private readonly ILogger _logger;
        private Process _process;
        private CancellationTokenSource _cts;
        private DateTime _startedUtc;
        private readonly string _exePath;
        private readonly string _installRoot;
        private readonly string _arguments;
        private readonly string _workingDir;
        private volatile bool _stopRequested;

        private static readonly object _activeLock = new object();
        private static readonly Dictionary<Guid, SteamInstantPlayController> _active =
            new Dictionary<Guid, SteamInstantPlayController>();

        /// <summary>
        /// Called after the user force-closed the game (Alt+F4 hotkey). Ends the install-folder
        /// watch so Playnite stops showing the game as running, and sweeps leftover processes
        /// from the game's install folder that would otherwise keep the session alive forever.
        /// </summary>
        internal static void NotifyForceClosed(Guid gameId)
        {
            SteamInstantPlayController c;
            lock (_activeLock)
                _active.TryGetValue(gameId, out c);
            if (c != null)
            {
                c._stopRequested = true;
                Log.Info("Steam Instant Play: force-close requested, ending session watch.");
            }
            else
            {
                Log.Info("Steam Instant Play: force-close requested but no Instant Play session is tracked for this game " +
                         "(Playnite is tracking it another way).");
            }
        }

        private static readonly ILogger Log = LogManager.GetLogger();

        /// <summary>True when the executable lives in the install folder of a game launched via Instant Play.</summary>
        internal static bool IsPathInActiveInstallRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                lock (_activeLock)
                {
                    foreach (var c in _active.Values)
                    {
                        if (string.IsNullOrEmpty(c._installRoot)) continue;
                        var root = Path.GetFullPath(c._installRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void KillProcessesInDirectory(string root, ILogger logger)
        {
            try
            {
                int self = Process.GetCurrentProcess().Id;
                var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id <= 4 || p.Id == self || p.HasExited) continue;
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { continue; }
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        if (WindowHelper.IsProtectedProcess(p.Id)) continue;
                        logger?.Info($"Force close: ending leftover process {p.ProcessName} ({p.Id}) from install folder.");
                        p.Kill();
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        public SteamInstantPlayController(Game game, string exePath, string installRoot, ILogger logger,
            string arguments = null, string workingDir = null)
            : base(game)
        {
            Name = SteamInstantPlay.IsSteamGame(game) ? "Instant Play (skip cloud sync)" : "Instant Play";
            _exePath = exePath;
            _installRoot = installRoot;
            _logger = logger;
            _arguments = arguments;
            _workingDir = workingDir;
        }

        public override void Play(PlayActionArgs args)
        {
            DisposeWatch();

            _process = SteamInstantPlay.LaunchDirect(Game, _exePath, _arguments, _workingDir);
            if (_process == null)
            {
                _logger?.Error($"Steam Instant Play: failed to start {_exePath}");
                InvokeOnStopped(new GameStoppedEventArgs { SessionLength = 0 });
                return;
            }

            _startedUtc = DateTime.UtcNow;
            var pid = _process.Id;

            // Signal Playnite that the game is running (enables tracking / IsRunning)
            InvokeOnStarted(new GameStartedEventArgs
            {
                StartedProcessId = pid
            });

            _stopRequested = false;
            lock (_activeLock) _active[Game.Id] = this;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var installRoot = _installRoot;
            var proc = _process;

            Task.Run(() =>
            {
                try
                {
                    // Wait for the process we started, then also wait until no more
                    // processes remain under the install folder (handles launchers).
                    // Poll instead of blocking forever so a force-close (Alt+F4) can end the session
                    // even if the launched process lingers or its handle never signals.
                    try
                    {
                        while (!proc.WaitForExit(500))
                        {
                            if (token.IsCancellationRequested) break;
                            if (_stopRequested)
                            {
                                try { if (!proc.HasExited) proc.Kill(); } catch { }
                                break;
                            }
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(installRoot) && Directory.Exists(installRoot))
                    {
                        // Grace period for child game processes to spawn
                        Thread.Sleep(2000);
                        while (!token.IsCancellationRequested)
                        {
                            if (_stopRequested)
                            {
                                // User force-closed the game: end leftovers and stop tracking
                                // instead of waiting on helper processes indefinitely.
                                KillProcessesInDirectory(installRoot, _logger);
                                Thread.Sleep(300);
                                break;
                            }
                            if (!AnyProcessInDirectory(installRoot))
                                break;
                            Thread.Sleep(1000);
                        }
                    }
                    else
                    {
                        try
                        {
                            if (!proc.HasExited)
                                proc.WaitForExit();
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error($"Steam Instant Play watch error: {ex.Message}");
                }
                finally
                {
                    lock (_activeLock)
                    {
                        SteamInstantPlayController cur;
                        if (_active.TryGetValue(Game.Id, out cur) && ReferenceEquals(cur, this))
                            _active.Remove(Game.Id);
                    }
                    var seconds = (ulong)Math.Max(0, (DateTime.UtcNow - _startedUtc).TotalSeconds);
                    Log.Info($"Steam Instant Play: session ended after {seconds}s (forceClosed={_stopRequested}).");
                    try
                    {
                        InvokeOnStopped(new GameStoppedEventArgs { SessionLength = seconds });
                    }
                    catch { }
                }
            }, token);
        }

        private static bool AnyProcessInDirectory(string root)
        {
            try
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.HasExited) continue;
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { continue; }
                        if (string.IsNullOrEmpty(path)) continue;
                        var full = Path.GetFullPath(path);
                        if (full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        private void DisposeWatch()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            try { _process?.Dispose(); } catch { }
            _process = null;
        }

        public override void Dispose()
        {
            DisposeWatch();
            base.Dispose();
        }
    }
}
