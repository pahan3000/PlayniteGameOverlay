using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    public sealed class FpsPresetViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public FpsPresetViewModel(double value, bool isSelected)
        {
            Value = value;
            _isSelected = isSelected;
        }

        public double Value { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class OverlayWindowViewModel : INotifyPropertyChanged
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private string _gameTitle;
        private string _coverImagePath;
        private TimeSpan _totalPlaytime;
        private TimeSpan _sessionPlaytime;
        private DateTime _sessionStartTime;
        private string _currentTime;
        private float _batteryPercentage;
        private bool _debugVisible;
        private string _debugInfo;
        private bool _isGameRunning;
        private int _processId;
        private Guid _gameId;
        private bool _hasAchievements;
        private AchievementData _lastAchievement;
        private string _unlockPercentText;
        private ObservableCollection<ShortcutButtonViewModel> _shortcutButtons;
        private ObservableCollection<AchievementData> _achievements = new ObservableCollection<AchievementData>();
        private ObservableCollection<AchievementData> _filteredAchievements = new ObservableCollection<AchievementData>();
        private bool _isAchievementBrowserOpen;
        private string _achievementFilter = "All"; // All | Unlocked | Locked
        private string _achievementStyle = "Console";
        private double _unlockProgress;
        private int _unlockedCount;
        private int _goldTrophyCount;
        private int _silverTrophyCount;
        private int _bronzeTrophyCount;
        private int _totalAchievementCount;
        private bool _hasBattery;
        private string _gameExecutablePath;
        private double _selectedFpsLimit = 0;
        private OverlayFpsSyncMode _selectedSyncMode = OverlayFpsSyncMode.Async;
        private OverlayFpsBackend _selectedFpsBackend = OverlayFpsBackend.Rtss;
        private bool _reflexMarkersEnabled = true;
        private double _scanlineSyncTarget = -1;
        private bool _rtssAvailable;
        private string _fpsStatusText = "";
        private double _volumeLevel = 1.0;
        private bool _usePerGameVolume;
        private bool _audioSessionAvailable;
        private bool _isGameMuted;
        private int _audioRefreshVersion;
        private Guid _lastGameVolumeRestoreId;
        private int _lastGameVolumeRestorePid;
        private bool _restoringGameVolumeProfile;
        private Guid _lastMasterSessionNormalizeId;
        private int _lastMasterSessionNormalizePid;
        private bool _normalizingMasterSession;
        private readonly object _gameMuteLock = new object();
        private bool? _pendingGameMute;
        private int _pendingGameMutePid;
        private string _pendingGameMuteExe;
        private bool _gameMuteWorkerRunning;
        private int _gameMuteRequestVersion;
        private string _audioStatusText = "";
        private ObservableCollection<FpsPresetViewModel> _fpsPresets = new ObservableCollection<FpsPresetViewModel>();
        private List<double> _cachedFpsPresets = new List<double>();
        private List<double> _cachedRefreshRates = null;

        public bool ShowShortcutButtons => ShortcutButtons.Count > 0;

        private AspectRatio _aspectRatio = AspectRatio.Portrait;
        public AspectRatio AspectRatio
        {
            get => _aspectRatio;
            set
            {
                _aspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CoverWidth));
                OnPropertyChanged(nameof(CoverHeight));
                OnPropertyChanged(nameof(InnerRect));
            }
        }

        public int CoverWidth
        {
            get
            {
                switch (AspectRatio)
                {
                    case AspectRatio.Portrait:
                        return 375;
                    case AspectRatio.Landscape:
                        return 550;
                    case AspectRatio.Square:
                        return 425;
                }
                return 375; // Default to Portrait if not set
            }
        }

        public int CoverHeight
        {
            get
            {
                switch (AspectRatio)
                {
                    case AspectRatio.Portrait:
                        return 525;
                    case AspectRatio.Landscape:
                        return 262;
                    case AspectRatio.Square:
                        return 425;
                }
                return 525; // Default to Portrait if not set
            }
        }

        public Rect InnerRect
        {
            get
            {
                switch (AspectRatio)
                {
                    case AspectRatio.Portrait:
                        return new Rect(0, 0, 371, 521);
                    case AspectRatio.Landscape:
                        return new Rect(0, 0, 546, 258);
                    case AspectRatio.Square:
                        return new Rect(0, 0, 421, 421);
                }
                return new Rect(0, 0, 373, 523); // Default to Portrait if not set
            }
        }

        public string GameTitle
        {
            get => _gameTitle;
            set { _gameTitle = value; OnPropertyChanged(); }
        }

        public string CoverImagePath
        {
            get => _coverImagePath;
            set { _coverImagePath = value; OnPropertyChanged(); }
        }

        private string _backgroundImagePath;
        /// <summary>Full path of the game's Playnite background art (used as the fullscreen-game backdrop).</summary>
        public string BackgroundImagePath
        {
            get => _backgroundImagePath;
            set { _backgroundImagePath = value; OnPropertyChanged(); }
        }

        public TimeSpan TotalPlaytime
        {
            get => _totalPlaytime;
            set { _totalPlaytime = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalPlaytimeText)); }
        }

        public string TotalPlaytimeText
        {
            get
            {
                int hours = (int)TotalPlaytime.TotalHours;
                return $"{hours} {(hours == 1 ? "Hour" : "Hours")}";
            }
        }

        public TimeSpan SessionPlaytime
        {
            get => _sessionPlaytime;
            set { _sessionPlaytime = value; OnPropertyChanged(); OnPropertyChanged(nameof(SessionPlaytimeText)); }
        }

        public string SessionPlaytimeText
        {
            get => SessionPlaytime.ToString(@"hh\:mm\:ss");
        }

        public DateTime SessionStartTime
        {
            get => _sessionStartTime;
            set { _sessionStartTime = value; OnPropertyChanged(); }
        }

        public string CurrentTime
        {
            get => _currentTime;
            set { _currentTime = value; OnPropertyChanged(); }
        }

        public float BatteryPercentage
        {
            get => _batteryPercentage;
            set { _batteryPercentage = value; OnPropertyChanged(); }
        }

        public int BatteryBarWidth
        {
            get => (int)(BatteryPercentage * 0.35); // Calculate width based on percentage
        }

        public bool DebugVisible
        {
            get => _debugVisible;
            set { _debugVisible = value; OnPropertyChanged(); }
        }

        public string DebugInfo
        {
            get => _debugInfo;
            set { _debugInfo = value; OnPropertyChanged(); }
        }

        public bool IsGameRunning
        {
            get => _isGameRunning;
            set { _isGameRunning = value; OnPropertyChanged(); }
        }

        public int ProcessId
        {
            get => _processId;
            set { _processId = value; OnPropertyChanged(); }
        }

        public Guid GameId
        {
            get => _gameId;
            set { _gameId = value; OnPropertyChanged(); }
        }

        public bool HasAchievements
        {
            get => _hasAchievements;
            set { _hasAchievements = value; OnPropertyChanged(); }
        }

        public bool HasBattery
        {
            get => _hasBattery;
            set { _hasBattery = value; OnPropertyChanged(); }
        }

        public AchievementData LastAchievement
        {
            get => _lastAchievement;
            set
            {
                _lastAchievement = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastAchievementTimeText));
            }
        }

        public string LastAchievementTimeText { 
            get {
                if (LastAchievement == null)
                    return string.Empty;

                if (!LastAchievement.UnlockDate.HasValue)
                    return "recently";

                // UnlockDate is stored as local time (see ParseUtcToLocal)
                var unlock = LastAchievement.UnlockDate.Value;
                if (unlock.Kind == DateTimeKind.Utc)
                    unlock = unlock.ToLocalTime();

                var span = DateTime.Now - unlock;
                if (span < TimeSpan.Zero)
                    span = TimeSpan.Zero;

                if (span.TotalSeconds < 60)
                    return "just now";
                if (span.TotalMinutes < 60)
                {
                    int m = Math.Max(1, (int)span.TotalMinutes);
                    return m == 1 ? "1 minute ago" : $"{m} minutes ago";
                }
                if (span.TotalHours < 24)
                {
                    int h = Math.Max(1, (int)span.TotalHours);
                    return h == 1 ? "1 hour ago" : $"{h} hours ago";
                }
                if (span.TotalDays < 30)
                {
                    int d = Math.Max(1, (int)span.TotalDays);
                    return d == 1 ? "1 day ago" : $"{d} days ago";
                }
                return unlock.ToString("MMM d, yyyy");
            }
        }

        public string UnlockPercentText
        {
            get => _unlockPercentText;
            set { _unlockPercentText = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ShortcutButtonViewModel> ShortcutButtons
        {
            get => _shortcutButtons;
            set { _shortcutButtons = value; OnPropertyChanged(); }
        }

        public ObservableCollection<AchievementData> Achievements
        {
            get => _achievements;
            set { _achievements = value; OnPropertyChanged(); }
        }

        public ObservableCollection<AchievementData> FilteredAchievements
        {
            get => _filteredAchievements;
            set { _filteredAchievements = value; OnPropertyChanged(); }
        }

        public bool IsAchievementBrowserOpen
        {
            get => _isAchievementBrowserOpen;
            set { _isAchievementBrowserOpen = value; OnPropertyChanged(); }
        }

        public string AchievementFilter
        {
            get => _achievementFilter;
            set
            {
                if (_achievementFilter == value) return;
                _achievementFilter = value;
                OnPropertyChanged();
                ApplyAchievementFilter();
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterUnlocked));
                OnPropertyChanged(nameof(IsFilterLocked));
            }
        }

        public bool IsFilterAll => AchievementFilter == "All";
        public bool IsFilterUnlocked => AchievementFilter == "Unlocked";
        public bool IsFilterLocked => AchievementFilter == "Locked";

        public string AchievementStyle
        {
            get => _achievementStyle;
            set
            {
                var normalized = string.Equals(value, "Desktop", StringComparison.OrdinalIgnoreCase) ? "Desktop" : "Console";
                if (_achievementStyle == normalized) return;
                _achievementStyle = normalized;
                OnPropertyChanged();
            }
        }

        public double UnlockProgress
        {
            get => _unlockProgress;
            set { _unlockProgress = value; OnPropertyChanged(); }
        }

        public int UnlockedCount
        {
            get => _unlockedCount;
            set { _unlockedCount = value; OnPropertyChanged(); }
        }

        /// <summary>Unlocked achievements rarer than 10% of players.</summary>
        public int GoldTrophyCount
        {
            get => _goldTrophyCount;
            set { _goldTrophyCount = value; OnPropertyChanged(); }
        }

        /// <summary>Unlocked achievements earned by 10% to under 50% of players.</summary>
        public int SilverTrophyCount
        {
            get => _silverTrophyCount;
            set { _silverTrophyCount = value; OnPropertyChanged(); }
        }

        /// <summary>Unlocked achievements earned by 50% or more of players (or with no rarity data).</summary>
        public int BronzeTrophyCount
        {
            get => _bronzeTrophyCount;
            set { _bronzeTrophyCount = value; OnPropertyChanged(); }
        }

        public int TotalAchievementCount
        {
            get => _totalAchievementCount;
            set { _totalAchievementCount = value; OnPropertyChanged(); }
        }


        public string GameExecutablePath
        {
            get => _gameExecutablePath;
            set { _gameExecutablePath = value; OnPropertyChanged(); }
        }

        public bool RtssAvailable
        {
            get => _rtssAvailable;
            set { _rtssAvailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFpsPanel)); }
        }

        private bool _showFpsPanel = true;
        private bool _showPlayniteButton = true;
        private bool _showSuspendButton;
        private bool _showAudioPanel = true;

        public bool ShowPlayniteButton
        {
            get => _showPlayniteButton;
            set { if (_showPlayniteButton != value) { _showPlayniteButton = value; OnPropertyChanged(); } }
        }

        public bool ShowFpsPanel
        {
            get => _showFpsPanel;
            set { _showFpsPanel = value; OnPropertyChanged(); }
        }

        public bool ShowAudioPanel
        {
            get => _showAudioPanel;
            set { _showAudioPanel = value; OnPropertyChanged(); }
        }

        private bool _showGamepadHints = true;
        public bool ShowGamepadHints
        {
            get => _showGamepadHints;
            set { if (_showGamepadHints != value) { _showGamepadHints = value; OnPropertyChanged(); OnPropertyChanged(nameof(GamepadHintsVisible)); } }
        }

        // True while the last input came from a gamepad; mouse or keyboard input turns it off again.
        private bool _gamepadInputActive;
        public bool GamepadInputActive
        {
            get => _gamepadInputActive;
            set { if (_gamepadInputActive != value) { _gamepadInputActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(GamepadHintsVisible)); } }
        }

        /// <summary>The button prompts show only when enabled in settings AND a gamepad is the active input.</summary>
        public bool GamepadHintsVisible => _showGamepadHints && _gamepadInputActive;

        // ---- Button prompt glyphs: Auto / Xbox / DualShock (General > Show gamepad button prompts)
        private ButtonPromptStyle _promptStyle = ButtonPromptStyle.Auto;
        public ButtonPromptStyle PromptStyle
        {
            get => _promptStyle;
            set { if (_promptStyle != value) { _promptStyle = value; NotifyPromptGlyphsChanged(); } }
        }

        // True while the pad in use is a DualShock 4 / DualSense (only matters for the Auto style).
        private bool _sonyPadDetected;
        public bool SonyPadDetected
        {
            get => _sonyPadDetected;
            set { if (_sonyPadDetected != value) { _sonyPadDetected = value; NotifyPromptGlyphsChanged(); } }
        }

        public bool UseSonyPrompts => _promptStyle == ButtonPromptStyle.DualShock ||
                                      (_promptStyle == ButtonPromptStyle.Auto && _sonyPadDetected);

        public string PromptLb => UseSonyPrompts ? "L1" : "LB";
        public string PromptRb => UseSonyPrompts ? "R1" : "RB";
        public string PromptLt => UseSonyPrompts ? "L2" : "LT";
        public string PromptRt => UseSonyPrompts ? "R2" : "RT";
        public string PromptConfirm => UseSonyPrompts ? "\u2715" : "A";   // cross / A
        public string PromptBack => UseSonyPrompts ? "\u25CB" : "B";      // circle / B

        private static readonly System.Windows.Media.Brush XboxGreen = MakeBrush(0xFF, 0x2E, 0x9E, 0x48);
        private static readonly System.Windows.Media.Brush XboxRed = MakeBrush(0xFF, 0xC0, 0x39, 0x2B);
        private static readonly System.Windows.Media.Brush PsBlue = MakeBrush(0xFF, 0x3B, 0x6E, 0xC4);
        private static readonly System.Windows.Media.Brush PsRed = MakeBrush(0xFF, 0xC2, 0x4A, 0x57);
        private static System.Windows.Media.Brush MakeBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
        public System.Windows.Media.Brush PromptConfirmBrush => UseSonyPrompts ? PsBlue : XboxGreen;
        public System.Windows.Media.Brush PromptBackBrush => UseSonyPrompts ? PsRed : XboxRed;

        private void NotifyPromptGlyphsChanged()
        {
            OnPropertyChanged(nameof(UseSonyPrompts));
            OnPropertyChanged(nameof(PromptLb));
            OnPropertyChanged(nameof(PromptRb));
            OnPropertyChanged(nameof(PromptLt));
            OnPropertyChanged(nameof(PromptRt));
            OnPropertyChanged(nameof(PromptConfirm));
            OnPropertyChanged(nameof(PromptBack));
            OnPropertyChanged(nameof(PromptConfirmBrush));
            OnPropertyChanged(nameof(PromptBackBrush));
        }



        public double SelectedFpsLimit
        {
            get => _selectedFpsLimit;
            set
            {
                _selectedFpsLimit = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FpsLimitLabel));
                UpdateFpsPresetSelection();
            }
        }

        public string FpsLimitLabel
        {
            get
            {
                if (SelectedFpsLimit <= 0) return "Unlimited";
                string num;
                if (Math.Abs(SelectedFpsLimit - Math.Round(SelectedFpsLimit)) < 0.001)
                    num = ((int)Math.Round(SelectedFpsLimit)).ToString();
                else
                    num = SelectedFpsLimit.ToString("0.###");
                return UseRefreshRatePresets ? num + (SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf ? " FPS (x/2)" : " Hz") : num;
            }
        }

        public OverlayFpsSyncMode SelectedSyncMode
        {
            get => _selectedSyncMode;
            set
            {
                if (_selectedSyncMode == value) return;
                _selectedSyncMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SyncModeLabel));
                OnPropertyChanged(nameof(UseRefreshRatePresets));
                OnPropertyChanged(nameof(ShowRtssSyncModes));
                OnPropertyChanged(nameof(ShowScanlineTarget));
                OnPropertyChanged(nameof(PresetSectionTitle));
                OnPropertyChanged(nameof(FpsValueUnitLabel));
                OnPropertyChanged(nameof(FpsLimitLabel));
                RebuildPresetList();
            }
        }

        public OverlayFpsBackend SelectedFpsBackend
        {
            get => _selectedFpsBackend;
            set
            {
                if (_selectedFpsBackend == value) return;
                _selectedFpsBackend = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseRefreshRatePresets));
                OnPropertyChanged(nameof(ShowRtssSyncModes));
                OnPropertyChanged(nameof(ShowScanlineTarget));
                OnPropertyChanged(nameof(PresetSectionTitle));
                OnPropertyChanged(nameof(FpsValueUnitLabel));
                OnPropertyChanged(nameof(FpsBackendLabel));
                OnPropertyChanged(nameof(FpsLimitLabel));
                RebuildPresetList();
            }
        }

        public string FpsBackendLabel =>
            SelectedFpsBackend == OverlayFpsBackend.SpecialK ? "Special K" : "RTSS";

        /// <summary>
        /// RTSS Scanline Sync → show display refresh rates instead of FPS presets (Special K uses the FPS presets).
        /// </summary>
        private bool _fixedRefreshDisplayMode;
        public bool FixedRefreshDisplayMode
        {
            get => _fixedRefreshDisplayMode;
            set
            {
                if (_fixedRefreshDisplayMode == value) return;
                _fixedRefreshDisplayMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseRefreshRatePresets));
                OnPropertyChanged(nameof(ShowScanlineTarget));
                OnPropertyChanged(nameof(PresetSectionTitle));
                OnPropertyChanged(nameof(FpsValueUnitLabel));
                RebuildPresetList();
            }
        }
        public bool UseRefreshRatePresets =>
            SelectedFpsBackend == OverlayFpsBackend.Rtss && FixedRefreshDisplayMode &&
            (SelectedSyncMode == OverlayFpsSyncMode.ScanlineSync || SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf);

        /// <summary>RTSS-only sync mode buttons (Async / Front / Back / Reflex / Scanline).</summary>
        public bool ShowRtssSyncModes => SelectedFpsBackend == OverlayFpsBackend.Rtss;

        /// <summary>Show Scanline Sync tuning only while RTSS Scanline Sync is active.</summary>
        public bool ShowScanlineTarget =>
            SelectedFpsBackend == OverlayFpsBackend.Rtss && FixedRefreshDisplayMode &&
            (SelectedSyncMode == OverlayFpsSyncMode.ScanlineSync || SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf);

        /// <summary>RTSS SyncScanline0 target. Default -1; slider range -500..500.</summary>
        public double ScanlineSyncTarget
        {
            get => _scanlineSyncTarget;
            set
            {
                var rounded = Math.Round(value);
                if (rounded < -500) rounded = -500;
                if (rounded > 500) rounded = 500;
                if (Math.Abs(_scanlineSyncTarget - rounded) < 0.001) return;
                _scanlineSyncTarget = rounded;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ScanlineSyncTargetLabel));
                // The slider/label follow the user instantly; the RTSS/FPS Limiter update (disk +
                // profile writes) is coalesced so dragging or holding a button stays smooth.
                ScheduleScanlineApply();
            }
        }

        private System.Windows.Threading.DispatcherTimer _scanlineApplyTimer;
        private int? _lastAppliedScanline;

        private void ScheduleScanlineApply()
        {
            if (_scanlineApplyTimer == null)
            {
                _scanlineApplyTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(140)
                };
                _scanlineApplyTimer.Tick += (s, e) => { _scanlineApplyTimer.Stop(); FlushScanlineApply(); };
            }
            if (!_scanlineApplyTimer.IsEnabled) _scanlineApplyTimer.Start();
        }

        private void FlushScanlineApply()
        {
            int v = (int)Math.Round(_scanlineSyncTarget);
            if (_lastAppliedScanline.HasValue && _lastAppliedScanline.Value == v) return;
            _lastAppliedScanline = v;
            FpsActionRequested?.Invoke("scanlineTarget", v);
        }

        /// <summary>Apply any pending scanline change right now (drag finished, Enter pressed, default button).</summary>
        public void CommitScanlineTarget()
        {
            _scanlineApplyTimer?.Stop();
            FlushScanlineApply();
        }

        /// <summary>Synchronize the displayed target from FPS Limiter without sending a second action back.</summary>
        public void SetScanlineTargetFromBridge(int value)
        {
            value = Math.Max(-500, Math.Min(500, value));
            _scanlineApplyTimer?.Stop();
            _lastAppliedScanline = value;
            if (_scanlineSyncTarget.Equals((double)value)) return;
            _scanlineSyncTarget = value;
            OnPropertyChanged(nameof(ScanlineSyncTarget));
            OnPropertyChanged(nameof(ScanlineSyncTargetLabel));
        }

        public string ScanlineSyncTargetLabel => ((int)Math.Round(ScanlineSyncTarget)).ToString();

        public string PresetSectionTitle => UseRefreshRatePresets ? (SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf ? "HALF-RATE FPS" : "REFRESH RATES") : "FPS PRESETS";

        public string FpsValueUnitLabel => UseRefreshRatePresets && SelectedSyncMode != OverlayFpsSyncMode.ScanlineSyncHalf ? "Hz" : "FPS";

        public string SyncModeLabel
        {
            get
            {
                switch (SelectedSyncMode)
                {
                    case OverlayFpsSyncMode.FrontEdgeSync: return "Front Edge";
                    case OverlayFpsSyncMode.BackEdgeSync: return "Back Edge";
                    case OverlayFpsSyncMode.ReflexSync: return "NVIDIA Reflex";
                    case OverlayFpsSyncMode.ScanlineSync: return "Scanline Sync";
                    case OverlayFpsSyncMode.ScanlineSyncHalf: return "Scanline Sync x/2";
                    default: return "Async";
                }
            }
        }

        public bool ReflexMarkersEnabled
        {
            get => _reflexMarkersEnabled;
            set { _reflexMarkersEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(ReflexMarkersLabel)); }
        }

        public string ReflexMarkersLabel => ReflexMarkersEnabled ? "Reflex Markers: On" : "Reflex Markers: Off";

        public ObservableCollection<FpsPresetViewModel> FpsPresets
        {
            get => _fpsPresets;
            set { _fpsPresets = value ?? new ObservableCollection<FpsPresetViewModel>(); OnPropertyChanged(); }
        }

        private void UpdateFpsPresetSelection()
        {
            foreach (var preset in FpsPresets)
                preset.IsSelected = SelectedFpsLimit > 0 &&
                    Math.Abs(preset.Value - SelectedFpsLimit) < 0.01;
        }

        public double VolumeLevel
        {
            get => _volumeLevel;
            set
            {
                double v = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(_volumeLevel - v) < 0.0001) return;
                _volumeLevel = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VolumePercent));
                System.Threading.Interlocked.Increment(ref _audioRefreshVersion);
                ScheduleVolumeApply();
                ScheduleVolumeProfileSave();
            }
        }

        public int VolumePercent => (int)Math.Round(VolumeLevel * 100);
        public bool UsePerGameVolume => _usePerGameVolume;

        public void SetVolumeControlMode(string mode)
        {
            bool usePerGame = string.Equals(mode, "PerGame", StringComparison.OrdinalIgnoreCase);
            if (_usePerGameVolume == usePerGame) return;

            _usePerGameVolume = usePerGame;
            _volApplyTimer?.Stop();
            _volSaveTimer?.Stop();
            lock (_volLock)
                _pendingVolume = null;
            _lastGameVolumeRestoreId = Guid.Empty;
            _lastGameVolumeRestorePid = 0;
            _restoringGameVolumeProfile = false;
            _lastMasterSessionNormalizeId = Guid.Empty;
            _lastMasterSessionNormalizePid = 0;
            OnPropertyChanged(nameof(UsePerGameVolume));
            System.Threading.Interlocked.Increment(ref _audioRefreshVersion);
            if (GameId != Guid.Empty && ProcessId > 0)
                RefreshAudioState();
        }

        private System.Windows.Threading.DispatcherTimer _volApplyTimer;
        private System.Windows.Threading.DispatcherTimer _volSaveTimer;
        private Guid _pendingProfileGameId;
        private bool _pendingProfileIsGameVolume;
        private float _pendingProfileVolume;

        private void ScheduleVolumeApply()
        {
            if (_volApplyTimer == null)
            {
                // ~1 frame: keeps audio in sync without flooding COM on every mouse move
                _volApplyTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(8)
                };
                _volApplyTimer.Tick += (s, e) => { _volApplyTimer.Stop(); ApplyVolumeNow(); };
            }
            if (!_volApplyTimer.IsEnabled) _volApplyTimer.Start();
        }

        private void ScheduleVolumeProfileSave()
        {
            _pendingProfileGameId = GameId;
            _pendingProfileIsGameVolume = UsePerGameVolume;
            _pendingProfileVolume = (float)VolumeLevel;
            if (_pendingProfileGameId == Guid.Empty)
                return;
            if (_volSaveTimer == null)
            {
                _volSaveTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(600)
                };
                _volSaveTimer.Tick += (s, e) =>
                {
                    _volSaveTimer.Stop();
                    try
                    {
                        SaveVolumeProfile(_pendingProfileGameId, _pendingProfileVolume, _pendingProfileIsGameVolume);
                    }
                    catch { }
                };
            }
            _volSaveTimer.Stop();
            _volSaveTimer.Start();
        }

        private static void SaveVolumeProfile(Guid gameId, float volume, bool gameSession)
        {
            if (gameSession)
                MasterVolumeProfiles.SaveGameVolume(gameId, volume);
            else
                MasterVolumeProfiles.Save(gameId, volume);
        }

        private readonly object _volLock = new object();
        private float? _pendingVolume;
        private bool _pendingVolumeIsGameSession;
        private int _pendingVolumeProcessId;
        private string _pendingVolumeExePath;
        private bool _volWorkerRunning;

        private void ApplyVolumeNow()
        {
            lock (_volLock)
            {
                _pendingVolume = (float)VolumeLevel;
                _pendingVolumeIsGameSession = UsePerGameVolume;
                _pendingVolumeProcessId = ProcessId;
                _pendingVolumeExePath = GameExecutablePath;
                if (_volWorkerRunning) return;
                _volWorkerRunning = true;
            }

            if (!System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false;
                string err = null;
                while (true)
                {
                    float v;
                    bool gameSession;
                    int pid;
                    string exe;
                    lock (_volLock)
                    {
                        if (!_pendingVolume.HasValue) { _volWorkerRunning = false; break; }
                        v = _pendingVolume.Value;
                        gameSession = _pendingVolumeIsGameSession;
                        pid = _pendingVolumeProcessId;
                        exe = _pendingVolumeExePath;
                        _pendingVolume = null;
                    }

                    ok = false;
                    err = null;
                    try
                    {
                        ok = gameSession
                            ? pid > 0 && GameAudioSession.TrySetVolume(pid, exe, v)
                            : GameAudioSession.TrySetMasterVolume(v);
                    }
                    catch (System.Exception ex) { err = ex.Message; }
                    if (!ok && err == null)
                        err = gameSession
                            ? (GameAudioSession.LastError ?? "Could not change game volume")
                            : (GameAudioSession.LastError ?? "Could not change master volume");
                }

                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null) return;
                try
                {
                    disp.BeginInvoke(new System.Action(() =>
                    {
                        if (ok)
                        {
                            if (!_audioSessionAvailable) AudioSessionAvailable = true;
                            if (!string.IsNullOrEmpty(_audioStatusText)) AudioStatusText = "";
                        }
                        else if (!string.IsNullOrEmpty(err))
                        {
                            AudioSessionAvailable = false;
                            AudioStatusText = err;
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { }
            }))
            {
                lock (_volLock)
                {
                    _volWorkerRunning = false;
                    _pendingVolume = null;
                }
                AudioStatusText = "Could not queue volume request";
            }
        }

        public bool AudioSessionAvailable
        {
            get => _audioSessionAvailable;
            set { _audioSessionAvailable = value; OnPropertyChanged(); }
        }

        public bool IsGameMuted
        {
            get => _isGameMuted;
            private set
            {
                if (_isGameMuted == value) return;
                _isGameMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GameMuteButtonToolTip));
            }
        }

        public string GameMuteButtonToolTip => IsGameMuted ? "Unmute game audio" : "Mute game audio";

        public string AudioStatusText
        {
            get => _audioStatusText;
            set
            {
                // The "No audio session (play sound in-game first)" hint is no longer shown in the panel.
                if (!string.IsNullOrEmpty(value) && value.StartsWith("No audio session", StringComparison.OrdinalIgnoreCase))
                    value = string.Empty;
                _audioStatusText = value;
                OnPropertyChanged();
            }
        }

        public System.Collections.ObjectModel.ObservableCollection<GameAudioSession.AudioDeviceInfo> AudioDevices { get; }
            = new System.Collections.ObjectModel.ObservableCollection<GameAudioSession.AudioDeviceInfo>();

        private GameAudioSession.AudioDeviceInfo _selectedAudioDevice;
        private bool _suppressDeviceChange;
        public bool IsRefreshingAudioDevices => _suppressDeviceChange;
        private string _lastDeviceListKey = "";

        public GameAudioSession.AudioDeviceInfo SelectedAudioDevice
        {
            get => _selectedAudioDevice;
            set
            {
                if (ReferenceEquals(_selectedAudioDevice, value)) return;
                _selectedAudioDevice = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentAudioDeviceName));
            }
        }

        // Confirmation is explicit: D-pad selection MUST NOT switch Windows devices.
        public void ApplySelectedAudioDevice()
        {
            var selected = SelectedAudioDevice;
            if (selected == null || string.IsNullOrEmpty(selected.Id)) return;
            if (!GameAudioSession.SetDefaultPlaybackDevice(selected.Id))
            {
                AudioStatusText = GameAudioSession.LastError ?? "Could not switch output";
                return;
            }
            AudioStatusText = "Output changed to " + selected.Name;
            // Update checkmarks via INotifyPropertyChanged on IsDefault (do not rebuild the list).
            foreach (var d in AudioDevices)
                d.IsDefault = string.Equals(d.Id, selected.Id, StringComparison.OrdinalIgnoreCase);
            selected.IsDefault = true;
            _lastDeviceListKey = string.Join("|", AudioDevices.Select(d => (d.Id ?? "") + (d.IsDefault ? "*" : "")));
            // Output device changed; re-read the game's session on the new endpoint.
            RefreshAudioState();
        }

        public string CurrentAudioDeviceName =>
            SelectedAudioDevice?.Name ?? (AudioDevices.Count == 0 ? "No devices found" : "");

        public void RefreshAudioDevices()
        {
            try
            {
                var list = GameAudioSession.ListPlaybackDevices()
                    ?? new System.Collections.Generic.List<GameAudioSession.AudioDeviceInfo>();
                string key = string.Join("|", list.ConvertAll(d => (d.Id ?? "") + (d.IsDefault ? "*" : "")));
                // Avoid rebuilding list when nothing changed (prevents flicker / selection loss)
                if (key == _lastDeviceListKey && AudioDevices.Count == list.Count)
                    return;
                _lastDeviceListKey = key;

                string keepId = _selectedAudioDevice?.Id;
                _suppressDeviceChange = true;
                try
                {
                    AudioDevices.Clear();
                    GameAudioSession.AudioDeviceInfo sel = null;
                    foreach (var d in list)
                    {
                        AudioDevices.Add(d);
                        if (!string.IsNullOrEmpty(keepId) &&
                            string.Equals(d.Id, keepId, StringComparison.OrdinalIgnoreCase))
                            sel = d;
                        else if (sel == null && d.IsDefault)
                            sel = d;
                    }
                    if (sel == null && AudioDevices.Count > 0)
                        sel = AudioDevices[0];

                    // Prefer the system default so the ListBox highlights the active device
                    if (sel == null || (list.Exists(d => d.IsDefault) && !sel.IsDefault))
                    {
                        var def = list.Find(d => d.IsDefault);
                        if (def != null) sel = def;
                    }

                    _selectedAudioDevice = sel;
                    OnPropertyChanged(nameof(SelectedAudioDevice));
                    OnPropertyChanged(nameof(CurrentAudioDeviceName));
                }
                finally
                {
                    _suppressDeviceChange = false;
                }
            }
            catch
            {
                _suppressDeviceChange = false;
            }
        }

        public string FpsStatusText
        {
            get => _fpsStatusText;
            set { _fpsStatusText = value; OnPropertyChanged(); }
        }

        // Commands
        public ICommand ReturnToGameCommand { get; }
        public ICommand ShowPlayniteCommand { get; }
        public ICommand CloseGameCommand { get; }
        public ICommand SuspendGameCommand { get; }

        public bool ShowSuspendButton
        {
            get => _showSuspendButton;
            set { if (_showSuspendButton != value) { _showSuspendButton = value; OnPropertyChanged(); } }
        }
        public ICommand ShortcutButtonCommand { get; }
        public ICommand OpenAchievementBrowserCommand { get; }
        public ICommand CloseAchievementBrowserCommand { get; }
        public ICommand SetAchievementFilterCommand { get; }
        public ICommand SetFpsLimitCommand { get; }
        public ICommand SetSyncModeCommand { get; }
        public ICommand SetScanlineTargetCommand { get; }
        public ICommand SetFpsBackendCommand { get; }
        public ICommand ToggleReflexMarkersCommand { get; }
        public ICommand DisableFpsCapCommand { get; }
        public ICommand ToggleVrrCommand { get; }
        // Event for showing Playnite
        public event Action<bool> ShowPlayniteRequested;
        public event Action HideOverlayRequested;
        public void RequestHideOverlay()
        {
            HideOverlayRequested?.Invoke();
        }
        public event Action CloseGameRequested;
        public event Action SuspendGameRequested;
        public event Action<ShortcutButtonViewModel> ExecuteShortcutRequested;

        // Constructor with design-time data support
        public OverlayWindowViewModel(bool designMode = false)
        {
            ShortcutButtons = new ObservableCollection<ShortcutButtonViewModel>();

            // Initialize commands
            ReturnToGameCommand = new RelayCommand(_ => HideOverlayRequested?.Invoke());
            ShowPlayniteCommand = new RelayCommand(_ => ShowPlayniteRequested?.Invoke(false));
            CloseGameCommand = new RelayCommand(_ => CloseGameRequested?.Invoke());
            SuspendGameCommand = new RelayCommand(_ => SuspendGameRequested?.Invoke());
            ShortcutButtonCommand = new RelayCommand(ExecuteShortcut);
            OpenAchievementBrowserCommand = new RelayCommand(_ =>
            {
                if (HasAchievements)
                    IsAchievementBrowserOpen = true;
            });
            CloseAchievementBrowserCommand = new RelayCommand(_ => IsAchievementBrowserOpen = false);
            SetAchievementFilterCommand = new RelayCommand(param =>
            {
                if (param is string filter)
                    AchievementFilter = filter;
            });
            SetFpsLimitCommand = new RelayCommand(param =>
            {
                if (param == null) return;
                double fps;
                if (param is double d) fps = d;
                else if (!double.TryParse(param.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out fps))
                    return;
                SelectedFpsLimit = fps;
                ApplyFpsSettings();
            });
            SetSyncModeCommand = new RelayCommand(param =>
            {
                OverlayFpsSyncMode mode;
                if (param is OverlayFpsSyncMode m)
                    mode = m;
                else if (param != null && Enum.TryParse(param.ToString(), true, out OverlayFpsSyncMode parsed))
                    mode = parsed;
                else
                    return;
                RequestSyncMode(mode);
            });
            SetScanlineTargetCommand = new RelayCommand(param =>
            {
                if (param == null) return;
                double value;
                if (param is double d) value = d;
                else if (!double.TryParse(param.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                    return;
                ScanlineSyncTarget = value;
                CommitScanlineTarget();
            });
            SetFpsBackendCommand = new RelayCommand(param =>
            {
                OverlayFpsBackend backend;
                if (param is OverlayFpsBackend b)
                    backend = b;
                else if (param != null && Enum.TryParse(param.ToString(), true, out OverlayFpsBackend parsed))
                    backend = parsed;
                else
                    return;
                // Optimistic UI update; host routes to FPS Limiter SetGameBackendPreference
                SelectedFpsBackend = backend;
                if (backend == OverlayFpsBackend.SpecialK && (SelectedSyncMode == OverlayFpsSyncMode.ScanlineSync || SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf))
                    SelectedSyncMode = OverlayFpsSyncMode.Async;
                FpsActionRequested?.Invoke("backend", backend == OverlayFpsBackend.SpecialK ? "SpecialK" : "Rtss");
            });
            ToggleReflexMarkersCommand = new RelayCommand(_ =>
            {
                ReflexMarkersEnabled = !ReflexMarkersEnabled;
                RequestReflex(ReflexMarkersEnabled);
            });
            DisableFpsCapCommand = new RelayCommand(_ => RequestDisableCap());
            ToggleVrrCommand = new RelayCommand(_ => RequestToggleVrr());
            RtssAvailable = OverlayRtssController.IsRtssAvailable();
            FpsStatusText = "";

            if (designMode || System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            {
                // Initialize with sample data for design time
                GameTitle = "Sample Game Title";
                HasBattery = true;
                BatteryPercentage = 75;
                CurrentTime = "12:34";
                TotalPlaytime = TimeSpan.FromHours(125);
                SessionPlaytime = TimeSpan.FromMinutes(154);
                HasAchievements = true;
                UnlockPercentText = "Unlocked 42/100 Achievements";
                UnlockProgress = 0.42;
                UnlockedCount = 42;
                TotalAchievementCount = 100;
                DebugVisible = true;
                DebugInfo = "DEBUG MODE ENABLED";

                // Sample achievement
                LastAchievement = new AchievementData
                {
                    Name = "Sample Achievement",
                    Description = "This is a sample achievement description for design-time display.",
                    IconUrl = "/PlayniteGameOverlay;component/assets/achievement.png",
                    IsUnlocked = true,
                    UnlockDate = DateTime.Now.AddDays(-2)
                };

                Achievements.Add(LastAchievement);
                Achievements.Add(new AchievementData
                {
                    Name = "Locked Trophy",
                    Description = "Still waiting to be unlocked.",
                    IsUnlocked = false,
                    IconUrl = "https://icons.veryicon.com/png/o/business/classic-icon/trophy-20.png"
                });
                ApplyAchievementFilter();

                // Add sample shortcut buttons
                InitializeShortcutButtons(new OverlaySettings
                {
                    ShowRecordGameplay = true,
                    ShowRecordRecent = true,
                    ShowStreaming = true,
                    ShowPerformanceOverlay = true,
                    ShowScreenshotGallery = true,
                    ShowWebBrowser = true,
                    ShowDiscord = true
                });
            }
        }


        public void UpdateFromGameOverlayData(GameOverlayData data)
        {
            if (data == null)
            {
                IsGameRunning = false;
                return;
            }

            IsGameRunning = true;
            GameTitle = data.GameName;
            ProcessId = data.ProcessId;
            GameId = data.GameId;
            GameExecutablePath = data.ExecutablePath;
            // Restore when overlay opens, not on every data refresh.
            SessionStartTime = data.GameStartTime;
            TotalPlaytime = data.Playtime;
            CoverImagePath = data.CoverImagePath;
            BackgroundImagePath = data.BackgroundImagePath;

            // Process achievements
            Achievements.Clear();
            IsAchievementBrowserOpen = false;

            HasAchievements = data.Achievements != null && data.Achievements.Count > 0;

            if (HasAchievements)
            {
                AchievementData lastUnlocked = null;
                int totalAchievements = data.Achievements.Count;
                int unlockedCount = 0;
                int goldCount = 0, silverCount = 0, bronzeCount = 0;

                // Sort: unlocked first as before; within unlocked/locked groups,
                // most common achievements first (higher global unlock percentage first).
                // Unknown rarity sorts last; unlock date/name are only tie-breakers.
                var sorted = data.Achievements
                    .OrderByDescending(a => a.IsUnlocked)
                    .ThenBy(a => a.GlobalPercent.HasValue ? 0 : 1)
                    .ThenByDescending(a => a.GlobalPercent ?? double.MinValue)
                    .ThenByDescending(a => a.UnlockDate ?? DateTime.MinValue)
                    .ThenBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var achievement in sorted)
                {
                    Achievements.Add(achievement);
                    if (achievement.IsUnlocked)
                    {
                        unlockedCount++;
                        // Trophy tiers by rarity: gold < 10%, silver < 50%, bronze otherwise.
                        if (achievement.GlobalPercent.HasValue && achievement.GlobalPercent.Value < 10.0) goldCount++;
                        else if (achievement.GlobalPercent.HasValue && achievement.GlobalPercent.Value < 50.0) silverCount++;
                        else bronzeCount++;
                        if (lastUnlocked == null ||
                            (achievement.UnlockDate.HasValue &&
                             (!lastUnlocked.UnlockDate.HasValue || achievement.UnlockDate > lastUnlocked.UnlockDate)))
                        {
                            lastUnlocked = achievement;
                        }
                    }
                }

                LastAchievement = lastUnlocked ?? new AchievementData
                {
                    Name = "No Achievements Unlocked",
                    Description = "Keep playing to unlock achievements",
                    IconUrl = "https://icons.veryicon.com/png/o/business/classic-icon/trophy-20.png"
                };

                UnlockedCount = unlockedCount;
                GoldTrophyCount = goldCount;
                SilverTrophyCount = silverCount;
                BronzeTrophyCount = bronzeCount;
                TotalAchievementCount = totalAchievements;
                UnlockProgress = totalAchievements > 0 ? (double)unlockedCount / totalAchievements : 0;
                UnlockPercentText = $"Unlocked {unlockedCount}/{totalAchievements} Achievements";
                ApplyAchievementFilter();
            }
            else
            {
                LastAchievement = new AchievementData
                {
                    Name = "No Achievements Available",
                    Description = "This game doesn't have achievements tracked",
                    IconUrl = "https://icons.veryicon.com/png/o/business/classic-icon/trophy-20.png"
                };
                UnlockPercentText = string.Empty;
                UnlockProgress = 0;
                UnlockedCount = 0;
                GoldTrophyCount = 0;
                SilverTrophyCount = 0;
                BronzeTrophyCount = 0;
                TotalAchievementCount = 0;
                FilteredAchievements.Clear();
            }
        }

        private void ApplyAchievementFilter()
        {
            FilteredAchievements.Clear();
            IEnumerable<AchievementData> source = Achievements;
            if (AchievementFilter == "Unlocked")
                source = Achievements.Where(a => a.IsUnlocked);
            else if (AchievementFilter == "Locked")
                source = Achievements.Where(a => !a.IsUnlocked);

            foreach (var a in source)
                FilteredAchievements.Add(a);
        }

        /// <summary>
        /// Returns true if the browser was open and is now closed (caller should not hide overlay).
        /// </summary>
        public bool TryCloseAchievementBrowser()
        {
            if (!IsAchievementBrowserOpen)
                return false;
            IsAchievementBrowserOpen = false;
            return true;
        }

        public void UpdateSessionTime()
        {
            if (SessionStartTime != default)
            {
                SessionPlaytime = DateTime.Now - SessionStartTime;
            }
        }

        public void UpdateCurrentTime()
        {
            CurrentTime = DateTime.Now.ToString("HH:mm");
        }

        public void UpdateBatteryStatus(int percentage)
        {
            BatteryPercentage = percentage;
        }

        private void ExecuteShortcut(object parameter)
        {
            if (parameter is ShortcutButtonViewModel shortcut)
            {
                ExecuteShortcutRequested?.Invoke(shortcut);
            }
        }

        public void UpdateDebugInfo(string info)
        {
            DebugInfo = info;
        }


        /// <summary>
        /// Live-update RTSS the same way FPS Limiter does on hotkey / menu change.
        /// Writes profile files + UpdateProfiles while the game stays suspended —
        /// no resume — so applying a cap does not minimize the game.
        /// The new limit is picked up when the game is resumed on overlay close.
        /// </summary>
        private void ApplyFpsSettings()
        {
            try
            {
                // Route through host → FPS Limiter LimiterService (tandem)
                FpsSettingsChanged?.Invoke(SelectedFpsLimit, SelectedSyncMode, ReflexMarkersEnabled);
                FpsActionRequested?.Invoke("limit", UseRefreshRatePresets && SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf ? SelectedFpsLimit * 2.0 : SelectedFpsLimit);

                string limit = SelectedFpsLimit <= 0 ? "Unlimited" : FpsLimitLabel + " FPS";
                FpsStatusText = "";
                RtssAvailable = true;
            }
            catch (Exception ex)
            {
                FpsStatusText = "";
                logger.Error($"FPS apply failed: {ex.Message}");
            }
        }

        public void RequestDisableCap()
        {
            SelectedFpsLimit = 0;
            FpsActionRequested?.Invoke("disable", null);
            FpsStatusText = "";
        }

        public void RequestToggleVrr()
        {
            FpsActionRequested?.Invoke("vrr", null);
            FpsStatusText = "";
        }

        public void RequestSyncMode(OverlayFpsSyncMode mode)
        {
            SelectedSyncMode = mode;
            FpsActionRequested?.Invoke("sync", (int)mode);
            FpsStatusText = "";
        }

        public void RequestReflex(bool enabled)
        {
            ReflexMarkersEnabled = enabled;
            FpsActionRequested?.Invoke("reflex", enabled);
            FpsStatusText = "";
        }

        public void LoadPresets(IList<double> presets)
        {
            _cachedFpsPresets = presets != null
                ? presets.Where(p => p > 0).Distinct().OrderBy(p => p).ToList()
                : new List<double> { 30, 60, 120 };
            RebuildPresetList();
        }

        /// <summary>Prefer rates from FPS Limiter RefreshRateManager (primary display, current resolution).</summary>
        public void SetRefreshRates(IList<double> rates)
        {
            if (rates != null && rates.Count > 0)
                _cachedRefreshRates = rates.Where(r => r > 1).Distinct().OrderBy(r => r).ToList();
            RebuildPresetList();
        }

        /// <summary>
        /// Rebuild the visible preset chips: FPS presets from the extension normally;
        /// display refresh rates when SpecialK is selected or RTSS Scanline Sync is active
        /// (matches extension Latent Sync / Scanline FPS=Hz behavior).
        /// </summary>
        public void RebuildPresetList()
        {
            FpsPresets.Clear();
            IEnumerable<double> source;
            if (UseRefreshRatePresets)
            {
                if (_cachedRefreshRates == null || _cachedRefreshRates.Count == 0)
                    _cachedRefreshRates = GetDisplayRefreshRates();
                source = _cachedRefreshRates.Select(r => SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf ? r / 2.0 : r);
            }
            else
            {
                source = _cachedFpsPresets ?? new List<double> { 30, 60, 120 };
            }

            foreach (var p in source)
                FpsPresets.Add(new FpsPresetViewModel(
                    p, SelectedFpsLimit > 0 && Math.Abs(p - SelectedFpsLimit) < 0.01));
            OnPropertyChanged(nameof(FpsPresets));
        }

        /// <summary>Enumerate unique refresh rates reported by connected displays (Win32).</summary>
        public static List<double> GetDisplayRefreshRates()
        {
            var rates = new HashSet<double>();
            try
            {
                // null device name = primary display; also walk a few numbered devices
                string[] devices = { null, @"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3" };
                foreach (var device in devices)
                {
                    var dm = new DEVMODE();
                    dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE));
                    if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) != 0)
                    {
                        if (dm.dmDisplayFrequency > 1)
                            rates.Add(dm.dmDisplayFrequency);
                    }
                    for (int i = 0; EnumDisplaySettings(device, i, ref dm) != 0; i++)
                    {
                        if (dm.dmDisplayFrequency > 1 && dm.dmDisplayFrequency < 1000)
                            rates.Add(dm.dmDisplayFrequency);
                        if (i > 250) break;
                    }
                }
            }
            catch
            {
                // Fallback common rates if Win32 query fails
            }

            if (rates.Count == 0)
            {
                rates.Add(60);
                rates.Add(120);
                rates.Add(144);
                rates.Add(165);
                rates.Add(240);
            }

            return rates.OrderBy(r => r).ToList();
        }

        private const int ENUM_CURRENT_SETTINGS = -1;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern int EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        /// <summary>Raised when the user applies FPS settings from the overlay panel.</summary>
        public event Action<double, OverlayFpsSyncMode, bool> FpsSettingsChanged;
        /// <summary>action: limit|disable|sync|reflex|vrr — payload optional.</summary>
        public event Action<string, object> FpsActionRequested;


        // Reads the selected volume target and always reads mute from the game's app session.
        private void RefreshAudioStateFromEndpoints()
        {
            int processId = ProcessId;
            string exePath = GameExecutablePath;
            bool gameVolumeMode = UsePerGameVolume;
            int version = System.Threading.Interlocked.Increment(ref _audioRefreshVersion);

            if (!System.Threading.ThreadPool.QueueUserWorkItem(workerState =>
            {
                bool volumeOk;
                float volume = 1f;
                float gameVolume = 1f;
                bool gameMuted = false;
                bool gameSessionOk = processId > 0 &&
                    GameAudioSession.TryGetVolume(processId, exePath, out gameVolume, out gameMuted);
                if (gameVolumeMode)
                {
                    volumeOk = gameSessionOk;
                    if (gameSessionOk) volume = gameVolume;
                }
                else
                {
                    volumeOk = GameAudioSession.TryGetMasterVolume(out volume, out _);
                }
                string error = volumeOk ? "" :
                    (gameVolumeMode
                        ? (processId > 0 ? "" : "Game process is unavailable")
                        : (GameAudioSession.LastError ?? "Could not read master volume"));
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                try
                {
                    dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        if (version != System.Threading.Interlocked.CompareExchange(ref _audioRefreshVersion, 0, 0))
                            return;
                        IsGameMuted = gameSessionOk && gameMuted;
                        if (volumeOk)
                        {
                            _volumeLevel = volume;
                            AudioSessionAvailable = true;
                            AudioStatusText = "";
                            OnPropertyChanged(nameof(VolumeLevel));
                            OnPropertyChanged(nameof(VolumePercent));
                        }
                        else
                        {
                            AudioSessionAvailable = false;
                            AudioStatusText = error;
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { }
            }))
            {
                AudioSessionAvailable = false;
                AudioStatusText = "Could not queue audio refresh";
            }
        }

        public void RefreshAudioState()
        {
            Guid gameId = GameId;
            int processId = ProcessId;
            string exePath = GameExecutablePath;

            if (UsePerGameVolume)
            {
                MasterVolumeProfiles.MigrateLegacyToGameVolume(gameId);
                if (processId > 0 && MasterVolumeProfiles.TryGetGameVolume(gameId, out float saved) &&
                    (_lastGameVolumeRestoreId != gameId || _lastGameVolumeRestorePid != processId))
                {
                    if (_restoringGameVolumeProfile) return;
                    _restoringGameVolumeProfile = true;
                    int version = System.Threading.Interlocked.Increment(ref _audioRefreshVersion);
                    int targetPid = processId;
                    string targetExe = exePath;
                    if (System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        bool restored = GameAudioSession.TrySetVolume(targetPid, targetExe, saved);
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher == null)
                        {
                            _restoringGameVolumeProfile = false;
                            return;
                        }
                        try
                        {
                            dispatcher.BeginInvoke(new System.Action(() =>
                            {
                                _restoringGameVolumeProfile = false;
                                if (restored && version == System.Threading.Interlocked.CompareExchange(ref _audioRefreshVersion, 0, 0) &&
                                    UsePerGameVolume && GameId == gameId && ProcessId == targetPid)
                                {
                                    _lastGameVolumeRestoreId = gameId;
                                    _lastGameVolumeRestorePid = targetPid;
                                }
                                if (UsePerGameVolume)
                                    RefreshAudioStateFromEndpoints();
                                else
                                    RefreshAudioState();
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                        catch { _restoringGameVolumeProfile = false; }
                    }))
                        return;
                    _restoringGameVolumeProfile = false;
                }

                RefreshAudioStateFromEndpoints();
                return;
            }

            bool hasLegacy = MasterVolumeProfiles.TryGetLegacy(gameId, out _);
            bool hasPerGameProfile = MasterVolumeProfiles.TryGetGameVolume(gameId, out _);
            if (processId > 0 && (hasLegacy || hasPerGameProfile))
            {
                if (_normalizingMasterSession) return;
                if (_lastMasterSessionNormalizeId == gameId && _lastMasterSessionNormalizePid == processId)
                {
                    RefreshAudioStateFromEndpoints();
                    return;
                }
                _normalizingMasterSession = true;
                int version = System.Threading.Interlocked.Increment(ref _audioRefreshVersion);
                if (System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool normalized = GameAudioSession.TrySetVolume(processId, exePath, 1f);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher == null)
                    {
                        _normalizingMasterSession = false;
                        return;
                    }
                    try
                    {
                        dispatcher.BeginInvoke(new System.Action(() =>
                        {
                            _normalizingMasterSession = false;
                            if (normalized)
                            {
                                if (hasLegacy && !UsePerGameVolume)
                                    MasterVolumeProfiles.RemoveLegacy(gameId);
                                if (version == System.Threading.Interlocked.CompareExchange(ref _audioRefreshVersion, 0, 0) &&
                                    !UsePerGameVolume && GameId == gameId && ProcessId == processId)
                                {
                                    _lastMasterSessionNormalizeId = gameId;
                                    _lastMasterSessionNormalizePid = processId;
                                }
                            }
                            if (UsePerGameVolume)
                                RefreshAudioState();
                            else
                                RefreshAudioStateFromEndpoints();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    catch { _normalizingMasterSession = false; }
                }))
                    return;
                _normalizingMasterSession = false;
            }

            RefreshAudioStateFromEndpoints();
        }

        public void CommitVolume()
        {
            _volApplyTimer?.Stop();
            _volSaveTimer?.Stop();
            ApplyVolumeNow();
            try { SaveVolumeProfile(GameId, (float)VolumeLevel, UsePerGameVolume); } catch { }
        }

        /// <summary>Raised (on a worker thread) after the overlay's mute button changed the game's audio session: pid, exe, muted.</summary>
        public static event System.Action<int, string, bool> GameMuteApplied;

        public void ToggleGameMute()
        {
            int processId = ProcessId;
            string exePath = GameExecutablePath;
            if (processId <= 0)
            {
                AudioStatusText = "Game process is unavailable";
                return;
            }

            bool target;
            lock (_gameMuteLock)
            {
                target = !IsGameMuted;
                // Update the icon immediately; only the game's audio session is touched.
                IsGameMuted = target;
                _pendingGameMute = target;
                _pendingGameMutePid = processId;
                _pendingGameMuteExe = exePath;
                _gameMuteRequestVersion++;
                if (_gameMuteWorkerRunning)
                    return;
                _gameMuteWorkerRunning = true;
            }

            if (!System.Threading.ThreadPool.QueueUserWorkItem(_ => ApplyPendingGameMute()))
            {
                lock (_gameMuteLock)
                {
                    _gameMuteWorkerRunning = false;
                    _pendingGameMute = null;
                }
                IsGameMuted = !target;
                AudioStatusText = "Could not queue game mute request";
            }
        }

        private void ApplyPendingGameMute()
        {
            while (true)
            {
                bool target;
                int processId;
                string exePath;
                int requestVersion;
                lock (_gameMuteLock)
                {
                    if (!_pendingGameMute.HasValue)
                    {
                        _gameMuteWorkerRunning = false;
                        return;
                    }

                    target = _pendingGameMute.Value;
                    _pendingGameMute = null;
                    processId = _pendingGameMutePid;
                    exePath = _pendingGameMuteExe;
                    requestVersion = _gameMuteRequestVersion;
                }

                bool success = GameAudioSession.TrySetMute(processId, exePath, target);
                if (success)
                {
                    // Windows keeps an app's mute between launches: let the plugin remember it so the next launch resets it.
                    try { GameMuteApplied?.Invoke(processId, exePath, target); } catch { }
                }
                string error = success ? "" :
                    (GameAudioSession.LastError ?? "Could not change game mute state");
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                    continue;

                try
                {
                    dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        lock (_gameMuteLock)
                        {
                            if (requestVersion != _gameMuteRequestVersion)
                                return;
                        }

                        if (success)
                        {
                            IsGameMuted = target;
                            AudioStatusText = "";
                        }
                        else
                        {
                            IsGameMuted = !target;
                            AudioStatusText = error;
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { }
            }
        }

        public void InitializeShortcutButtons(OverlaySettings settings)
        {
            ShortcutButtons.Clear();

            // Add buttons based on settings
            if (settings.ShowRecordGameplay)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Record Gameplay",
                    IconPath = "/PlayniteGameOverlay;component/assets/record.png",
                    ShortcutCommand = settings.RecordGameplayShortcut,
                    Type = ShortcutType.KbdShortcut,
                });
            }

            if (settings.ShowRecordRecent)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Record Last 30s",
                    IconPath = "/PlayniteGameOverlay;component/assets/record-recent.png",
                    ShortcutCommand = settings.RecordRecentShortcut,
                    Type = ShortcutType.KbdShortcut,
                });
            }

            if (settings.ShowStreaming)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Streaming",
                    IconPath = "/PlayniteGameOverlay;component/assets/stream.png",
                    ShortcutCommand = settings.StreamingShortcut,
                    Type = ShortcutType.KbdShortcut,
                });
            }

            if (settings.ShowPerformanceOverlay)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Performance",
                    IconPath = "/PlayniteGameOverlay;component/assets/performance.png",
                    ShortcutCommand = settings.PerformanceOverlayShortcut,
                    Type = ShortcutType.KbdShortcut,
                });
            }

            if (settings.ShowScreenshotGallery)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Screenshots",
                    IconPath = "/PlayniteGameOverlay;component/assets/screenshot.png",
                    ShortcutCommand = settings.ScreenshotGalleryPath,
                    Type = ShortcutType.Path,
                });
            }

            if (settings.ShowWebBrowser)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Web Browser",
                    IconPath = "/PlayniteGameOverlay;component/assets/browser.png",
                    ShortcutCommand = settings.WebBrowserPath,
                    Type = ShortcutType.Path,
                });
            }

            if (settings.ShowSteamFriendsButton && !string.IsNullOrEmpty(SteamInstantPlay.GetSteamPath()))
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Steam Friends",
                    IconPath = "/PlayniteGameOverlay;component/assets/friends.png",
                    ShortcutCommand = "steam://open/friends",
                    Type = ShortcutType.SteamFriends
                });
            }

            if (settings.ShowDiscord)
            {
                ShortcutButtons.Add(new ShortcutButtonViewModel
                {
                    Title = "Discord",
                    IconPath = "/PlayniteGameOverlay;component/assets/discord.png",
                    ShortcutCommand = "discord",
                    Type = ShortcutType.Discord
                });
            }
        }

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ShortcutButtonViewModel
    {
        public string Title { get; set; }
        public string IconPath { get; set; }
        public string ShortcutCommand { get; set; }
        public ShortcutType Type { get; set; }
    }

    public enum ShortcutType
    {
        Discord,
        SteamFriends,
        Path,
        KbdShortcut
    }

    // Command implementation
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}