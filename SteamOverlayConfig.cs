using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Best-effort Steam overlay control via localconfig.vdf.
    /// Steam model: global EnableGameOverlay must be ON for any per-game overlay.
    /// Per-game: apps/{appid}/OverlayAppEnable = 0|1
    /// So "disable by default, keep for some games" = global ON + OverlayAppEnable 0 for all
    /// except an allowlist of AppIDs set to 1.
    /// Changes may require Steam restart if the client overwrites the file while running.
    /// </summary>
    internal static class SteamOverlayConfig
    {
        public static string GetSteamPath()
        {
            return SteamInstantPlay.GetSteamPath();
        }

        /// <summary>SteamID3 folder name under userdata (e.g. 12345678).</summary>
        public static string GetActiveUserId3(string steamPath)
        {
            if (string.IsNullOrEmpty(steamPath)) return null;
            var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsers))
            {
                // Fallback: first numeric userdata folder
                var ud = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(ud)) return null;
                return Directory.GetDirectories(ud)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(n => n.All(char.IsDigit));
            }

            try
            {
                var text = File.ReadAllText(loginUsers);
                // Prefer MostRecent "1"
                var blocks = Regex.Matches(text, "\"(\\d{5,})\"\\s*\\{([^}]*)\\}", RegexOptions.Singleline);
                string fallback = null;
                foreach (Match b in blocks)
                {
                    var id64 = b.Groups[1].Value;
                    var body = b.Groups[2].Value;
                    fallback = fallback ?? SteamId64ToId3(id64);
                    if (Regex.IsMatch(body, "\"MostRecent\"\\s*\"1\""))
                        return SteamId64ToId3(id64);
                }
                return fallback;
            }
            catch
            {
                return null;
            }
        }

        private static string SteamId64ToId3(string id64)
        {
            // SteamID3 account id = steamID64 - 76561197960265728
            if (!ulong.TryParse(id64, out var u)) return id64;
            const ulong baseId = 76561197960265728UL;
            if (u < baseId) return id64;
            return (u - baseId).ToString();
        }

        public static string GetLocalConfigPath(string steamPath = null)
        {
            steamPath = steamPath ?? GetSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return null;
            var user = GetActiveUserId3(steamPath);
            if (string.IsNullOrEmpty(user)) return null;
            var path = Path.Combine(steamPath, "userdata", user, "config", "localconfig.vdf");
            return File.Exists(path) ? path : path; // return even if missing so caller can report
        }

        /// <summary>
        /// Apply allowlist policy:
        /// - Global EnableGameOverlay = 1 (required)
        /// - OverlayAppEnable = 1 for allowlist AppIDs
        /// - OverlayAppEnable = 0 for all other Steam AppIDs passed in
        /// </summary>
        public static bool ApplyAllowlistPolicy(
            IEnumerable<string> allSteamAppIds,
            IEnumerable<string> allowlistAppIds,
            out string message)
        {
            message = null;
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                message = "Steam path not found.";
                return false;
            }

            var localConfig = GetLocalConfigPath(steamPath);
            if (string.IsNullOrEmpty(localConfig) || !File.Exists(localConfig))
            {
                message = "localconfig.vdf not found (is a Steam user logged in once?).";
                return false;
            }

            string text;
            try
            {
                // Backup
                var bak = localConfig + ".playnite-overlay.bak";
                try { File.Copy(localConfig, bak, overwrite: true); } catch { }

                text = File.ReadAllText(localConfig, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                message = "Could not read localconfig.vdf: " + ex.Message;
                return false;
            }

            var allow = new HashSet<string>(
                (allowlistAppIds ?? Enumerable.Empty<string>())
                    .Select(s => (s ?? "").Trim())
                    .Where(s => s.Length > 0 && s.All(char.IsDigit)),
                StringComparer.Ordinal);

            var all = new HashSet<string>(
                (allSteamAppIds ?? Enumerable.Empty<string>())
                    .Select(s => (s ?? "").Trim())
                    .Where(s => s.Length > 0 && s.All(char.IsDigit)),
                StringComparer.Ordinal);

            foreach (var a in allow) all.Add(a);

            // Global must be ON so per-game allowlist can enable overlay
            text = EnsureSystemEnableGameOverlay(text, enabled: true);

            foreach (var appId in all)
            {
                bool enable = allow.Contains(appId);
                text = EnsureAppOverlayEnable(text, appId, enable);
            }

            try
            {
                File.WriteAllText(localConfig, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                message = "Could not write localconfig.vdf (is Steam locking it?): " + ex.Message;
                return false;
            }

            message = allow.Count == 0
                ? "Overlay disabled for all managed Steam games (global still on; per-game off). Restart Steam if it was open."
                : $"Overlay allowlist applied ({allow.Count} enabled, rest disabled). Restart Steam if it was open.";
            return true;
        }

        private static string EnsureSystemEnableGameOverlay(string vdf, bool enabled)
        {
            var val = enabled ? "1" : "0";
            // "EnableGameOverlay" "0/1" anywhere under system — replace existing
            if (Regex.IsMatch(vdf, "\"EnableGameOverlay\"\\s*\"[01]\""))
            {
                return Regex.Replace(vdf, "\"EnableGameOverlay\"\\s*\"[01]\"",
                    "\"EnableGameOverlay\"\t\t\"" + val + "\"");
            }

            // Insert under "system" { ... }
            var m = Regex.Match(vdf, "\"system\"\\s*\\{", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int insertAt = m.Index + m.Length;
                return vdf.Insert(insertAt, "\n\t\t\"EnableGameOverlay\"\t\t\"" + val + "\"");
            }

            // Last resort: append near UserLocalConfigStore
            return vdf + "\n\"EnableGameOverlay\"\t\t\"" + val + "\"\n";
        }

        private static string EnsureAppOverlayEnable(string vdf, string appId, bool enable)
        {
            var val = enable ? "1" : "0";
            // Existing app block with OverlayAppEnable
            var appHeader = "\"" + appId + "\"";
            var appBlockPattern = new Regex(
                "\"" + Regex.Escape(appId) + "\"\\s*\\{([^{}]*(?:\\{[^{}]*\\}[^{}]*)*)\\}",
                RegexOptions.Singleline);

            var m = appBlockPattern.Match(vdf);
            if (m.Success)
            {
                var inner = m.Groups[1].Value;
                if (Regex.IsMatch(inner, "\"OverlayAppEnable\"\\s*\"[01]\""))
                {
                    var newInner = Regex.Replace(inner, "\"OverlayAppEnable\"\\s*\"[01]\"",
                        "\"OverlayAppEnable\"\t\t\"" + val + "\"");
                    return vdf.Substring(0, m.Groups[1].Index) + newInner +
                           vdf.Substring(m.Groups[1].Index + m.Groups[1].Length);
                }

                // Insert key into existing app block
                var insert = "\n\t\t\t\"OverlayAppEnable\"\t\t\"" + val + "\"";
                return vdf.Substring(0, m.Groups[1].Index) + insert + inner +
                       vdf.Substring(m.Groups[1].Index + m.Groups[1].Length);
            }

            // No app block — try insert into "apps" { }
            var appsMatch = Regex.Match(vdf, "\"apps\"\\s*\\{", RegexOptions.IgnoreCase);
            if (appsMatch.Success)
            {
                int at = appsMatch.Index + appsMatch.Length;
                var block = "\n\t\t\"" + appId + "\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"" + val + "\"\n\t\t}";
                return vdf.Insert(at, block);
            }

            return vdf;
        }

        public static IEnumerable<string> ParseAllowlist(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) yield break;
            foreach (var part in Regex.Split(raw, @"[\s,;]+"))
            {
                var t = part.Trim();
                if (t.Length > 0 && t.All(char.IsDigit))
                    yield return t;
            }
        }

        public static List<string> CollectSteamAppIdsFromPlaynite(IEnumerable<Game> games)
        {
            var list = new List<string>();
            if (games == null) return list;
            foreach (var g in games)
            {
                if (!SteamInstantPlay.IsSteamGame(g)) continue;
                if (string.IsNullOrEmpty(g.GameId) || !g.GameId.All(char.IsDigit)) continue;
                list.Add(g.GameId);
            }
            return list.Distinct().ToList();
        }
    }
}
