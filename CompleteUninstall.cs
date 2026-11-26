using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    public class PendingUninstall
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string InstallDir { get; set; }
        public List<string> ExtraPaths { get; set; } = new List<string>();
        public DateTime StartedUtc { get; set; }
        /// <summary>No launcher uninstaller exists (manual game): after cleanup, mark the game as not installed.</summary>
        public bool MarkNotInstalled { get; set; }
        /// <summary>Heading for ExtraPaths in the confirmation (null = Steam leftovers).</summary>
        public string ExtraTitle { get; set; }
        /// <summary>Vortex staging / download folders (also listed in ExtraPaths, shown under their own heading).</summary>
        public List<string> VortexPaths { get; set; } = new List<string>();
    }

    /// <summary>
    /// "Complete uninstall": run the normal launcher uninstall first, then find what it left behind
    /// (mods, added files, shader cache, workshop data), show exactly what that is and delete it
    /// only after an explicit confirmation. Symlinks/junctions are removed as links and never followed.
    /// </summary>
    internal sealed class CompleteUninstallManager
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _log;
        private readonly string _file;
        private readonly object _lock = new object();
        private readonly HashSet<Guid> _busy = new HashSet<Guid>();
        private List<PendingUninstall> _pending;

        public CompleteUninstallManager(IPlayniteAPI api, ILogger log, string dataDir)
        {
            _api = api;
            _log = log;
            _file = Path.Combine(dataDir, "pending_uninstalls.json");
        }

        // ------------------------------------------------------------------ what to remove

        private sealed class Plan
        {
            public string Dir;                       // whole game folder (null = files only)
            public List<string> Files = new List<string>();
            public string FilesTitle;
            public List<string> Vortex = new List<string>();   // mods installed / downloaded with Vortex
        }

        private static readonly string[] RomSideExtensions =
        {
            ".cue", ".bin", ".img", ".sub", ".ccd", ".mds", ".mdf", ".gdi", ".raw", ".m3u",
            ".iso", ".chd", ".cso", ".zso", ".rvz", ".wbfs", ".nsp", ".xci"
        };

        /// <summary>True when there is something this game can be completely uninstalled from.</summary>
        /// <summary>
        /// True when the game has anything an uninstall could act on (an install folder, an exe or ROM files).
        /// The button is shown for these even when the safety checks refuse, because clicking it then
        /// explains exactly why instead of the button silently missing.
        /// </summary>
        public bool ShouldOfferButton(Game game)
        {
            try
            {
                if (game == null) return false;
                if (CanStart(game)) return true;
                if (!string.IsNullOrWhiteSpace(game.InstallDirectory)) return true;
                if (game.Roms != null && game.Roms.Count > 0) return true;
                return game.GameActions != null && game.GameActions.Any(a => a != null && a.Type == GameActionType.File);
            }
            catch { return false; }
        }

        private bool LibraryPluginLoaded(Guid pluginId)
        {
            try { return _api.Addons.Plugins.OfType<LibraryPlugin>().Any(pl => pl.Id == pluginId); }
            catch { return true; }
        }

        public bool CanStart(Game game)
        {
            try { string problem; return BuildPlan(game, out problem) != null; }
            catch { return false; }
        }

        private string ExpandPath(Game game, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var p = _api.ExpandGameVariables(game, raw);
                if (string.IsNullOrWhiteSpace(p)) p = raw;
                if (!Path.IsPathRooted(p) && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                    p = Path.Combine(_api.ExpandGameVariables(game, game.InstallDirectory), p);
                return Normalize(p);
            }
            catch { return null; }
        }

        /// <summary>Emulator games: the game's own ROM files plus matching disc-image side files (cue/bin ...).</summary>
        private List<string> GetRomFiles(Game game)
        {
            var result = new List<string>();
            try
            {
                if (game.Roms == null) return result;
                foreach (var rom in game.Roms)
                {
                    var path = ExpandPath(game, rom?.Path);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (Directory.Exists(path))
                    {
                        // Folder based games: the folder itself is the game (checked by IsSafeFileToDelete).
                        if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
                        continue;
                    }
                    if (!File.Exists(path)) continue;
                    if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);

                    var dir = Path.GetDirectoryName(path);
                    var baseName = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        var ext = Path.GetExtension(f);
                        if (!RomSideExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                        var name = Path.GetFileNameWithoutExtension(f);
                        if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith(baseName + " (", StringComparison.OrdinalIgnoreCase))
                        {
                            var full = Normalize(f);
                            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Local games without an install folder: use the folder of the program they start.</summary>
        private string DeriveDirFromActions(Game game)
        {
            try
            {
                foreach (var a in game.GameActions ?? Enumerable.Empty<GameAction>())
                {
                    if (a == null || a.Type != GameActionType.File) continue;
                    var path = ExpandPath(game, a.Path);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return Normalize(Path.GetDirectoryName(path));
                }
            }
            catch { }
            return null;
        }

        private Plan BuildPlan(Game game, out string problem)
        {
            problem = null;
            if (game == null) { problem = "no game."; return null; }

            // Emulator games: only the game's own files, never a (possibly shared) ROM folder.
            var roms = GetRomFiles(game);
            bool emulator = roms.Count > 0 || (game.Roms != null && game.Roms.Count > 0) ||
                            (game.GameActions?.Any(a => a != null && a.Type == GameActionType.Emulator) ?? false);
            if (emulator)
            {
                // Files already gone: nothing to delete, the game is only marked as not installed.
                if (roms.Count == 0) return new Plan { FilesTitle = "Game files:" };
                foreach (var f in roms)
                {
                    string r;
                    if (!IsSafeFileToDelete(f, game.Id, out r)) { problem = r; return null; }
                }
                var files = new Plan { FilesTitle = "Game files:" };
                files.Files.AddRange(roms);
                return files;
            }

            string dir = ResolveDir(game);
            if (string.IsNullOrEmpty(dir)) dir = DeriveDirFromActions(game);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                problem = "it has no game folder on disk, so there is nothing to remove.";
                return null;
            }
            string reason;
            if (!IsSafeToDelete(dir, game.Id, out reason))
            {
                problem = "this folder is not safe to remove:\n" + dir + "\n\n" + reason;
                return null;
            }
            var plan2 = new Plan { Dir = dir, Files = GetSteamExtras(game, dir), FilesTitle = "Steam leftovers:" };
            plan2.Vortex = GetVortexPaths(game, dir);
            return plan2;
        }

        internal bool IsSafeFileToDelete(string path, Guid gameId, out string reason)
        {
            reason = null;
            try
            {
                string full = Normalize(path);
                if (string.IsNullOrEmpty(full)) { reason = "Invalid file path."; return false; }
                if (Directory.Exists(full) && !IsSafeToDelete(full, gameId, out reason)) return false;

                foreach (var f in new[]
                {
                    Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                    Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData
                })
                {
                    var prot = Normalize(Environment.GetFolderPath(f));
                    if (!string.IsNullOrEmpty(prot) && full.StartsWith(prot + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "The file is inside a system folder:\n" + full;
                        return false;
                    }
                }

                // Another game in the library using the same file keeps it.
                foreach (var g in _api.Database.Games)
                {
                    if (g.Id == gameId || g.Roms == null) continue;
                    foreach (var rom in g.Roms)
                    {
                        var other = ExpandPath(g, rom?.Path);
                        if (!string.IsNullOrEmpty(other) && string.Equals(other, full, StringComparison.OrdinalIgnoreCase))
                        {
                            reason = $"The file is also used by another game ({g.Name}).";
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "Safety check failed: " + ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>Game menu action (UI thread).</summary>
        public void Start(Game game)
        {
            if (game == null) return;
            try
            {
                string problem;
                var plan = BuildPlan(game, out problem);
                if (plan == null)
                {
                    Info("Complete uninstall", $"{game.Name}: {problem}");
                    return;
                }

                string dir = plan.Dir;
                bool filesOnly = string.IsNullOrEmpty(dir);
                bool hasUninstaller = !filesOnly && game.IsInstalled && game.PluginId != Guid.Empty &&
                                      LibraryPluginLoaded(game.PluginId);
                var p = new PendingUninstall
                {
                    GameId = game.Id,
                    Name = game.Name,
                    InstallDir = dir ?? "",
                    ExtraPaths = plan.Files.Concat(plan.Vortex).ToList(),
                    ExtraTitle = plan.FilesTitle,
                    VortexPaths = plan.Vortex,
                    StartedUtc = DateTime.UtcNow,
                    MarkNotInstalled = !hasUninstaller
                };

                // For games without a launcher uninstaller, skip the generic first
                // confirmation. BeginCleanup scans the target and asks once with the
                // exact paths and sizes before deleting anything.
                if (hasUninstaller)
                {
                    string text = $"Completely uninstall \"{game.Name}\"?\n\n" +
                      "1. The normal uninstall runs first.\n" +
                      $"2. Then the game folder is checked for leftovers:\n   {dir}\n" +
                      "3. Nothing extra is deleted until you confirm the list.\n\n" +
                      "Continue?";

                    if (_api.Dialogs.ShowMessage(text, "Complete uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question)
                        != MessageBoxResult.Yes)
                        return;
                }

                if (hasUninstaller)
                {
                    AddPending(p);
                    try { _api.UninstallGame(game.Id); }
                    catch (Exception ex)
                    {
                        // The launcher cannot uninstall this game: remove it the way a manual game is removed.
                        _log.Warn($"Complete uninstall: UninstallGame failed, using manual cleanup: {ex.Message}");
                        RemovePending(game.Id);
                        p.MarkNotInstalled = true;
                        AddPending(p);
                        BeginCleanup(p, true);
                        return;
                    }
                    Notify(game.Id, $"Finish the uninstall of {game.Name}. Leftovers are checked right after it completes.");
                }
                else
                {
                    AddPending(p);
                    BeginCleanup(p, true);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Complete uninstall failed: {ex}");
                Info("Complete uninstall", "Unexpected error: " + ex.Message);
            }
        }

        /// <summary>Database ItemUpdated: the normal uninstall finished when a pending game flips to not installed.</summary>
        public void OnGamesUpdated(IEnumerable<Game> games)
        {
            if (games == null) return;
            List<PendingUninstall> due;
            lock (_lock)
            {
                EnsureLoaded();
                var ids = new HashSet<Guid>(games.Where(g => g != null && !g.IsInstalled).Select(g => g.Id));
                due = _pending.Where(p => !p.MarkNotInstalled && ids.Contains(p.GameId)).ToList();
            }
            foreach (var p in due) BeginCleanup(p, false);
        }

        /// <summary>Startup sweep / main-menu retry for uninstalls that finished while Playnite was closed.</summary>
        public void CheckPending(bool userInitiated)
        {
            List<PendingUninstall> list;
            lock (_lock) { EnsureLoaded(); list = _pending.ToList(); }
            if (list.Count == 0)
            {
                if (userInitiated) Info("Complete uninstall", "No complete uninstalls are waiting.");
                return;
            }

            foreach (var p in list)
            {
                var game = _api.Database.Games.Get(p.GameId);
                bool stillInstalled = game != null && game.IsInstalled && !p.MarkNotInstalled;
                if (stillInstalled)
                {
                    // The normal uninstall was cancelled or never finished.
                    if (userInitiated || (DateTime.UtcNow - p.StartedUtc).TotalHours > 24)
                    {
                        RemovePending(p.GameId);
                        if (userInitiated)
                            Info("Complete uninstall", $"{p.Name} is still installed. The pending request was cancelled.");
                    }
                    continue;
                }
                BeginCleanup(p, userInitiated);
            }
        }

        // ------------------------------------------------------------------ cleanup

        private void BeginCleanup(PendingUninstall p, bool userInitiated)
        {
            lock (_lock)
            {
                if (!_busy.Add(p.GameId)) return;
            }

            Task.Run(async () =>
            {
                try
                {
                    // Give Steam/launchers a moment to finish removing their own files.
                    if (!userInitiated) await Task.Delay(3000);

                    string reason;
                    if (!Exists(p.InstallDir) && !p.ExtraPaths.Any(Exists))
                    {
                        FinishGame(p);
                        RemovePending(p.GameId);
                        if (userInitiated) Notify(p.GameId, $"{p.Name}: nothing is left to remove.");
                        return;
                    }
                    if (Exists(p.InstallDir) && !IsSafeToDelete(p.InstallDir, p.GameId, out reason))
                    {
                        Info("Complete uninstall", $"{p.Name}: leftover cleanup skipped. {reason}\n\n" +
                            "The request is still pending. If the folder is safe but access is denied, close the game and launcher, " +
                            "then restart Playnite as administrator and use Playnite Overlay 3000 > Check pending complete uninstalls.");
                        return;
                    }
                    if (Directory.Exists(p.InstallDir) && AnyProcessInside(p.InstallDir))
                    {
                        Notify(p.GameId, $"{p.Name}: close the game (a process still runs from its folder), then run the check again from the main menu.");
                        return; // keep pending
                    }

                    var scan = new ScanResult();
                    if (Exists(p.InstallDir)) scan.Add(p.InstallDir, true);
                    foreach (var e in p.ExtraPaths.Where(Exists)) scan.Add(e, false);

                    // Never treat an incomplete scan as an empty directory. Keep the
                    // request so it can be retried after access is restored.
                    if (scan.Errors.Count > 0)
                    {
                        foreach (var failure in scan.Errors.Take(50)) _log.Warn("Complete uninstall scan failed: " + failure);
                        Info("Complete uninstall", $"{p.Name}: could not inspect every leftover, so nothing was deleted.\n\n" +
                            string.Join("\n", scan.Errors.Take(5)) +
                            "\n\nClose the game and launcher, or restart Playnite as administrator if the folder requires it. " +
                            "Then use Playnite Overlay 3000 > Check pending complete uninstalls. No permissions were changed.");
                        return;
                    }

                    var answer = MessageBoxResult.No;
                    RunOnUi(() =>
                    {
                        answer = _api.Dialogs.ShowMessage(BuildPrompt(p, scan), "Complete uninstall",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    });

                    if (answer != MessageBoxResult.Yes)
                    {
                        RemovePending(p.GameId);
                        Notify(p.GameId, $"{p.Name}: leftovers were kept.");
                        return;
                    }

                    var failures = new List<string>();
                    long freed = scan.Bytes;
                    foreach (var e in p.ExtraPaths.Where(Exists)) DeleteTree(e, failures);
                    if (Exists(p.InstallDir)) DeleteTree(p.InstallDir, failures);

                    if (failures.Count == 0)
                    {
                        FinishGame(p);
                        RemovePending(p.GameId);
                        Notify(p.GameId, $"{p.Name}: completely uninstalled, {FormatBytes(freed)} of leftovers removed.");
                    }
                    else
                    {
                        foreach (var f in failures.Take(50)) _log.Warn("Complete uninstall could not remove: " + f);
                        // Keep pending and ask again on retry: confirmation applies to the
                        // newly scanned leftovers, not to future additions to the folder.
                        Info("Complete uninstall", $"{p.Name}: some leftovers could not be deleted.\n\n" +
                            string.Join("\n", failures.Take(5)) +
                            (failures.Count > 5 ? $"\n...and {failures.Count - 5} more. See the Playnite log." : "") +
                            "\n\nClose the game and launcher. If access is denied, restart Playnite as administrator " +
                            "only if you trust the files and need that access. Then use Playnite Overlay 3000 > " +
                            "Check pending complete uninstalls to retry. The remaining files will be shown for confirmation.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"Complete uninstall cleanup failed: {ex}");
                    Notify(p.GameId, $"{p.Name}: leftover cleanup failed: {ex.Message}");
                }
                finally
                {
                    lock (_lock) { _busy.Remove(p.GameId); }
                }
            });
        }

        private void FinishGame(PendingUninstall p)
        {
            if (!p.MarkNotInstalled) return;
            try
            {
                RunOnUi(() =>
                {
                    var g = _api.Database.Games.Get(p.GameId);
                    if (g != null && g.IsInstalled)
                    {
                        g.IsInstalled = false;
                        _api.Database.Games.Update(g);
                    }
                });
            }
            catch (Exception ex) { _log.Warn("Complete uninstall: could not mark game as not installed: " + ex.Message); }
        }

        private static string BuildPrompt(PendingUninstall p, ScanResult scan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{p.Name}: these leftovers will be PERMANENTLY deleted:");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(p.InstallDir))
            {
                sb.AppendLine(p.InstallDir);
                sb.AppendLine($"   {scan.Files} file(s), {scan.Dirs} folder(s), {FormatBytes(scan.Bytes)}" +
                              (scan.Links > 0 ? $", {scan.Links} link(s) (removed as links, targets untouched)" : ""));
                foreach (var t in scan.Top.OrderByDescending(x => x.Bytes).Take(10))
                    sb.AppendLine($"   - {t.Name}  ({FormatBytes(t.Bytes)})");
                if (scan.Top.Count > 10) sb.AppendLine($"   ... and {scan.Top.Count - 10} more item(s)");
            }

            var vortex = (p.VortexPaths ?? new List<string>()).Where(Exists).ToList();
            var extras = p.ExtraPaths.Where(Exists)
                .Where(e => !vortex.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (extras.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(p.ExtraTitle ?? "Steam leftovers:");
                foreach (var e in extras) sb.AppendLine("   - " + e);
            }
            if (vortex.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Vortex mods and downloads:");
                foreach (var e in vortex) sb.AppendLine("   - " + e);
            }
            sb.AppendLine();
            sb.AppendLine("Save files outside the game folder (Documents, AppData, Steam cloud) are not touched.");
            sb.AppendLine("This cannot be undone. Delete everything listed?");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ safety

        internal bool IsSafeToDelete(string path, Guid gameId, out string reason)
        {
            reason = null;
            try
            {
                string full = Normalize(path);
                string root = Normalize(Path.GetPathRoot(full) ?? "");
                if (string.IsNullOrEmpty(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "That is a drive root.";
                    return false;
                }

                int depth = full.Substring(root.Length)
                    .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (depth < 2)
                {
                    reason = "The folder is too close to the drive root (it looks like a games library, not a single game).";
                    return false;
                }

                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "The install folder is a symlink/junction.";
                    return false;
                }

                string name = Path.GetFileName(full);
                string parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
                if (string.Equals(name, "steamapps", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(parent, "steamapps", StringComparison.OrdinalIgnoreCase)))
                {
                    reason = "That is a Steam library folder, not a single game.";
                    return false;
                }

                foreach (var prot in ProtectedFolders())
                {
                    if (string.IsNullOrEmpty(prot)) continue;
                    if (string.Equals(full, prot, StringComparison.OrdinalIgnoreCase) ||
                        prot.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "It is, or contains, a protected folder:\n" + prot;
                        return false;
                    }
                }

                foreach (var g in _api.Database.Games)
                {
                    if (g.Id == gameId || !g.IsInstalled || string.IsNullOrWhiteSpace(g.InstallDirectory)) continue;
                    string other = ResolveDir(g);
                    if (string.IsNullOrEmpty(other)) continue;
                    if (string.Equals(other, full, StringComparison.OrdinalIgnoreCase) ||
                        other.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"It overlaps the install folder of another installed game ({g.Name}).";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "Safety check failed: " + ex.Message;
                return false;
            }
        }

        private IEnumerable<string> ProtectedFolders()
        {
            var list = new List<string>();
            foreach (var f in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData
            })
            {
                try { list.Add(Normalize(Environment.GetFolderPath(f))); } catch { }
            }
            try { list.Add(Normalize(_api.Paths.ApplicationPath)); } catch { }
            try { list.Add(Normalize(_api.Paths.ConfigurationPath)); } catch { }
            try { list.Add(Normalize(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))); } catch { }
            try { list.Add(Normalize(SteamInstantPlay.GetSteamPath())); } catch { }
            return list.Where(x => !string.IsNullOrEmpty(x));
        }

        private static bool AnyProcessInside(string dir)
        {
            try
            {
                string root = Normalize(dir) + "\\";
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string exe = null;
                        try { exe = p.MainModule?.FileName; } catch { continue; }
                        if (!string.IsNullOrEmpty(exe) &&
                            Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ Steam extras

        /// <summary>Mods installed (staging) and downloaded with Vortex for this game.</summary>
        private List<string> GetVortexPaths(Game game, string installDir)
        {
            var list = new List<string>();
            try
            {
                string install = Normalize(installDir);
                Action<string> add = raw =>
                {
                    var path = Normalize(raw);
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                    // Inside the game folder: removed together with it.
                    if (!string.IsNullOrEmpty(install) &&
                        path.StartsWith(install + "\\", StringComparison.OrdinalIgnoreCase)) return;
                    string reason;
                    if (!IsSafeToDelete(path, game.Id, out reason)) return;
                    if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
                };

                foreach (var m in VortexInfo.ReadManifests(installDir))
                {
                    add(m.stagingPath);                                         // installed mods
                    if (!string.IsNullOrWhiteSpace(m.game))
                        add(Path.Combine(VortexInfo.DefaultDownloadsRoot(), m.game));   // downloaded archives
                }
            }
            catch { }
            return list;
        }

        private static List<string> GetSteamExtras(Game game, string installDir)
        {
            var list = new List<string>();
            try
            {
                if (!SteamInstantPlay.IsSteamGame(game) || string.IsNullOrEmpty(game.GameId) ||
                    !game.GameId.All(char.IsDigit))
                    return list;

                var d = new DirectoryInfo(installDir);
                while (d != null && !string.Equals(d.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                    d = d.Parent;
                if (d == null) return list;

                string apps = d.FullName, id = game.GameId;
                list.Add(Path.Combine(apps, "shadercache", id));
                list.Add(Path.Combine(apps, "workshop", "content", id));
                list.Add(Path.Combine(apps, "workshop", "appworkshop_" + id + ".acf"));
                list.Add(Path.Combine(apps, "downloading", id));
                list.Add(Path.Combine(apps, "temp", id));
            }
            catch { }
            return list;
        }

        // ------------------------------------------------------------------ scanning / deleting

        private sealed class TopItem { public string Name; public long Bytes; }

        private sealed class ScanResult
        {
            public int Files, Dirs, Links;
            public long Bytes;
            public readonly List<TopItem> Top = new List<TopItem>();
            public readonly List<string> Errors = new List<string>();

            public void Add(string path, bool listChildren)
            {
                if (listChildren)
                {
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                        {
                            long before = Bytes;
                            Measure(entry);
                            Top.Add(new TopItem { Name = Path.GetFileName(entry), Bytes = Bytes - before });
                        }
                    }
                    catch (Exception ex) { Errors.Add(path + " : " + ex.Message); }
                }
                else Measure(path);
            }

            private void Measure(string path)
            {
                try
                {
                    var attr = File.GetAttributes(path);
                    bool isDir = (attr & FileAttributes.Directory) != 0;
                    if ((attr & FileAttributes.ReparsePoint) != 0) { Links++; return; }
                    if (!isDir)
                    {
                        Files++;
                        try { Bytes += new FileInfo(path).Length; } catch { }
                        return;
                    }
                    Dirs++;
                    foreach (var e in Directory.EnumerateFileSystemEntries(path)) Measure(e);
                }
                catch (Exception ex) { Errors.Add(path + " : " + ex.Message); }
            }
        }

        // File.Exists/Directory.Exists return false on access errors; treat those as
        // potentially present so an unreadable path is never reported as removed.
        private static bool Exists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }
        }

        private static void DeleteTree(string path, List<string> failures)
        {
            try
            {
                var attr = File.GetAttributes(path);
                bool isDir = (attr & FileAttributes.Directory) != 0;
                bool isLink = (attr & FileAttributes.ReparsePoint) != 0;

                // Never change attributes on a link: it may affect the target.
                if (isLink)
                {
                    RetrySharingViolation(() => { if (isDir) Directory.Delete(path, false); else File.Delete(path); });
                    return;
                }
                if (!isDir)
                {
                    // Only clear ReadOnly; retain unrelated attributes. Attribute writes
                    // may be denied even when delete permission is granted, so still try.
                    if ((attr & FileAttributes.ReadOnly) != 0)
                    {
                        try { File.SetAttributes(path, attr & ~FileAttributes.ReadOnly); } catch (UnauthorizedAccessException) { }
                    }
                    RetrySharingViolation(() => File.Delete(path));
                    return;
                }

                int failuresBefore = failures.Count;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToList())
                    DeleteTree(entry, failures);

                // Avoid duplicate ancestor errors, but never report success if new
                // entries appeared while cleanup was in progress.
                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    if (failures.Count == failuresBefore)
                        failures.Add(path + " : folder still contains files (it may have changed during cleanup).");
                    return;
                }
                if ((attr & FileAttributes.ReadOnly) != 0)
                {
                    try { File.SetAttributes(path, attr & ~FileAttributes.ReadOnly); } catch (UnauthorizedAccessException) { }
                }
                RetrySharingViolation(() => Directory.Delete(path, false));
            }
            catch (FileNotFoundException) { /* already removed by the launcher */ }
            catch (DirectoryNotFoundException) { /* already removed by the launcher */ }
            catch (Exception ex) { failures.Add(path + " : " + ex.Message); }
        }

        // Let launchers release recently closed file handles without repeatedly
        // retrying true permission failures or changing ACLs/ownership.
        private static void RetrySharingViolation(Action action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { action(); return; }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xFFFF;
                    if (attempt >= 3 || (code != 32 && code != 33)) throw;
                    System.Threading.Thread.Sleep(250 * (attempt + 1));
                }
            }
        }

        // ------------------------------------------------------------------ helpers / persistence

        private string ResolveDir(Game game)
        {
            try
            {
                string raw = game.InstallDirectory;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string expanded = _api.ExpandGameVariables(game, raw);
                return Normalize(string.IsNullOrWhiteSpace(expanded) ? raw : expanded);
            }
            catch { return null; }
        }

        private static string Normalize(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return ""; }
        }

        internal static string FormatBytes(long b)
        {
            double v = b;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.#")) + " " + u[i];
        }

        private void RunOnUi(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a();
            else d.Invoke(a);
        }

        private void Info(string caption, string text)
        {
            RunOnUi(() => _api.Dialogs.ShowMessage(text, caption));
        }

        private void Notify(Guid id, string text)
        {
            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    "complete-uninstall-" + id.ToString("N") + "-" + DateTime.UtcNow.Ticks, text, NotificationType.Info));
            }
            catch { }
        }

        private void EnsureLoaded()
        {
            if (_pending != null) return;
            _pending = new List<PendingUninstall>();
            try
            {
                if (File.Exists(_file))
                    _pending = JsonSerializer.Deserialize<List<PendingUninstall>>(File.ReadAllText(_file))
                               ?? new List<PendingUninstall>();
            }
            catch (Exception ex) { _log.Warn("Complete uninstall: could not read pending list: " + ex.Message); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file, JsonSerializer.Serialize(_pending,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { _log.Warn("Complete uninstall: could not save pending list: " + ex.Message); }
        }

        private void AddPending(PendingUninstall p)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _pending.RemoveAll(x => x.GameId == p.GameId);
                _pending.Add(p);
                Save();
            }
        }

        private void RemovePending(Guid id)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_pending.RemoveAll(x => x.GameId == id) > 0) Save();
            }
        }
    }

    /// <summary>Finds Vortex (the Nexus Mods manager) and what it installed for a game.</summary>
    internal static class VortexInfo
    {
        internal class VortexManifest
        {
            public string game { get; set; }
            public string stagingPath { get; set; }
            public string targetPath { get; set; }
        }

        private static string _exe;
        private static int _exeTick;

        /// <summary>
        /// Vortex.exe. Looked up through the nxm:// link handler first, then the Windows uninstall entries and
        /// the default install folders, because another app (e.g. the Nexus Mods app) can own the nxm handler
        /// while Vortex is still installed. Null if Vortex cannot be found.
        /// </summary>
        public static string GetExePath()
        {
            int now = Environment.TickCount;
            // Only a successful lookup is cached for long; a miss is retried after 10 s.
            if (_exeTick != 0 && unchecked(now - _exeTick) < (_exe != null ? 60000 : 10000)) return _exe;
            string path = FromNxmHandler() ?? FromUninstallKeys() ?? FromDefaultFolders();
            _exe = path;
            _exeTick = now == 0 ? 1 : now;
            return path;
        }

        private static bool IsVortexExe(string p)
        {
            try
            {
                return !string.IsNullOrEmpty(p) && File.Exists(p) &&
                       Path.GetFileName(p).StartsWith("Vortex", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string FromNxmHandler()
        {
            var hives = new Func<Microsoft.Win32.RegistryKey>[]
            {
                () => Microsoft.Win32.Registry.ClassesRoot,
                () => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes"),
                () => Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Classes")
            };
            foreach (var open in hives)
            {
                try
                {
                    using (var root = open())
                    using (var k = root?.OpenSubKey(@"nxm\shell\open\command"))
                    {
                        var cmd = (k?.GetValue("") as string ?? "").Trim();
                        if (cmd.Length == 0) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(cmd, "^\"([^\"]+)\"|^(\\S+)");
                        var p = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
                        if (IsVortexExe(p)) return p;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FromUninstallKeys()
        {
            var roots = new[]
            {
                new { Hive = Microsoft.Win32.Registry.CurrentUser, Sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall" },
                new { Hive = Microsoft.Win32.Registry.LocalMachine, Sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall" },
                new { Hive = Microsoft.Win32.Registry.LocalMachine, Sub = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" }
            };
            foreach (var r in roots)
            {
                try
                {
                    using (var parent = r.Hive.OpenSubKey(r.Sub))
                    {
                        if (parent == null) continue;
                        foreach (var name in parent.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = parent.OpenSubKey(name))
                                {
                                    var display = k?.GetValue("DisplayName") as string;
                                    if (!string.Equals((display ?? "").Trim(), "Vortex", StringComparison.OrdinalIgnoreCase)) continue;
                                    var loc = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                                    if (loc.Length > 0)
                                    {
                                        var exe = Path.Combine(loc, "Vortex.exe");
                                        if (IsVortexExe(exe)) return exe;
                                    }
                                    var icon = (k.GetValue("DisplayIcon") as string ?? "").Trim();
                                    int comma = icon.LastIndexOf(',');
                                    if (comma > 0) icon = icon.Substring(0, comma);
                                    icon = icon.Trim('"');
                                    if (IsVortexExe(icon)) return icon;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FromDefaultFolders()
        {
            var folders = new List<string>();
            Action<Environment.SpecialFolder, string> add = (f, sub) =>
            {
                try
                {
                    var b = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(b)) folders.Add(Path.Combine(b, sub));
                }
                catch { }
            };
            add(Environment.SpecialFolder.LocalApplicationData, @"Programs\Vortex");
            add(Environment.SpecialFolder.ProgramFiles, @"Black Tree Gaming Ltd\Vortex");
            add(Environment.SpecialFolder.ProgramFilesX86, @"Black Tree Gaming Ltd\Vortex");
            add(Environment.SpecialFolder.ProgramFiles, "Vortex");
            foreach (var f in folders)
            {
                var exe = Path.Combine(f, "Vortex.exe");
                if (IsVortexExe(exe)) return exe;
            }
            return null;
        }

        public static string DefaultDownloadsRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex", "downloads");
        }

        /// <summary>Vortex writes vortex.deployment*.json into the game folder; it names the staging folder.</summary>
        public static List<VortexManifest> ReadManifests(string installDir)
        {
            var result = new List<VortexManifest>();
            try
            {
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return result;
                var queue = new Queue<Tuple<string, int>>();
                queue.Enqueue(Tuple.Create(installDir, 0));
                int visited = 0;
                while (queue.Count > 0 && visited < 400)
                {
                    var cur = queue.Dequeue();
                    visited++;
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(cur.Item1, "vortex.deployment*.json"))
                        {
                            try
                            {
                                var m = Playnite.SDK.Data.Serialization.FromJson<VortexManifest>(File.ReadAllText(f));
                                if (m != null) result.Add(m);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    if (cur.Item2 < 3)
                    {
                        try
                        {
                            foreach (var d in Directory.EnumerateDirectories(cur.Item1))
                                queue.Enqueue(Tuple.Create(d, cur.Item2 + 1));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
