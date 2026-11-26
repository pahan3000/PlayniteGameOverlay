using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Bridge into pahan3000 FPS Limiter fork (cd5ffd73… / use-global).
    /// </summary>
    public static class FpsLimiterBridge
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        public static readonly Guid PluginId = Guid.Parse("08c6a58f-461c-4629-850c-d07a820e26c1");
        public static readonly Guid PluginIdLegacy = Guid.Parse("cd5ffd73-3b1a-45d7-86b4-9183f1d858f5");
        public static readonly Guid PluginIdAlt = Guid.Parse("4b308964-9a0d-4775-b7c2-78b92af4d7b6");

        private static object _service;
        private static Type _serviceType;
        private static bool _resolved;
        private static INotifyPropertyChanged _observedSettings;
        private static PropertyChangedEventHandler _settingsChangedHandler;

        /// <summary>Raised when the shared FPS Limiter Scanline target changes in either UI.</summary>
        public static event Action<int> ScanlineTargetChanged;

        private static void ObserveSettingsNotifications(object service, Type serviceType)
        {
            try
            {
                var settings = serviceType.GetProperty("Settings")?.GetValue(service);
                if (!(settings is INotifyPropertyChanged notifier) || ReferenceEquals(notifier, _observedSettings))
                    return;

                if (_observedSettings != null && _settingsChangedHandler != null)
                    _observedSettings.PropertyChanged -= _settingsChangedHandler;

                _observedSettings = notifier;
                _settingsChangedHandler = (sender, args) =>
                {
                    if (args?.PropertyName != "RtssScanlineSyncTarget") return;
                    try
                    {
                        var property = settings.GetType().GetProperty("RtssScanlineSyncTarget");
                        if (property != null)
                            ScanlineTargetChanged?.Invoke(Convert.ToInt32(property.GetValue(settings)));
                    }
                    catch (Exception ex) { Logger.Error($"Scanline target notification failed: {ex.Message}"); }
                };
                notifier.PropertyChanged += _settingsChangedHandler;
            }
            catch (Exception ex) { Logger.Error($"Could not subscribe to FPS Limiter settings: {ex.Message}"); }
        }

        public static bool IsAvailable(IPlayniteAPI api) => GetService(api) != null;

        public static bool IsFixedRefreshDisplay(IPlayniteAPI api)
        {
            var service = GetService(api);
            if (service == null) return false;
            try
            {
                var settings = _serviceType.GetProperty("Settings")?.GetValue(service);
                var property = settings?.GetType().GetProperty("FixedRefreshDisplayMode");
                return property != null && Convert.ToBoolean(property.GetValue(settings));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Display mode lookup failed: {ex.Message}");
                return false;
            }
        }


        /// <summary>Read the FPS Limiter extension's RTSS SyncScanline0 target.</summary>
        public static int? GetRtssScanlineSyncTarget(IPlayniteAPI api)
        {
            var svc = GetService(api);
            if (svc == null) return null;
            try
            {
                var settings = _serviceType.GetProperty("Settings")?.GetValue(svc);
                var prop = settings?.GetType().GetProperty("RtssScanlineSyncTarget");
                if (prop == null) return null;
                return Convert.ToInt32(prop.GetValue(settings));
            }
            catch (Exception ex)
            {
                Logger.Error($"GetRtssScanlineSyncTarget failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Set the FPS Limiter extension's RTSS SyncScanline0 target and reapply the running game.</summary>
        public static bool SetRtssScanlineSyncTarget(IPlayniteAPI api, Game game, int value)
        {
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                value = Math.Max(-500, Math.Min(500, value));

                // Preferred API owns persistence and live re-application in FPS Limiter.
                // This avoids writing RTSS independently and leaving its active-session state stale.
                var sharedSetter = _serviceType.GetMethod("SetRtssScanlineSyncTargetPublic", new[] { typeof(Game), typeof(int) });
                if (sharedSetter != null)
                {
                    var result = sharedSetter.Invoke(svc, new object[] { game, value });
                    bool ok = !(result is bool success) || success;
                    Logger.Info($"SetRtssScanlineSyncTarget({value}) via shared FPS Limiter API: {ok}");
                    return ok;
                }

                // Compatibility fallback for older FPS Limiter builds. It still changes the one
                // shared settings object; no independent RTSS profile write is performed here.
                var settings = _serviceType.GetProperty("Settings")?.GetValue(svc);
                var prop = settings?.GetType().GetProperty("RtssScanlineSyncTarget");
                if (prop == null || !prop.CanWrite) return false;
                prop.SetValue(settings, value);
                _serviceType.GetMethod("SaveSettingsPublic")?.Invoke(svc, null);
                if (game != null)
                    _serviceType.GetMethod("ReapplyIfRunningPublic")?.Invoke(svc, new object[] { game });
                Logger.Info($"SetRtssScanlineSyncTarget({value}) saved through legacy FPS Limiter bridge");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetRtssScanlineSyncTarget failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// FPS presets from the limiter (primary monitor). Prefer the game overload when a
        /// game is known so per-monitor matched/refresh presets are used.
        /// </summary>
        public static IList<double> GetPresets(IPlayniteAPI api) => GetPresets(api, null);

        public static IList<double> GetPresets(IPlayniteAPI api, Game game)
        {
            var svc = GetService(api);
            if (svc == null)
                return new List<double> { 30, 35, 40, 45, 50, 59.9, 60, 120 };

            try
            {
                // LimiterService has GetPresets() and GetPresets(Game) — plain GetMethod("GetPresets")
                // throws AmbiguousMatchException and we used to fall back to 30/60/120 forever.
                System.Collections.IEnumerable result = null;
                if (game != null)
                {
                    var withGame = _serviceType.GetMethod("GetPresets", new[] { typeof(Game) })
                        ?? _serviceType.GetMethods().FirstOrDefault(m =>
                            m.Name == "GetPresets" && m.GetParameters().Length == 1);
                    if (withGame != null)
                        result = withGame.Invoke(svc, new object[] { game }) as System.Collections.IEnumerable;
                }

                if (result == null)
                {
                    var noArgs = _serviceType.GetMethod("GetPresets", Type.EmptyTypes)
                        ?? _serviceType.GetMethods().FirstOrDefault(m =>
                            m.Name == "GetPresets" && m.GetParameters().Length == 0);
                    if (noArgs != null)
                        result = noArgs.Invoke(svc, null) as System.Collections.IEnumerable;
                }

                if (result == null)
                    return new List<double> { 30, 60, 120 };
                return result.Cast<object>().Select(o => Convert.ToDouble(o)).ToList();
            }
            catch (Exception ex)
            {
                Logger.Error($"GetPresets failed: {ex.Message}");
                return new List<double> { 30, 60, 120 };
            }
        }

        /// <summary>
        /// Effective FPS limit for a game (same rules as LimiterService.TryApplyForGame).
        /// Returns 0 when unlimited / disabled; null if bridge unavailable.
        /// </summary>
        public static double? GetGameLimit(IPlayniteAPI api, Game game)
        {
            if (game == null) return null;
            var svc = GetService(api);
            if (svc == null) return null;

            try
            {
                // Prefer ActiveSessions via settings is handled in SettingsSync;
                // here resolve from live objects in memory.
                var profile = _serviceType.GetMethod("GetGameProfile")?.Invoke(svc, new object[] { game });
                object modeLimit = null;
                if (profile != null)
                {
                    // profile.GetMode(CurrentMode) — CurrentMode is private; try Desktop/Fullscreen props
                    var currentMode = GetCurrentMode(svc);
                    var getMode = profile.GetType().GetMethod("GetMode");
                    if (getMode != null && currentMode != null)
                        modeLimit = getMode.Invoke(profile, new object[] { currentMode });
                    else
                    {
                        // Fallback: Desktop property
                        modeLimit = profile.GetType().GetProperty("Desktop")?.GetValue(profile);
                    }
                }

                bool hasGame = false;
                double gameLimit = 0;
                if (modeLimit != null)
                {
                    bool en = Convert.ToBoolean(modeLimit.GetType().GetProperty("Enabled")?.GetValue(modeLimit) ?? false);
                    gameLimit = Convert.ToDouble(modeLimit.GetType().GetProperty("FrameLimit")?.GetValue(modeLimit) ?? 0d);
                    // Enabled with zero is an explicit unlimited override, not Use global.
                    hasGame = en && gameLimit >= 0;
                }

                if (hasGame && gameLimit == 0)
                    return 0;

                // The limiter knows about Scanline, monitor and backend-specific caps.
                var effective = _serviceType.GetMethod("GetEffectiveLimit", new[] { typeof(Game) });
                if (effective != null)
                {
                    var result = effective.Invoke(svc, new object[] { game });
                    return result == null ? 0 : Convert.ToDouble(result);
                }
                if (hasGame)
                    return gameLimit;

                // Global fallback: GlobalFrameLimitForGame(game) or GlobalFrameLimit
                var forGame = _serviceType.GetMethod("GlobalFrameLimitForGame");
                if (forGame != null)
                {
                    // property not method - try properties first
                }
                var propForGame = _serviceType.GetProperty("GlobalFrameLimitForGame");
                // Method GlobalFrameLimitForGame(Game)
                var mForGame = _serviceType.GetMethod("GlobalFrameLimitForGame", new[] { typeof(Game) });
                if (mForGame != null)
                {
                    // Check IsGlobalLimitEnabledForGame
                    var enM = _serviceType.GetMethod("IsGlobalLimitEnabledForGame", new[] { typeof(Game) });
                    bool gen = enM != null && Convert.ToBoolean(enM.Invoke(svc, new object[] { game }));
                    double gl = Convert.ToDouble(mForGame.Invoke(svc, new object[] { game }));
                    if (gen && gl > 0) return gl;
                    return 0;
                }

                var enProp = _serviceType.GetProperty("IsGlobalLimitEnabled");
                var limProp = _serviceType.GetProperty("GlobalFrameLimit");
                if (enProp != null && limProp != null)
                {
                    bool gen = Convert.ToBoolean(enProp.GetValue(svc));
                    double gl = Convert.ToDouble(limProp.GetValue(svc));
                    if (gen && gl > 0) return gl;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"GetGameLimit failed: {ex.Message}");
                return null;
            }
        }

        private static object GetCurrentMode(object svc)
        {
            try
            {
                // CurrentMode is private property returning PlayniteUiMode enum
                var prop = _serviceType.GetProperty("CurrentMode",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                    return prop.GetValue(svc);

                // Fallback: Desktop = 0
                var enumType = _serviceType.Assembly.GetType("FPSLimiter.PlayniteUiMode");
                if (enumType != null)
                    return Enum.ToObject(enumType, 0);
            }
            catch { }
            return null;
        }

        public static bool SetGameLimit(IPlayniteAPI api, Game game, double frameLimit)
        {
            if (game == null) return false;
            return Invoke(api, "SetGameLimit", new object[] { new[] { game }, frameLimit });
        }

        /// <summary>
        /// Extension SetGameForcedRefreshRate: sets ForcedRefreshRateHz, FPS cap = Hz,
        /// writes SpecialK TargetFPS when on SK backend, then ReapplyIfRunning.
        /// </summary>
        public static bool SetGameForcedRefreshRate(IPlayniteAPI api, Game game, int hz)
        {
            if (game == null) return false;
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("SetGameForcedRefreshRate");
                if (method == null)
                {
                    Logger.Error("FPS Limiter does not support SetGameForcedRefreshRate; refusing to reinterpret Hz as FPS.");
                    return false;
                }
                method.Invoke(svc, new object[] { new[] { game }, hz });
                Logger.Info($"SetGameForcedRefreshRate({hz}) ok for {game.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetGameForcedRefreshRate failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        public static bool DisableGameLimit(IPlayniteAPI api, Game game)
        {
            if (game == null) return false;
            return Invoke(api, "DisableGameLimit", new object[] { new[] { game } });
        }

        public static bool SetGameSyncMode(IPlayniteAPI api, Game game, int syncMode)
        {
            if (game == null) return SetGlobalSyncMode(api, syncMode);
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                // Prefer exact signature: SetGameSyncMode(IEnumerable<Game>, FpsSyncMode)
                MethodInfo method = null;
                foreach (var m in _serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "SetGameSyncMode") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[1].ParameterType.IsEnum)
                    {
                        method = m;
                        break;
                    }
                }
                if (method == null) return false;
                var enumType = method.GetParameters()[1].ParameterType;
                // Clamp to valid FpsSyncMode: Async=0 … ScanlineSync=4
                if (syncMode < 0) syncMode = 0;
                if (syncMode > 5) syncMode = 0;
                method.Invoke(svc, new object[] { new[] { game }, Enum.ToObject(enumType, syncMode) });
                Logger.Info($"SetGameSyncMode({syncMode}) ok for {game.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetGameSyncMode failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Effective sync mode for a game (resolves UseGlobal → Desktop/Fullscreen global),
        /// including ScanlineSync = 4.
        /// </summary>
        public static int? GetEffectiveGameSyncMode(IPlayniteAPI api, Game game)
        {
            if (game == null) return null;
            var svc = GetService(api);
            if (svc == null) return null;
            try
            {
                var method = _serviceType.GetMethod("GetEffectiveGameSyncMode");
                if (method == null) return null;
                var result = method.Invoke(svc, new object[] { game });
                if (result == null) return null;
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Logger.Error($"GetEffectiveGameSyncMode failed: {ex.Message}");
                return null;
            }
        }

        public static bool SetGameReflexMarkers(IPlayniteAPI api, Game game, bool enabled)
        {
            int mode = enabled ? 0 : 1;
            if (game == null) return SetGlobalReflexMarkers(api, mode);
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("SetGameReflexMarkers");
                var enumType = method.GetParameters()[1].ParameterType;
                method.Invoke(svc, new object[] { new[] { game }, Enum.ToObject(enumType, mode) });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetGameReflexMarkers failed: {ex.Message}");
                return false;
            }
        }

        public static bool SetGlobalLimit(IPlayniteAPI api, double frameLimit)
        {
            return Invoke(api, "SetGlobalLimit", new object[] { frameLimit });
        }

        public static bool DisableGlobalLimit(IPlayniteAPI api)
        {
            return Invoke(api, "DisableGlobalLimit", null);
        }

        public static bool SetGlobalSyncMode(IPlayniteAPI api, int syncMode)
        {
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("SetGlobalSyncMode", new[] { _serviceType.Assembly.GetType("FPSLimiter.FpsSyncMode") })
                    ?? _serviceType.GetMethods().FirstOrDefault(m => m.Name == "SetGlobalSyncMode" && m.GetParameters().Length == 1);
                if (method == null) return false;
                var enumType = method.GetParameters()[0].ParameterType;
                method.Invoke(svc, new object[] { Enum.ToObject(enumType, syncMode) });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetGlobalSyncMode failed: {ex.Message}");
                return false;
            }
        }

        public static bool SetGlobalReflexMarkers(IPlayniteAPI api, int mode)
        {
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethods().FirstOrDefault(m =>
                    m.Name == "SetGlobalReflexMarkers" && m.GetParameters().Length == 1);
                if (method == null) return false;
                var enumType = method.GetParameters()[0].ParameterType;
                method.Invoke(svc, new object[] { Enum.ToObject(enumType, mode) });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetGlobalReflexMarkers failed: {ex.Message}");
                return false;
            }
        }

        public static bool ToggleVrr(IPlayniteAPI api)
        {
            return Invoke(api, "ToggleVrrRefreshRateEnabled", null);
        }

        public static bool? GetVrrEnabled(IPlayniteAPI api)
        {
            var svc = GetService(api);
            if (svc == null) return null;
            try
            {
                var prop = _serviceType.GetProperty("VrrRefreshRateEnabled");
                return (bool?)prop?.GetValue(svc);
            }
            catch { return null; }
        }

        /// <summary>True when the extension would use Special K for this game (or global if game null).</summary>
        public static bool ShouldUseSpecialKBackend(IPlayniteAPI api, Game game = null)
        {
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("ShouldUseSpecialKBackend");
                if (method == null) return false;
                object[] args = method.GetParameters().Length == 0 ? null : new object[] { game };
                return Convert.ToBoolean(method.Invoke(svc, args));
            }
            catch (Exception ex)
            {
                Logger.Error($"ShouldUseSpecialKBackend failed: {ex.Message}");
                return false;
            }
        }

        public static string GetEffectiveBackendLabel(IPlayniteAPI api, Game game = null)
        {
            var svc = GetService(api);
            if (svc == null) return "RTSS";
            try
            {
                var method = _serviceType.GetMethod("GetEffectiveBackendLabel");
                if (method != null)
                {
                    var result = method.Invoke(svc, new object[] { game }) as string;
                    if (!string.IsNullOrWhiteSpace(result)) return result;
                }
            }
            catch { }
            return ShouldUseSpecialKBackend(api, game) ? "Special K" : "RTSS";
        }

        /// <summary>
        /// Set per-game backend. preference matches FPSLimiter.GameBackendPreference:
        /// -1 UseGlobal, 0 Auto, 1 Rtss, 2 SpecialK
        /// </summary>
        public static bool SetGameBackendPreference(IPlayniteAPI api, Game game, int preference)
        {
            if (game == null) return SetDefaultBackend(api, preference == 2 ? 2 : 1);
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("SetGameBackendPreference");
                if (method == null) return false;
                var enumType = method.GetParameters()[1].ParameterType;
                method.Invoke(svc, new object[] { new[] { game }, Enum.ToObject(enumType, preference) });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetGameBackendPreference failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Set extension default backend. 0 Auto, 1 Rtss, 2 SpecialK.</summary>
        public static bool SetDefaultBackend(IPlayniteAPI api, int preference)
        {
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("SetDefaultBackend");
                if (method == null) return false;
                var enumType = method.GetParameters()[0].ParameterType;
                method.Invoke(svc, new object[] { Enum.ToObject(enumType, preference) });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetDefaultBackend failed: {ex.Message}");
                return false;
            }
        }

        public static bool ToggleGameBackend(IPlayniteAPI api, Game game)
        {
            if (game == null) return false;
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                var method = _serviceType.GetMethod("ToggleGameBackend");
                if (method == null) return false;
                method.Invoke(svc, new object[] { new[] { game } });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"ToggleGameBackend failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Supported primary-display Hz from extension RefreshRateManager (or empty).</summary>
        public static IList<double> GetSupportedRefreshRates(IPlayniteAPI api)
        {
            try
            {
                var svc = GetService(api);
                if (svc != null)
                {
                    var asm = _serviceType.Assembly;
                    var rrType = asm.GetType("FPSLimiter.RefreshRateManager");
                    var method = rrType?.GetMethod("GetSupportedRefreshRates",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        // Optional parameters still require an argument when invoked through reflection.
                        var parameters = method.GetParameters();
                        object[] arguments = parameters.Length == 0 ? null :
                            parameters.Select(p => p.HasDefaultValue ? p.DefaultValue :
                                (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();
                        var result = method.Invoke(null, arguments) as System.Collections.IEnumerable;
                        if (result != null)
                        {
                            var list = result.Cast<object>()
                                .Select(o => Convert.ToDouble(o))
                                .Where(d => d > 1)
                                .Distinct()
                                .OrderBy(d => d)
                                .ToList();
                            if (list.Count > 0) return list;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"GetSupportedRefreshRates failed: {ex.Message}");
            }
            return new List<double>();
        }

        public static double? GetEffectiveGameLimit(IPlayniteAPI api, Game game) => GetGameLimit(api, game);

        private static bool Invoke(IPlayniteAPI api, string methodName, object[] args)
        {
            var svc = GetService(api);
            if (svc == null) return false;
            try
            {
                MethodInfo method;
                if (args == null)
                    method = _serviceType.GetMethod(methodName, Type.EmptyTypes)
                        ?? _serviceType.GetMethods().FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0);
                else
                    method = _serviceType.GetMethods().FirstOrDefault(m =>
                    {
                        if (m.Name != methodName) return false;
                        var parameters = m.GetParameters();
                        if (parameters.Length != args.Length) return false;
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]))
                                return false;
                        }
                        return true;
                    });
                if (method == null)
                {
                    Logger.Error($"LimiterService.{methodName} not found");
                    return false;
                }
                method.Invoke(svc, args);
                Logger.Info($"FPS Limiter.{methodName} invoked successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"FPS Limiter.{methodName} failed: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
        }

        private static object GetService(IPlayniteAPI api)
        {
            // Do not permanently cache a failed lookup. Playnite may initialize
            // FPS Limiter after the overlay, so retry when the service is absent.
            if (_resolved && _service != null)
                return _service;
            try
            {
                if (api?.Addons?.Plugins == null)
                    return null;

                Plugin plugin = null;
                foreach (var p in api.Addons.Plugins)
                {
                    if (p.Id == PluginId || p.Id == PluginIdLegacy || p.Id == PluginIdAlt)
                    {
                        plugin = p;
                        break;
                    }
                    try
                    {
                        var t = p.GetType().FullName ?? "";
                        if (t.IndexOf("FPSLimiter", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            plugin = p;
                            break;
                        }
                    }
                    catch { }
                }

                if (plugin == null)
                {
                    Logger.Info("FPS Limiter plugin not loaded");
                    return null;
                }

                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                object service = null;
                foreach (var name in new[] { "limiterService", "LimiterService", "_limiterService" })
                {
                    var field = plugin.GetType().GetField(name, flags);
                    if (field != null)
                    {
                        service = field.GetValue(plugin);
                        if (service != null) break;
                    }
                    var prop = plugin.GetType().GetProperty(name, flags);
                    if (prop != null)
                    {
                        service = prop.GetValue(plugin);
                        if (service != null) break;
                    }
                }

                if (service == null)
                {
                    Logger.Error("FPS Limiter plugin found but limiterService is null");
                    return null;
                }

                _service = service;
                _serviceType = service.GetType();
                ObserveSettingsNotifications(_service, _serviceType);
                _resolved = true;
                Logger.Info($"Bridged to FPS Limiter ({_serviceType.FullName})");
                return _service;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to bridge FPS Limiter: {ex.Message}");
                _service = null;
                return null;
            }
        }

        public static void Reset()
        {
            _resolved = false;
            _service = null;
            _serviceType = null;
        }
    }
}
