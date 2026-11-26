using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Lets automatic work on a new game (metadata, links, platform) wait until Playnite's Edit Game window is
    /// closed. A game dropped onto Playnite is already in the database while that window is still open.
    /// </summary>
    internal static class GameEditorWatch
    {
        private static string _lastBlocker;

        private static bool IsEditorOpen()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return false;
                foreach (System.Windows.Window w in app.Windows)
                {
                    if (w == null || !w.IsVisible) continue;
                    string type = w.GetType().Name ?? string.Empty;
                    string vm = w.DataContext != null ? (w.DataContext.GetType().Name ?? string.Empty) : string.Empty;
                    if (type.IndexOf("GameEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        vm.IndexOf("GameEdit", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!string.Equals(_lastBlocker, type + "/" + vm, StringComparison.Ordinal))
                        {
                            _lastBlocker = type + "/" + vm;
                            try { Breadcrumb.Write("GameEditorWatch: waiting, editor window " + _lastBlocker); } catch { }
                        }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Puts a better title into the Edit Game window that opens for a freshly dropped exe, while the window is
        /// still open (Playnite names the game after the exe's folder, e.g. "Win64"). Only touches the field when
        /// it still holds the original name, so a title the user typed is never overwritten.
        /// </summary>
        public static void PrefillName(Guid gameId, string currentName, string newName)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current != null
                    ? System.Windows.Application.Current.Dispatcher : null;
                if (dispatcher == null || string.IsNullOrWhiteSpace(newName)) return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    var started = DateTime.UtcNow;
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (s, e) =>
                    {
                        try
                        {
                            if ((DateTime.UtcNow - started).TotalSeconds > 12)
                            {
                                timer.Stop();
                                try { Breadcrumb.Write("Editor: no Edit Game window found to pre-fill '" + newName + "'"); } catch { }
                                return;
                            }
                            var app = System.Windows.Application.Current;
                            if (app == null) { timer.Stop(); return; }
                            foreach (System.Windows.Window w in app.Windows)
                            {
                                if (w == null || !w.IsVisible || w.DataContext == null) continue;
                                var dc = w.DataContext;
                                var dcType = dc.GetType();
                                if ((dcType.Name ?? string.Empty).IndexOf("GameEdit", StringComparison.OrdinalIgnoreCase) < 0) continue;

                                Game editing = null;
                                foreach (var propName in new[] { "EditingGame", "Game", "EditedGame" })
                                {
                                    var pi = dcType.GetProperty(propName);
                                    if (pi == null) continue;
                                    try { editing = pi.GetValue(dc) as Game; } catch { }
                                    if (editing != null) break;
                                }
                                timer.Stop();
                                if (editing == null)
                                {
                                    try
                                    {
                                        Breadcrumb.Write("Editor: " + dcType.Name + " has no game property I know; members: " +
                                            string.Join(",", dcType.GetProperties().Select(pp => pp.Name).Take(40)));
                                    }
                                    catch { }
                                    return;
                                }
                                if (string.Equals(editing.Name, currentName, StringComparison.Ordinal))
                                {
                                    editing.Name = newName;
                                    try { Breadcrumb.Write("Editor: name box '" + currentName + "' -> '" + newName + "'"); } catch { }
                                }
                                else
                                {
                                    try { Breadcrumb.Write("Editor: name box already '" + editing.Name + "', left alone"); } catch { }
                                }
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            timer.Stop();
                            try { Breadcrumb.Write("Editor: pre-fill failed: " + ex.Message); } catch { }
                        }
                    };
                    timer.Start();
                }));
            }
            catch { }
        }

        /// <summary>
        /// Runs the action on the UI thread once no Edit Game window is open (and at least ~1.2 s have passed, so
        /// the window has had time to appear after the add event). Gives up waiting after 30 minutes.
        /// </summary>
        public static void WhenClosed(Action action)
        {
            if (action == null) return;
            try
            {
                var dispatcher = System.Windows.Application.Current != null
                    ? System.Windows.Application.Current.Dispatcher : null;
                if (dispatcher == null) { action(); return; }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    var started = DateTime.UtcNow;
                    var timer = new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromMilliseconds(400) };
                    timer.Tick += (s, e) =>
                    {
                        double seconds = (DateTime.UtcNow - started).TotalSeconds;
                        if (seconds < 1.2) return;
                        if (seconds < 1800 && IsEditorOpen()) return;
                        timer.Stop();
                        try { Breadcrumb.Write("GameEditorWatch: editor closed after " + seconds.ToString("0.0") + "s, running deferred work"); } catch { }
                        try { action(); } catch { }
                    };
                    timer.Start();
                }));
            }
            catch { try { action(); } catch { } }
        }
    }

    /// <summary>
    /// "Local Games Store": scans a user-chosen folder for games that did not come from an online
    /// storefront, adds them to Playnite and downloads their metadata. It also downloads metadata
    /// for games added by hand ("custom games"), the way Playnite does for storefront games.
    /// </summary>
    internal sealed class LocalGamesStore
    {
        public static LocalGamesStore Instance { get; private set; }

        private const string StoreSourceName = "Local Games Store";
        private static readonly Guid IgdbPluginId = new Guid("000001DB-DBD1-46C6-B5D0-B1BA559D10E4");

        private readonly IPlayniteAPI api;
        private readonly ILogger log;
        private readonly Func<OverlaySettings> getSettings;
        private readonly string dataDir;

        private int scanning;                                   // 0 / 1
        private readonly object knownLock = new object();
        private readonly ConcurrentDictionary<Guid, bool> scanAdded = new ConcurrentDictionary<Guid, bool>();
        private readonly ConcurrentDictionary<Guid, string> metadataSeen = new ConcurrentDictionary<Guid, string>();
        private readonly ConcurrentDictionary<Guid, bool> metadataQueued = new ConcurrentDictionary<Guid, bool>();
        private readonly ConcurrentQueue<Guid> metadataQueue = new ConcurrentQueue<Guid>();
        private int metadataWorkerRunning;                      // 0 / 1

        /// <summary>
        /// Set by the plugin. Adds the enabled PCGamingWiki / Nexus / Vortex / save / SCSKiller links to
        /// a game inside the same database update that stores its metadata. Returns true if the game changed.
        /// Always called on the UI thread.
        /// </summary>
        public Func<Game, bool> ApplyLinks;

        /// <summary>Set by the plugin. Loads the downloaded link sources (Nexus game list) before a batch.</summary>
        public Action PrepareLinks;

        /// <summary>
        /// Set by the plugin. Called once per batch of freshly looked-up games, before their metadata is written:
        /// runs the PCGamingWiki page check and the save-location lookup for them, so the links can be added in
        /// the same database update as the metadata (instead of waiting for the separate background link pass).
        /// Runs on a worker thread; the games are private copies carrying their final display name.
        /// </summary>
        public Action<List<Game>, CancellationToken> PrepareLinksForGames;

        /// <summary>
        /// Set by the plugin. True while the PCGamingWiki page check has no answer for the game (wiki unreachable
        /// or blocked). Only then is the Steam id-redirect used as the PCGamingWiki link.
        /// </summary>
        public Func<Game, bool> IsPcgwUnresolved;

        /// <summary>True while the metadata worker (and its progress dialog) is running.</summary>
        public bool IsMetadataBusy { get { return Volatile.Read(ref metadataWorkerRunning) != 0; } }

        /// <summary>
        /// Set by the plugin. Asks the Playnite Achievements extension to fetch achievements for these games
        /// (one batched request). Called once per game, after its final name, platform, metadata and links
        /// are stored, so the extension looks the game up under the right title.
        /// </summary>
        public Action<List<Guid>> RequestAchievements;
        private readonly ConcurrentDictionary<Guid, bool> achievementsRequested = new ConcurrentDictionary<Guid, bool>();
        // Custom games added during this Playnite session (scanned or dropped in by hand). Games merely re-queued
        // for a metadata catch-up at startup are not in here, so they never trigger an achievements request.
        private readonly ConcurrentDictionary<Guid, bool> addedThisSession = new ConcurrentDictionary<Guid, bool>();

        private void RequestAchievementsFor(IEnumerable<Guid> ids)
        {
            try
            {
                var s = S;
                if (s == null || !s.LocalStoreDownloadAchievements || RequestAchievements == null || ids == null) return;
                var list = new List<Guid>();
                foreach (var id in ids)
                    if (addedThisSession.ContainsKey(id) && achievementsRequested.TryAdd(id, true)) list.Add(id);
                if (list.Count == 0) return;
                try { Breadcrumb.Write("Achievements: requesting refresh for " + list.Count + " Local Games Store game(s)"); } catch { }
                RequestAchievements(list);
            }
            catch (Exception ex) { log.Warn("Local Games Store: achievement request failed: " + ex.Message); }
        }

        /// <summary>
        /// Set by the plugin. Lets it veto a link a metadata provider supplied (a PCGamingWiki link for a game
        /// the wiki has no page for). Null accepts everything.
        /// </summary>
        public Func<Link, bool> AcceptLink;

        public LocalGamesStore(IPlayniteAPI api, ILogger log, Func<OverlaySettings> getSettings, string dataDir)
        {
            this.api = api;
            this.log = log;
            this.getSettings = getSettings;
            this.dataDir = dataDir;
            Instance = this;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        private OverlaySettings S { get { return getSettings(); } }

        // ------------------------------------------------------------------------------------
        // Triggers
        // ------------------------------------------------------------------------------------

        /// <summary>Scan on Playnite launch (only if enabled). Waits a little so libraries can load first.</summary>
        public void OnApplicationStarted()
        {
            Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(8)); } catch { }
                try { QueueExistingCustomGamesMissingMetadata(); }
                catch (Exception ex) { log.Warn("Local Games Store metadata catch-up failed: " + ex.Message); }

                var s = S;
                if (s != null && s.LocalStoreEnabled && s.LocalStoreScanOnStartup)
                    RequestScan("startup", false);
            });
        }

        /// <summary>Scan when Playnite updates its libraries (the F5 refresh), only if enabled.</summary>
        public void OnLibraryUpdated()
        {
            var s = S;
            if (s == null || !s.LocalStoreEnabled || !s.LocalStoreScanOnLibraryUpdate) return;
            RequestScan("library refresh", false);
        }

        /// <summary>Manual scan (hotkey, menu, or settings button). Ignores the auto-scan toggles.</summary>
        public void RequestScanManual(string reason)
        {
            RequestScan(reason, true);
        }

        private void RequestScan(string reason, bool manual)
        {
            var s = S;
            if (s == null) return;
            if (!s.LocalStoreEnabled)
            {
                if (manual) Notify("Metadata for local games is turned off in the overlay settings.", NotificationType.Info);
                return;
            }
            if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0)
            {
                if (manual) Notify("A local games scan is already running.", NotificationType.Info);
                return;
            }
            Task.Run(() =>
            {
                try { Scan(reason, manual); }
                catch (Exception ex) { log.Error("Local Games Store scan failed: " + ex); }
                finally { Interlocked.Exchange(ref scanning, 0); }
            });
        }

        /// <summary>Games added to the database: download metadata for custom (non-library) games.</summary>
        public void OnGamesAdded(IEnumerable<Game> added)
        {
            try
            {
                var s = S;
                if (s == null || added == null) return;
                foreach (var g in added.ToList())
                {
                    if (IsCustomGame(g)) addedThisSession[g.Id] = true;
                    PrefillEditorName(g);
                    QueueMetadataForGame(g, s);
                }
            }
            catch (Exception ex) { log.Error("Local Games Store OnGamesAdded failed: " + ex.Message); }
        }

        /// <summary>
        /// Retry metadata when a manually-added game's title or platform is changed. Updates Playnite
        /// raises for other reasons (launching or stopping a game, playtime, last played, ...) never
        /// trigger a lookup, so starting a game no longer checks for metadata or applies any.
        /// </summary>
        public void OnGamesUpdated(IEnumerable<ItemUpdateEvent<Game>> updated)
        {
            try
            {
                var s = S;
                if (s == null || updated == null) return;
                foreach (var u in updated.ToList())
                {
                    var g = u?.NewData;
                    if (g == null) continue;
                    if (g.IsLaunching || g.IsRunning || g.IsInstalling) continue;
                    if (u.OldData != null &&
                        string.Equals(MetadataRequestKey(u.OldData), MetadataRequestKey(g), StringComparison.Ordinal))
                        continue;   // title and platform unchanged: nothing to look up again
                    QueueMetadataForGame(g, s);
                }
            }
            catch (Exception ex) { log.Error("Local Games Store OnGamesUpdated failed: " + ex.Message); }
        }

        /// <summary>A dropped exe arrives named after its folder ("Win64"): show the real title in the Edit Game window.</summary>
        private void PrefillEditorName(Game game)
        {
            try
            {
                if (game == null || scanAdded.ContainsKey(game.Id) || !IsCustomGame(game)) return;
                string current = game.Name;
                if (!GameNameCleaner.NeedsNameFix(current)) return;
                string nice = GameNameCleaner.FixBoilerplateName(current);
                if (nice == null)
                {
                    string derived = null;
                    RunOnUi(() => { derived = DeriveNameFromGame(game.Id); });
                    try { Breadcrumb.Write("Editor: '" + current + "' is generic, path suggests '" + derived + "'"); } catch { }
                    if (string.IsNullOrWhiteSpace(derived) || GameNameCleaner.IsGenericName(derived)) return;
                    nice = GameNameCleaner.SplitWords(GameNameCleaner.StripJunk(derived));
                }
                if (string.IsNullOrWhiteSpace(nice) || string.Equals(nice, current, StringComparison.Ordinal)) return;
                GameEditorWatch.PrefillName(game.Id, current, nice);
            }
            catch (Exception ex) { log.Warn("Local Games Store: editor pre-fill failed: " + ex.Message); }
        }

        private void QueueMetadataForGame(Game game, OverlaySettings settings)
        {
            if (game == null) return;
            bool fromScan = scanAdded.ContainsKey(game.Id);
            bool metadataWanted = fromScan ? settings.LocalStoreDownloadMetadata : settings.AutoDownloadMetadataForCustomGames;
            if (!metadataWanted)
            {
                try { Breadcrumb.Write("Metadata: automatic download is off for '" + game.Name + "' (the name is still tidied)"); } catch { }
            }
            if (!IsCustomGame(game)) return;

            // Dropping an exe onto Playnite adds the game to the database straight away and opens the Edit Game
            // window. Looking things up now would use the unsaved placeholder name and be overwritten by the
            // save: wait until the editor is closed, then use the game as it was actually saved.
            var id = game.Id;
            GameEditorWatch.WhenClosed(() =>
            {
                try
                {
                    var live = api.Database.Games.Get(id);
                    if (live == null || !IsCustomGame(live))
                    {
                        try { Breadcrumb.Write("Metadata: skipped, game gone or not custom (" + (live == null ? "gone" : "plugin " + live.PluginId) + ")"); } catch { }
                        return;
                    }

                    // A dropped exe is named after its folder ("Win64"). Fix that first and unconditionally, so the
                    // game never keeps it, whether or not the metadata lookup runs or finds anything.
                    FixGenericName(live);

                    if (!metadataWanted) return;
                    try { Breadcrumb.Write("Metadata: queued '" + live.Name + "'"); } catch { }
                    EnqueueMetadata(id, MetadataRequestKey(live));
                }
                catch (Exception ex) { log.Warn("Local Games Store: deferred metadata queue failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// Replaces a folder-style name ("Win64", "Binaries", "bin") with the title found in the game's exe /
        /// install path. Call on the UI thread, with the game as it is stored now.
        /// </summary>
        private void FixGenericName(Game live)
        {
            try
            {
                if (live == null) return;
                bool generic = GameNameCleaner.NeedsNameFix(live.Name);
                try { Breadcrumb.Write("Name: '" + live.Name + "' needsFix=" + generic); } catch { }
                if (!generic) return;

                // "MaxPayne Application" -> "Max Payne" straight from the name; folder-style names ("Win64")
                // are read from the exe / install path instead.
                string nice = GameNameCleaner.FixBoilerplateName(live.Name);
                if (nice == null)
                {
                    string derived = DeriveNameFromGame(live.Id);
                    if (string.IsNullOrWhiteSpace(derived) || GameNameCleaner.IsGenericName(derived))
                    {
                        try { Breadcrumb.Write("Name: nothing usable found in the path of '" + live.Name + "'"); } catch { }
                        return;
                    }
                    nice = GameNameCleaner.SplitWords(GameNameCleaner.StripJunk(derived));
                }
                if (string.IsNullOrWhiteSpace(nice) || string.Equals(nice, live.Name, StringComparison.Ordinal)) return;

                string old = live.Name;
                live.Name = nice;
                api.Database.Games.Update(live);
                log.Info("Local Games Store: renamed '" + old + "' -> '" + nice + "'");
                try { Breadcrumb.Write("Name: renamed '" + old + "' -> '" + nice + "'"); } catch { }
            }
            catch (Exception ex) { log.Warn("Local Games Store: name fix failed: " + ex.Message); }
        }

        /// <summary>
        /// One-time catch-up for manually-added games already in the library. Missing background
        /// art is common even when Playnite has downloaded a cover and description, so retry those
        /// records too. Existing metadata is still never overwritten by DownloadMetadata.
        /// </summary>
        private void QueueExistingCustomGamesMissingMetadata()
        {
            var settings = S;
            if (settings == null || !settings.AutoDownloadMetadataForCustomGames) return;

            List<Game> games = null;
            RunOnUi(() => { games = api.Database.Games.ToList(); });
            if (games == null) return;

            int queued = 0;
            foreach (var game in games)
            {
                if (game == null || !IsCustomGame(game)) continue;

                bool missingArt = string.IsNullOrEmpty(game.CoverImage) ||
                    string.IsNullOrEmpty(game.BackgroundImage);
                bool missingMostMetadata = string.IsNullOrWhiteSpace(game.Description) &&
                    string.IsNullOrEmpty(game.CoverImage) && string.IsNullOrEmpty(game.BackgroundImage) &&
                    Empty(game.GenreIds) && Empty(game.DeveloperIds) && Empty(game.PublisherIds) &&
                    game.ReleaseDate == null;
                // Do not re-download metadata for the whole library just because games lack
                // platform or install-size values; that causes needless update churn and can
                // make Playnite's live image view appear to lose its backgrounds.
                if (!missingArt && !missingMostMetadata) continue;

                EnqueueMetadata(game.Id, MetadataRequestKey(game));
                queued++;
            }

            if (queued > 0)
                log.Info("Local Games Store: found " + queued + " existing custom game(s) with missing metadata/background art.");
        }

        /// <summary>A game that is not owned by a library plugin.</summary>
        private static bool IsCustomGame(Game g)
        {
            // Include manually-added/emulated games too; platform and emulator metadata must not
            // prevent the selected Playnite metadata providers from supplying missing art.
            return g != null && g.PluginId == Guid.Empty;
        }

        // ------------------------------------------------------------------------------------
        // Scanning
        // ------------------------------------------------------------------------------------

        private static readonly string[] ExcludedExePatterns =
        {
            @"^unins\w*", @"^uninst\w*", @"uninstall", @"^setup", @"^install", @"^vc_?redist", @"^vcredist",
            @"^dxsetup", @"^dxwebsetup", @"^dotnet", @"^ndp\d", @"^oalinst", @"crash\s?(handler|report|pad)",
            @"^unitycrashhandler", @"^ue\d?prereq", @"^notification_helper", @"^updater?\b", @"^cefprocess",
            @"^qtwebengineprocess", @"^easyanticheat", @"^eac", @"^beservice", @"^battleye", @"^webview2",
            @"^microsoftedgewebview2", @"^7z", @"^helper\b", @"^redist", @"^physx", @"^xnafx", @"^gamingrepair"
        };

        private static readonly string[] ExcludedDirPatterns =
        {
            @"^_?commonredist$", @"^_?redist", @"^directx$", @"^vcredist", @"^__installer", @"^support$",
            @"^engine$", @"^crashpad", @"^mono$", @"^monobleedingedge$", @"_data$", @"^\.git$", @"^node_modules$",
            @"^\$recycle\.bin$", @"^system volume information$", @"^dotnet", @"^_?installer"
        };

        private const int MaxDepth = 3;

        private sealed class Candidate
        {
            public string Name;
            public string Exe;
            public string Dir;       // install directory
            public bool IsRoot;      // exe sits directly in the store folder (the folder itself is not a game folder)
        }

        private void Scan(string reason, bool manual)
        {
            var s = S;
            string root = (s.LocalGamesFolder ?? "").Trim();
            if (root.Length == 0 || !Directory.Exists(root))
            {
                log.Info("Local Games Store: folder not set or missing (" + root + ")");
                if (manual) Notify("Local games: choose an existing folder in the overlay settings first.", NotificationType.Error);
                return;
            }

            log.Info("Local Games Store: scanning '" + root + "' (" + reason + ")");

            var candidates = FindCandidates(root);
            var known = BuildKnownSet();
            var history = LoadHistory();

            var toAdd = new List<Candidate>();
            foreach (var c in candidates)
            {
                string exeKey = Norm(c.Exe);
                if (known.Contains(exeKey)) continue;
                if (!c.IsRoot && known.Contains(Norm(c.Dir) + "\\")) continue;   // an existing game already installs here
                if (history.Contains(exeKey)) continue;                 // added before (and maybe removed by the user)
                toAdd.Add(c);
            }

            if (toAdd.Count == 0)
            {
                log.Info("Local Games Store: nothing new (" + candidates.Count + " candidate(s) already known)");
                if (manual) Notify("Local games: no new games found.", NotificationType.Info);
                return;
            }

            int added = 0;
            var addedIds = new List<Guid>();
            foreach (var c in toAdd)
            {
                try
                {
                    var game = BuildGame(c);
                    scanAdded[game.Id] = true;                           // lets OnGamesAdded force metadata
                    addedThisSession[game.Id] = true;
                    RunOnUi(() => api.Database.Games.Add(game));
                    AppendHistory(Norm(c.Exe));
                    addedIds.Add(game.Id);
                    added++;
                    log.Info("Local Games Store: added '" + game.Name + "' -> " + c.Exe);
                }
                catch (Exception ex)
                {
                    log.Error("Local Games Store: could not add " + c.Exe + ": " + ex.Message);
                }
            }

            Notify("Local games: added " + added + " game" + (added == 1 ? "" : "s") + ".", NotificationType.Info);

            // With metadata download on, achievements are requested once each game's metadata is stored (see
            // FlushPending). With it off nothing else would trigger them, so ask now.
            if (!s.LocalStoreDownloadMetadata)
                RequestAchievementsFor(addedIds);
        }

        private List<Candidate> FindCandidates(string root)
        {
            var result = new List<Candidate>();

            // 1) executables directly inside the root folder: each one is a game.
            foreach (var exe in SafeFiles(root, "*.exe"))
            {
                if (IsExcludedExe(exe)) continue;
                result.Add(new Candidate
                {
                    Name = CleanName(Path.GetFileNameWithoutExtension(exe)),
                    Exe = exe,
                    Dir = root,
                    IsRoot = true
                });
            }

            // 2) every subfolder: one game, using the most likely executable.
            foreach (var dir in SafeDirs(root))
            {
                if (IsExcludedDir(Path.GetFileName(dir))) continue;
                var exes = new List<KeyValuePair<string, int>>();
                CollectExes(dir, 0, exes);
                if (exes.Count == 0) continue;

                string folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                string normFolder = NormName(folderName);
                string best = null;
                double bestScore = double.MinValue;
                foreach (var kv in exes)
                {
                    double score = ScoreExe(kv.Key, kv.Value, normFolder);
                    if (score > bestScore) { bestScore = score; best = kv.Key; }
                }
                if (best == null) continue;

                result.Add(new Candidate
                {
                    Name = CleanName(folderName),
                    Exe = best,
                    Dir = dir
                });
            }
            return result;
        }

        private void CollectExes(string dir, int depth, List<KeyValuePair<string, int>> into)
        {
            foreach (var exe in SafeFiles(dir, "*.exe"))
                if (!IsExcludedExe(exe)) into.Add(new KeyValuePair<string, int>(exe, depth));
            if (depth >= MaxDepth) return;
            foreach (var sub in SafeDirs(dir))
            {
                if (IsExcludedDir(Path.GetFileName(sub))) continue;
                CollectExes(sub, depth + 1, into);
            }
        }

        private static double ScoreExe(string exe, int depth, string normFolder)
        {
            double score = 0;
            string normExe = NormName(Path.GetFileNameWithoutExtension(exe));
            if (normExe.Length > 0 && normFolder.Length > 0)
            {
                if (normExe == normFolder) score += 1000;
                else if (normFolder.Contains(normExe) || normExe.Contains(normFolder)) score += 500;
            }
            score -= depth * 150;
            try { score += Math.Min(new FileInfo(exe).Length / (1024.0 * 1024.0), 300); } catch { }
            return score;
        }

        private static bool IsExcludedExe(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? "";
            foreach (var p in ExcludedExePatterns)
                if (Regex.IsMatch(name, p, RegexOptions.IgnoreCase)) return true;
            return false;
        }

        private static bool IsExcludedDir(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var p in ExcludedDirPatterns)
                if (Regex.IsMatch(name, p, RegexOptions.IgnoreCase)) return true;
            return false;
        }

        private static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
            catch { return new string[0]; }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return new string[0]; }
        }

        // ------------------------------------------------------------------------------------
        // Game creation
        // ------------------------------------------------------------------------------------

        private Game BuildGame(Candidate c)
        {
            var game = new Game(string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileNameWithoutExtension(c.Exe) : c.Name)
            {
                InstallDirectory = c.Dir,
                IsInstalled = true,
                Added = DateTime.Now
            };

            string exeDir = Path.GetDirectoryName(c.Exe) ?? c.Dir;
            string rel = RelativeTo(c.Dir, c.Exe);
            string relDir = RelativeTo(c.Dir, exeDir);
            game.GameActions = new ObservableCollection<GameAction>
            {
                new GameAction
                {
                    Name = "Play",
                    Type = GameActionType.File,
                    Path = rel != null ? "{InstallDir}\\" + rel : c.Exe,
                    WorkingDir = relDir != null ? (relDir.Length == 0 ? "{InstallDir}" : "{InstallDir}\\" + relDir) : exeDir,
                    IsPlayAction = true
                }
            };

            RunOnUi(() =>
            {
                try
                {
                    var platform = api.Database.Platforms.FirstOrDefault(p =>
                                       string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase)) ??
                                   api.Database.Platforms.FirstOrDefault(p =>
                                       string.Equals(p.Name, "PC (Windows)", StringComparison.OrdinalIgnoreCase));
                    if (platform == null)
                    {
                        platform = new Platform("PC (Windows)") { SpecificationId = "pc_windows" };
                        api.Database.Platforms.Add(platform);
                    }
                    game.PlatformIds = new List<Guid> { platform.Id };

                    var source = api.Database.Sources.FirstOrDefault(x =>
                                     string.Equals(x.Name, StoreSourceName, StringComparison.OrdinalIgnoreCase)) ??
                                 api.Database.Sources.Add(StoreSourceName);
                    game.SourceId = source.Id;
                }
                catch (Exception ex) { log.Error("Local Games Store: platform/source setup failed: " + ex.Message); }
            });
            return game;
        }

        /// <summary>Path of <paramref name="path"/> below <paramref name="baseDir"/>, or null if it is outside.</summary>
        private static string RelativeTo(string baseDir, string path)
        {
            try
            {
                string b = Path.GetFullPath(baseDir).TrimEnd('\\', '/');
                string p = Path.GetFullPath(path).TrimEnd('\\', '/');
                if (string.Equals(b, p, StringComparison.OrdinalIgnoreCase)) return "";
                if (!p.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                return p.Substring(b.Length + 1);
            }
            catch { return null; }
        }

        private static string CleanName(string raw)
        {
            // Scanned folders / exe names: drop build junk (Win64, Shipping, versions ...) and split
            // run-together words ("DarksidersGenesis" -> "Darksiders Genesis").
            return GameNameCleaner.SplitWords(GameNameCleaner.StripJunk(raw));
        }

        private static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        private static string Norm(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return (path ?? "").Trim().ToLowerInvariant(); }
        }

        /// <summary>All executable paths (and install folders, suffixed with a backslash) Playnite already knows.</summary>
        private HashSet<string> BuildKnownSet()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            List<Game> games = null;
            RunOnUi(() => { games = api.Database.Games.ToList(); });
            foreach (var g in games ?? new List<Game>())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(g.InstallDirectory))
                        set.Add(Norm(g.InstallDirectory) + "\\");
                    if (g.GameActions == null) continue;
                    foreach (var a in g.GameActions)
                    {
                        if (a == null || a.Type != GameActionType.File || string.IsNullOrWhiteSpace(a.Path)) continue;
                        string full = null;
                        try { full = api.ExpandGameVariables(g, a.Path); } catch { }
                        if (!string.IsNullOrWhiteSpace(full))
                        {
                            try
                            {
                                if (!Path.IsPathRooted(full) && !string.IsNullOrWhiteSpace(g.InstallDirectory))
                                    full = Path.Combine(g.InstallDirectory, full);
                            }
                            catch { }
                            set.Add(Norm(full));
                        }
                    }
                }
                catch { }
            }
            return set;
        }

        // History of executables this scanner already added. A game the user deleted afterwards is NOT re-added.
        private string HistoryPath { get { return Path.Combine(dataDir, "local_games_store_history.txt"); } }

        private HashSet<string> LoadHistory()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            lock (knownLock)
            {
                try
                {
                    if (File.Exists(HistoryPath))
                        foreach (var line in File.ReadAllLines(HistoryPath))
                            if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
                }
                catch { }
            }
            return set;
        }

        private void AppendHistory(string normExe)
        {
            lock (knownLock)
            {
                try
                {
                    Directory.CreateDirectory(dataDir);
                    File.AppendAllLines(HistoryPath, new[] { normExe });
                }
                catch { }
            }
        }

        /// <summary>Forget what was added before, so deleted games can be picked up again by the next scan.</summary>
        public void ResetHistory()
        {
            lock (knownLock)
            {
                try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); } catch { }
            }
            Notify("Local games: scan history cleared. Games you deleted can be found again.", NotificationType.Info);
        }

        // ------------------------------------------------------------------------------------
        // Metadata download
        // ------------------------------------------------------------------------------------

        private static string MetadataRequestKey(Game game)
        {
            if (game == null) return string.Empty;
            string name = (game.Name ?? string.Empty).Trim();
            string platforms = game.PlatformIds == null
                ? string.Empty
                : string.Join(",", game.PlatformIds
                    .Select(platformId => platformId.ToString("N"))
                    .OrderBy(platformId => platformId, StringComparer.OrdinalIgnoreCase));
            // The platform is part of the metadata query context. A user may change the
            // platform immediately after adding a game (the default may be PC); that change
            // must trigger a fresh lookup using the selected platform.
            return name + "\u001f" + platforms;
        }

        private void EnqueueMetadata(Guid id, string requestKey)
        {
            // Deduplicate the same title/platform query, but allow a retry if the user
            // finishes the title or changes platform after the add event fired.
            while (true)
            {
                string previous;
                if (metadataSeen.TryGetValue(id, out previous))
                {
                    if (string.Equals(previous, requestKey, StringComparison.OrdinalIgnoreCase)) return;
                    if (metadataSeen.TryUpdate(id, requestKey, previous)) break;
                }
                else if (metadataSeen.TryAdd(id, requestKey))
                {
                    break;
                }
            }
            // If an entry is still waiting, it will read the latest game snapshot when
            // processed. If it is already in flight, enqueue one follow-up for the change.
            if (!metadataQueued.TryAdd(id, true)) return;
            metadataQueue.Enqueue(id);
            if (Interlocked.CompareExchange(ref metadataWorkerRunning, 1, 0) != 0) return;
            Task.Run(() =>
            {
                bool cancelled = false;
                try
                {
                    // Give the user a moment (e.g. the Edit Game window may still be closing).
                    Thread.Sleep(1500);
                    Action<GlobalProgressActionArgs> process = progress =>
                    {
                        cancelled = ProcessMetadataQueue(progress);
                    };
                    OverlayProgressDialog.Run(
                        api,
                        process,
                        new GlobalProgressOptions("Downloading game metadata", true),
                        S?.ShowMetadataDownloadProgress == true);
                }
                catch (Exception ex)
                {
                    // Playnite may be shutting down or its progress service may be unavailable.
                    log.Warn("Metadata progress dialog unavailable; continuing in background: " + ex.Message);
                    cancelled = ProcessMetadataQueue(null);
                }
                finally
                {
                    Interlocked.Exchange(ref metadataWorkerRunning, 0);
                    if (cancelled)
                    {
                        // A canceled batch should not silently restart its modal progress dialog.
                        Guid pending;
                        while (metadataQueue.TryDequeue(out pending))
                        {
                            metadataQueued.TryRemove(pending, out bool _);
                            metadataSeen.TryRemove(pending, out string _);
                        }
                    }
                    else if (!metadataQueue.IsEmpty) // something arrived while shutting down
                    {
                        Guid again;
                        if (metadataQueue.TryDequeue(out again))
                        {
                            metadataQueued.TryRemove(again, out bool _);
                            metadataSeen.TryRemove(again, out string _);
                            Game retryGame = null;
                            RunOnUi(() => { retryGame = api.Database.Games.Get(again); });
                            EnqueueMetadata(again, MetadataRequestKey(retryGame));
                        }
                    }
                }
            });
        }

        private bool ProcessMetadataQueue(GlobalProgressActionArgs progress)
        {
            int processed = 0;
            int total = Math.Max(1, metadataQueue.Count);
            if (progress != null)
            {
                progress.IsIndeterminate = false;
                progress.ProgressMaxValue = total;
                progress.CurrentProgressValue = 0;
            }

            // Links are fetched together with the metadata, so their sources must be ready first.
            try { PrepareLinks?.Invoke(); }
            catch (Exception ex) { log.Warn("Local Games Store: link sources unavailable: " + ex.Message); }

            // Metadata is fetched game by game, but links are prepared and everything is written in small batches:
            // the PCGamingWiki check and the save-location lookup (a large manifest) then run once per batch.
            var pending = new List<PendingMetadata>();

            Guid next;
            while (metadataQueue.TryDequeue(out next))
            {
                if (progress != null && progress.CancelToken.IsCancellationRequested)
                {
                    metadataQueue.Enqueue(next);
                    FlushPending(pending, progress);
                    return true;
                }
                metadataQueued.TryRemove(next, out bool _);

                string gameName = null;
                RunOnUi(() => { gameName = api.Database.Games.Get(next)?.Name; });
                int projectedTotal = processed + 1 + metadataQueue.Count;
                if (projectedTotal > total)
                {
                    total = projectedTotal;
                    if (progress != null) progress.ProgressMaxValue = total;
                }
                if (progress != null)
                    progress.Text = "Metadata " + (processed + 1) + " of " + total +
                        (string.IsNullOrWhiteSpace(gameName) ? string.Empty : ": " + gameName);

                try
                {
                    var fetched = FetchMetadata(next, progress?.CancelToken ?? CancellationToken.None);
                    if (fetched != null) pending.Add(fetched);
                }
                catch (Exception ex) { log.Error("Local Games Store metadata failed: " + ex.Message); }

                if (pending.Count >= 8 || metadataQueue.IsEmpty)
                    FlushPending(pending, progress);

                processed++;
                if (progress != null) progress.CurrentProgressValue = processed;
                if (progress != null && progress.CancelToken.IsCancellationRequested)
                {
                    FlushPending(pending, progress);
                    return true;
                }
            }

            FlushPending(pending, progress);
            return false;
        }

        /// <summary>The game's install folder with Playnite variables expanded. Call on the UI thread.</summary>
        private string GetExpandedInstallDir(Guid gameId)
        {
            try
            {
                var live = api.Database.Games.Get(gameId);
                if (live == null || string.IsNullOrWhiteSpace(live.InstallDirectory)) return null;
                try { return api.ExpandGameVariables(live, live.InstallDirectory); }
                catch { return live.InstallDirectory; }
            }
            catch { return null; }
        }

        /// <summary>Title taken from the game's play action / install folder. Call on the UI thread.</summary>
        private string DeriveNameFromGame(Guid gameId)
        {
            try
            {
                var live = api.Database.Games.Get(gameId);
                if (live == null) return null;
                string install = null;
                try { install = string.IsNullOrWhiteSpace(live.InstallDirectory) ? null : api.ExpandGameVariables(live, live.InstallDirectory); }
                catch { install = live.InstallDirectory; }

                string exe = null;
                var actions = live.GameActions == null ? new List<GameAction>() : live.GameActions.ToList();
                var action = actions.FirstOrDefault(a => a != null && a.IsPlayAction && a.Type == GameActionType.File) ??
                             actions.FirstOrDefault(a => a != null && a.Type == GameActionType.File);
                if (action != null && !string.IsNullOrWhiteSpace(action.Path))
                {
                    try { exe = api.ExpandGameVariables(live, action.Path); } catch { exe = action.Path; }
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(exe) && !Path.IsPathRooted(exe) && !string.IsNullOrWhiteSpace(install))
                            exe = Path.Combine(install, exe);
                    }
                    catch { }
                }
                return GameNameCleaner.FromPath(exe, install);
            }
            catch (Exception ex)
            {
                log.Warn("Local Games Store: could not read the game's path: " + ex.Message);
                return null;
            }
        }

        private sealed class PendingMetadata
        {
            public Guid Id;
            public string RequestKey;
            public Collected Got;
            public Game Snapshot;       // private copy, carries the final display name
            public bool NeedsLinkPrep;  // PC / unknown platform: PCGamingWiki + save locations are worth checking
        }

        /// <summary>Prepares the link sources for the whole batch once, then writes metadata + links per game.</summary>
        private void FlushPending(List<PendingMetadata> batch, GlobalProgressActionArgs progress)
        {
            if (batch == null || batch.Count == 0) return;
            var items = batch.ToList();
            batch.Clear();
            var ct = progress != null ? progress.CancelToken : CancellationToken.None;

            var prep = items.Where(i => i.NeedsLinkPrep && i.Snapshot != null).Select(i => i.Snapshot).ToList();
            if (prep.Count > 0 && PrepareLinksForGames != null && !ct.IsCancellationRequested)
            {
                string previousText = progress != null ? progress.Text : null;
                if (progress != null) progress.Text = "Looking up links (PCGamingWiki, save locations)…";
                try { PrepareLinksForGames(prep, ct); }
                catch (Exception ex) { log.Warn("Local Games Store: preparing links failed: " + ex.Message); }
                if (progress != null && previousText != null) progress.Text = previousText;
            }

            foreach (var item in items)
            {
                try { ApplyPending(item); }
                catch (Exception ex) { log.Error("Local Games Store: applying metadata failed: " + ex.Message); }
            }

            // Name, platform, metadata and links are stored now: the achievements lookup can use them.
            // (PC games only: the extension's Local provider is for those.)
            RequestAchievementsFor(items.Where(i => i.NeedsLinkPrep).Select(i => i.Id).ToList());
        }

        private sealed class LookupAttempt
        {
            public string Name;
            public List<Guid> Platforms;
            public string Label;
        }

        private sealed class Collected
        {
            public string Description;
            public ReleaseDate? Release;
            public int? Critic, Community;
            public ulong? InstallSize;
            public List<MetadataProperty> Genres, Developers, Publishers, Tags, Features, Series, AgeRatings, Regions, Platforms;
            public List<Link> Links;
            public string CoverStored, BackgroundStored, IconStored;
            public string NameSuggestion;
            public string FinalName;        // name the game should carry (cleaned up), applied together with the metadata
            public bool LinksWanted;        // the game had no links when the lookup started
            public int GameBananaId;        // 0 = none found / not looked up
            public int SteamAppId;          // read from the Steam install's appmanifest; 0 = not a Steam install
        }

        private static bool Empty<T>(ICollection<T> c) { return c == null || c.Count == 0; }

        private PendingMetadata FetchMetadata(Guid gameId, CancellationToken cancelToken)
        {
            // Work on a private copy. The database object is the one Playnite's views are bound to:
            // changing it (even only the search name below) from this worker thread raises property
            // changes on the wrong thread and was never reverted.
            Game snapshot = null;
            RunOnUi(() =>
            {
                try
                {
                    var live = api.Database.Games.Get(gameId);
                    if (live != null)
                        snapshot = Playnite.SDK.Data.Serialization.FromJson<Game>(Playnite.SDK.Data.Serialization.ToJson(live));
                }
                catch (Exception ex) { log.Warn("Local Games Store: could not copy game record: " + ex.Message); }
            });
            if (snapshot == null) return null;
            string requestKey = MetadataRequestKey(snapshot);

            // What is still missing (existing values are never overwritten, like Playnite's "skip existing").
            bool needDesc = string.IsNullOrWhiteSpace(snapshot.Description);
            bool needCover = string.IsNullOrEmpty(snapshot.CoverImage);
            bool needBg = string.IsNullOrEmpty(snapshot.BackgroundImage);
            bool needIcon = string.IsNullOrEmpty(snapshot.Icon);
            bool needGenres = Empty(snapshot.GenreIds);
            bool needDevs = Empty(snapshot.DeveloperIds);
            bool needPubs = Empty(snapshot.PublisherIds);
            bool needTags = Empty(snapshot.TagIds);
            bool needFeatures = Empty(snapshot.FeatureIds);
            bool needSeries = Empty(snapshot.SeriesIds);
            bool needAge = Empty(snapshot.AgeRatingIds);
            bool needRegion = Empty(snapshot.RegionIds);
            bool needRelease = snapshot.ReleaseDate == null;
            bool needCritic = snapshot.CriticScore == null;
            bool needCommunity = snapshot.CommunityScore == null;
            bool needLinks = Empty(snapshot.Links);
            bool needPlatforms = Empty(snapshot.PlatformIds);
            bool needInstallSize = snapshot.InstallSize == null;

            var plugins = api.Addons.Plugins.OfType<MetadataPlugin>()
                .OrderBy(p => p.Id == IgdbPluginId ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string originalName = snapshot.Name ?? string.Empty;

            // Names that came from an exe / folder ("DarksidersGenesis", "Game-Win64-Shipping") are cleaned up:
            // build junk is dropped and run-together words are split. Names the user typed are left alone.
            bool autoName = GameNameCleaner.LooksAutoGenerated(originalName);
            string nameSource = originalName;
            if (GameNameCleaner.IsGenericName(originalName))
            {
                // A dropped exe is named after its folder ("Win64"): read the real title from the exe / install path.
                string derived = null;
                RunOnUi(() => { derived = DeriveNameFromGame(gameId); });
                if (!string.IsNullOrWhiteSpace(derived) && !GameNameCleaner.IsGenericName(derived))
                {
                    log.Info("Local Games Store: '" + originalName + "' is a folder name, using '" + derived + "' from the game's path.");
                    nameSource = derived;
                    autoName = true;
                }
            }
            string junkFreeName = autoName ? GameNameCleaner.StripJunk(nameSource) : originalName;
            string splitName = autoName ? GameNameCleaner.SplitWords(junkFreeName) : originalName;
            string searchName = CleanMetadataSearchName(splitName);
            string unsplitSearchName = CleanMetadataSearchName(junkFreeName);
            if (!string.Equals(originalName, searchName, StringComparison.Ordinal))
                snapshot.Name = searchName; // query only; this is a private copy, the database game is untouched

            try
            {
                Breadcrumb.Write("Metadata: lookup '" + originalName + "' search='" + searchName + "' providers=" +
                    string.Join(",", plugins.Select(pl => pl.Name)) + " needDesc=" + needDesc + " needCover=" + needCover +
                    " needBg=" + needBg + " platforms=" + (snapshot.PlatformIds == null ? 0 : snapshot.PlatformIds.Count));
            }
            catch { }
            log.Info("Local Games Store: looking up metadata for '" + originalName + "'" +
                (searchName == originalName ? string.Empty : " (search: '" + searchName + "')") +
                " with " + plugins.Count + " metadata provider(s).");
            if (plugins.Count == 0)
                log.Warn("Local Games Store: no metadata providers are enabled in Playnite.");

            var got = new Collected();

            // Exact title first. Fan ports / remasters (e.g. "God Hand Rearmed") are unknown to the providers
            // under that name: when nothing at all is found, retry with the edition / port suffix stripped.
            var candidates = new List<string> { searchName };
            // The unsplit spelling is a second chance for titles written in CamelCase on purpose ("BioShock").
            if (!string.IsNullOrWhiteSpace(unsplitSearchName) &&
                !candidates.Contains(unsplitSearchName, StringComparer.OrdinalIgnoreCase))
                candidates.Add(unsplitSearchName);
            int fallbackCount = 0;
            foreach (var alt in BuildFallbackSearchNames(searchName))
                if (!candidates.Contains(alt, StringComparer.OrdinalIgnoreCase)) { candidates.Add(alt); fallbackCount++; }

            // Providers such as IGDB match on the platform too: a PC fan port of a console game (God Hand on PS2)
            // is found only when the platform is left out. So: every name with the game's platform first, then
            // every name again without it.
            var originalPlatforms = snapshot.PlatformIds;
            var attempts = new List<LookupAttempt>();
            foreach (var c in candidates)
                attempts.Add(new LookupAttempt { Name = c, Platforms = originalPlatforms, Label = string.Empty });
            if (originalPlatforms != null && originalPlatforms.Count > 0)
                foreach (var c in candidates)
                    attempts.Add(new LookupAttempt { Name = c, Platforms = new List<Guid>(), Label = " / no platform" });

            // Last resort: the original game lives on another platform (God Hand is PS2). Try the most stripped
            // title once per platform that exists in this library; the game's own platform is never changed.
            if (fallbackCount > 0)
            {
                List<KeyValuePair<Guid, string>> libraryPlatforms = null;
                RunOnUi(() =>
                {
                    try { libraryPlatforms = api.Database.Platforms.Select(pl => new KeyValuePair<Guid, string>(pl.Id, pl.Name)).ToList(); }
                    catch { }
                });
                string baseName = candidates[candidates.Count - 1];
                if (libraryPlatforms != null)
                    foreach (var lp in libraryPlatforms)
                    {
                        if (originalPlatforms != null && originalPlatforms.Contains(lp.Key)) continue;
                        attempts.Add(new LookupAttempt
                        {
                            Name = baseName,
                            Platforms = new List<Guid> { lp.Key },
                            Label = " / platform " + lp.Value
                        });
                    }
            }

            for (int candidateIndex = 0; candidateIndex < attempts.Count; candidateIndex++)
            {
                if (cancelToken.IsCancellationRequested) break;
                snapshot.Name = attempts[candidateIndex].Name;
                snapshot.PlatformIds = attempts[candidateIndex].Platforms;
                if (candidateIndex > 0)
                    log.Info("Local Games Store: no description / details yet, retrying as '" + snapshot.Name + "'" +
                        attempts[candidateIndex].Label + ".");

                foreach (var plugin in plugins)
                {
                    // Stop early once everything we care about is filled.
                    if (!needDesc && !needIcon && !needGenres && !needDevs && !needPubs &&
                        !needTags && !needFeatures && !needSeries && !needAge && !needRegion && !needRelease &&
                        !needCritic && !needCommunity && !needLinks && !needPlatforms && !needInstallSize)
                        break;
                    if (cancelToken.IsCancellationRequested) break;

                    OnDemandMetadataProvider provider = null;
                    try
                    {
                        provider = plugin.GetMetadataProvider(new MetadataRequestOptions(snapshot, true));
                        if (provider == null) { try { Breadcrumb.Write("Metadata: [" + snapshot.Name + "] " + plugin.Name + " gave no provider"); } catch { } continue; }
                        var fields = provider.AvailableFields ?? new List<MetadataField>();
                        try
                        {
                            Breadcrumb.Write("Metadata: [" + snapshot.Name + attempts[candidateIndex].Label + "] " +
                                plugin.Name + " fields=" + string.Join("|", fields));
                        }
                        catch { }
                        var args = new GetMetadataFieldArgs();

                        if (fields.Contains(MetadataField.Name) && got.NameSuggestion == null)
                        {
                            string n = Try(() => provider.GetName(args));
                            if (!string.IsNullOrWhiteSpace(n)) got.NameSuggestion = n;
                        }
                        if (needDesc && fields.Contains(MetadataField.Description))
                        {
                            string d = Try(() => provider.GetDescription(args));
                            if (!string.IsNullOrWhiteSpace(d)) { got.Description = d; needDesc = false; }
                        }
                        if (needRelease && fields.Contains(MetadataField.ReleaseDate))
                        {
                            var r = Try(() => provider.GetReleaseDate(args));
                            if (r != null) { got.Release = r; needRelease = false; }
                        }
                        if (needCritic && fields.Contains(MetadataField.CriticScore))
                        {
                            var v = Try(() => provider.GetCriticScore(args));
                            if (v != null) { got.Critic = v; needCritic = false; }
                        }
                        if (needCommunity && fields.Contains(MetadataField.CommunityScore))
                        {
                            var v = Try(() => provider.GetCommunityScore(args));
                            if (v != null) { got.Community = v; needCommunity = false; }
                        }
                        if (needGenres && fields.Contains(MetadataField.Genres))
                            needGenres = !Take(ref got.Genres, Try(() => provider.GetGenres(args)));
                        if (needDevs && fields.Contains(MetadataField.Developers))
                            needDevs = !Take(ref got.Developers, Try(() => provider.GetDevelopers(args)));
                        if (needPubs && fields.Contains(MetadataField.Publishers))
                            needPubs = !Take(ref got.Publishers, Try(() => provider.GetPublishers(args)));
                        if (needTags && fields.Contains(MetadataField.Tags))
                            needTags = !Take(ref got.Tags, Try(() => provider.GetTags(args)));
                        if (needFeatures && fields.Contains(MetadataField.Features))
                            needFeatures = !Take(ref got.Features, Try(() => provider.GetFeatures(args)));
                        if (needSeries && fields.Contains(MetadataField.Series))
                            needSeries = !Take(ref got.Series, Try(() => provider.GetSeries(args)));
                        if (needAge && fields.Contains(MetadataField.AgeRating))
                            needAge = !Take(ref got.AgeRatings, Try(() => provider.GetAgeRatings(args)));
                        if (needRegion && fields.Contains(MetadataField.Region))
                            needRegion = !Take(ref got.Regions, Try(() => provider.GetRegions(args)));
                        if (needPlatforms && fields.Contains(MetadataField.Platform))
                            needPlatforms = !Take(ref got.Platforms, Try(() => provider.GetPlatforms(args)));
                        if (needInstallSize && fields.Contains(MetadataField.InstallSize))
                        {
                            var size = Try(() => provider.GetInstallSize(args));
                            if (size != null) { got.InstallSize = size; needInstallSize = false; }
                        }
                        if (needLinks && fields.Contains(MetadataField.Links))
                        {
                            var links = Try(() => provider.GetLinks(args));
                            var list = links == null ? null : links.Where(l => l != null && !string.IsNullOrWhiteSpace(l.Url)).ToList();
                            if (list != null && list.Count > 0) { got.Links = list; needLinks = false; }
                        }
                        if (needIcon && fields.Contains(MetadataField.Icon))
                        {
                            string stored = StoreImage(Try(() => provider.GetIcon(args)), gameId);
                            if (stored != null) { got.IconStored = stored; needIcon = false; }
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warn("Local Games Store: metadata provider '" + plugin.Name + "' failed: " + ex.Message);
                    }
                    finally
                    {
                        try { provider?.Dispose(); } catch { }
                    }
                }

                // Use the per-field provider order selected in Playnite's Metadata settings for art.
                // This deliberately does not filter by platform: a configured source gets the same
                // opportunity for PC, console, and emulated/custom games.
                if (needCover && got.CoverStored == null)
                    got.CoverStored = DownloadConfiguredImage(snapshot, gameId, plugins, "CoverImage",
                        MetadataField.CoverImage, cancelToken);
                if (needBg && got.BackgroundStored == null)
                    got.BackgroundStored = DownloadConfiguredImage(snapshot, gameId, plugins, "BackgroundImage",
                        MetadataField.BackgroundImage, cancelToken);

                if (FoundAnyMetadata(got)) break;
            }
            snapshot.Name = searchName;
            snapshot.PlatformIds = originalPlatforms;
            try
            {
                Breadcrumb.Write("Metadata: result '" + originalName + "' description=" + (got.Description != null) +
                    " genres=" + !Empty(got.Genres) + " developers=" + !Empty(got.Developers) +
                    " cover=" + (got.CoverStored != null) + " background=" + (got.BackgroundStored != null) +
                    " banana=" + got.GameBananaId);
            }
            catch { }
            log.Info("Local Games Store: '" + originalName + "' -> description=" + (got.Description != null) +
                " genres=" + !Empty(got.Genres) + " developers=" + !Empty(got.Developers) +
                " cover=" + (got.CoverStored != null) + " background=" + (got.BackgroundStored != null) + ".");


            // Links are looked up in the same pass as the metadata so both land in one update.
            got.LinksWanted = needLinks;
            try
            {
                var settings = S;
                if (settings != null && settings.AutoAddGameBananaLink && !cancelToken.IsCancellationRequested &&
                    !HasLinkHost(snapshot, "gamebanana.com"))
                {
                    var cachePath = Path.Combine(dataDir, "gamebanana_matches.json");
                    GameBananaLookup.Load(cachePath);
                    int? bananaId = Try(() => GameBananaLookup.Find(searchName, snapshot.InstallDirectory));
                    if (!(bananaId.HasValue && bananaId.Value > 0))
                    {
                        foreach (var alt in BuildFallbackSearchNames(searchName))
                        {
                            int? altId = Try(() => GameBananaLookup.Find(alt, snapshot.InstallDirectory));
                            if (altId.HasValue && altId.Value > 0) { bananaId = altId; break; }
                        }
                    }
                    if (bananaId.HasValue && bananaId.Value > 0) got.GameBananaId = bananaId.Value;
                    GameBananaLookup.Save(cachePath);
                }
            }
            catch (Exception ex) { log.Warn("Local Games Store: GameBanana lookup failed: " + ex.Message); }

            // The name the game keeps: cleaned-up when it was auto-generated, and the provider's spelling when it
            // only differs in capitalisation / spacing. Without any provider hit a CamelCase split is not
            // trusted ("BioShock" must not become "Bio Shock"), only the junk removal is.
            string finalName = originalName;
            if (autoName) finalName = FoundAnyMetadata(got) ? splitName : junkFreeName;
            if (!string.IsNullOrWhiteSpace(got.NameSuggestion) &&
                NormName(got.NameSuggestion) == NormName(finalName))
                finalName = got.NameSuggestion.Trim();
            if (string.IsNullOrWhiteSpace(finalName)) finalName = originalName;
            got.FinalName = finalName;

            // The link lookups below and in AddMissingLinks must use this final name.
            snapshot.Name = finalName;

            // A copy installed by Steam carries its App ID in steamapps\appmanifest_<id>.acf. Adding the Steam store
            // link lets the Playnite Achievements extension (which reads "/app/<id>" from links) identify the game.
            if (!HasLinkHost(snapshot, "store.steampowered.com"))
            {
                string installDir = null;
                RunOnUi(() => { installDir = GetExpandedInstallDir(gameId); });
                got.SteamAppId = SteamInstallInfo.FindAppId(installDir);
                if (got.SteamAppId > 0)
                {
                    try { Breadcrumb.Write("Steam: '" + finalName + "' is Steam app " + got.SteamAppId + " (from appmanifest)"); } catch { }
                }
            }

            bool pcOrUnknown = true;
            RunOnUi(() =>
            {
                try
                {
                    var ids = snapshot.PlatformIds;
                    if (ids == null || ids.Count == 0) return;       // platform decided by the provider / later
                    pcOrUnknown = ids.Select(id => api.Database.Platforms.Get(id))
                        .Any(pl => pl != null &&
                            (string.Equals(pl.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(pl.Name, "PC (Windows)", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(pl.Name, "PC", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(pl.Name, "Windows", StringComparison.OrdinalIgnoreCase)));
                }
                catch { }
            });
            if (!pcOrUnknown && got.Platforms != null)
                pcOrUnknown = got.Platforms.OfType<MetadataNameProperty>().Any(pp =>
                    pp.Name != null && (pp.Name.IndexOf("PC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        pp.Name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0));

            return new PendingMetadata
            {
                Id = gameId,
                RequestKey = requestKey,
                Got = got,
                Snapshot = snapshot,
                NeedsLinkPrep = pcOrUnknown
            };
        }

        /// <summary>Applies fetched metadata (and links) to a fresh copy of the game on the UI thread.</summary>
        private void ApplyPending(PendingMetadata p)
        {
            if (p == null) return;
            RunOnUi(() =>
            {
                var current = api.Database.Games.Get(p.Id);
                if (current == null) return;
                if (!string.Equals(p.RequestKey, MetadataRequestKey(current), StringComparison.OrdinalIgnoreCase))
                {
                    // Do not let a lookup started with the temporary default platform (often PC)
                    // fill fields after the user has selected a different platform. The update
                    // event queues a fresh lookup using the current platform context.
                    log.Info("Local Games Store: discarded stale metadata for '" + current.Name +
                        "' because its title or platform changed during the lookup.");
                    return;
                }
                ApplyMetadata(p.Id, p.Got);
            });
        }

        private sealed class ImageSourceSelection
        {
            public bool IsConfigured;
            public bool Import = true;
            public List<Guid> Sources = new List<Guid>();
        }

        private string DownloadConfiguredImage(
            Game game,
            Guid gameId,
            List<MetadataPlugin> plugins,
            string settingName,
            MetadataField field,
            CancellationToken cancelToken)
        {
            var selection = ReadImageSourceSelection(settingName);
            if (selection.IsConfigured && !selection.Import)
                return null;

            IEnumerable<MetadataPlugin> orderedPlugins;
            if (selection.IsConfigured)
            {
                // Guid.Empty is Playnite's library/store source. These custom games have no
                // library owner, so continue through the configured list to the next metadata
                // provider, matching Playnite's source fallback behavior.
                orderedPlugins = selection.Sources
                    .Where(id => id != Guid.Empty)
                    .Select(id => plugins.FirstOrDefault(p => p.Id == id))
                    .Where(p => p != null)
                    .Distinct();
            }
            else
            {
                // Preserve previous behavior if the config file is unavailable or its schema is
                // not recognized, rather than silently disabling image downloads.
                orderedPlugins = plugins;
            }

            foreach (var plugin in orderedPlugins)
            {
                if (cancelToken.IsCancellationRequested) break;
                OnDemandMetadataProvider provider = null;
                try
                {
                    if (plugin.SupportedFields?.Contains(field) != true) continue;
                    provider = plugin.GetMetadataProvider(new MetadataRequestOptions(game, true));
                    if (provider?.AvailableFields?.Contains(field) != true) continue;

                    var args = new GetMetadataFieldArgs();
                    var file = field == MetadataField.CoverImage
                        ? Try(() => provider.GetCoverImage(args))
                        : Try(() => provider.GetBackgroundImage(args));
                    var stored = StoreImage(file, gameId);
                    if (!string.IsNullOrWhiteSpace(stored))
                    {
                        log.Info("Local Games Store: " + field + " for '" + game.Name + "' from '" + plugin.Name + "'");
                        return stored;
                    }
                }
                catch (Exception ex)
                {
                    log.Warn("Local Games Store: " + field + " provider '" + plugin.Name +
                        "' failed: " + ex.Message);
                }
                finally
                {
                    try { provider?.Dispose(); } catch { }
                }
            }

            return null;
        }

        private ImageSourceSelection ReadImageSourceSelection(string settingName)
        {
            var result = new ImageSourceSelection();
            try
            {
                var configPath = Path.Combine(api.Paths.ConfigurationPath, "config.json");
                if (!File.Exists(configPath)) return result;

                using (var document = JsonDocument.Parse(File.ReadAllText(configPath)))
                {
                    JsonElement metadataSettings;
                    JsonElement fieldSettings;
                    if (!TryGetPropertyIgnoreCase(document.RootElement, "MetadataSettings", out metadataSettings) ||
                        !TryGetPropertyIgnoreCase(metadataSettings, settingName, out fieldSettings))
                        return result;

                    result.IsConfigured = true;
                    JsonElement import;
                    if (TryGetPropertyIgnoreCase(fieldSettings, "Import", out import) &&
                        (import.ValueKind == JsonValueKind.True || import.ValueKind == JsonValueKind.False))
                        result.Import = import.GetBoolean();

                    JsonElement sources;
                    if (TryGetPropertyIgnoreCase(fieldSettings, "Sources", out sources) &&
                        sources.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var source in sources.EnumerateArray())
                        {
                            Guid id;
                            if (source.ValueKind == JsonValueKind.String &&
                                Guid.TryParse(source.GetString(), out id) &&
                                !result.Sources.Contains(id))
                                result.Sources.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("Local Games Store: could not read Playnite's " + settingName +
                    " metadata-source settings: " + ex.Message);
            }

            return result;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
        {
            value = default(JsonElement);
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Real text metadata found (art alone does not count: a cover can come from an image-only source).</summary>
        private static bool FoundAnyMetadata(Collected m)
        {
            return m != null && (!string.IsNullOrWhiteSpace(m.Description) || m.Release != null ||
                !Empty(m.Genres) || !Empty(m.Developers) || !Empty(m.Publishers));
        }

        private static readonly Regex FallbackSuffixRegex = new Regex(
            @"(?:\s+|\s*[-:–]\s*)(?:" +
            @"re-?armed|re-?mastered|re-?master|re-?make|re-?loaded|redux|reborn|recompiled|recomp|decomp|" +
            @"hd|hd\s+remaster(?:ed)?|definitive(?:\s+edition)?|complete(?:\s+edition)?|enhanced(?:\s+edition)?|" +
            @"anniversary(?:\s+edition)?|director'?s\s+cut|goty(?:\s+edition)?|game\s+of\s+the\s+year(?:\s+edition)?|" +
            @"deluxe(?:\s+edition)?|special\s+edition|ultimate(?:\s+edition)?|" +
            @"(?:unofficial\s+|fan\s+|native\s+|community\s+)?(?:pc\s+)?(?:port|remake|edition|version|mod)|" +
            @"pc|for\s+pc|windows)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Alternative titles for unofficial ports / remasters ("God Hand Rearmed" -> "God Hand",
        /// "Dynasty Warriors 3: Complete Edition Remastered" -> "Dynasty Warriors 3"). Only used when the exact
        /// title found nothing, so real games with such words in their name still match themselves first.
        /// </summary>
        internal static List<string> BuildFallbackSearchNames(string name)
        {
            var result = new List<string>();
            string cur = (name ?? string.Empty).Trim();
            if (cur.Length == 0) return result;

            // "(Fan Port)", "[PC]" ...
            string noBrackets = Regex.Replace(cur, @"\s*[\(\[][^\)\]]*[\)\]]", string.Empty).Trim();
            if (noBrackets.Length > 0 && !string.Equals(noBrackets, cur, StringComparison.Ordinal))
            {
                result.Add(noBrackets);
                cur = noBrackets;
            }

            // Strip trailing edition / port words repeatedly.
            for (int i = 0; i < 6; i++)
            {
                string next = FallbackSuffixRegex.Replace(cur, string.Empty).Trim(' ', '-', '_', '.', ':', '–');
                if (next.Length < 2 || string.Equals(next, cur, StringComparison.Ordinal)) break;
                result.Add(next);
                cur = next;
            }
            return result;
        }

        private static string CleanMetadataSearchName(string name)
        {
            string cleaned = (name ?? string.Empty).Trim();
            // Executable versions commonly get appended to manually-created game titles. Strip
            // only a trailing version token so a real title such as "Doom V" remains untouched.
            cleaned = Regex.Replace(cleaned,
                @"(?:\s+|[-_])v(?:ersion)?\s*\d+(?:[._-][a-z0-9]+)*\s*$",
                string.Empty, RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned,
                @"\s+(?:build|version)\s*[a-z0-9][a-z0-9._-]*\s*$",
                string.Empty, RegexOptions.IgnoreCase);
            return cleaned.Trim(' ', '-', '_', '.', ':');
        }

        private static T Try<T>(Func<T> f)
        {
            try { return f(); } catch { return default(T); }
        }

        private static bool Take(ref List<MetadataProperty> target, IEnumerable<MetadataProperty> source)
        {
            if (source == null) return false;
            var list = source.Where(p => p != null).ToList();
            if (list.Count == 0) return false;
            target = list;
            return true;
        }

        private void ApplyMetadata(Guid gameId, Collected m)
        {
            var game = api.Database.Games.Get(gameId);
            if (game == null) return;
            bool changed = false;

            // Cleaned-up name first, so the links added below are looked up under the final title.
            if (!string.IsNullOrWhiteSpace(m.FinalName) && !string.Equals(m.FinalName, game.Name, StringComparison.Ordinal))
            {
                log.Info("Local Games Store: renaming '" + game.Name + "' -> '" + m.FinalName + "'");
                game.Name = m.FinalName;
                // The rename raises an update event: remember the new title so it does not trigger a second lookup.
                metadataSeen[gameId] = MetadataRequestKey(game);
                changed = true;
            }

            if (m.Description != null && string.IsNullOrWhiteSpace(game.Description)) { game.Description = m.Description; changed = true; }
            if (m.Release != null && game.ReleaseDate == null) { game.ReleaseDate = m.Release; changed = true; }
            if (m.Critic != null && game.CriticScore == null) { game.CriticScore = m.Critic; changed = true; }
            if (m.Community != null && game.CommunityScore == null) { game.CommunityScore = m.Community; changed = true; }
            if (m.InstallSize != null && game.InstallSize == null) { game.InstallSize = m.InstallSize; changed = true; }

            if (m.Genres != null && Empty(game.GenreIds))
            { game.GenreIds = ResolveIds(m.Genres, n => EnsureGenre(n)); changed |= !Empty(game.GenreIds); }
            if (m.Developers != null && Empty(game.DeveloperIds))
            { game.DeveloperIds = ResolveIds(m.Developers, n => EnsureCompany(n)); changed |= !Empty(game.DeveloperIds); }
            if (m.Publishers != null && Empty(game.PublisherIds))
            { game.PublisherIds = ResolveIds(m.Publishers, n => EnsureCompany(n)); changed |= !Empty(game.PublisherIds); }
            if (m.Tags != null && Empty(game.TagIds))
            { game.TagIds = ResolveIds(m.Tags, n => EnsureTag(n)); changed |= !Empty(game.TagIds); }
            if (m.Features != null && Empty(game.FeatureIds))
            { game.FeatureIds = ResolveIds(m.Features, n => EnsureFeature(n)); changed |= !Empty(game.FeatureIds); }
            if (m.Series != null && Empty(game.SeriesIds))
            { game.SeriesIds = ResolveIds(m.Series, n => EnsureSeries(n)); changed |= !Empty(game.SeriesIds); }
            if (m.AgeRatings != null && Empty(game.AgeRatingIds))
            { game.AgeRatingIds = ResolveIds(m.AgeRatings, n => EnsureAgeRating(n)); changed |= !Empty(game.AgeRatingIds); }
            if (m.Regions != null && Empty(game.RegionIds))
            { game.RegionIds = ResolveIds(m.Regions, n => EnsureRegion(n)); changed |= !Empty(game.RegionIds); }
            if (m.Platforms != null && Empty(game.PlatformIds))
            { game.PlatformIds = ResolveIds(m.Platforms, n => EnsurePlatform(n)); changed |= !Empty(game.PlatformIds); }

            // Merge, never replace: the link pass may already have added links to this game.
            if (m.Links != null && m.LinksWanted)
            {
                if (game.Links == null) game.Links = new ObservableCollection<Link>();
                foreach (var l in m.Links)
                {
                    if (l == null || string.IsNullOrWhiteSpace(l.Url)) continue;
                    if (AcceptLink != null && !AcceptLink(l)) continue;
                    if (game.Links.Any(x => x != null && string.Equals(x.Url, l.Url, StringComparison.OrdinalIgnoreCase))) continue;
                    game.Links.Add(new Link(l.Name, l.Url));
                    changed = true;
                }
            }

            if (m.SteamAppId > 0 && !HasLinkHost(game, "store.steampowered.com"))
            {
                if (game.Links == null) game.Links = new ObservableCollection<Link>();
                game.Links.Add(new Link("Steam", "https://store.steampowered.com/app/" + m.SteamAppId));
                changed = true;
            }

            if (m.GameBananaId > 0 && !HasLinkHost(game, "gamebanana.com"))
            {
                if (game.Links == null) game.Links = new ObservableCollection<Link>();
                game.Links.Add(new Link("GameBanana", "https://gamebanana.com/mods/games/" + m.GameBananaId));
                changed = true;
            }

            // PCGamingWiki / Nexus Mods / Vortex / save links go into the same update as the metadata.
            try { if (ApplyLinks != null && ApplyLinks(game)) changed = true; }
            catch (Exception ex) { log.Warn("Local Games Store: adding links failed: " + ex.Message); }

            // The wiki's API refuses some connections (403), which leaves the page check unanswered. A Steam copy
            // still has its App ID, and PCGamingWiki redirects /api/appid.php?appid=<id> to the game's page. The
            // plugin swaps it for the real page, or drops it, as soon as a page check does answer.
            try
            {
                var settings = S;
                if (m.SteamAppId > 0 && settings != null && settings.AutoAddGameLinks &&
                    !HasLinkHost(game, "pcgamingwiki.com") &&
                    IsPcgwUnresolved != null && IsPcgwUnresolved(game))
                {
                    if (game.Links == null) game.Links = new ObservableCollection<Link>();
                    game.Links.Add(new Link("PCGamingWiki", "https://www.pcgamingwiki.com/api/appid.php?appid=" + m.SteamAppId));
                    changed = true;
                    try { Breadcrumb.Write("PCGW: wiki unreachable, linked via Steam app " + m.SteamAppId); } catch { }
                }
            }
            catch (Exception ex) { log.Warn("Local Games Store: PCGamingWiki fallback link failed: " + ex.Message); }

            // Never replace an existing image reference, and never write an empty result back
            // into the database. This keeps metadata refreshes from clearing existing artwork.
            if (!string.IsNullOrWhiteSpace(m.CoverStored) && string.IsNullOrEmpty(game.CoverImage))
            { game.CoverImage = m.CoverStored; changed = true; }
            if (!string.IsNullOrWhiteSpace(m.BackgroundStored) && string.IsNullOrEmpty(game.BackgroundImage))
            { game.BackgroundImage = m.BackgroundStored; changed = true; }
            if (!string.IsNullOrWhiteSpace(m.IconStored) && string.IsNullOrEmpty(game.Icon))
            { game.Icon = m.IconStored; changed = true; }

            if (changed)
            {
                api.Database.Games.Update(game);
                log.Info("Local Games Store: metadata applied to '" + game.Name + "'");
            }
            else
            {
                log.Info("Local Games Store: no metadata found for '" + game.Name + "'");
            }
        }

        private static bool HasLinkHost(Game game, string host)
        {
            try
            {
                return game?.Links != null && game.Links.Any(l =>
                {
                    Uri u;
                    return l != null && Uri.TryCreate(l.Url, UriKind.Absolute, out u) &&
                           (u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                            u.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
                });
            }
            catch { return false; }
        }

        private static List<Guid> ResolveIds(IEnumerable<MetadataProperty> props, Func<string, Guid?> byName)
        {
            var ids = new List<Guid>();
            foreach (var p in props)
            {
                Guid? id = null;
                var np = p as MetadataNameProperty;
                var ip = p as MetadataIdProperty;
                if (np != null && !string.IsNullOrWhiteSpace(np.Name)) id = byName(np.Name.Trim());
                else if (ip != null && ip.Id != Guid.Empty) id = ip.Id;
                if (id != null && !ids.Contains(id.Value)) ids.Add(id.Value);
            }
            return ids;
        }

        private static bool NameEq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private Guid? EnsureGenre(string n)
        {
            var x = api.Database.Genres.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Genres.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsureCompany(string n)
        {
            var x = api.Database.Companies.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Companies.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsureTag(string n)
        {
            var x = api.Database.Tags.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Tags.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsureFeature(string n)
        {
            var x = api.Database.Features.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Features.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsureSeries(string n)
        {
            var x = api.Database.Series.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Series.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsureAgeRating(string n)
        {
            var x = api.Database.AgeRatings.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.AgeRatings.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsureRegion(string n)
        {
            var x = api.Database.Regions.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Regions.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }
        private Guid? EnsurePlatform(string n)
        {
            var x = api.Database.Platforms.FirstOrDefault(i => NameEq(i.Name, n)) ?? api.Database.Platforms.Add(n);
            return x == null ? (Guid?)null : x.Id;
        }

        /// <summary>Copies a metadata image (URL, local path or raw bytes) into Playnite's database storage.</summary>
        private string StoreImage(MetadataFile file, Guid gameId)
        {
            if (file == null) return null;
            string tmp = null;
            bool deleteTmp = false;
            try
            {
                if (file.HasContent)
                {
                    string ext = SafeExt(file.FileName, ".png");
                    tmp = Path.Combine(Path.GetTempPath(), "pgo_" + Guid.NewGuid().ToString("N") + ext);
                    File.WriteAllBytes(tmp, file.Content);
                    deleteTmp = true;
                }
                else if (!string.IsNullOrWhiteSpace(file.Path))
                {
                    if (file.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        file.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        string ext = ".jpg";
                        try { ext = SafeExt(new Uri(file.Path).AbsolutePath, ".jpg"); } catch { }
                        tmp = Path.Combine(Path.GetTempPath(), "pgo_" + Guid.NewGuid().ToString("N") + ext);
                        using (var wc = new WebClient())
                            wc.DownloadFile(file.Path, tmp);
                        deleteTmp = true;
                    }
                    else if (File.Exists(file.Path))
                    {
                        tmp = file.Path;
                    }
                }
                if (tmp == null || !File.Exists(tmp)) return null;
                var stored = api.Database.AddFile(tmp, gameId);
                return string.IsNullOrWhiteSpace(stored) ? null : stored;
            }
            catch (Exception ex)
            {
                log.Warn("Local Games Store: image download failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (deleteTmp && tmp != null) { try { File.Delete(tmp); } catch { } }
            }
        }

        private static string SafeExt(string nameOrPath, string fallback)
        {
            try
            {
                string e = Path.GetExtension(nameOrPath ?? "");
                if (!string.IsNullOrEmpty(e) && e.Length <= 5 && Regex.IsMatch(e, @"^\.[A-Za-z0-9]+$")) return e;
            }
            catch { }
            return fallback;
        }

        // ------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------

        private void RunOnUi(Action a)
        {
            try
            {
                var d = System.Windows.Application.Current != null ? System.Windows.Application.Current.Dispatcher : null;
                if (d == null || d.CheckAccess()) a();
                else d.Invoke(a);
            }
            catch (Exception ex) { log.Error("Local Games Store UI call failed: " + ex.Message); }
        }

        private void Notify(string text, NotificationType type)
        {
            try
            {
                api.Notifications.Add(new NotificationMessage("LocalGamesStore_" + Guid.NewGuid().ToString("N"), text, type));
            }
            catch { }
        }
    }

    /// <summary>Finds the Steam App ID of a game installed by Steam, from the library's appmanifest files.</summary>
    internal static class SteamInstallInfo
    {
        private static readonly Regex InstallDirRx = new Regex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AppIdRx = new Regex("\"appid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// For a path like "...\steamapps\common\<Folder>\..." returns the App ID of the appmanifest whose
        /// "installdir" is that folder, or 0.
        /// </summary>
        public static int FindAppId(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return 0;
                string p = path.Replace('/', '\\');
                const string marker = "\\steamapps\\common\\";
                int idx = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return 0;

                string steamApps = p.Substring(0, idx + "\\steamapps".Length);
                string rest = p.Substring(idx + marker.Length);
                int slash = rest.IndexOf('\\');
                string folder = slash >= 0 ? rest.Substring(0, slash) : rest;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(steamApps)) return 0;

                foreach (var file in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    string text;
                    try { text = File.ReadAllText(file); } catch { continue; }
                    var dir = InstallDirRx.Match(text);
                    if (!dir.Success || !string.Equals(dir.Groups[1].Value, folder, StringComparison.OrdinalIgnoreCase)) continue;
                    var id = AppIdRx.Match(text);
                    int appId;
                    if (id.Success && int.TryParse(id.Groups[1].Value, out appId) && appId > 0) return appId;
                }
            }
            catch { }
            return 0;
        }
    }

    /// <summary>
    /// Turns names taken from executables / folders into readable titles:
    /// "DarksidersGenesis" -> "Darksiders Genesis", "Game-Win64-Shipping" -> "Game", "my_game_v1.2" -> "my game".
    /// </summary>
    internal static class GameNameCleaner
    {
        private static readonly Regex JunkToken = new Regex(
            @"^(win(32|64)|windows(32|64)|x64|x86|x86_?64|amd64|64-?bit|32-?bit|shipping|binaries|noeditor|retail|portable|dx\d{1,2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Folder / file names that say nothing about the game. Playnite names a dropped exe after its folder,
        // so "...\\Binaries\\Win64\\Game-Win64-Shipping.exe" arrives as "Win64".
        private static readonly HashSet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "win64", "win32", "windows", "windowsnoeditor", "x64", "x86", "x8664", "amd64", "binaries", "binary",
            "bin", "bin32", "bin64", "release", "retail", "shipping", "game", "games", "app", "client", "launcher",
            "start", "main", "run", "play", "data", "common", "steamapps", "programfiles", "programfilesx86", "apps",
            "program", "executable", "exe", "pc", "none", "application"
        };

        /// <summary>True for names such as "Win64", "Binaries" or "bin" that are folder / build names, not titles.</summary>
        public static bool IsGenericName(string name)
        {
            string norm = new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            if (norm.Length == 0) return true;
            if (GenericNames.Contains(norm)) return true;
            // "Win64-Shipping", "Binaries Win64": every word is generic.
            var parts = Regex.Split((name ?? string.Empty).Trim(), @"[\s_\-.]+").Where(x => x.Length > 0).ToList();
            return parts.Count > 0 && parts.All(x => GenericNames.Contains(new string(x.Where(char.IsLetterOrDigit).ToArray())) || JunkToken.IsMatch(x));
        }

        /// <summary>
        /// Best title for a game when only its executable is known: the exe's own name unless that is generic
        /// ("DarksidersGenesis-Win64-Shipping.exe" -> "DarksidersGenesis"), otherwise the nearest folder above it
        /// that is not generic. Returns null when nothing usable is found.
        /// </summary>
        public static string FromPath(string exePath, string installDir)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    string stem = Path.GetFileNameWithoutExtension(exePath);
                    string cleaned = StripJunk(stem);
                    if (!IsGenericName(stem) && !IsGenericName(cleaned)) return cleaned;
                }
                string dir = !string.IsNullOrWhiteSpace(exePath) ? Path.GetDirectoryName(exePath) : installDir;
                if (string.IsNullOrWhiteSpace(dir)) dir = installDir;
                for (int i = 0; i < 12 && !string.IsNullOrWhiteSpace(dir); i++)
                {
                    string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(name)) break;               // drive root
                    string cleaned = StripJunk(name);
                    if (!IsGenericName(name) && !IsGenericName(cleaned)) return cleaned;
                    dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                }
            }
            catch { }
            return null;
        }

        private static readonly Regex BoilerplateTail = new Regex(
            @"[\s_\-]+(application|executable|exe)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True for a name like "MaxPayne Application": Windows exes often carry a file description of
        /// "<Title> Application", which Playnite uses as the game name when an exe is added.
        /// </summary>
        public static bool HasBoilerplate(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && BoilerplateTail.IsMatch(name.Trim());
        }

        /// <summary>"MaxPayne Application" -> "Max Payne". Null when the name has no such filler or nothing is left.</summary>
        public static string FixBoilerplateName(string name)
        {
            if (!HasBoilerplate(name)) return null;
            string nice = SplitWords(StripJunk(name));
            return string.IsNullOrWhiteSpace(nice) || IsGenericName(nice) ? null : nice;
        }

        /// <summary>True for a name that should be replaced: a folder / build name or one ending in "Application".</summary>
        public static bool NeedsNameFix(string name)
        {
            return IsGenericName(name) || HasBoilerplate(name);
        }

        /// <summary>True for names that look generated from a file / folder name rather than typed by a person.</summary>
        public static bool LooksAutoGenerated(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (HasBoilerplate(name)) return true;
            string n = name.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.IndexOf('_') >= 0) return true;
            if (!n.Any(char.IsWhiteSpace)) return true;     // a single run-together word
            foreach (var t in Regex.Split(n, @"[\s\-]+"))
                if (JunkToken.IsMatch(t)) return true;
            return false;
        }

        /// <summary>Drops build / platform junk, versions and bracketed notes; keeps the words as written.</summary>
        public static string StripJunk(string raw)
        {
            string s = (raw ?? string.Empty).Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            s = Regex.Replace(s, @"\[[^\]]*\]|\([^)]*\)", " ");
            s = s.Replace('_', ' ');
            s = Regex.Replace(s, @"(?<![A-Za-z])(v|ver|version)\s*\d[\w.\-]*", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"(?<!\d)\.|\.(?!\d)", " ");   // dots, except inside numbers like 1.2

            var words = new List<string>();
            foreach (var token in Regex.Split(s, @"\s+"))
            {
                if (token.Length == 0) continue;
                if (token.IndexOf('-') >= 0)
                {
                    // "Game-Win64-Shipping": split only when a part is junk, so "Half-Life" stays intact.
                    var parts = token.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Any(pt => JunkToken.IsMatch(pt)))
                    {
                        foreach (var pt in parts) if (!JunkToken.IsMatch(pt)) words.Add(pt);
                        continue;
                    }
                }
                if (JunkToken.IsMatch(token)) continue;
                words.Add(token);
            }
            // A trailing bare "Win" / "Windows" is a platform tag, not part of the title.
            while (words.Count > 1 && Regex.IsMatch(words[words.Count - 1], @"^(win|windows|pc|application|executable|exe)$", RegexOptions.IgnoreCase))
                words.RemoveAt(words.Count - 1);

            string result = string.Join(" ", words).Trim(' ', '-', ':');
            return result.Length > 0 ? result : (raw ?? string.Empty).Trim();
        }

        /// <summary>Splits run-together words: "DarksidersGenesis" -> "Darksiders Genesis", "Left4Dead" -> "Left 4 Dead".</summary>
        public static string SplitWords(string name)
        {
            string s = (name ?? string.Empty).Trim();
            if (s.Length == 0) return s;
            bool singleToken = !s.Any(char.IsWhiteSpace);
            var words = new List<string>();
            foreach (var token in Regex.Split(s, @"\s+"))
            {
                // Short tokens ("DiRT", "iPad", "McCoy") are left alone unless they are the whole name.
                if (token.Length == 0 || (!singleToken && token.Length < 7) || token.IndexOf('-') >= 0 ||
                    token.IndexOf(':') >= 0) { words.Add(token); continue; }
                string t = Regex.Replace(token, @"(?<=[a-z])(?=[A-Z])", " ");
                t = Regex.Replace(t, @"(?<=[A-Z])(?=[A-Z][a-z])", " ");
                t = Regex.Replace(t, @"(?<=\d)(?=[A-Z][a-z])", " ");
                if (char.IsUpper(token[0])) t = Regex.Replace(t, @"(?<=[a-z]{3})(?=\d)", " ");
                words.Add(t);
            }
            return Regex.Replace(string.Join(" ", words), @"\s+", " ").Trim();
        }
    }

    /// <summary>
    /// Runs background jobs with an optional Playnite progress dialog.
    /// Jobs tolerate a null progress argument and simply run to completion.
    /// </summary>
    internal static class OverlayProgressDialog
    {
        public static void Run(
            IPlayniteAPI api,
            Action<GlobalProgressActionArgs> action,
            GlobalProgressOptions options,
            bool showProgress = false)
        {
            if (action == null) return;
            if (showProgress && api?.Dialogs != null)
                api.Dialogs.ActivateGlobalProgress(action, options);
            else
                action(null);
        }
    }
}
