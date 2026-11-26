using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Save / config folder lookup for recompilations and unofficial PC ports (PCSX2 / shadPS4 style recomps,
    /// "NinjaGaiden2-recomp", "ShadowOfTheColossus-PC", ...). These are not in the Ludusavi manifest and do not use the
    /// usual "saves" / "config" folder names, so the generic guess in GuessSaveAndConfig finds nothing for them.
    /// Only folders that exist are returned. Remove this file (and the two hooks in PlayniteGameOverlay.cs) to go back
    /// to the old behaviour.
    /// </summary>
    internal static class RecompLocations
    {
        // Folders that hold saves in recomp layouts. "memcards" before "mc0": it is the parent of the card folders.
        private static readonly string[] SaveDirs =
            { "memcards", "memcard", "memory_cards", "mc0", "user" };

        // Inside a "user" folder (shadPS4 style) the real saves are usually one level down.
        private static readonly string[] SaveSubDirs =
            { "savedata", "saves", "save", "savegames", "savegame", "save_data" };

        private static readonly string[] ConfigExts = { ".ini", ".toml", ".cfg", ".conf", ".yaml", ".yml" };

        // Config-looking files that belong to tools around the game, not to the game itself.
        private static readonly HashSet<string> NotGameConfig = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "reshade", "reshadepreset", "reshadepresets", "desktop", "dxvk", "d3d8", "d3d9", "d3d11", "dxgi",
            "steam_appid", "nvngx", "optiscaler", "fakenvapi", "unins000", "version", "autorun"
        };

        // Folders the game exe often lives in; the real game root is then the parent.
        private static readonly HashSet<string> ExeSubDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "binaries", "win64", "x64", "release"
        };

        /// <summary>
        /// Candidate game root folders: the install directory, then the folder of the play action's program
        /// (and its parent when that is a "bin"-like folder), for games whose install directory is empty.
        /// </summary>
        public static List<string> Roots(Game g, string install, Func<string, string> expand)
        {
            var roots = new List<string>();
            Action<string> add = p =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p)) return;
                    p = p.Trim().TrimEnd('\\', '/');
                    if (p.Length < 4 || !Directory.Exists(p)) return;   // never a drive root
                    if (!roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p);
                }
                catch { }
            };

            add(install);
            try
            {
                var action = g?.GameActions?.FirstOrDefault(a => a != null && a.IsPlayAction && a.Type == GameActionType.File);
                if (action != null && !string.IsNullOrWhiteSpace(action.Path))
                {
                    string p = expand != null ? expand(action.Path) : action.Path;
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        if (!Path.IsPathRooted(p))
                        {
                            string wd = expand != null ? expand(action.WorkingDir ?? "") : (action.WorkingDir ?? "");
                            if (string.IsNullOrWhiteSpace(wd)) wd = install ?? "";
                            if (!string.IsNullOrWhiteSpace(wd)) p = Path.Combine(wd, p);
                        }
                        if (Path.IsPathRooted(p))
                        {
                            string dir = Path.GetDirectoryName(p);
                            add(dir);
                            if (!string.IsNullOrEmpty(dir) && ExeSubDirs.Contains(Path.GetFileName(dir.TrimEnd('\\', '/'))))
                                add(Path.GetDirectoryName(dir.TrimEnd('\\', '/')));
                        }
                    }
                }
            }
            catch { }
            return roots;
        }

        /// <summary>Save and config folder for a recomp / port layout. Either result may be null.</summary>
        public static void Guess(IEnumerable<string> roots, out string save, out string cfg)
        {
            save = null; cfg = null;
            if (roots == null) return;
            foreach (var root in roots)
            {
                try
                {
                    if (save == null) save = FindSave(root);
                    if (cfg == null) cfg = FindConfig(root, save);
                    if (save != null && cfg != null) return;
                }
                catch { }
            }
        }

        private static string FindSave(string root)
        {
            foreach (var name in SaveDirs)
            {
                string d = Path.Combine(root, name);
                if (!Directory.Exists(d)) continue;
                if (string.Equals(name, "user", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sub in SaveSubDirs)
                    {
                        string s = Path.Combine(d, sub);
                        if (Directory.Exists(s)) return s;
                    }
                }
                return d;
            }
            return null;
        }

        private static string FindConfig(string root, string save)
        {
            if (HasGameConfigFile(root)) return root;
            if (save != null && HasGameConfigFile(save)) return save;
            // A "user" folder next to the game often keeps the config one level above its savedata folder.
            try
            {
                string user = Path.Combine(root, "user");
                if (Directory.Exists(user) && HasGameConfigFile(user)) return user;
            }
            catch { }
            return null;
        }

        /// <summary>True when the folder itself holds an .ini / .toml / .cfg ... that looks like the game's own settings.</summary>
        internal static bool HasGameConfigFile(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var ext = Path.GetExtension(f);
                    if (!ConfigExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    var stem = Path.GetFileNameWithoutExtension(f);
                    if (NotGameConfig.Contains(stem)) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
