using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Fills in "Install size" for installed games that have none: games added by hand, emulated games (the size of the
    /// game's own ROM files) and library games the launcher plugin gave no size for. The size is measured off the UI thread
    /// and only written when the game still has no size. Remove this file and its hooks to undo it.
    /// </summary>
    internal sealed class InstallSizeFiller
    {
        private static readonly string[] RomSideExtensions =
        {
            ".cue", ".bin", ".img", ".sub", ".ccd", ".mds", ".mdf", ".gdi", ".raw", ".m3u",
            ".iso", ".chd", ".cso", ".zso", ".rvz", ".wbfs"
        };

        private readonly IPlayniteAPI _api;
        private readonly ILogger _log;
        private readonly HashSet<Guid> _busy = new HashSet<Guid>();
        private readonly object _lock = new object();

        public InstallSizeFiller(IPlayniteAPI api, ILogger log) { _api = api; _log = log; }

        /// <summary>UI thread: game added, or turned installed. No-op when it is not installed or already has a size.</summary>
        public void Queue(Guid gameId)
        {
            try
            {
                var g = _api.Database.Games.Get(gameId);
                if (g == null || !g.IsInstalled || (g.InstallSize != null && g.InstallSize > 0)) return;
                lock (_lock) { if (!_busy.Add(gameId)) return; }

                // Everything the background thread needs is resolved here, on the UI thread.
                var targets = ResolveTargets(g);
                if (targets.Count == 0) { lock (_lock) _busy.Remove(gameId); return; }

                Task.Run(() =>
                {
                    try
                    {
                        ulong total = 0;
                        var clock = Stopwatch.StartNew();
                        foreach (var t in targets)
                        {
                            ulong part;
                            if (!Measure(t, clock, out part)) return;   // too slow or unreadable: leave it empty
                            total += part;
                        }
                        if (total == 0) return;
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var cur = _api.Database.Games.Get(gameId);
                                if (cur == null || (cur.InstallSize != null && cur.InstallSize > 0)) return;
                                cur.InstallSize = total;
                                _api.Database.Games.Update(cur);
                            }
                            catch (Exception ex) { _log.Warn("Install size: update failed: " + ex.Message); }
                        }));
                    }
                    catch (Exception ex) { _log.Warn("Install size: " + ex.Message); }
                    finally { lock (_lock) _busy.Remove(gameId); }
                });
            }
            catch (Exception ex)
            {
                lock (_lock) _busy.Remove(gameId);
                _log.Warn("Install size: " + ex.Message);
            }
        }

        private string Expand(Game g, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var p = _api.ExpandGameVariables(g, raw);
                return string.IsNullOrWhiteSpace(p) ? raw : p;
            }
            catch { return raw; }
        }

        private List<string> ResolveTargets(Game g)
        {
            var list = new List<string>();
            Action<string> add = p =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p)) return;
                    p = Path.GetFullPath(p).TrimEnd('\\', '/');
                    if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
                }
                catch { }
            };

            bool emulated = (g.Roms != null && g.Roms.Count > 0) ||
                            (g.GameActions?.Any(a => a != null && a.Type == GameActionType.Emulator) ?? false);
            if (emulated)
            {
                // Only the game's own files, never the (shared) ROM folder.
                foreach (var rom in (IEnumerable<GameRom>)g.Roms ?? new GameRom[0])
                {
                    string path = Expand(g, rom?.Path);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (File.Exists(path))
                    {
                        add(path);
                        string dir = Path.GetDirectoryName(path), baseName = Path.GetFileNameWithoutExtension(path);
                        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) continue;
                        foreach (var f in Directory.EnumerateFiles(dir))
                        {
                            if (!RomSideExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                            string name = Path.GetFileNameWithoutExtension(f);
                            if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(baseName + " (", StringComparison.OrdinalIgnoreCase))
                                add(f);
                        }
                    }
                    else if (Directory.Exists(path) && IsGameFolder(path)) add(path);   // folder based games (PS3, Wii U ...)
                }
                return list;
            }

            string install = Expand(g, g.InstallDirectory);
            if (string.IsNullOrEmpty(install) || !Directory.Exists(install))
            {
                install = null;
                foreach (var a in g.GameActions ?? Enumerable.Empty<GameAction>())
                {
                    if (a == null || a.Type != GameActionType.File) continue;
                    string p = Expand(g, a.Path);
                    if (!string.IsNullOrEmpty(p) && Path.IsPathRooted(p) && File.Exists(p)) { install = Path.GetDirectoryName(p); break; }
                }
            }
            if (!string.IsNullOrEmpty(install) && Directory.Exists(install) && IsGameFolder(install)) add(install);
            return list;
        }

        /// <summary>Not a drive root and not a games library (at least two levels below the root).</summary>
        private static bool IsGameFolder(string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\', '/');
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
                int depth = full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
                return depth >= 2;
            }
            catch { return false; }
        }

        private static bool Measure(string path, Stopwatch clock, out ulong bytes)
        {
            bytes = 0;
            try
            {
                if (File.Exists(path)) { bytes = (ulong)new FileInfo(path).Length; return true; }
                var pending = new Stack<string>();
                pending.Push(path);
                while (pending.Count > 0)
                {
                    if (clock.Elapsed.TotalSeconds > 120) return false;
                    string d = pending.Pop();
                    foreach (var f in Directory.EnumerateFiles(d))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) == 0) bytes += (ulong)fi.Length;
                        }
                        catch { }
                    }
                    foreach (var sub in Directory.EnumerateDirectories(d))
                    {
                        try
                        {
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
                        }
                        catch { }
                    }
                }
                return true;
            }
            catch { return false; }
        }
    }
}
