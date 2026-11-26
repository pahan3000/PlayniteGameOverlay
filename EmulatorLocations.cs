using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Save / config folder lookup for emulated games (any game with an emulator action or a non-PC platform).
    /// Most emulators keep one save folder for all their games, so the links point at the emulator's own save and
    /// config folders: portable layout next to the emulator first, then the usual Documents / AppData folders.
    /// Only folders that exist are returned. Remove this file and the hooks in PlayniteGameOverlay.cs to undo it.
    /// </summary>
    internal static class EmulatorLocations
    {
        private sealed class Profile
        {
            public string[] Keys;     // matched (lower case) against emulator name, install folder and exe name
            public string[] Save;     // relative to the emulator folder, or @docs / @appdata / @localappdata prefixed
            public string[] Config;   // same; "." means the emulator folder itself (only when it holds a config file)
        }

        private static Profile P(string[] keys, string[] save, string[] cfg)
        {
            return new Profile { Keys = keys, Save = save, Config = cfg };
        }

        private static readonly Profile[] Profiles =
        {
            P(new[] { "pcsx2" },
              new[] { "memcards", @"@docs\PCSX2\memcards" },
              new[] { "inis", @"@docs\PCSX2\inis" }),
            P(new[] { "duckstation" },
              new[] { "memcards", @"@docs\DuckStation\memcards", @"@appdata\DuckStation\memcards" },
              new[] { ".", @"@docs\DuckStation", @"@appdata\DuckStation" }),
            P(new[] { "dolphin" },
              new[] { @"User\GC", @"User\Wii", @"@appdata\Dolphin Emulator\GC", @"@appdata\Dolphin Emulator\Wii", @"@docs\Dolphin Emulator\GC" },
              new[] { @"User\Config", @"@appdata\Dolphin Emulator\Config", @"@docs\Dolphin Emulator\Config" }),
            P(new[] { "rpcs3" },
              new[] { @"dev_hdd0\home\00000001\savedata", @"dev_hdd0\home" },
              new[] { "config", "." }),
            P(new[] { "ppsspp" },
              new[] { @"memstick\PSP\SAVEDATA", @"@docs\PPSSPP\PSP\SAVEDATA" },
              new[] { @"memstick\PSP\SYSTEM", @"@docs\PPSSPP\PSP\SYSTEM" }),
            P(new[] { "cemu" },
              new[] { @"mlc01\usr\save" },
              new[] { "." }),
            P(new[] { "ryujinx" },
              new[] { @"portable\bis\user\save", @"@appdata\Ryujinx\bis\user\save" },
              new[] { "portable", @"@appdata\Ryujinx" }),
            P(new[] { "yuzu", "suyu", "sudachi", "eden", "citron" },
              new[] { @"user\nand\user\save", @"@appdata\yuzu\nand\user\save", @"@appdata\suyu\nand\user\save",
                      @"@appdata\sudachi\nand\user\save", @"@appdata\eden\nand\user\save", @"@appdata\citron\nand\user\save" },
              new[] { @"user\config", @"@appdata\yuzu\config", @"@appdata\suyu\config",
                      @"@appdata\sudachi\config", @"@appdata\eden\config", @"@appdata\citron\config" }),
            P(new[] { "citra", "lime3ds", "azahar" },
              new[] { @"user\sdmc", @"@appdata\Citra\sdmc", @"@appdata\Lime3DS\sdmc", @"@appdata\Azahar\sdmc" },
              new[] { @"user\config", @"@appdata\Citra\config", @"@appdata\Lime3DS\config", @"@appdata\Azahar\config" }),
            P(new[] { "shadps4" },
              new[] { @"user\savedata" },
              new[] { "user", "." }),
            P(new[] { "retroarch" },
              new[] { "saves", "states" },
              new[] { "." }),
            P(new[] { "xenia" },
              new[] { "content" },
              new[] { "." }),
            P(new[] { "zsnes" },                      // also matches SuperZSNES and other ZSNES builds
              new[] { "saves", "sram", "states" },
              new[] { "." }),
            P(new[] { "mednafen" },
              new[] { "sav" },
              new[] { "." }),
        };

        // Tried for emulators without a profile above.
        private static readonly string[] GenericSave =
        {
            "memcards", "memcard", "memory_cards", "saves", "save", "savedata", "savegames", "sav", "sram", "battery",
            "saved", @"user\savedata", @"user\saves", "user"
        };
        private static readonly string[] GenericConfig =
        {
            "config", "configs", "inis", "settings", @"user\config", "cfg"
        };

        public static bool HasEmulatorAction(Game g)
        {
            try { return g?.GameActions != null && g.GameActions.Any(a => a != null && a.Type == GameActionType.Emulator); }
            catch { return false; }
        }

        /// <summary>Save and config folders of the emulator that runs the game. Either result may be null.</summary>
        public static void Resolve(Game g, IPlayniteAPI api, out string save, out string cfg)
        {
            save = null; cfg = null;
            try
            {
                string emuName = null, emuDir = null, exeName = null;
                var action = g?.GameActions?.FirstOrDefault(a => a != null && a.Type == GameActionType.Emulator && a.IsPlayAction)
                          ?? g?.GameActions?.FirstOrDefault(a => a != null && a.Type == GameActionType.Emulator);
                if (action != null && action.EmulatorId != Guid.Empty)
                {
                    var emu = api.Database.Emulators.Get(action.EmulatorId);
                    if (emu != null)
                    {
                        emuName = emu.Name;
                        if (!string.IsNullOrWhiteSpace(emu.InstallDir))
                            emuDir = Expand(api, g, emu.InstallDir, null);
                    }
                }
                if (string.IsNullOrWhiteSpace(emuDir))
                {
                    // No emulator record (game launches the emulator through a plain file action): use that exe's folder.
                    var file = g?.GameActions?.FirstOrDefault(a => a != null && a.IsPlayAction && a.Type == GameActionType.File);
                    if (file != null && !string.IsNullOrWhiteSpace(file.Path))
                    {
                        string p = Expand(api, g, file.Path, null);
                        if (!string.IsNullOrWhiteSpace(p) && Path.IsPathRooted(p))
                        {
                            exeName = Path.GetFileNameWithoutExtension(p);
                            emuDir = Path.GetDirectoryName(p);
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(emuDir)) emuDir = emuDir.TrimEnd('\\', '/');
                bool haveDir = !string.IsNullOrWhiteSpace(emuDir) && emuDir.Length >= 4 && Directory.Exists(emuDir);

                // The Steam edition of RetroArch is often started through a Steam URL, so the emulator record has no folder.
                string nameKey = ((emuName ?? "") + " " + (exeName ?? "")).ToLowerInvariant();
                if (!haveDir && nameKey.Contains("retroarch"))
                {
                    string steamRa = SteamRetroArchDir();
                    if (steamRa != null) { emuDir = steamRa; haveDir = true; }
                }

                string ident = ((emuName ?? "") + " " + (haveDir ? Path.GetFileName(emuDir) : "") + " " + (exeName ?? "")).ToLowerInvariant();
                var profile = Profiles.FirstOrDefault(p => p.Keys.Any(k => ident.Contains(k)));

                if (haveDir && ident.Contains("retroarch"))
                    save = RetroArchConfiguredDir(emuDir, "savefile_directory");

                if (profile != null)
                {
                    if (save == null) save = FirstExisting(profile.Save, emuDir, haveDir, false);
                    cfg = FirstExisting(profile.Config, emuDir, haveDir, true);
                }
                if (save == null) save = FirstExisting(GenericSave, emuDir, haveDir, false);
                if (cfg == null) cfg = FirstExisting(GenericConfig, emuDir, haveDir, false);
                if (cfg == null && haveDir && RecompLocations.HasGameConfigFile(emuDir)) cfg = emuDir;
                // Unity based emulators (SuperZSNES ...) and emulators with an AppData / Documents folder named after them.
                if (save == null) save = UnityOrNamedFolder(emuDir, haveDir, emuName, exeName);
                // Emulators that write saves next to the ROM (ZSNES, snes9x without a Saves folder, ...).
                if (save == null) save = RomFolderWithSaves(g, api);
            }
            catch { }
        }

        // ---- RetroArch (Steam edition and others): saves/states folders can be set in retroarch.cfg ----

        private static string SteamRetroArchDir()
        {
            try
            {
                var libs = new List<string>();
                string steam = SteamInstantPlay.GetSteamPath();
                if (!string.IsNullOrWhiteSpace(steam))
                {
                    libs.Add(steam);
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                foreach (var lib in libs)
                {
                    string dir = Path.Combine(lib, "steamapps", "common", "RetroArch");
                    if (Directory.Exists(dir)) return dir;
                }
            }
            catch { }
            return null;
        }

        private static string RetroArchConfiguredDir(string emuDir, string key)
        {
            try
            {
                string cfg = Path.Combine(emuDir, "retroarch.cfg");
                if (!File.Exists(cfg)) return null;
                foreach (var line in File.ReadLines(cfg))
                {
                    var m = Regex.Match(line, "^\\s*" + key + "\\s*=\\s*\"?([^\"]*)\"?\\s*$");
                    if (!m.Success) continue;
                    string v = m.Groups[1].Value.Trim();
                    if (v.Length == 0 || v.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
                    if (v[0] == ':') v = Path.Combine(emuDir, v.TrimStart(':', '\\', '/'));
                    if (Path.IsPathRooted(v) && Directory.Exists(v)) return v;
                    return null;
                }
            }
            catch { }
            return null;
        }

        // ---- Unity builds keep their data in LocalLow\<company>\<product> (named in <Game>_Data\app.info) ----

        private static string UnityOrNamedFolder(string emuDir, bool haveDir, string emuName, string exeName)
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string lowLow = Path.Combine(profile, "AppData", "LocalLow");
                if (haveDir)
                {
                    foreach (var data in Directory.EnumerateDirectories(emuDir, "*_Data"))
                    {
                        string info = Path.Combine(data, "app.info");
                        if (!File.Exists(info)) continue;
                        var lines = File.ReadAllLines(info);
                        if (lines.Length >= 2 && lines[0].Trim().Length > 0 && lines[1].Trim().Length > 0)
                        {
                            string p = Path.Combine(lowLow, lines[0].Trim(), lines[1].Trim());
                            if (Directory.Exists(p)) return p;
                        }
                    }
                }

                // Folder named after the emulator: Documents, Roaming, Local, LocalLow (also one level inside LocalLow\<company>).
                var keys = new List<string>();
                Action<string> add = n =>
                {
                    if (string.IsNullOrWhiteSpace(n)) return;
                    n = Regex.Replace(n, @"[-_ .]*v?\d+(\.\d+)+.*$", "");
                    string k = new string(n.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (k.Length >= 5 && !keys.Contains(k)) keys.Add(k);
                };
                add(emuName);
                add(exeName);
                if (haveDir) add(Path.GetFileName(emuDir));
                if (keys.Count == 0) return null;

                var roots = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    lowLow
                };
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.EnumerateDirectories(root))
                    {
                        string k = new string(Path.GetFileName(d).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                        if (keys.Contains(k)) return d;
                    }
                }
                if (Directory.Exists(lowLow))
                    foreach (var company in Directory.EnumerateDirectories(lowLow))
                        foreach (var d in Directory.EnumerateDirectories(company))
                        {
                            string k = new string(Path.GetFileName(d).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                            if (keys.Contains(k)) return d;
                        }
            }
            catch { }
            return null;
        }

        private static readonly string[] SaveExts = { ".srm", ".sav", ".zst" };

        private static string RomFolderWithSaves(Game g, IPlayniteAPI api)
        {
            try
            {
                if (g?.Roms == null) return null;
                foreach (var rom in g.Roms)
                {
                    if (rom == null || string.IsNullOrWhiteSpace(rom.Path)) continue;
                    string p = Expand(api, g, rom.Path, null);
                    if (string.IsNullOrWhiteSpace(p) || !Path.IsPathRooted(p)) continue;
                    string dir = Path.GetDirectoryName(p);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                        if (SaveExts.Contains(ext) ||
                            (ext.Length == 4 && (ext.StartsWith(".zs") || ext.StartsWith(".st")) && char.IsDigit(ext[3])))
                            return dir;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Expand(IPlayniteAPI api, Game g, string value, string unused)
        {
            try { return api.ExpandGameVariables(g, value); }
            catch { return value; }
        }

        private static string FirstExisting(string[] candidates, string emuDir, bool haveDir, bool dotNeedsConfigFile)
        {
            foreach (var c in candidates)
            {
                try
                {
                    string path = null;
                    if (c == ".")
                    {
                        if (!haveDir) continue;
                        if (dotNeedsConfigFile && !RecompLocations.HasGameConfigFile(emuDir)) continue;
                        path = emuDir;
                    }
                    else if (c.StartsWith("@docs\\"))
                        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), c.Substring(6));
                    else if (c.StartsWith("@appdata\\"))
                        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), c.Substring(9));
                    else if (c.StartsWith("@localappdata\\"))
                        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), c.Substring(14));
                    else
                    {
                        if (!haveDir) continue;
                        path = Path.Combine(emuDir, c);
                    }
                    if (path != null && Directory.Exists(path)) return path;
                }
                catch { }
            }
            return null;
        }
    }
}
