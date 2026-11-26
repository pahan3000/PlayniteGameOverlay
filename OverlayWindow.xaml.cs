using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Linq;
using System.Diagnostics;

namespace PlayniteGameOverlay
{
    public partial class OverlayWindow : Window
    {
        private readonly Logger _logger;
        private readonly GameStateManager _gameStateManager;
        private readonly BatteryManager _batteryManager;

        private readonly DispatcherTimer clockTimer;
        private DispatcherTimer batteryUpdateTimer;
        private DispatcherTimer controllerTimer;
        private DispatcherTimer mouseUnlockTimer;
        private DispatcherTimer mouseHoverTimer;
        private Point? _lastPolledMouseScreen;
        private bool _fpsDragging;
        private bool _audioDragging;
        private Point _audioDragStart;
        private double _audioOriginX;
        private double _audioOriginY;
        private Point _fpsDragStart;
        private double _fpsOriginX;
        private double _fpsOriginY;

        private DateTime lastUpTime = DateTime.MinValue;
        private DateTime lastDownTime = DateTime.MinValue;
        private DateTime lastLeftTime = DateTime.MinValue;
        private DateTime lastRightTime = DateTime.MinValue;

        OverlaySettings Settings;

        /// <summary>Called after panel drag so the plugin can persist offsets.</summary>
        public Action SaveSettingsCallback { get; set; }

        public bool Active = false;

        /// <summary>The overlay was opened over a true exclusive-fullscreen game (see WindowHelper).</summary>
        private bool _exclusiveMode;
        private Brush _rootDefaultBackground;
        private BitmapImage _backdropBitmap;
        private string _backdropBitmapPath;

        private static readonly Playnite.SDK.ILogger _olog = Playnite.SDK.LogManager.GetLogger("GameOverlay.Window");
        private DispatcherTimer _softPauseTimer;

        public OverlayWindowViewModel ViewModel { get; private set; }

