using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Reads FPS Limiter config for pahan3000 fork (cd5ffd73… / use-global).
    /// Effective limit = per-game mode (Desktop/Fullscreen) if Enabled, else Global* for that mode.
    /// </summary>
    public static class FpsLimiterSettingsSync
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        public static readonly Guid FpsLimiterPluginId =
            Guid.Parse("08c6a58f-461c-4629-850c-d07a820e26c1");

        /// <summary>Id used before the extension was renamed to FPS Limiter 3000.</summary>
        public static readonly Guid FpsLimiterPluginIdLegacy =
            Guid.Parse("cd5ffd73-3b1a-45d7-86b4-9183f1d858f5");

        public static readonly Guid FpsLimiterPluginIdAlt =
            Guid.Parse("4b308964-9a0d-4775-b7c2-78b92af4d7b6");

        public sealed class Snapshot
        {
            public double FrameLimit { get; set; }
            public OverlayFpsSyncMode SyncMode { get; set; }
            public bool ReflexMarkersEnabled { get; set; }
            public bool Enabled { get; set; }
            public bool Found { get; set; }
            public bool FromGameProfile { get; set; }
            /// <summary>True when config/session indicates Special K for this game.</summary>
            public bool UseSpecialK { get; set; }
            /// <summary>0 Auto, 1 Rtss, 2 SpecialK</summary>
            public int DefaultBackend { get; set; } = 1;
            /// <summary>-1 UseGlobal, 0 Auto, 1 Rtss, 2 SpecialK</summary>
            public int GameBackend { get; set; } = -1;
        }

        public static string ResolveConfigPath(IPlayniteAPI api)
        {
            if (api?.Paths?.ExtensionsDataPath == null)
                return null;

            string root = api.Paths.ExtensionsDataPath;
            string[] candidates =
            {
                Path.Combine(root, FpsLimiterPluginId.ToString(), "config.json"),
                Path.Combine(root, "FPSLimiter_" + FpsLimiterPluginId.ToString(), "config.json"),
                Path.Combine(root, FpsLimiterPluginIdLegacy.ToString(), "config.json"),
                Path.Combine(root, "FPSLimiter_" + FpsLimiterPluginIdLegacy.ToString(), "config.json"),
                Path.Combine(root, FpsLimiterPluginIdAlt.ToString(), "config.json"),
                Path.Combine(root, "FPSLimiter_" + FpsLimiterPluginIdAlt.ToString(), "config.json"),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }

            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var name = Path.GetFileName(dir) ?? "";
                        if (name.IndexOf("FPSLimiter", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("cd5ffd73", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("08c6a58f", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("4b308964", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var cfg = Path.Combine(dir, "config.json");
                        if (File.Exists(cfg))
                            return cfg;
                    }
                }
            }
            catch { }

            return candidates[0];
        }

        public static string GetConfigPath(IPlayniteAPI api) => ResolveConfigPath(api);

        public static bool IsFpsLimiterInstalled(IPlayniteAPI api)
        {
            try
            {
                if (api?.Addons?.Plugins != null)
                {
                    foreach (var p in api.Addons.Plugins)
                    {
                        if (p.Id == FpsLimiterPluginId || p.Id == FpsLimiterPluginIdLegacy || p.Id == FpsLimiterPluginIdAlt)
                            return true;
                        try
                        {
                            var t = p.GetType().FullName ?? "";
                            if (t.IndexOf("FPSLimiter", StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            var path = ResolveConfigPath(api);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <summary>
        /// Effective FPS limit for the current Playnite UI mode, matching LimiterService.TryApplyForGame:
        ///   per-game Desktop/Fullscreen if Enabled (including explicit zero), else global.
        /// </summary>
        public static Snapshot Read(IPlayniteAPI api, Guid? gameId = null)
        {
            var snap = new Snapshot
            {
                Found = false,
                ReflexMarkersEnabled = true,
                SyncMode = OverlayFpsSyncMode.Async
            };

            var path = ResolveConfigPath(api);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Logger.Debug("FPS Limiter config not found");
                return snap;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root == null)
                    return snap;

                bool fullscreen = api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
                string modeKey = fullscreen ? "Fullscreen" : "Desktop";
                string globalKey = fullscreen ? "GlobalFullscreen" : "GlobalDesktop";

                // Global default backend: 0 Auto, 1 Rtss, 2 SpecialK (extension default is SpecialK)
                snap.DefaultBackend = ReadInt(root["DefaultBackend"], 2);
                bool preferSk = ReadBool(root["PreferSpecialKWhenAvailable"], true);

                JsonObject gameProfile = null;
                if (gameId.HasValue && gameId.Value != Guid.Empty)
                    gameProfile = FindByGameId(root["GameLimits"] as JsonArray, gameId.Value);

                // Per-game Backend: -1 UseGlobal, 0 Auto, 1 Rtss, 2 SpecialK
                if (gameProfile != null)
                    snap.GameBackend = ReadInt(gameProfile["Backend"], -1);

                // Resolve effective Special K from config (same rules as LimiterService)
                snap.UseSpecialK = ResolveUseSpecialK(snap.GameBackend, snap.DefaultBackend, preferSk);

                // A saved explicit zero must beat both the global and an older active-session value.
                var configuredMode = gameProfile?[modeKey] as JsonObject;
                bool explicitZero = configuredMode != null && ReadBool(configuredMode["Enabled"], false) &&
                    ReadDouble(configuredMode["FrameLimit"] ?? configuredMode["FrameLimitText"]) == 0;

                // Active session (live applied values) — also carries LimiterBackendKind 0=Rtss 1=SpecialK
                if (!explicitZero && gameId.HasValue && gameId.Value != Guid.Empty)
                {
                    var session = FindByGameId(root["ActiveSessions"] as JsonArray, gameId.Value);
                    if (session != null)
                    {
                        // Session backend wins for UI (what was actually applied)
                        int sessionBackend = ReadInt(session["Backend"], -1);
                        if (sessionBackend == 1)
                            snap.UseSpecialK = true;
                        else if (sessionBackend == 0)
                            snap.UseSpecialK = false;

                        double applied = ReadDouble(session["AppliedLimit"]);
                        if (applied > 0)
                        {
                            snap.Found = true;
                            snap.Enabled = true;
                            snap.FrameLimit = applied;
                            snap.FromGameProfile = true;
                            int sync = ReadInt(session["AppliedSyncMode"], 0);
                            if (sync < 0) sync = 0;
                            if (sync > 5) sync = 0; // allow ScanlineSync = 4
                            snap.SyncMode = (OverlayFpsSyncMode)sync;
                            snap.ReflexMarkersEnabled = !ReadBool(session["AppliedReflexMarkersDisabled"], false);
                            Logger.Info(
                                $"FPS Limiter ActiveSession AppliedLimit={applied}, UseSpecialK={snap.UseSpecialK}, sync={sync}");
                            return snap;
                        }
                    }
                }

                JsonObject modeNode = null;
                bool fromGame = false;

                // Per-game profile for current mode
                if (gameProfile != null)
                {
                    modeNode = gameProfile[modeKey] as JsonObject;
                    if (modeNode != null && ReadBool(modeNode["Enabled"], false))
                    {
                        fromGame = true;
                    }
                    else
                    {
                        modeNode = null; // fall through to global
                    }
                }

                // Global for current mode (Windows-game globals)
                if (modeNode == null)
                {
                    modeNode = root[globalKey] as JsonObject;
                }

                if (modeNode == null)
                {
                    snap.Found = true; // still report backend even without a limit
                    return snap;
                }

                double limit = ReadDouble(modeNode["FrameLimit"]);
                if (modeNode["FrameLimit"] == null)
                    limit = ReadDouble(modeNode["FrameLimitText"]);

                bool enabled = ReadBool(modeNode["Enabled"], false);

                snap.Found = true;
                snap.FromGameProfile = fromGame;
                snap.Enabled = enabled;
                snap.FrameLimit = enabled ? limit : 0;

                int syncMode = ReadInt(modeNode["SyncMode"], 0);
                // UseGlobal (-1) → resolve from global Desktop/Fullscreen so ScanlineSync shows correctly
                if (syncMode < 0)
                {
                    var globalNode = root[globalKey] as JsonObject;
                    if (globalNode != null)
                        syncMode = ReadInt(globalNode["SyncMode"], 0);
                    if (syncMode < 0) syncMode = 0;
                }
                if (syncMode > 5) syncMode = 0; // allow ScanlineSync = 4
                snap.SyncMode = (OverlayFpsSyncMode)syncMode;

                int reflex = ReadInt(modeNode["ReflexMarkers"], 0);
                // UseGlobal (-1) or Enabled (0) → markers on; Disabled (1) → off
                snap.ReflexMarkersEnabled = reflex != 1;

                Logger.Info(
                    $"FPS Limiter Read: limit={snap.FrameLimit}, enabled={enabled}, fromGame={fromGame}, " +
                    $"mode={modeKey}, UseSpecialK={snap.UseSpecialK}, gameBackend={snap.GameBackend}, defaultBackend={snap.DefaultBackend}");
                return snap;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed reading FPS Limiter config: {ex.Message}");
                return snap;
            }
        }

        public static bool Write(IPlayniteAPI api, double frameLimit, OverlayFpsSyncMode syncMode, bool reflexMarkersEnabled)
        {
            if (api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen)
                return false;

            var path = ResolveConfigPath(api);
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                JsonObject root;
                if (File.Exists(path))
                    root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                else
                    root = new JsonObject();

                bool enabled = frameLimit > 0;
                var mode = root["GlobalDesktop"] as JsonObject ?? new JsonObject();
                mode["Enabled"] = enabled;
                mode["FrameLimit"] = frameLimit;
                mode["FrameLimitText"] = frameLimit.ToString("0.###", CultureInfo.InvariantCulture);
                mode["SyncMode"] = (int)syncMode;
                mode["ReflexMarkers"] = reflexMarkersEnabled ? 0 : 1;
                root["GlobalDesktop"] = mode;

                File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed writing FPS Limiter config: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Mirror LimiterService.ShouldUseSpecialKBackend config resolution (without SK path check).
        /// gameBackend: -1 UseGlobal, 0 Auto, 1 Rtss, 2 SpecialK
        /// defaultBackend: 0 Auto, 1 Rtss, 2 SpecialK
        /// </summary>
        private static bool ResolveUseSpecialK(int gameBackend, int defaultBackend, bool preferSkWhenAvailable)
        {
            int effective = gameBackend >= 0 ? gameBackend : defaultBackend;
            // GameBackendPreference and LimiterBackendPreference share values for Auto/Rtss/SpecialK
            if (effective == 1) // Rtss
                return false;
            if (effective == 2) // SpecialK
                return true;
            // Auto
            return preferSkWhenAvailable;
        }

        private static JsonObject FindByGameId(JsonArray arr, Guid gameId)
        {
            if (arr == null) return null;
            string idD = gameId.ToString("D");
            string idN = gameId.ToString("N");
            foreach (var item in arr)
            {
                var obj = item as JsonObject;
                if (obj == null) continue;
                var raw = obj["GameId"]?.ToString() ?? obj["gameId"]?.ToString() ?? obj["Id"]?.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                raw = raw.Trim().Trim('{', '}');
                if (raw.Equals(idD, StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals(idN, StringComparison.OrdinalIgnoreCase) ||
                    (Guid.TryParse(raw, out var g) && g == gameId))
                    return obj;
            }
            return null;
        }

        private static bool ReadBool(JsonNode node, bool defaultValue)
        {
            if (node == null) return defaultValue;
            try
            {
                if (node is JsonValue jv)
                {
                    if (jv.TryGetValue<bool>(out var b)) return b;
                    if (jv.TryGetValue<int>(out var i)) return i != 0;
                    if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out var pb)) return pb;
                }
            }
            catch { }
            return defaultValue;
        }

        private static int ReadInt(JsonNode node, int defaultValue)
        {
            if (node == null) return defaultValue;
            try
            {
                if (node is JsonValue jv)
                {
                    if (jv.TryGetValue<int>(out var i)) return i;
                    if (jv.TryGetValue<long>(out var l)) return (int)l;
                    if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var pi)) return pi;
                }
            }
            catch { }
            return defaultValue;
        }

        private static double ReadDouble(JsonNode node)
        {
            if (node == null) return 0;
            try
            {
                if (node is JsonValue jv)
                {
                    if (jv.TryGetValue<double>(out var d)) return d;
                    if (jv.TryGetValue<float>(out var f)) return f;
                    if (jv.TryGetValue<int>(out var i)) return i;
                    if (jv.TryGetValue<long>(out var l)) return l;
                    if (jv.TryGetValue<string>(out var s) &&
                        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                }
                var text = node.ToString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    double.TryParse(text.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var fromText))
                    return fromText;
            }
            catch { }
            return 0;
        }
    }
}