        public OverlayWindow(OverlaySettings settings)
        {
            InitializeComponent();
            _rootDefaultBackground = RootGrid.Background;
            // The volume panel sits under the achievements panel in the left column. When that panel
            // appears or disappears, the volume panel must not move (it would overlap the FPS panel).
            AchievementPanel.IsVisibleChanged += (s0, e0) => KeepAudioPanelInPlace();
            AchievementPanel.SizeChanged += (s0, e0) => KeepAudioPanelInPlace();
            Settings = settings;
            ApplyTheme(settings);

            _logger = new Logger(settings?.DebugMode ?? false);
            _gameStateManager = new GameStateManager(_logger);
            _batteryManager = new BatteryManager();

            // Initialize ViewModel
            ViewModel = new OverlayWindowViewModel();
            ViewModel.SetVolumeControlMode(settings?.VolumeControlMode);
            this.DataContext = ViewModel;

            // Smooth pointer drag (native Thumb is clunky inside the scaled Viewbox)
            HookSmoothSliders();

            // Connect ViewModel events to handlers
            ViewModel.HideOverlayRequested += () => {
                int pid = ViewModel.ProcessId;
                try
                {
                    if (WindowHelper.IsParked)
                        WindowHelper.ResumeAndRestore();
                    else
                        WindowHelper.ResumeGameProcess();
                }
                catch { }
                try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }

                bool wasExclusive = _exclusiveMode;
                _exclusiveMode = false;
                Active = false;
                try { PauseTimers(); } catch { }
                if (wasExclusive && ViewModel != null && ViewModel.IsGameRunning && WindowHelper.IsRestorableGameProcess(pid))
                {
                    // Bring the game back first so the desktop never flashes between overlay and game.
                    try
                    {
                        WindowHelper.SettleAfterResume();
                        var gh = WindowHelper.FindMainWindow(pid);
                        if (gh != IntPtr.Zero) WindowHelper.SafeRestoreAndFocus(gh);
                    }
                    catch { }
                }
                try { base.Hide(); } catch { }

                // Focus game only after overlay is gone. Use SafeRestoreAndFocus — never
                // AttachThreadInput to a process that may still be waking from NtSuspend.
                if (ViewModel != null && ViewModel.IsGameRunning && pid > 0)
                {
                    try
                    {
                        var hwnd = WindowHelper.FindMainWindow(pid);
                        if (hwnd != IntPtr.Zero)
                            WindowHelper.SafeRestoreAndFocus(hwnd);
                    }
                    catch { }
                }
            };
            ViewModel.ShowPlayniteRequested += (fullscreen) => {
                bool wasExclusive = _exclusiveMode;
                int gamePid = ViewModel?.ProcessId ?? 0;
                IntPtr gameHwnd = IntPtr.Zero;
                if (gamePid > 0)
                {
                    try { gameHwnd = WindowHelper.FindMainWindow(gamePid); } catch { }
                }

                Active = false;
                PauseTimers();
                try
                {
                    if (WindowHelper.IsParked)
                        // Clear the suspend state without bringing an exclusive game back
                        // over the desktop while Playnite is being shown.
                        WindowHelper.ResumeAndRestore(restoreWindow: false);
                    else
                        WindowHelper.ResumeGameProcess();
                }
                catch { }

                if (wasExclusive && gamePid > 0)
                {
                    try
                    {
                        IntPtr currentGameHwnd = WindowHelper.FindMainWindow(gamePid);
                        if (currentGameHwnd != IntPtr.Zero)
                            gameHwnd = currentGameHwnd;
                    }
                    catch { }
                }

                if (wasExclusive && gameHwnd != IntPtr.Zero)
                {
                    // Exclusive/OpenGL/Vulkan surfaces can remain visible over Playnite unless
                    // the game has yielded its display. Resume first, then request minimize.
                    try
                    {
                        if (!WindowHelper.HasGameYieldedDisplay(gameHwnd))
                        {
                            WindowHelper.RequestGameMinimize(gameHwnd);
                            for (int i = 0; i < 40 && !WindowHelper.HasGameYieldedDisplay(gameHwnd); i++)
                                System.Threading.Thread.Sleep(15);
                        }
                    }
                    catch { }
                }

                _exclusiveMode = false;
                WindowHelper.ReleaseMouseLock(nudgeCursor: false);
                base.Hide();
                OnShowPlayniteRequested?.Invoke();
            };
            ViewModel.CloseGameRequested += async () => {
                // CRITICAL: fully unfreeze before CloseMainWindow.
                // 1) Suspended CloseMainWindow freezes Playnite's UI thread.
                // 2) Resume + immediate kill on DX12/UE5 often triggers "GPU Crash dump".
                //    Give the GPU a short settle after NtResume before any close/kill.
                try
                {
                    Active = false;
                    PauseTimers();
                    try { WindowHelper.SoftResumeFromOverlay(); } catch { }
                    try { WindowHelper.ForceClearAllSuspendState(); } catch { }
                    try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
                    try
                    {
                        this.Topmost = false;
                        base.Hide();
                    }
                    catch
                    {
                        try { base.Hide(); } catch { }
                    }
                }
                catch { }

                try { await System.Threading.Tasks.Task.Delay(400); } catch { }
                CloseGame();
            };
            ViewModel.SuspendGameRequested += () => {
                try
                {
                    int pid = ViewModel.ProcessId;
                    // Unfreeze overlay soft-suspend BEFORE park (prevents hang)
                    WindowHelper.ResumeGameProcess();
                    Active = false;
                    PauseTimers();
                    WindowHelper.ReleaseMouseLock(nudgeCursor: false);
                    // Use base.Hide so custom Hide() does not ResumeAndRestore before we park
                    base.Hide();

                    // Never park Playnite itself; SuspendAndMinimize also guards this
                    if (pid > 0 && !WindowHelper.IsProtectedProcess(pid))
                    {
                        try
                        {
                            WindowHelper.SuspendAndMinimize(pid);   // minimize + freeze
                        }
                        catch
                        {
                            // If park throws mid-way, force resume so the game is not left frozen
                            try { WindowHelper.ResumeAndRestore(); } catch { }
                        }
                    }
                }
                catch { /* best-effort */ }
            };
            ViewModel.ExecuteShortcutRequested += ExecuteShortcut;
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(OverlayWindowViewModel.IsAchievementBrowserOpen) &&
                    ViewModel.IsAchievementBrowserOpen)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (AchievementListBox != null && AchievementListBox.Items.Count > 0)
                        {
                            AchievementListBox.Focus();
                            AchievementListBox.SelectedIndex = 0;
                            var item = AchievementListBox.ItemContainerGenerator
                                .ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem;
                            item?.Focus();
                        }
                    }), DispatcherPriority.Loaded);
                }
            };

            // Set the window to fullscreen
            this.WindowState = WindowState.Maximized;

            // Initialize and start the clock timer
            clockTimer = new DispatcherTimer();
            clockTimer.Interval = TimeSpan.FromSeconds(1);
            clockTimer.Tick += UpdateClock;
            clockTimer.Start();

            // While overlay is open, some games re-apply ClipCursor every frame.
            // Periodically release so the mouse stays free for the UI.
            mouseUnlockTimer = new DispatcherTimer();
            mouseUnlockTimer.Interval = TimeSpan.FromMilliseconds(150);
            mouseUnlockTimer.Tick += (s, e) =>
            {
                if (Active && IsVisible)
                    WindowHelper.ReleaseMouseLock(
                        nudgeCursor: false,
                        restoreCursors: false,
                        releaseCapture: false);
            };
            // Some games keep mouse capture while the overlay is open. In that case WPF
            // receives clicks, but can miss WM_MOUSEMOVE and never update IsMouseOver.
            // Poll the system cursor so hover feedback still works while the game is paused.
            mouseHoverTimer = new DispatcherTimer();
            mouseHoverTimer.Interval = TimeSpan.FromMilliseconds(40);
            mouseHoverTimer.Tick += (s, e) => PollMouseHover();


            InitializeBatteryDisplay();

            // Disable keyboard navigation
            this.PreviewKeyDown += OverlayWindow_PreviewKeyDown;
            this.PreviewMouseMove += OverlayWindow_PreviewMouseMove;
            this.PreviewMouseDown += OverlayWindow_PreviewMouseDown;

            // Initialize buttons and setup from settings
            ViewModel.DebugVisible = settings?.DebugMode ?? false;
            ViewModel.AspectRatio = settings?.AspectRatio ?? AspectRatio.Portrait;
            ViewModel.ShowPlayniteButton = settings?.ShowPlayniteButton ?? true;
            ViewModel.ShowFpsPanel = settings?.ShowFpsLimiterPanel ?? true;
            ViewModel.ShowAudioPanel = settings?.ShowGameVolume ?? true;
            ViewModel.ShowGamepadHints = settings?.ShowGamepadHints ?? true;
            ViewModel.PromptStyle = settings?.ButtonPromptStyle ?? ButtonPromptStyle.Auto;
            ViewModel.SonyPadDetected = PadPrompts.SonyDetected;
            ViewModel.ShowSuspendButton = settings?.EnableSuspendMinimize ?? true;
            ViewModel.AchievementStyle = settings?.AchievementStyle ?? "Console";
            // Battery visibility applied in InitializeBatteryDisplay via Settings
            ViewModel.InitializeShortcutButtons(settings);
        }

        private void InitializeBatteryDisplay()
        {
            bool showBattery = Settings?.ShowBattery ?? true;
            if (!showBattery || !_batteryManager.HasBattery)
            {
                ViewModel.HasBattery = false;
            }
            else
            {
                ViewModel.HasBattery = true;
                UpdateBattery(null, EventArgs.Empty);
                batteryUpdateTimer = new DispatcherTimer();
                batteryUpdateTimer.Interval = TimeSpan.FromSeconds(60);
                batteryUpdateTimer.Tick += UpdateBattery;
                batteryUpdateTimer.Start();
            }
        }

        private void UpdateBattery(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var batteryStatus = _batteryManager.GetStatus();
                ViewModel.BatteryPercentage = ((int)batteryStatus.Percentage*100);
            });
        }

        private void UpdateClock(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Update session time if game is active
                ViewModel.UpdateSessionTime();
                ViewModel.UpdateCurrentTime();
            });
        }

        public void UpdateGameOverlay(GameOverlayData gameData)
        {
            Dispatcher.Invoke(() =>
            {
                ViewModel.UpdateFromGameOverlayData(gameData);
                ApplyTheme(Settings);

                if (ViewModel.DebugVisible)
                {
                    if (gameData == null)
                    {
                        ViewModel.UpdateDebugInfo("No active game");
                    }
                    else
                    {
                        ViewModel.UpdateDebugInfo($"DEBUG INFO:\n" +
                            $"Game: {gameData.GameName}\n" +
                            $"PID: {gameData.ProcessId}\n" +
                            $"Start Time: {gameData.GameStartTime}");
                    }
                }
            });
        }

        private void CloseGame()
        {
            _gameStateManager.CloseGame(CreateGameOverlayDataFromViewModel());
            this.Hide();
        }

        /// <summary>True while another window (Steam friends, a web page) is in front of the open overlay.</summary>
        public bool InputPaused { get; private set; }

        /// <summary>
        /// Opens Steam's friends list while the overlay stays open. The overlay stays on top (so the
        /// taskbar never shows) and the friends window is lifted above it, so one click is enough.
        /// </summary>
        private DateTime _friendsLastOpenUtc = DateTime.MinValue;
        private static readonly string[] FriendsTitleWords =
            { "Friends", "Freunde", "Amis", "Amigos", "Amici", "Друзья", "好友", "フレンド", "친구" };

        private void OpenSteamFriends()
        {
            // One click = one open (ignore a repeated press while it is still opening).
            if ((DateTime.UtcNow - _friendsLastOpenUtc).TotalSeconds < 1.5) return;
            _friendsLastOpenUtc = DateTime.UtcNow;

            // Windows that already exist, so the new friends window can be recognised.
            var before = new System.Collections.Generic.HashSet<IntPtr>(WindowHelper.GetSteamWindows());
            InputPaused = true;            // controller / keys belong to the friends window now
            WindowHelper.AllowForegroundHandoff();
            try
            {
                Breadcrumb.Write("Opening Steam friends");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://open/friends")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                InputPaused = false;
                return;
            }

            // Steam may take a few seconds to start: keep looking, lift the window above the overlay and
            // make sure it really has the focus.
            int ticks = 0, focusTries = 0;
            IntPtr target = IntPtr.Zero;
            var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            watch.Tick += (s, e) =>
            {
                ticks++;
                try
                {
                    if (target == IntPtr.Zero)
                    {
                        var windows = WindowHelper.GetSteamWindows();
                        // The friends list by its title (also when it was already open behind the overlay) ...
                        foreach (var w in windows)
                        {
                            var sb = new System.Text.StringBuilder(256);
                            GetWindowText(w, sb, sb.Capacity);
                            var title = sb.ToString();
                            if (FriendsTitleWords.Any(word => title.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                            { target = w; break; }
                        }
                        // ... otherwise the window that just appeared.
                        if (target == IntPtr.Zero)
                            foreach (var w in windows)
                                if (!before.Contains(w)) { target = w; break; }
                    }

                    if (target != IntPtr.Zero)
                    {
                        focusTries++;
                        if (WindowHelper.BringAboveOverlay(target) || focusTries >= 12)
                        {
                            watch.Stop();
                            return;
                        }
                    }
                }
                catch { }
                if (ticks >= 100)   // ~15 s
                {
                    watch.Stop();
                    InputPaused = false;
                }
            };
            watch.Start();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        private DateTime _shownUtc = DateTime.MinValue;
        private DispatcherTimer _altTabTimer;

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            try { ScheduleCloseWhenUserLeaves(); } catch { }
        }

        /// <summary>
        /// Alt+Tab to another app: the overlay must not stay on top of it. Waits a moment (so focus changes
        /// caused by the overlay itself, the game or Windows do not count), then closes and resumes the game.
        /// </summary>
        private void ScheduleCloseWhenUserLeaves()
        {
            if (!Active || !IsVisible || InputPaused) return;
            if ((DateTime.UtcNow - _shownUtc).TotalSeconds < 2.5) return;   // still opening / game stepping aside

            _altTabTimer?.Stop();
            _altTabTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _altTabTimer.Tick += (s, e) =>
            {
                _altTabTimer.Stop();
                try
                {
                    if (Active && IsVisible && !InputPaused && !IsActive &&
                        WindowHelper.IsAnotherAppInForeground(new WindowInteropHelper(this).Handle, ViewModel?.ProcessId ?? 0))
                        CloseBecauseUserLeft();
                }
                catch { }
            };
            _altTabTimer.Start();
        }

        private void CloseBecauseUserLeft()
        {
            Breadcrumb.Write("Overlay closed: switched to another app");
            Active = false;
            _exclusiveMode = false;
            PauseTimers();
            try { WindowHelper.ResumeGameProcess(); } catch { }   // no focus grab: the user is in another app
            WindowHelper.ReleaseMouseLock(nudgeCursor: false);
            try { this.Topmost = false; } catch { }
            base.Hide();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // Back from the other window: the overlay is in charge again.
            if (InputPaused)
            {
                InputPaused = false;
                try { this.Topmost = true; } catch { }
            }
        }

        private void ExecuteShortcut(ShortcutButtonViewModel btn)
        {
            switch (btn.Type)
            {
                case ShortcutType.Discord:
                    ButtonActions.FocusOrLaunchDiscord();
                    break;
                case ShortcutType.SteamFriends:
                    OpenSteamFriends();
                    return;
                case ShortcutType.Path:
                    ButtonActions.FocusOrLaunch(btn.ShortcutCommand);
                    break;
                case ShortcutType.KbdShortcut:
                    {
                        // Hide first (this also unfreezes the game), then send the keys only once the
                        // game is running again and has the foreground. Sending immediately hit a
                        // frozen / unfocused game, so RTSS (and similar) never saw the hotkey.
                        int pid = ViewModel?.ProcessId ?? 0;
                        this.Hide();
                        ButtonActions.ExecuteKbdShortcutWhenGameReady(btn.ShortcutCommand, pid);
                        return;
                    }
            }

            // Return to game after executing other shortcuts
            this.Hide();
        }

        private GameOverlayData CreateGameOverlayDataFromViewModel()
        {
            // Create a GameOverlayData object from the ViewModel
            return new GameOverlayData
            {
                GameName = ViewModel.GameTitle,
                ProcessId = ViewModel.ProcessId,
                GameStartTime = ViewModel.SessionStartTime,
                Playtime = ViewModel.TotalPlaytime,
                CoverImagePath = ViewModel.CoverImagePath
                // Add other properties as needed
            };
        }


        /// <summary>
        /// Pull current FPS Limiter settings into the panel (call before Show).
        /// Prefer per-game profile when gameId is set so the overlay matches what RTSS applied.
        /// </summary>
        public void RefreshFpsFromLimiter(Playnite.SDK.IPlayniteAPI api, Guid? gameId = null)
        {
            try
            {
                if (ViewModel == null || api == null)
                    return;

                // Prefer running game id if caller did not pass one
                if (!gameId.HasValue || gameId.Value == Guid.Empty)
                {
                    try
                    {
                        if (ViewModel.GameId != Guid.Empty)
                            gameId = ViewModel.GameId;
                    }
                    catch { }
                }

                Playnite.SDK.Models.Game game = null;
                try
                {
                    if (gameId.HasValue && gameId.Value != Guid.Empty)
                        game = api.Database?.Games?.Get(gameId.Value);
                }
                catch { }

                // Always re-pull presets + refresh rates from extension (pass game for per-monitor lists)
                try
                {
                    ViewModel.FixedRefreshDisplayMode = FpsLimiterBridge.IsFixedRefreshDisplay(api);
                    var presets = FpsLimiterBridge.GetPresets(api, game);
                    ViewModel.LoadPresets(presets);
                    var rates = FpsLimiterBridge.GetSupportedRefreshRates(api);
                    if (rates != null && rates.Count > 0)
                        ViewModel.SetRefreshRates(rates);
                }
                catch { /* keep previous lists */ }

                // Config/session is authoritative for backend + limit
                var snap = FpsLimiterSettingsSync.Read(api, gameId);
                bool useSk = snap.UseSpecialK;
                try
                {
                    if (FpsLimiterBridge.IsAvailable(api))
                    {
                        if (FpsLimiterBridge.ShouldUseSpecialKBackend(api, game))
                            useSk = true;
                    }
                }
                catch { }

                ViewModel.SelectedFpsBackend = useSk
                    ? OverlayFpsBackend.SpecialK
                    : OverlayFpsBackend.Rtss;

                // 1) Live limit + effective sync mode from LimiterService
                bool applied = false;
                try
                {
                    if (game != null && FpsLimiterBridge.IsAvailable(api))
                    {
                        var live = FpsLimiterBridge.GetGameLimit(api, game);
                        if (live.HasValue)
                        {
                            ViewModel.SelectedFpsLimit = live.Value;
                            applied = true;
                        }
                        var effSync = FpsLimiterBridge.GetEffectiveGameSyncMode(api, game);
                        if (effSync.HasValue && effSync.Value >= 0 && effSync.Value <= 5)
                            ViewModel.SelectedSyncMode = (OverlayFpsSyncMode)effSync.Value;
                        else if (snap.Found)
                            ViewModel.SelectedSyncMode = snap.SyncMode;
                    }
                    else if (snap.Found)
                    {
                        ViewModel.SelectedSyncMode = snap.SyncMode;
                    }
                }
                catch
                {
                    if (snap.Found)
                        ViewModel.SelectedSyncMode = snap.SyncMode;
                }

                // 2) Fallback: config.json ActiveSessions / GameLimits for limit + markers
                if (!applied && snap.Found)
                {
                    ViewModel.SelectedFpsLimit = snap.Enabled ? snap.FrameLimit : 0;
                    ViewModel.ReflexMarkersEnabled = snap.ReflexMarkersEnabled;
                    if (ViewModel.SelectedSyncMode == OverlayFpsSyncMode.Async && snap.SyncMode != OverlayFpsSyncMode.Async)
                        ViewModel.SelectedSyncMode = snap.SyncMode;
                }
                else if (snap.Found)
                {
                    ViewModel.ReflexMarkersEnabled = snap.ReflexMarkersEnabled;
                }

                if (FpsLimiterBridge.IsAvailable(api))
                    ViewModel.FpsStatusText = useSk ? "Backend: Special K" : "Backend: RTSS";
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Applies the Appearance settings by replacing the theme resources the XAML refers to.
        /// Defaults reproduce the original look exactly.
        /// </summary>
        public void ApplyTheme(OverlaySettings s)
        {
            try
            {
                ViewModel.AchievementStyle = s?.AchievementStyle ?? "Console";
                var t = OverlayTheme.Compute(s, ViewModel?.CoverImagePath);
                Resources["AccentBrush"] = FrozenBrush(t.Accent);
                Resources["AccentDarkBrush"] = FrozenBrush(t.AccentDark);
                Resources["PanelBackgroundBrush"] = OverlayTheme.CreatePanelBrush(t);
                Resources["SurfaceBrush"] = OverlayTheme.CreateSurfaceBrush(t, t.Surface);
                Resources["SurfaceHoverBrush"] = OverlayTheme.CreateSurfaceBrush(t, t.SurfaceHover);
                Resources["SelectedSurfaceBrush"] = OverlayTheme.CreateSelectedBrush(t);
                // "Active" state (e.g. the current FPS preset): accent glass pane, bright rim and soft glow.
                Resources["SelectedAccentBrush"] = OverlayTheme.CreateAccentSelectedBrush(t);
                Resources["SelectedAccentRimBrush"] = OverlayTheme.CreateAccentRimBrush(t);
                Resources["AccentGlowColor"] = t.Accent;
                Resources["SelectedForegroundBrush"] = FrozenBrush(t.SelectedForeground);
                Resources["DangerSurfaceBrush"] = FrozenBrush(t.Glass
                    ? t.Surface
                    : Color.FromRgb(0x2A, 0x22, 0x22));
                Resources["DangerForegroundBrush"] = FrozenBrush(t.Glass
                    ? Colors.White
                    : Color.FromRgb(0xFF, 0x8A, 0x80));
                Resources["CoverFrameBrush"] = t.Glass
                    ? OverlayTheme.CreatePanelBrush(t)
                    : FrozenBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
                Resources["PanelCornerRadius"] = new CornerRadius(t.PanelRadius);
                Resources["ControlCornerRadius"] = new CornerRadius(t.ControlRadius);
                Resources["PanelBorderBrush"] = OverlayTheme.CreatePanelBorderBrush(t);
                Resources["PanelBorderThickness"] = new Thickness(t.PanelBorderThickness);

                // Text-heavy surfaces stay readable when frosted glass is on: the achievement summary and
                // list use (near) opaque fills instead of the translucent glass control surfaces.
                Color solidPanel = Color.FromRgb(t.Panel.R, t.Panel.G, t.Panel.B);
                Func<byte, byte> glassAlpha = alpha => (byte)Math.Round(alpha * t.GlassStrength);
                Resources["AchievementPanelBrush"] = t.Glass
                    ? OverlayTheme.CreatePanelBrush(t, true, 0xF2)
                    : OverlayTheme.CreatePanelBrush(t);
                Func<byte, Color> whiteA = al => Color.FromArgb(al, 0xFF, 0xFF, 0xFF);
                Color selectionWash = Color.FromArgb(t.Glass
                    ? (byte)Math.Round(0x14 + 0x14 * t.GlassStrength)
                    : (byte)0x22, 0xFF, 0xFF, 0xFF);
                // Rows: barely-there lift on glass (instead of a flat grey slab), the original greys otherwise.
                Resources["AchievementItemBrush"] = FrozenBrush(t.Glass ? OverlayTheme.Blend(solidPanel, whiteA(glassAlpha(0x0D))) : t.Surface);
                Resources["AchievementItemHoverBrush"] = FrozenBrush(t.Glass ? OverlayTheme.Blend(solidPanel, whiteA(glassAlpha(0x1A))) : t.SurfaceHover);
                // Focus rims follow each panel preset with a neutral, contrast-aware treatment.
                Resources["FocusRimBrush"] = OverlayTheme.CreateFocusRimBrush(t);
                Resources["FocusRimThickness"] = new Thickness(t.Glass ? 1 : 2);
                Resources["AchievementItemBorderBrush"] = FrozenBrush(t.Glass ? whiteA(glassAlpha(0x12)) : Color.FromArgb(0, 0, 0, 0));
                // Selection is a neutral panel lift with the same contrast-aware rim as other controls.
                Resources["AchievementSelectedBrush"] = FrozenBrush(OverlayTheme.Blend(solidPanel, selectionWash));
                Resources["AchievementSelectedBorderBrush"] = OverlayTheme.CreateFocusRimBrush(t);
                // Achievement browser window chrome.
                Resources["AchievementWindowBrush"] = t.Glass
                    ? OverlayTheme.CreatePanelBrush(t, true, 0xFF)
                    : FrozenBrush(Color.FromRgb(0x16, 0x18, 0x1D));
                Resources["AchievementWindowBorderBrush"] = t.Glass
                    ? OverlayTheme.CreatePanelBorderBrush(t)
                    : FrozenBrush(t.SurfaceHover);
                Resources["AchievementHeaderBrush"] = t.Glass
                    ? FrozenBrush(whiteA(glassAlpha(0x08)))
                    : OverlayTheme.CreatePanelBrush(t);
                Resources["AchievementDividerBrush"] = FrozenBrush(t.Glass ? whiteA(glassAlpha(0x16)) : t.SurfaceHover);

                // Alpha 1 (not 0) keeps the window hit-testable, as the original #01000000 did.
                Background = FrozenBrush(Color.FromArgb((byte)Math.Max(1, Math.Round(t.Dim * 255)), 0, 0, 0));
            }
            catch { /* keep whatever theme is currently applied */ }
        }

        private static SolidColorBrush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public void ShowOverlay()
        {
            // Decide this BEFORE the overlay takes the foreground (the state describes the
            // foreground app). A repeated open while already open keeps the earlier decision.
            bool wasOpen = Active && IsVisible;
            InputPaused = false;
            _shownUtc = DateTime.UtcNow;
            bool exclusive = wasOpen ? _exclusiveMode : false;
            if (!wasOpen && (Settings?.ExclusiveFullscreenSupport ?? true))
            {
                int detectPid = ViewModel?.ProcessId ?? 0;
                IntPtr detectHwnd = IntPtr.Zero;
                if (detectPid > 0 && !WindowHelper.IsParked)
                {
                    try { detectHwnd = WindowHelper.FindMainWindow(detectPid); } catch { }
                }
                exclusive = WindowHelper.IsFullscreenGameNeedingFallback(detectHwnd);
            }
            _exclusiveMode = exclusive;
            Breadcrumb.Write("ShowOverlay exclusive=" + exclusive + " wasOpen=" + wasOpen);

            ApplyTheme(Settings);
            _overlayShownUtc = DateTime.UtcNow;
            _mouseAnchor = null;
            _lastPolledMouseScreen = null;
            _focusHiddenByMouse = false;
            if (!exclusive)
                ClearBackdropImage();
            if (exclusive)
            {
                ApplyExclusiveBackdrop();
                ApplyBackdropImage();
                if (!wasOpen)
                    _olog.Info("ShowOverlay: fullscreen game that hides overlays (exclusive D3D / Vulkan / DXVK / OpenGL) detected; using desktop backdrop and not restoring the game while the overlay is open.");
            }
            Active = true;
            // Resume timers and immediately update
            ResumeTimers();

            // Update immediately
            UpdateClock(null, EventArgs.Empty);
            UpdateBattery(null, EventArgs.Empty);

            int gamePid = ViewModel?.ProcessId ?? 0;

            // If the game was parked (Suspend Game), fully unpark first.
            // Opening the overlay on top of NtSuspendProcess state re-suspends / fights
            // minimize and can lock the overlay so it never closes.
            if (WindowHelper.IsParked)
            {
                try { WindowHelper.ResumeAndRestore(); } catch { }
            }

            IntPtr gameHwnd = WindowHelper.FindMainWindow(gamePid);
            bool pauseGame = Settings?.PauseGameWhileOverlayOpen ?? false;

            // Show overlay first, then optional soft-pause (main PID only — no child suspend).
            Dispatcher.Invoke(() =>
            {
                // Restore last dragged panel positions (do not reset)
                ApplySavedPanelOffsets();

                this.Show();
                this.Topmost = true;
                this.Activate();
                var overlayHwnd = new WindowInteropHelper(this).Handle;
                WindowHelper.EnsureTopmost(overlayHwnd);
                // A controller press gives Playnite no input event, so Windows refuses a plain
                // Activate() and the game keeps the foreground. Force it now, while the game is
                // still running (attaching input to a frozen thread is what hangs).
                if (!WindowHelper.IsOwnProcessForeground())
                {
                    try { WindowHelper.ForceFocusWindow(overlayHwnd); } catch { }
                }
                if (exclusive && !wasOpen)
                {
                    // Step the game aside (some DXVK/Vulkan/OpenGL games do not minimize on focus loss), then
                    // take the foreground back: when the game minimizes, Windows hands focus to
                    // another window, and an overlay without focus gets no Esc / controller input.
                    int ticks = 0;
                    var stepAside = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    stepAside.Tick += (s2, e2) =>
                    {
                        ticks++;
                        try
                        {
                            if (!Active) { stepAside.Stop(); return; }
                            if (ticks == 1 && gameHwnd != IntPtr.Zero && !WindowHelper.HasGameYieldedDisplay(gameHwnd))
                                WindowHelper.RequestGameMinimize(gameHwnd);
                            if (ticks >= 2 && !WindowHelper.IsOwnProcessForeground())
                            {
                                this.Topmost = true;
                                WindowHelper.EnsureTopmost(overlayHwnd);
                                WindowHelper.ForceFocusWindow(overlayHwnd);
                            }
                        }
                        catch { }
                        if (ticks >= 8) stepAside.Stop(); // ~800 ms
                    };
                    stepAside.Start();
                }
                ViewModel?.RefreshAudioState();
                ViewModel?.RefreshAudioDevices();
                RestoreLastFocus();

                WindowHelper.ReleaseMouseLock(nudgeCursor: true);

                // Optional soft-pause. The game is only frozen once the overlay really is the
                // foreground window (see ScheduleSoftPause); otherwise the game would freeze on
                // screen with the overlay hidden behind it.
                bool softPause = gamePid > 0 && pauseGame && !WindowHelper.IsSoftPauseUnsafe(gamePid);
                if (softPause)
                    ScheduleSoftPause(gamePid, overlayHwnd, exclusive, gameHwnd);

                _olog.Info($"ShowOverlay: pid={gamePid}, pauseSetting={pauseGame}, softPauseScheduled={softPause}, " +
                           $"overlayShown={WindowHelper.IsWindowShownAndRestored(overlayHwnd)}, " +
                           $"overlayIsForeground={WindowHelper.IsOwnProcessForeground()}");

                // Only fight shell minimize when the game is not going to be soft-paused
                // In exclusive mode the game minimizes itself on focus loss; restoring it here would
                // re-enter exclusive fullscreen and cover the overlay again.
                if (!softPause && !exclusive && (gameHwnd != IntPtr.Zero || gamePid > 0))
                {
                    int attempts = 0;
                    var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                    restoreTimer.Tick += (s, e) =>
                    {
                        attempts++;
                        try
                        {
                            if (gameHwnd == IntPtr.Zero)
                                gameHwnd = WindowHelper.FindMainWindow(gamePid);

                            if (gameHwnd != IntPtr.Zero && WindowHelper.IsIconic(gameHwnd))
                            {
                                WindowHelper.RestoreWindowNoActivate(gameHwnd);
                                this.Topmost = true;
                                WindowHelper.EnsureTopmost(overlayHwnd);
                                this.Activate();
                                FocusFirstMainActionButton();
                            }

                            WindowHelper.ReleaseMouseLock(nudgeCursor: false);
                        }
                        catch { /* ignore */ }

                        if (attempts >= 8 || !Active)
                        {
                            restoreTimer.Stop();
                        }
                    };
                    restoreTimer.Start();
                }
            });
        }



        /// <summary>
        /// Create the HWND, run the first layout/render pass and the first Show/Hide cycle
        /// invisibly at startup. Symptom this targets: the very first real Show() did not put the
        /// overlay on screen, while the second one did. The window never takes focus here.
        /// </summary>
        public void Prewarm()
        {
            try
            {
                double oldOpacity = Opacity;
                bool oldShowActivated = ShowActivated;
                Opacity = 0;
                ShowActivated = false;
                base.Show();
                UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                base.Hide();
                Opacity = oldOpacity;
                ShowActivated = oldShowActivated;
                Active = false;
                _olog.Info("Overlay window pre-warmed.");
            }
            catch (Exception ex)
            {
                _olog.Warn($"Overlay pre-warm failed: {ex.Message}");
                try { Opacity = 1; ShowActivated = true; base.Hide(); } catch { }
            }
        }

        private void CancelPendingSoftPause()
        {
            var t = _softPauseTimer;
            _softPauseTimer = null;
            if (t != null)
            {
                try { t.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Freeze the game a moment after the overlay opened, and only if the overlay is still
        /// open and actually shown. Closing the overlay before the timer fires cancels it
        /// (PauseTimers is called on every close path).
        /// </summary>
        private void ScheduleSoftPause(int gamePid, IntPtr overlayHwnd, bool exclusive = false, IntPtr gameHwnd = default(IntPtr))
        {
            CancelPendingSoftPause();
            // Exclusive games need time to process the focus loss (minimize + leave exclusive mode).
            int maxAttempts = exclusive ? 40 : 12; // ~2 s vs ~600 ms
            var t = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _softPauseTimer = t;
            int attempts = 0;
            t.Tick += (s, ev) =>
            {
                if (!ReferenceEquals(_softPauseTimer, t))
                {
                    t.Stop();
                    return; // cancelled or superseded
                }

                try
                {
                    if (!Active || !IsVisible)
                    {
                        t.Stop(); _softPauseTimer = null;
                        return; // overlay was closed while we waited
                    }

                    attempts++;
                    bool shown = WindowHelper.IsWindowShownAndRestored(overlayHwnd);
                    bool front = WindowHelper.IsOwnProcessForeground();

                    if (shown && front)
                    {
                        // Exclusive fullscreen: do not freeze until the game has minimized/hidden
                        // itself, otherwise its black fullscreen surface is frozen on screen.
                        // After ~1 s with the overlay in front it is safe regardless.
                        if (exclusive && attempts < 20 && !WindowHelper.HasGameYieldedDisplay(gameHwnd))
                            return;

                        t.Stop(); _softPauseTimer = null;
                        _olog.Info($"SoftPause: overlay is foreground after {attempts * 50} ms, freezing game.");
                        WindowHelper.SoftPauseForOverlay(gamePid);
                        return;
                    }

                    if (attempts == 4 && !front)
                    {
                        // one more nudge, game is still running at this point
                        try { WindowHelper.ForceFocusWindow(overlayHwnd); } catch { }
                    }

                    if (attempts >= maxAttempts)
                    {
                        t.Stop(); _softPauseTimer = null;
                        _olog.Info($"SoftPause NOT applied: overlay never became foreground (shown={shown}, foreground={WindowHelper.DescribeForeground()}). Game keeps running.");
                    }
                }
                catch (Exception ex)
                {
                    t.Stop(); _softPauseTimer = null;
                    _olog.Warn($"Deferred SoftPause failed: {ex.Message}");
                }
            };
            t.Start();
        }

        private void HookSmoothSliders()
        {
            if (GameVolumeSlider != null)
                SmoothSlider.Attach(GameVolumeSlider, () => ViewModel?.CommitVolume());
            if (ScanlineTargetSlider != null)
                SmoothSlider.Attach(ScanlineTargetSlider, () => ViewModel?.CommitScanlineTarget());
        }

        private void ScanlineTargetSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            ViewModel?.CommitScanlineTarget();
        }

        private void ScanlineTargetSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            ViewModel?.CommitScanlineTarget();
        }

        private void ScanlineTargetBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            var tb = sender as TextBox;
            tb?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ViewModel?.CommitScanlineTarget();
            e.Handled = true;
        }

        private void ScanlineTargetBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var tb = sender as TextBox;
            tb?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ViewModel?.CommitScanlineTarget();
        }

        private void VolumeValueText_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (ViewModel == null) return;
            try
            {
                VolumeValueText.Visibility = Visibility.Collapsed;
                VolumeValueEdit.Text = ViewModel.VolumePercent.ToString();
                VolumeValueEdit.Visibility = Visibility.Visible;
                VolumeValueEdit.Focus();
                VolumeValueEdit.SelectAll();
            }
            catch (Exception ex)
            {
                _logger?.Log("Could not start volume edit: " + ex.Message, "AUDIO");
                CancelVolumeEdit();
            }
        }

        private void MasterMuteButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleGameMute();
        }

        private void VolumeValueEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                CommitVolumeEdit();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                CancelVolumeEdit();
                e.Handled = true;
            }
        }

        private void VolumeValueEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitVolumeEdit();
        }

        private void CommitVolumeEdit()
        {
            if (VolumeValueEdit.Visibility != Visibility.Visible)
                return;
            try
            {
                if (ViewModel != null &&
                    int.TryParse((VolumeValueEdit.Text ?? "").Trim().TrimEnd('%'), out int pct))
                {
                    pct = Math.Max(0, Math.Min(100, pct));
                    ViewModel.VolumeLevel = pct / 100.0;
                    ViewModel.CommitVolume();
                }
            }
            catch (Exception ex)
            {
                _logger?.Log("Manual volume edit failed: " + ex.Message, "AUDIO");
                if (ViewModel != null)
                    ViewModel.AudioStatusText = "Could not set volume";
            }
            finally
            {
                CancelVolumeEdit();
            }
        }

        private void CancelVolumeEdit()
        {
            VolumeValueEdit.Visibility = Visibility.Collapsed;
            VolumeValueText.Visibility = Visibility.Visible;
        }

        private void FpsValueText_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (ViewModel == null) return;
            FpsValueText.Visibility = Visibility.Collapsed;
            string seed = ViewModel.SelectedFpsLimit <= 0
                ? "0"
                : ViewModel.FpsLimitLabel;
            FpsValueEdit.Text = seed;
            FpsValueEdit.Visibility = Visibility.Visible;
            FpsValueEdit.Focus();
            FpsValueEdit.SelectAll();
        }

        private void FpsValueEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                CommitFpsEdit();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                CancelFpsEdit();
                e.Handled = true;
            }
        }

        private void FpsValueEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitFpsEdit();
        }

        private void CommitFpsEdit()
        {
            if (FpsValueEdit.Visibility != Visibility.Visible)
                return;
            if (ViewModel != null)
            {
                string t = FpsValueEdit.Text.Trim();
                // strip trailing "FPS" if user left it
                if (t.EndsWith("FPS", StringComparison.OrdinalIgnoreCase))
                    t = t.Substring(0, t.Length - 3).Trim();
                if (string.Equals(t, "unlimited", StringComparison.OrdinalIgnoreCase) || t == "0")
                {
                    ViewModel.SelectedFpsLimit = 0;
                    ViewModel.SetFpsLimitCommand.Execute(0.0);
                }
                else if (double.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double fps) ||
                    double.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out fps))
                {
                    fps = Math.Max(0, Math.Min(1000, fps));
                    ViewModel.SetFpsLimitCommand.Execute(fps);
                }
            }
            CancelFpsEdit();
        }

        private void CancelFpsEdit()
        {
            FpsValueEdit.Visibility = Visibility.Collapsed;
            FpsValueText.Visibility = Visibility.Visible;
        }


        /// <summary>
        /// Start panel drag only when the press is on empty chrome (not slider / list / buttons).
        /// Uses Preview so we can decide before children, but we do NOT mark Handled when
        /// skipping — that lets the ListBox receive the click and change selection.
        /// </summary>
        private void AudioPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if ((VolumeValueText != null &&
                 (ReferenceEquals(source, VolumeValueText) || IsDescendantOf(source, VolumeValueText))) ||
                (MasterMuteButton != null &&
                 (ReferenceEquals(source, MasterMuteButton) || IsDescendantOf(source, MasterMuteButton))) ||
                IsInteractiveAudioControl(source))
                return;

            _audioDragging = true;
            _audioDragStart = e.GetPosition(this);
            if (AudioPanelTransform != null)
            {
                _audioOriginX = AudioPanelTransform.X;
                _audioOriginY = AudioPanelTransform.Y;
            }
            AudioPanel.CaptureMouse();
            e.Handled = true;
        }

        private static bool IsInteractiveAudioControl(DependencyObject src)
        {
            while (src != null)
            {
                if (src is System.Windows.Controls.Primitives.Thumb ||
                    src is System.Windows.Controls.Slider ||
                    src is System.Windows.Controls.Button ||
                    src is System.Windows.Controls.Primitives.ButtonBase ||
                    src is System.Windows.Controls.ListBox ||
                    src is System.Windows.Controls.ListBoxItem ||
                    src is System.Windows.Controls.ScrollViewer ||
                    src is System.Windows.Controls.ScrollContentPresenter ||
                    src is System.Windows.Controls.TextBox)
                    return true;
                src = GetSafeParent(src);
            }
            return false;
        }

        private static DependencyObject GetSafeParent(DependencyObject child)
        {
            try
            {
                if (child is FrameworkContentElement contentElement)
                    return contentElement.Parent ?? ContentOperations.GetParent(contentElement);
                if (child is ContentElement content)
                    return ContentOperations.GetParent(content);
                if (child is System.Windows.Media.Visual || child is System.Windows.Media.Media3D.Visual3D)
                    return System.Windows.Media.VisualTreeHelper.GetParent(child);
                return LogicalTreeHelper.GetParent(child);
            }
            catch { return null; }
        }

        /// <summary>
        /// Ensure ListBox click is never captured by the panel drag logic.
        /// </summary>
        // Selection is only a highlight. D-pad moves through devices without causing a switch.
        private void AudioDeviceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Do not enumerate or rebuild devices here: it invalidates the selection while
            // WPF is processing it, causing flickering and repeated output changes.
        }

        private void AudioDeviceListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Never let the panel-drag capture steal list clicks
            if (_audioDragging)
            {
                _audioDragging = false;
                try { if (AudioPanel != null && AudioPanel.IsMouseCaptured) AudioPanel.ReleaseMouseCapture(); } catch { }
                try { ReleaseMouseCapture(); } catch { }
            }

            // Resolve the clicked row immediately — SelectedItem is still the *previous*
            // device at this point, so we must not rely on it.
            var item = FindAncestorListBoxItem(e.OriginalSource as DependencyObject);
            if (item == null) return;
            var device = item.DataContext as GameAudioSession.AudioDeviceInfo;
            if (device == null || ViewModel == null || ViewModel.IsRefreshingAudioDevices) return;

            AudioDeviceListBox.SelectedItem = device;
            ViewModel.SelectedAudioDevice = device;
            // Apply on mouse-up so a drag-out can cancel; stash for up handler
            _pendingAudioDevice = device;
        }

        private GameAudioSession.AudioDeviceInfo _pendingAudioDevice;

        private void AudioDeviceListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Only commit if the up happened on a device row (empty list area must not switch).
            var item = FindAncestorListBoxItem(e.OriginalSource as DependencyObject);
            var device = (item?.DataContext as GameAudioSession.AudioDeviceInfo) ?? _pendingAudioDevice;
            _pendingAudioDevice = null;
            if (device == null || ViewModel == null || ViewModel.IsRefreshingAudioDevices) return;

            AudioDeviceListBox.SelectedItem = device;
            ViewModel.SelectedAudioDevice = device;
            ViewModel.ApplySelectedAudioDevice();
            try { item?.Focus(); } catch { }
            e.Handled = true;
        }

        private static ListBoxItem FindAncestorListBoxItem(DependencyObject src)
        {
            while (src != null && !(src is ListBoxItem))
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            return src as ListBoxItem;
        }

        // When the list itself receives focus (panel cycling / focus traversal), put the cursor on
        // the active device so Up/Down start from where you expect.
        private void AudioDeviceListBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, AudioDeviceListBox) || AudioDeviceListBox.Items.Count == 0) return;
            int idx = AudioDeviceListBox.SelectedIndex >= 0 ? AudioDeviceListBox.SelectedIndex : 0;
            Dispatcher.BeginInvoke(new Action(() => FocusAudioDeviceAt(idx)), DispatcherPriority.Input);
        }

        private ListBoxItem FocusedAudioItem()
        {
            var f = Keyboard.FocusedElement as DependencyObject;
            while (f != null && !(f is ListBoxItem))
                f = System.Windows.Media.VisualTreeHelper.GetParent(f);
            var item = f as ListBoxItem;
            if (item != null && ItemsControl.ItemsControlFromItemContainer(item) == AudioDeviceListBox)
                return item;
            return null;
        }

        private void FocusAudioDeviceAt(int index)
        {
            if (AudioDeviceListBox == null || index < 0 || index >= AudioDeviceListBox.Items.Count) return;
            AudioDeviceListBox.ScrollIntoView(AudioDeviceListBox.Items[index]);
            AudioDeviceListBox.UpdateLayout();
            var c = AudioDeviceListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            c?.Focus();
        }

        private void CommitSelectedAudioDevice()
        {
            if (ViewModel == null || ViewModel.IsRefreshingAudioDevices) return;
            var device = (FocusedAudioItem()?.DataContext as GameAudioSession.AudioDeviceInfo)
                         ?? (AudioDeviceListBox?.SelectedItem as GameAudioSession.AudioDeviceInfo);
            if (device != null)
            {
                AudioDeviceListBox.SelectedItem = device; // themed highlight = active device
                ViewModel.SelectedAudioDevice = device;
                ViewModel.ApplySelectedAudioDevice();
            }
        }

        private void AudioPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_audioDragging) return;
            _audioDragging = false;
            AudioPanel.ReleaseMouseCapture();
            SnapPanelToOther(AudioPanel, AudioPanelTransform, FpsPanel);
            PersistPanelOffsets();
            e.Handled = true;
        }

        private void AudioPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_audioDragging || AudioPanelTransform == null) return;
            var pos = e.GetPosition(this);
            double scale = 1.0;
            try
            {
                var root = this.Content as FrameworkElement;
                if (root != null && root.ActualWidth > 0)
                    scale = Math.Min(root.ActualWidth / 1600.0, root.ActualHeight / 900.0);
            }
            catch { scale = 1.0; }
            if (scale < 0.01) scale = 1.0;
            AudioPanelTransform.X = _audioOriginX + (pos.X - _audioDragStart.X) / scale;
            AudioPanelTransform.Y = _audioOriginY + (pos.Y - _audioDragStart.Y) / scale;
        }

        private void FpsPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only start drag from empty chrome / header, not from buttons
            if (e.OriginalSource is System.Windows.Controls.Button ||
                e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase)
                return;

            _fpsDragging = true;
            _fpsDragStart = e.GetPosition(this);
            if (FpsPanelTransform != null)
            {
                _fpsOriginX = FpsPanelTransform.X;
                _fpsOriginY = FpsPanelTransform.Y;
            }
            FpsPanel.CaptureMouse();
            e.Handled = true;
        }

        private void FpsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_fpsDragging) return;
            _fpsDragging = false;
            FpsPanel.ReleaseMouseCapture();
            SnapPanelToOther(FpsPanel, FpsPanelTransform, AudioPanel);
            PersistPanelOffsets();
            e.Handled = true;
        }


        /// <summary>
        /// After a drag, align the moved panel with the other visible panel when the edges are close
        /// (left/right edges and top/bottom edges, within a few design pixels). Panels are dragged by
        /// hand and ended up a few pixels off each other; this makes them line up exactly.
        /// </summary>
        private void SnapPanelToOther(FrameworkElement moved, TranslateTransform movedT, FrameworkElement other)
        {
            try
            {
                if (moved == null || movedT == null || other == null) return;
                if (moved.Visibility != Visibility.Visible || other.Visibility != Visibility.Visible) return;

                // Measure in design space (the Viewbox child), so window scaling doesn't matter.
                DependencyObject d = moved;
                while (d != null && !(d is System.Windows.Controls.Viewbox)) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                var design = (d as System.Windows.Controls.Viewbox)?.Child as UIElement;
                if (design == null) return;

                var m = moved.TransformToAncestor(design).Transform(new Point(0, 0));
                var o = other.TransformToAncestor(design).Transform(new Point(0, 0));
                const double snap = 16.0;
                double mw = moved.ActualWidth, mh = moved.ActualHeight;
                double ow = other.ActualWidth, oh = other.ActualHeight;

                double dx = 0, dy = 0;
                double[] xs = { o.X - m.X, (o.X + ow) - (m.X + mw) };
                double[] ys = { o.Y - m.Y, (o.Y + oh) - (m.Y + mh), (o.Y + oh) - m.Y, o.Y - (m.Y + mh) };
                foreach (var c in xs) if (Math.Abs(c) <= snap && (dx == 0 || Math.Abs(c) < Math.Abs(dx))) dx = c;
                foreach (var c in ys) if (Math.Abs(c) <= snap && (dy == 0 || Math.Abs(c) < Math.Abs(dy))) dy = c;

                // Pure left-edge alignment is the usual case: prefer it exactly.
                if (Math.Abs(xs[0]) <= snap) dx = xs[0];
                // Stacked panels: keep a consistent 16px gap instead of touching/overlapping.
                if (Math.Abs(ys[2]) <= snap) dy = ys[2] + 16.0;
                else if (Math.Abs(ys[3]) <= snap) dy = ys[3] - 16.0;

                if (dx != 0) movedT.X += dx;
                if (dy != 0) movedT.Y += dy;
            }
            catch { /* alignment is cosmetic: never break dragging */ }
        }

        private double _achievementBlockHeight = 159;   // panel + gap, as measured with achievements shown

        /// <summary>How far the volume panel moved up because the achievements panel is not shown.</summary>
        private double AchievementShift()
        {
            return AchievementPanel != null && AchievementPanel.Visibility != Visibility.Visible ? _achievementBlockHeight : 0;
        }

        private void KeepAudioPanelInPlace()
        {
            if (_audioDragging) return;
            try
            {
                if (AchievementPanel != null && AchievementPanel.Visibility == Visibility.Visible && AchievementPanel.ActualHeight > 20)
                    _achievementBlockHeight = AchievementPanel.ActualHeight + AchievementPanel.Margin.Top + AchievementPanel.Margin.Bottom;
                if (AudioPanelTransform != null && Settings != null)
                    AudioPanelTransform.Y = Settings.AudioPanelOffsetY + AchievementShift();
            }
            catch { }
        }

        private void ApplySavedPanelOffsets()
        {
            if (Settings == null) return;
            if (FpsPanelTransform != null)
            {
                FpsPanelTransform.X = Settings.FpsPanelOffsetX;
                FpsPanelTransform.Y = Settings.FpsPanelOffsetY;
            }
            if (AudioPanelTransform != null)
            {
                AudioPanelTransform.X = Settings.AudioPanelOffsetX;
                AudioPanelTransform.Y = Settings.AudioPanelOffsetY + AchievementShift();
            }
        }

        private void PersistPanelOffsets()
        {
            if (Settings == null) return;
            try
            {
                if (FpsPanelTransform != null)
                {
                    Settings.FpsPanelOffsetX = FpsPanelTransform.X;
                    Settings.FpsPanelOffsetY = FpsPanelTransform.Y;
                }
                if (AudioPanelTransform != null)
                {
                    Settings.AudioPanelOffsetX = AudioPanelTransform.X;
                    Settings.AudioPanelOffsetY = AudioPanelTransform.Y - AchievementShift();
                }
                SaveSettingsCallback?.Invoke();
            }
            catch { /* ignore */ }
        }

        private void FpsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_fpsDragging || FpsPanelTransform == null) return;
            var pos = e.GetPosition(this);
            // Account for Viewbox scaling: positions are in window space;
            // transform is in the fixed 1600x900 design space inside Viewbox.
            // Approximate scale from ActualWidth of the Viewbox content.
            double scaleX = 1.0;
            double scaleY = 1.0;
            try
            {
                // FpsPanel lives inside Viewbox Uniform — use ActualWidth vs design width
                if (FpsPanel.ActualWidth > 0)
                {
                    // GetPosition is relative to Window; design space is 1600x900 letterboxed
                    var root = this.Content as System.Windows.FrameworkElement;
                    if (root != null && root.ActualWidth > 0 && root.ActualHeight > 0)
                    {
                        // Uniform scale based on the smaller ratio
                        scaleX = root.ActualWidth / 1600.0;
                        scaleY = root.ActualHeight / 900.0;
                        double s = Math.Min(scaleX, scaleY);
                        scaleX = scaleY = s;
                    }
                }
            }
            catch { scaleX = scaleY = 1.0; }

            if (scaleX < 0.01) scaleX = 1.0;
            if (scaleY < 0.01) scaleY = 1.0;

            FpsPanelTransform.X = _fpsOriginX + (pos.X - _fpsDragStart.X) / scaleX;
            FpsPanelTransform.Y = _fpsOriginY + (pos.Y - _fpsDragStart.Y) / scaleY;
        }

        public void ForceFocusOverlay()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            WindowHelper.ForceFocusWindow(hwnd);
        }

        private void ResumeTimers()
        {
            Dispatcher.Invoke(() =>
            {
                clockTimer.Start();

                if (batteryUpdateTimer != null)
                {
                    batteryUpdateTimer.Start();
                }

                if (controllerTimer != null)
                {
                    controllerTimer.Start();
                }

                if (mouseUnlockTimer != null)
                {
                    mouseUnlockTimer.Start();
                }
                if (mouseHoverTimer != null)
                {
                    mouseHoverTimer.Start();
                }

            });
        }

        private void PauseTimers()
        {
            Dispatcher.Invoke(() =>
            {
                CancelPendingSoftPause();
                clockTimer.Stop();

                if (batteryUpdateTimer != null)
                {
                    batteryUpdateTimer.Stop();
                }

                if (controllerTimer != null)
                {
                    controllerTimer.Stop();
                }

                if (mouseUnlockTimer != null)
                {
                    mouseUnlockTimer.Stop();
                }
                if (mouseHoverTimer != null)
                {
                    mouseHoverTimer.Stop();
                }

            });
        }

        private Process FindProcessById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch
            {
                return null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            PauseTimers();
            try
            {
                if (WindowHelper.IsParked)
                    WindowHelper.ResumeAndRestore();
                else if (WindowHelper.IsSoftPausedForOverlay)
                    WindowHelper.SoftResumeFromOverlay();
                else
                    WindowHelper.ResumeGameProcess();
            }
            catch { }
            base.OnClosed(e);
        }


        public new void Hide()
        {
            // An exclusive-fullscreen game minimized itself when the overlay opened, so closing the
            // overlay has to bring it back (non-exclusive games are simply uncovered). Callers that
            // set Active=false first (park/suspend) own the game window and are left alone.
            Breadcrumb.Write("Hide overlay exclusive=" + _exclusiveMode);
            InputPaused = false;
            bool restoreExclusiveGame = _exclusiveMode && Active;
            _exclusiveMode = false;

            Active = false;
            PauseTimers();

            // Always clear park + soft-suspend so the overlay can never stick open
            // with a frozen game underneath.
            try
            {
                if (WindowHelper.IsParked)
                    WindowHelper.ResumeAndRestore();
                else if (WindowHelper.IsSoftPausedForOverlay)
                    WindowHelper.SoftResumeFromOverlay();
                else
                    WindowHelper.ResumeGameProcess();
            }
            catch
            {
                try { WindowHelper.ForceClearAllSuspendState(); } catch { }
            }

            if (restoreExclusiveGame)
            {
                RestoreExclusiveGame();
                restoreExclusiveGame = false;
            }

            try
            {
                void DoHide()
                {
                    // Drop Topmost before hide — a hidden Topmost window can still
                    // steal / block activation of Playnite after the game exits.
                    try { this.Topmost = false; } catch { }
                    base.Hide();
                }

                if (Dispatcher.CheckAccess())
                    DoHide();
                else
                    Dispatcher.Invoke(DoHide);
            }
            catch
            {
                try { this.Topmost = false; } catch { }
                try { base.Hide(); } catch { }
            }
        }

        /// <summary>
        /// Show the game's (blurred) background art behind the overlay instead of a black screen.
        /// Falls back to the cover image, then to the plain dark backdrop.
        /// </summary>
        private bool ApplyBackdropImage()
        {
            try
            {
                if (!(Settings?.UseGameBackgroundBackdrop ?? true))
                {
                    ClearBackdropImage();
                    return false;
                }

                string path = ViewModel?.BackgroundImagePath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                    path = ViewModel?.CoverImagePath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    ClearBackdropImage();
                    return false;
                }

                if (_backdropBitmap == null || !string.Equals(_backdropBitmapPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad; // do not keep the file locked
                    bi.DecodePixelWidth = 1920;                // banner mode shows it sharp at screen width
                    bi.EndInit();
                    bi.Freeze();
                    _backdropBitmap = bi;
                    _backdropBitmapPath = path;
                }

                double blur = Math.Max(0, Math.Min(100, Settings?.BackdropBlur ?? 30));
                double zoom = Math.Max(50, Math.Min(200, Settings?.BackdropImageScale ?? 100)) / 100.0;
                double screenW = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
                double screenH = ActualHeight > 0 ? ActualHeight : SystemParameters.PrimaryScreenHeight;
                string mode = Settings?.BackdropImageMode ?? "Auto";
                bool banner = BackdropLayout.UseBanner(mode, _backdropBitmap.PixelWidth, _backdropBitmap.PixelHeight, screenW, screenH);

                // Layer 1: the artwork blurred and filling the screen (ambient background).
                BackdropImage.Source = _backdropBitmap;
                BackdropImage.Stretch = BackdropLayout.FillStretch(mode, banner);
                BackdropImage.RenderTransformOrigin = new Point(0.5, 0.5);
                BackdropImage.RenderTransform = banner ? Transform.Identity : new ScaleTransform(zoom, zoom);
                BackdropImage.Effect = blur > 0
                    ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
                    : null;
                // Bleed past the edges so the blur does not fade to transparent at the borders.
                BackdropImage.Margin = new Thickness(-blur * 1.5);
                BackdropImage.Visibility = Visibility.Visible;

                double dim = Math.Max(0, Math.Min(0.85, Settings?.BackdropDim ?? 0));
                double alpha = Math.Min(0.8, 0.45 + dim * 0.4);
                BackdropScrim.Fill = FrozenBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0));
                BackdropScrim.Visibility = Visibility.Visible;

                // Layer 2 (banner mode): the artwork sharp at its natural shape, edges faded into layer 1,
                // instead of cropping/stretching it to the screen.
                if (banner)
                {
                    bool fadeVertical = BackdropLayout.IsWiderThan(_backdropBitmap.PixelWidth, _backdropBitmap.PixelHeight, screenW, screenH);
                    BackdropBanner.Source = _backdropBitmap;
                    BackdropBanner.Stretch = Stretch.Uniform;
                    BackdropBanner.RenderTransformOrigin = new Point(0.5, 0.5);
                    BackdropBanner.RenderTransform = new ScaleTransform(zoom, zoom);
                    BackdropBanner.OpacityMask = BackdropLayout.FadeMask(fadeVertical);
                    BackdropBanner.Opacity = Math.Max(0.35, 0.9 - dim * 0.5);
                    BackdropBanner.Visibility = Visibility.Visible;
                }
                else
                {
                    BackdropBanner.Visibility = Visibility.Collapsed;
                }

                // The root grid's built-in dark layer would bury the artwork; keep it hit-testable.
                RootGrid.Background = FrozenBrush(Color.FromArgb(1, 0, 0, 0));
                Background = FrozenBrush(Colors.Black);
                return true;
            }
            catch
            {
                ClearBackdropImage();
                return false;
            }
        }

        private void ClearBackdropImage()
        {
            try
            {
                BackdropImage.Visibility = Visibility.Collapsed;
                BackdropBanner.Visibility = Visibility.Collapsed;
                BackdropScrim.Visibility = Visibility.Collapsed;
                if (_rootDefaultBackground != null)
                    RootGrid.Background = _rootDefaultBackground;
            }
            catch { }
        }

        /// <summary>Bring the minimized exclusive-fullscreen game back after the overlay closed.</summary>
        private void RestoreExclusiveGame()
        {
            try
            {
                int pid = ViewModel?.ProcessId ?? 0;
                if (ViewModel == null || !ViewModel.IsGameRunning || WindowHelper.IsParked)
                    return;
                if (!WindowHelper.IsRestorableGameProcess(pid))
                    return;

                // Never touch the window in the same instant the process was resumed.
                WindowHelper.SettleAfterResume();
                var hwnd = WindowHelper.FindMainWindow(pid);
                if (hwnd != IntPtr.Zero)
                    WindowHelper.SafeRestoreAndFocus(hwnd);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// With an exclusive-fullscreen game the overlay sits on the desktop, not over the game.
        /// Use a mostly opaque backdrop so it does not look like desktop icons behind a panel.
        /// </summary>
        private void ApplyExclusiveBackdrop()
        {
            try
            {
                byte alpha = 0xE6;
                var current = Background as SolidColorBrush;
                if (current != null && current.Color.A > alpha)
                    alpha = current.Color.A;
                Background = FrozenBrush(Color.FromArgb(alpha, 0x0B, 0x0D, 0x12));
            }
            catch { /* keep the themed backdrop */ }
        }

        // Navigation methods for the OverlayWindow class
        // ---- Mouse vs. controller/keyboard focus highlight ----
        // The first button is focused when the overlay opens so a controller can act immediately. Once the
        // mouse is actually used, that keyboard-focus highlight is dropped (hover still highlights); the next
        // keyboard or controller input brings it back at the last focused spot.
        private bool _focusHiddenByMouse;
        private System.Windows.Controls.Control _mouseHoverFocusTarget;
        private DateTime _overlayShownUtc = DateTime.MinValue;
        private System.Windows.Point? _mouseAnchor;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeCursorPoint
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativeCursorPoint point);

        private void PollMouseHover()
        {
            if (!Active || !IsVisible ||
                (DateTime.UtcNow - _overlayShownUtc).TotalMilliseconds < 700 ||
                (ViewModel != null && ViewModel.IsAchievementBrowserOpen))
                return;

            NativeCursorPoint cursor;
            if (!GetCursorPos(out cursor)) return;
            var screenPoint = new System.Windows.Point(cursor.X, cursor.Y);
            if (_lastPolledMouseScreen.HasValue &&
                (screenPoint - _lastPolledMouseScreen.Value).Length < 1.0)
                return;
            _lastPolledMouseScreen = screenPoint;

            System.Windows.Point localPoint;
            try { localPoint = PointFromScreen(screenPoint); }
            catch { return; }

            if (localPoint.X < 0 || localPoint.Y < 0 ||
                localPoint.X >= ActualWidth || localPoint.Y >= ActualHeight)
            {
                UpdateMouseHoverFocus(null, localPoint);
                return;
            }

            DependencyObject source = null;
            try { source = InputHitTest(localPoint) as DependencyObject; }
            catch { }
            UpdateMouseHoverFocus(source, localPoint);
        }

        // Pad input has priority over mouse / keyboard "input" for this long. In fullscreen the game (or Playnite
        // Fullscreen) re-centres the cursor and turns pad presses into key events, which used to flip the prompt
        // bar off and on again every few frames while the pad was in use.
        private const long PadPriorityTicks = 2000L * TimeSpan.TicksPerMillisecond;
        private long _lastPadInputTicks;      // UTC ticks of the last real pad input (any thread)
        private int _softMouseHits;           // consecutive qualifying mouse moves while no pad is in use

        /// <summary>
        /// Called when input arrives: true for a gamepad, false for mouse / keyboard. Safe from any thread.
        /// A non-forced "false" is ignored while a pad was used a moment ago; <paramref name="force"/> is for a
        /// deliberate mouse click, which always switches to mouse mode.
        /// </summary>
        public void NoteInputSource(bool gamepad, bool force = false)
        {
            try
            {
                var vm = ViewModel;
                if (vm == null) return;

                if (gamepad)
                {
                    System.Threading.Interlocked.Exchange(ref _lastPadInputTicks, DateTime.UtcNow.Ticks);
                    _softMouseHits = 0;
                }
                else if (!force)
                {
                    long last = System.Threading.Interlocked.Read(ref _lastPadInputTicks);
                    if (DateTime.UtcNow.Ticks - last < PadPriorityTicks) return;
                }

                bool sony = PadPrompts.SonyDetected;
                if (vm.GamepadInputActive == gamepad && (!gamepad || vm.SonyPadDetected == sony)) return;

                Action apply = () =>
                {
                    try
                    {
                        if (gamepad && vm.SonyPadDetected != sony) vm.SonyPadDetected = sony;
                        if (vm.GamepadInputActive != gamepad) vm.GamepadInputActive = gamepad;
                    }
                    catch { }
                };
                if (Dispatcher.CheckAccess()) apply();
                else Dispatcher.BeginInvoke(apply);
            }
            catch { }
        }

        private System.Windows.Point? _lastRealMousePos;

        private void OverlayWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!IsVisible) return;
            // Ignore the cursor nudge that happens while the overlay is opening.
            if ((DateTime.UtcNow - _overlayShownUtc).TotalMilliseconds < 700) { _mouseAnchor = null; _lastRealMousePos = e.GetPosition(this); return; }
            {
                // Only an actual pointer movement (not a re-sent event at the same spot) counts as mouse input.
                var pos = e.GetPosition(this);
                if (_lastRealMousePos == null || Math.Abs(pos.X - _lastRealMousePos.Value.X) > 4 || Math.Abs(pos.Y - _lastRealMousePos.Value.Y) > 4)
                {
                    // A single jump (cursor re-centred by the game) is not the player using the mouse.
                    if (++_softMouseHits >= 3) NoteInputSource(false);
                }
                _lastRealMousePos = pos;
            }
            if (ViewModel != null && ViewModel.IsAchievementBrowserOpen) return;

            UpdateMouseHoverFocus(e.OriginalSource as DependencyObject, e.GetPosition(this));
        }

        private void UpdateMouseHoverFocus(DependencyObject source, System.Windows.Point position)
        {
            // Follow the pointer with keyboard focus on actionable controls. This also makes
            // hover feedback work when a fullscreen game captures the mouse and WPF misses
            // its normal mouse-move messages.
            var hoverTarget = FindMouseHoverFocusTarget(source);
            if (hoverTarget != null && hoverTarget.IsEnabled && hoverTarget.IsVisible)
            {
                _mouseAnchor = position;
                if (!ReferenceEquals(_mouseHoverFocusTarget, hoverTarget) ||
                    (!hoverTarget.IsFocused && !hoverTarget.IsKeyboardFocusWithin))
                {
                    FocusSink.Focusable = false;
                    hoverTarget.Focus();
                    if (hoverTarget.IsFocused || hoverTarget.IsKeyboardFocusWithin)
                    {
                        _mouseHoverFocusTarget = hoverTarget;
                        _focusHiddenByMouse = false;
                        RememberLastFocus();
                    }
                }
                return;
            }

            // Clear the mouse-driven focus when the pointer leaves an actionable control.
            if (_mouseHoverFocusTarget != null)
            {
                bool wasFocused = _mouseHoverFocusTarget.IsKeyboardFocusWithin;
                _mouseHoverFocusTarget = null;
                if (wasFocused)
                {
                    _mouseAnchor = position;
                    ParkMouseFocusOnSink();
                    return;
                }
            }
            if (_focusHiddenByMouse) return;

            var p = position;
            if (_mouseAnchor == null) { _mouseAnchor = p; return; }
            if ((p - _mouseAnchor.Value).Length < 8) return;
            _mouseAnchor = p;

            // The overlay is usually not the activated window (the game is), so the button has *logical*
            // focus (IsFocused) while Keyboard.FocusedElement is empty. Look at the buttons themselves.
            var focused = FindFocusedButton(MainActionsPanel) ?? FindFocusedButton(FpsPanel)
                          ?? FindFocusedButton(AudioPanel) ?? FindFocusedButton(AchievementPanel)
                          ?? FindFocusedButton(ShortcutBar);
            if (focused == null) return;

            ParkMouseFocusOnSink();
        }

        private System.Windows.Controls.Control FindMouseHoverFocusTarget(DependencyObject source)
        {
            while (source != null)
            {
                var button = source as System.Windows.Controls.Primitives.ButtonBase;
                if (button != null)
                    return button;
                var listItem = source as System.Windows.Controls.ListBoxItem;
                if (listItem != null)
                    return listItem;
                source = GetSafeParent(source);
            }
            return null;
        }

        private void ParkMouseFocusOnSink()
        {
            _focusHiddenByMouse = true;
            try
            {
                FocusSink.Focusable = true;
                FocusSink.Focus();
                FocusManager.SetFocusedElement(this, FocusSink);
                Keyboard.Focus(FocusSink);
            }
            catch { try { Keyboard.ClearFocus(); } catch { } }
        }

        // A click gives a button focus again; forget the hidden state so the next real mouse movement hides it.
        private void OverlayWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            NoteInputSource(false, force: true);
            _mouseAnchor = e.GetPosition(this);
            // Decide after the click has been processed: if it gave a real control focus, the sink is no
            // longer holding focus and the next real mouse movement should hide that highlight again.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!FocusSink.IsFocused && !FocusSink.IsKeyboardFocused)
                {
                    _focusHiddenByMouse = false;
                    FocusSink.Focusable = false;
                }
            }), DispatcherPriority.Input);
        }

        private System.Windows.Controls.Button FindFocusedButton(DependencyObject root)
        {
            if (root == null) return null;
            var self = root as System.Windows.Controls.Button;
            if (self != null && (self.IsFocused || self.IsKeyboardFocused)) return self;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                var found = FindFocusedButton(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// If the mouse hid the focus highlight, restore it and report true so the triggering
        /// keyboard/controller input only reveals the selection instead of also acting on it.
        /// </summary>
        private bool RevealFocusIfHidden()
        {
            if (!_focusHiddenByMouse) return false;
            _focusHiddenByMouse = false;
            if (ViewModel != null && ViewModel.IsAchievementBrowserOpen) { FocusSink.Focusable = false; return false; }
            RestoreLastFocus();
            FocusSink.Focusable = false;   // never a navigation target while real controls have focus
            return true;
        }

        private void OverlayWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            NoteInputSource(false);
            if (_focusHiddenByMouse && e.Key != System.Windows.Input.Key.Escape &&
                (e.Key == System.Windows.Input.Key.Tab || e.Key == System.Windows.Input.Key.Left ||
                 e.Key == System.Windows.Input.Key.Right || e.Key == System.Windows.Input.Key.Up ||
                 e.Key == System.Windows.Input.Key.Down || e.Key == System.Windows.Input.Key.Enter ||
                 e.Key == System.Windows.Input.Key.Space))
            {
                if (RevealFocusIfHidden()) { e.Handled = true; return; }
            }
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (ViewModel != null && ViewModel.TryCloseAchievementBrowser())
                {
                    e.Handled = true;
                    return;
                }
                // Escape closes overlay (Return to Game button removed)
                ViewModel?.RequestHideOverlay();
                e.Handled = true;
                return;
            }

            // When the achievement list is open, allow arrow keys so ListBox can scroll/navigate
            if (ViewModel != null && ViewModel.IsAchievementBrowserOpen)
                return;

            // List of keys to block on the main overlay (prevents WPF default focus jumps)
            var blockedKeys = new[]
            {
                System.Windows.Input.Key.Tab,
                System.Windows.Input.Key.Left,
                System.Windows.Input.Key.Right,
                System.Windows.Input.Key.Up,
                System.Windows.Input.Key.Down
            };

            if (blockedKeys.Contains(e.Key))
            {
                e.Handled = true;
            }
        }

        // Focus navigation methods for controller support

        /// <summary>
        /// LT / RT jump between major panels (buttons, achievements, FPS, volume).
        /// LB / RB step through every focusable control one at a time (like Shift+Tab / Tab).
        /// </summary>
        public void FocusPreviousPanel() => CyclePanel(-1);
        public void FocusNextPanel() => CyclePanel(1);
        public void FocusPreviousItem() => StepFocus(-1);
        public void FocusNextItem() => StepFocus(1);

        private void StepFocus(int direction)
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                // Achievement browser: bumpers keep switching the All / Unlocked / Locked filter
                if (ViewModel != null && ViewModel.IsAchievementBrowserOpen)
                {
                    CycleAchievementFilter(direction);
                    return;
                }
                // Inside the audio device list the bumpers move the cursor; at either end they fall through.
                if (TryNavigateAudioDeviceList(direction))
                    return;

                UIElement focused = Keyboard.FocusedElement as UIElement;
                if (focused == null)
                {
                    RestoreLastFocus();
                    return;
                }

                var dir = direction > 0 ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous;
                if (!focused.MoveFocus(new TraversalRequest(dir)))
                {
                    // End of the tab order: wrap around.
                    this.MoveFocus(new TraversalRequest(direction > 0 ? FocusNavigationDirection.First : FocusNavigationDirection.Last));
                }
                _logger.Log($"Item step dir={direction} -> {(Keyboard.FocusedElement as FrameworkElement)?.Name ?? Keyboard.FocusedElement?.GetType().Name}", "SDL_NAV");
                RememberLastFocus();
            });
        }

        private void CyclePanel(int direction)
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                // Achievement browser is a single modal list: panel switching has nothing to do there.
                if (ViewModel != null && ViewModel.IsAchievementBrowserOpen)
                    return;

                var panels = BuildFocusablePanels();
                if (panels.Count == 0)
                    return;

                int current = FindPanelIndexContainingFocus(panels);
                int next = current < 0
                    ? (direction > 0 ? 0 : panels.Count - 1)
                    : (current + direction + panels.Count) % panels.Count;

                var panel = panels[next];
                // Focus a control within the destination, but retain the actual panel as the
                // navigation boundary. Any control in that panel must map to the same index.
                UIElement target = ReferenceEquals(panel, MainActionsPanel) ? (UIElement)FindFirstFocusableButton(MainActionsPanel)
                    : ReferenceEquals(panel, AudioPanel) ? (UIElement)GameVolumeSlider
                    : ReferenceEquals(panel, FpsPanel) ? FindFirstFocusableButton(FpsPanel)
                    : panel;
                if (target != null && target.IsVisible && target.IsEnabled && target.Focusable)
                {
                    target.Focus();
                    RememberLastFocus();
                }
                _logger.Log($"Panel cycle dir={direction} -> index {next} ({(panel as FrameworkElement)?.Name ?? panel.GetType().Name})", "SDL_NAV");
            });
        }

        private System.Collections.Generic.List<UIElement> BuildFocusablePanels()
        {
            var list = new System.Collections.Generic.List<UIElement>();
            if (MainActionsPanel != null && MainActionsPanel.IsVisible && MainActionsPanel.IsEnabled &&
                FindFirstFocusableButton(MainActionsPanel) != null)
                list.Add(MainActionsPanel);
            if (AchievementPanel != null && AchievementPanel.IsVisible && AchievementPanel.IsEnabled)
                list.Add(AchievementPanel);
            if (FpsPanel != null && FpsPanel.IsVisible && FpsPanel.IsEnabled && FindFirstFocusableButton(FpsPanel) != null)
                list.Add(FpsPanel);
            if (AudioPanel != null && AudioPanel.IsVisible && AudioPanel.IsEnabled &&
                GameVolumeSlider != null && GameVolumeSlider.IsVisible && GameVolumeSlider.IsEnabled)
                list.Add(AudioPanel);
            return list;
        }

        private static System.Windows.Controls.Control FindFirstFocusableButton(DependencyObject root)
        {
            if (root == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Button btn && btn.IsVisible && btn.IsEnabled && btn.Focusable)
                    return btn;
                var nested = FindFirstFocusableButton(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private int FindPanelIndexContainingFocus(System.Collections.Generic.List<UIElement> panels)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null) return -1;
            for (int i = 0; i < panels.Count; i++)
            {
                if (ReferenceEquals(focused, panels[i]) || IsDescendantOf(focused, panels[i] as DependencyObject))
                    return i;
            }
            return -1;
        }

        public void FocusDown()
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                if (TryNavigateAchievementList(1))
                    return;
                if (TryNavigateAudioDeviceList(1))
                    return;

                // Never treat Up/Down as volume Left/Right
                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus DOWN from {focusedElement.GetType().Name}", "SDL_NAV");
                    if (!focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down)))
                        focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    RememberLastFocus();
                }
            });
        }

        public void FocusUp()
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                if (TryNavigateAchievementList(-1))
                    return;
                if (TryNavigateAudioDeviceList(-1))
                    return;

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus UP from {focusedElement.GetType().Name}", "SDL_NAV");
                    if (!focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up)))
                        focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                    RememberLastFocus();
                }
            });
        }

        public void FocusLeft()
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                if (ViewModel != null && ViewModel.IsAchievementBrowserOpen)
                {
                    CycleAchievementFilter(-1);
                    return;
                }

                if (TryAdjustFocusedSlider(-1))
                {
                    RememberLastFocus();
                    return;
                }

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus LEFT from {focusedElement.GetType().Name}", "SDL_NAV");
                    focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
                    RememberLastFocus();
                }
            });
        }

        public void FocusRight()
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                if (ViewModel != null && ViewModel.IsAchievementBrowserOpen)
                {
                    CycleAchievementFilter(1);
                    return;
                }

                if (TryAdjustFocusedSlider(1))
                {
                    RememberLastFocus();
                    return;
                }

                UIElement focusedElement = Keyboard.FocusedElement as UIElement;
                if (focusedElement != null)
                {
                    _logger.Log($"Moving focus RIGHT from {focusedElement.GetType().Name}", "SDL_NAV");
                    focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
                    RememberLastFocus();
                }
            });
        }

        private void RememberLastFocus()
        {
            if (Settings == null) return;
            try
            {
                var focused = Keyboard.FocusedElement as DependencyObject;
                string key = null;
                if (focused != null)
                {
                    if (IsDescendantOf(focused, GameVolumeSlider) || ReferenceEquals(focused, GameVolumeSlider))
                        key = "volume";
                    else if (FpsPanel != null && IsDescendantOf(focused, FpsPanel))
                        key = "fps";
                    else if (AchievementPanel != null && IsDescendantOf(focused, AchievementPanel))
                        key = "achievements";
                    else if (MainActionsPanel != null && IsDescendantOf(focused, MainActionsPanel))
                        key = "playnite";
                    else if (focused is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                        key = fe.Name;
                }
                if (!string.IsNullOrEmpty(key) && Settings.LastFocusedPanel != key)
                {
                    Settings.LastFocusedPanel = key;
                    SaveSettingsCallback?.Invoke();
                }
            }
            catch { }
        }

        private void RestoreLastFocus()
        {
            if (Settings == null) return;
            string key = Settings.LastFocusedPanel;
            if (string.IsNullOrEmpty(key))
            {
                FocusFirstMainActionButton();
                return;
            }

            switch (key)
            {
                case "volume":
                    if (GameVolumeSlider != null && GameVolumeSlider.IsVisible)
                    {
                        GameVolumeSlider.Focus();
                        return;
                    }
                    break;
                case "fps":
                    {
                        var btn = FindFirstFocusableButton(FpsPanel);
                        if (btn != null) { btn.Focus(); return; }
                        FpsPanel?.Focus();
                        return;
                    }
                case "achievements":
                    if (AchievementPanel != null && AchievementPanel.IsVisible)
                    {
                        AchievementPanel.Focus();
                        AchievementPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                        return;
                    }
                    break;
                case "playnite":
                    FocusFirstMainActionButton();
                    return;
            }
            FocusFirstMainActionButton();
        }

        private void FocusFirstMainActionButton()
        {
            var button = FindFirstFocusableButton(MainActionsPanel);
            if (button != null)
                button.Focus();
        }

        private DateTime _sliderAdjustUtc = DateTime.MinValue;
        private int _sliderAdjustDir;
        private int _sliderAdjustStreak;

        // Holding the stick/D-pad ramps the step up so a full sweep doesn't take forever.
        private int NextSliderStreak(int dir)
        {
            var now = DateTime.UtcNow;
            if (dir == _sliderAdjustDir && (now - _sliderAdjustUtc).TotalMilliseconds < 300)
                _sliderAdjustStreak++;
            else
                _sliderAdjustStreak = 0;
            _sliderAdjustDir = dir;
            _sliderAdjustUtc = now;
            return _sliderAdjustStreak;
        }

        private bool TryAdjustFocusedSlider(int dir)
        {
            if (ViewModel == null) return false;
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null) return false;

            if (GameVolumeSlider != null &&
                (ReferenceEquals(focused, GameVolumeSlider) || IsDescendantOf(focused, GameVolumeSlider)))
            {
                int streak = NextSliderStreak(dir);
                double step = streak < 6 ? 0.02 : (streak < 16 ? 0.05 : 0.10);
                double next = Math.Round((ViewModel.VolumeLevel + dir * step) * 100.0) / 100.0;
                next = Math.Max(0, Math.Min(1, next));
                ViewModel.VolumeLevel = next; // applies to the selected volume target
                _logger.Log($"Volume adjust dir={dir} step={step:0.##} -> {next:0.##}", "SDL_NAV");
                return true;
            }

            if (ScanlineTargetSlider != null && ScanlineTargetSlider.IsVisible &&
                (ReferenceEquals(focused, ScanlineTargetSlider) || IsDescendantOf(focused, ScanlineTargetSlider)))
            {
                int streak = NextSliderStreak(dir);
                int step = streak < 6 ? 1 : (streak < 18 ? 5 : 15);
                ViewModel.ScanlineSyncTarget = ViewModel.ScanlineSyncTarget + dir * step;
                _logger.Log($"Scanline adjust dir={dir} step={step} -> {ViewModel.ScanlineSyncTarget:0}", "SDL_NAV");
                return true;
            }

            return false;
        }

        /// <summary>
        /// D-pad Up/Down inside the output device list moves the SELECTION (focus-traversal would
        /// jump out of the list, or move focus without selecting, so A confirmed the wrong device).
        /// At the first/last device it returns false so focus can leave the list normally.
        /// </summary>
        private bool TryNavigateAudioDeviceList(int dir)
        {
            if (AudioDeviceListBox == null || !AudioDeviceListBox.IsVisible || AudioDeviceListBox.Items.Count == 0)
                return false;
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null ||
                !(ReferenceEquals(focused, AudioDeviceListBox) || IsDescendantOf(focused, AudioDeviceListBox)))
                return false;

            int count = AudioDeviceListBox.Items.Count;
            var cur = FocusedAudioItem();
            int idx = cur != null
                ? AudioDeviceListBox.ItemContainerGenerator.IndexFromContainer(cur)
                : AudioDeviceListBox.SelectedIndex;
            if (idx < 0) idx = dir > 0 ? -1 : count;
            int next = idx + dir;
            if (next < 0 || next >= count) return false; // leave the list at either end

            FocusAudioDeviceAt(next); // cursor only: the active device highlight is unchanged until A
            _logger.Log($"Audio device cursor dir={dir} -> index {next}", "SDL_NAV");
            return true;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            while (child != null)
            {
                if (ReferenceEquals(child, parent))
                    return true;
                child = GetSafeParent(child);
            }
            return false;
        }

        private void GameVolumeSlider_GotFocus(object sender, RoutedEventArgs e)
        {
            if (VolumeSliderFocusBorder != null)
            {
                VolumeSliderFocusBorder.SetResourceReference(Border.BorderBrushProperty, "FocusRimBrush");
                VolumeSliderFocusBorder.SetResourceReference(Border.BorderThicknessProperty, "FocusRimThickness");
            }
        }

        private void GameVolumeSlider_LostFocus(object sender, RoutedEventArgs e)
        {
            if (VolumeSliderFocusBorder != null)
            {
                VolumeSliderFocusBorder.BorderBrush = Brushes.Transparent;
                VolumeSliderFocusBorder.BorderThickness = new Thickness(2);
            }
        }

        /// <summary>
        /// Moves selection in the achievement ListBox by delta (-1 up, +1 down).
        /// Returns true if the achievement browser handled the input.
        /// </summary>
        private bool TryNavigateAchievementList(int delta)
        {
            if (ViewModel == null || !ViewModel.IsAchievementBrowserOpen)
                return false;
            if (AchievementListBox == null || AchievementListBox.Items.Count == 0)
                return false;

            int count = AchievementListBox.Items.Count;
            int current = AchievementListBox.SelectedIndex;
            if (current < 0)
                current = 0;

            int next = Math.Max(0, Math.Min(count - 1, current + delta));
            AchievementListBox.SelectedIndex = next;
            EnsureAchievementListFocus(next);
            _logger.Log($"Achievement list select index {next}/{count - 1}", "SDL_NAV");
            return true;
        }

        private void EnsureAchievementListFocus(int index)
        {
            if (AchievementListBox == null)
                return;

            AchievementListBox.Focus();
            AchievementListBox.UpdateLayout();

            var item = AchievementListBox.ItemContainerGenerator.ContainerFromIndex(index)
                as System.Windows.Controls.ListBoxItem;
            if (item != null)
            {
                item.Focus();
                item.BringIntoView();
            }
            else if (index >= 0 && index < AchievementListBox.Items.Count)
            {
                // Virtualized item may not be realized yet
                AchievementListBox.ScrollIntoView(AchievementListBox.Items[index]);
                AchievementListBox.UpdateLayout();
                item = AchievementListBox.ItemContainerGenerator.ContainerFromIndex(index)
                    as System.Windows.Controls.ListBoxItem;
                item?.Focus();
            }
        }

        private static readonly string[] AchievementFilterOrder = { "All", "Unlocked", "Locked" };

        private void CycleAchievementFilter(int direction)
        {
            if (ViewModel == null)
                return;

            string current = ViewModel.AchievementFilter ?? "All";
            int idx = Array.IndexOf(AchievementFilterOrder, current);
            if (idx < 0) idx = 0;
            int next = (idx + direction + AchievementFilterOrder.Length) % AchievementFilterOrder.Length;
            string nextFilter = AchievementFilterOrder[next];

            if (ViewModel.SetAchievementFilterCommand != null &&
                ViewModel.SetAchievementFilterCommand.CanExecute(nextFilter))
            {
                ViewModel.SetAchievementFilterCommand.Execute(nextFilter);
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AchievementListBox != null && AchievementListBox.Items.Count > 0)
                {
                    AchievementListBox.SelectedIndex = 0;
                    EnsureAchievementListFocus(0);
                }
            }), DispatcherPriority.Loaded);

            _logger.Log($"Achievement filter -> {nextFilter}", "SDL_NAV");
        }

        public void ClickFocusedElement()
        {
            Dispatcher.Invoke(() =>
            {
                if (RevealFocusIfHidden()) return;
                RememberLastFocus();
                if (Keyboard.FocusedElement is System.Windows.Controls.Button button)
                {
                    _logger.Log($"Clicking button: {button.Name}", "SDL_NAV");

                    if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
                    {
                        button.Command.Execute(button.CommandParameter);
                    }
                    else
                    {
                        RoutedEventArgs args = new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent);
                        button.RaiseEvent(args);
                    }
                    return;
                }

                var focused = Keyboard.FocusedElement as DependencyObject;
                if (focused != null && AudioDeviceListBox != null &&
                    (ReferenceEquals(focused, AudioDeviceListBox) || IsDescendantOf(focused, AudioDeviceListBox)))
                {
                    CommitSelectedAudioDevice();
                    return;
                }

                if (Keyboard.FocusedElement is System.Windows.Controls.ListBoxItem ||
                    (ViewModel != null && ViewModel.IsAchievementBrowserOpen))
                {
                    _logger.Log("A pressed on achievement list item (selection only)", "SDL_NAV");
                    return;
                }

                _logger.Log($"Focused element is not a button: {Keyboard.FocusedElement?.GetType().Name ?? "null"}", "SDL_NAV");
            });
        }

        /// <summary>
        /// Closes the achievement browser if open. Returns true if it was open.
        /// </summary>
        public bool TryCloseAchievementBrowser()
        {
            bool closed = false;
            Dispatcher.Invoke(() =>
            {
                closed = ViewModel != null && ViewModel.TryCloseAchievementBrowser();
            });
            return closed;
        }

        // Event to request showing Playnite (to be handled by the plugin)
        public event Action OnShowPlayniteRequested;
        // Event to request showing the Overlay (to be handled by the plugin)
        public event Action OnShowOverlayRequested;
    }

    /// <summary>Shared layout rules for the fullscreen-game backdrop (overlay window + settings preview).</summary>
    internal static class BackdropLayout
    {
        public static bool IsWiderThan(double imgW, double imgH, double screenW, double screenH)
        {
            if (imgH <= 0 || screenH <= 0) return false;
            return (imgW / imgH) > (screenW / screenH);
        }

        public static bool UseBanner(string mode, double imgW, double imgH, double screenW, double screenH)
        {
            if (mode == "Banner") return true;
            if (mode != "Auto" || imgH <= 0 || screenH <= 0) return false;
            double ratio = (imgW / imgH) / (screenW / screenH);
            return ratio > 1.25 || ratio < 0.8; // clearly wider (SteamGridDB heroes/grids) or taller (covers)
        }

        public static System.Windows.Media.Stretch FillStretch(string mode, bool banner)
        {
            if (banner) return System.Windows.Media.Stretch.UniformToFill;
            if (mode == "Fit") return System.Windows.Media.Stretch.Uniform;
            if (mode == "Stretch") return System.Windows.Media.Stretch.Fill;
            return System.Windows.Media.Stretch.UniformToFill;
        }

        public static System.Windows.Media.Brush FadeMask(bool vertical)
        {
            var b = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = vertical ? new System.Windows.Point(0, 1) : new System.Windows.Point(1, 0)
            };
            b.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 0.0));
            b.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(255, 0, 0, 0), 0.22));
            b.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(255, 0, 0, 0), 0.78));
            b.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 1.0));
            b.Freeze();
            return b;
        }
    }
}