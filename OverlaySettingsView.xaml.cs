using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace PlayniteGameOverlay
{
    public partial class OverlaySettingsView : UserControl
    {
        private string _capturingSlot; // Overlay | ForceClose | Suspend
        private Button _capturingButton;
        private Panel _previewInlineParent;
        private int _previewInlineIndex = -1;
        private bool _previewDocked;
        private const double PreviewDockThreshold = 850;
        private const double PreviewDockWidth = 400;
        private string _previewCoverImagePath;

        public OverlaySettingsView()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                RefreshHotkeyButtonLabels();
                HookPreview();
                HookSmoothSliders();
                ApplySettingsAppearance();
                UpdatePreviewDocking();
                UpdateAppearancePreviewVisibility();
            };
            Unloaded += (_, __) =>
            {
                try { StopPadCapture(commit: false); } catch { }
                if (_previewSettings != null) _previewSettings.PropertyChanged -= PreviewSettings_PropertyChanged;
                _previewSettings = null;
            };
            DataContextChanged += (_, __) => { RefreshHotkeyButtonLabels(); HookPreview(); ApplySettingsAppearance(); };
            SizeChanged += OverlaySettingsView_SizeChanged;
            PreviewKeyDown += OverlaySettingsView_PreviewKeyDown;
            AddHandler(Expander.ExpandedEvent, new RoutedEventHandler(Section_Expanded), true);
        }

        private bool _accordionBusy;

        /// <summary>Opening a section closes the other open sections at the same level and scrolls to it.</summary>
        private void Section_Expanded(object sender, RoutedEventArgs e)
        {
            var expander = e.OriginalSource as Expander;
            if (expander == null || _accordionBusy) return;
            var panel = (LogicalTreeHelper.GetParent(expander) ?? System.Windows.Media.VisualTreeHelper.GetParent(expander)) as Panel;
            if (panel == null) return;

            _accordionBusy = true;
            try
            {
                foreach (var other in panel.Children.OfType<Expander>().Where(x => !ReferenceEquals(x, expander) && x.IsExpanded).ToList())
                    other.IsExpanded = false;
            }
            finally { _accordionBusy = false; }

            // Wait for the collapse to re-layout, then put the pressed section at the top.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var sv = SettingsScrollViewer;
                    if (sv == null || !expander.IsVisible) return;
                    sv.UpdateLayout();
                    var y = expander.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
                    sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + y - 8));
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OverlaySettingsView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePreviewDocking();
        }

        private bool IsAppearanceSectionOpen =>
            PanelsExpander?.IsExpanded == true && AppearanceExpander?.IsExpanded == true;

        private void AppearanceSection_StateChanged(object sender, RoutedEventArgs e)
        {
            UpdateAppearancePreviewVisibility();
        }

        private void UpdateAppearancePreviewVisibility()
        {
            if (!_previewDocked || PreviewDockFrame == null || PreviewPaneColumn == null) return;
            bool show = IsAppearanceSectionOpen;
            PreviewDockFrame.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            PreviewPaneColumn.Width = show ? new GridLength(PreviewDockWidth) : new GridLength(0);
        }

        /// <summary>Keeps the live preview beside the settings while there is enough width.</summary>
        private void UpdatePreviewDocking()
        {
            if (!IsLoaded || PreviewGroup == null || PreviewDockHost == null || PreviewPaneColumn == null)
                return;

            bool shouldDock = ActualWidth >= PreviewDockThreshold;
            if (shouldDock == _previewDocked)
                return;

            if (shouldDock)
            {
                var parent = PreviewGroup.Parent as Panel;
                if (parent == null)
                    return;

                int index = parent.Children.IndexOf(PreviewGroup);
                if (index < 0)
                    return;

                _previewInlineParent = parent;
                _previewInlineIndex = index;
                parent.Children.Remove(PreviewGroup);
                PreviewDockHost.Children.Add(PreviewGroup);
                PreviewPaneColumn.Width = new GridLength(PreviewDockWidth);
                _previewDocked = true;
                UpdateAppearancePreviewVisibility();
                return;
            }

            PreviewDockHost.Children.Remove(PreviewGroup);
            if (_previewInlineParent != null)
            {
                int index = Math.Max(0, Math.Min(_previewInlineIndex, _previewInlineParent.Children.Count));
                _previewInlineParent.Children.Insert(index, PreviewGroup);
            }
            PreviewPaneColumn.Width = new GridLength(0);
            PreviewDockFrame.Visibility = Visibility.Collapsed;
            _previewDocked = false;
        }

        public Dictionary<ControllerShortcut, string> ControllerShortcutDisplayNames { get; } = new Dictionary<ControllerShortcut, string>()
        {
            { ControllerShortcut.StartBack, "View + Menu (Back + Start)" },
            { ControllerShortcut.Guide, "Xbox Button (Guide Button)" }
        };

        public event PropertyChangedEventHandler PropertyChanged;

        private ControllerShortcut _controllerShortcut;

        public ControllerShortcut ControllerShortcut
        {
            get => _controllerShortcut;
            set
            {
                _controllerShortcut = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ControllerShortcut)));
            }
        }

        private CloseBehavior _closeBehavior;

        public CloseBehavior CloseBehavior
        {
            get => _closeBehavior;
            set
            {
                _closeBehavior = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseBehavior)));
            }
        }

        private OverlaySettings Settings => DataContext as OverlaySettings;

        /// <summary>Sets the active game cover used by the dynamic-accent settings preview.</summary>
        public void SetPreviewCoverImagePath(string path)
        {
            if (string.Equals(_previewCoverImagePath, path, StringComparison.OrdinalIgnoreCase)) return;
            _previewCoverImagePath = path;
            UpdatePreview();
        }

        private OverlaySettings _previewSettings;

        private void HookPreview()
        {
            if (_previewSettings != null)
                _previewSettings.PropertyChanged -= PreviewSettings_PropertyChanged;
            _previewSettings = Settings;
            _customPinned = false;
            if (_previewSettings != null)
            {
                _previewSettings.PropertyChanged += PreviewSettings_PropertyChanged;
                // Keep stored accent in sync with the live system color for the dynamic preset
                if (_previewSettings.UseWindowsAccent)
                {
                    _applyingPreset = true;
                    try { _previewSettings.AccentColor = OverlayTheme.WindowsAccentHex(); }
                    finally { _applyingPreset = false; }
                }
            }
            UpdatePreview();
            SyncPresetCombo();
        }

        private void ApplySettingsAppearance()
        {
            AppearancePalette.Apply(this, Settings?.SettingsThemePreset, Settings != null && Settings.SettingsMenuGlass);
        }

        private void PreviewSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) || e.PropertyName == nameof(OverlaySettings.SettingsThemePreset) ||
                e.PropertyName == nameof(OverlaySettings.SettingsMenuGlass))
                ApplySettingsAppearance();
            UpdatePreview();
            if (_applyingPreset) return;

            // Manual tweaks leave named presets → treat as Custom and keep a snapshot
            if (IsAppearanceProperty(e?.PropertyName))
            {
                var s = Settings;
                if (s != null)
                {
                    // Editing accent while on Windows dynamic turns it into a fixed custom accent
                    if (string.Equals(e.PropertyName, nameof(OverlaySettings.AccentColor), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.PanelBackgroundColor), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.PanelOpacity), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.PanelCornerRadius), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.PanelGlassBorder), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.PanelGlassStrength), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.BackdropDim), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(OverlaySettings.UseCoverAccent), StringComparison.Ordinal))
                    {
                        if ((s.UseWindowsAccent || s.UseCoverAccent) &&
                            !string.Equals(e.PropertyName, nameof(OverlaySettings.AccentColor), StringComparison.Ordinal))
                        {
                            // Panel chrome edits keep the selected dynamic accent source.
                        }
                        else if (string.Equals(e.PropertyName, nameof(OverlaySettings.AccentColor), StringComparison.Ordinal))
                        {
                            s.UseWindowsAccent = false;
                            s.UseCoverAccent = false;
                        }
                        s.CaptureCustomAppearance();
                    }
                }
            }
            SyncPresetCombo();
        }

        private static bool IsAppearanceProperty(string name)
        {
            return name == nameof(OverlaySettings.AccentColor)
                || name == nameof(OverlaySettings.PanelBackgroundColor)
                || name == nameof(OverlaySettings.PanelOpacity)
                || name == nameof(OverlaySettings.PanelCornerRadius)
                || name == nameof(OverlaySettings.PanelGlassBorder)
                || name == nameof(OverlaySettings.PanelGlassStrength)
                || name == nameof(OverlaySettings.BackdropDim)
                || name == nameof(OverlaySettings.UseWindowsAccent)
                || name == nameof(OverlaySettings.UseCoverAccent);
        }

        private bool _applyingPreset;
        private bool _syncingPreset;
        // True once the user explicitly picked "Custom". Without it, restoring a Custom snapshot
        // that happens to equal a built-in preset is detected as that preset and the combo snaps back.
        private bool _customPinned;

        private static bool MatchesPanel(OverlaySettings s, string panel, double opacity, double radius)
        {
            return string.Equals(s.PanelBackgroundColor, panel, StringComparison.OrdinalIgnoreCase) &&
                   Math.Abs(s.PanelOpacity - opacity) < 0.005 &&
                   Math.Abs(s.PanelCornerRadius - radius) < 0.5 &&
                   Math.Abs(s.PanelGlassStrength - 0.5) < 0.005;
        }

        private static bool Matches(OverlaySettings s, string accent, string panel, double opacity, double radius)
        {
            return string.Equals(s.AccentColor, accent, StringComparison.OrdinalIgnoreCase) &&
                   MatchesPanel(s, panel, opacity, radius);
        }

        private const string SavedPresetItemTag = "SavedAppearancePreset";

        private static bool IsReservedPresetName(string name)
        {
            return string.Equals(name, "Steam (default)", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Steam Dark", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Windows 11", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Dynamic (cover accent)", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Custom", StringComparison.OrdinalIgnoreCase);
        }

        private bool BuiltInPresetMatches(OverlaySettings s, string name)
        {
            if (string.Equals(name, "Windows 11", StringComparison.OrdinalIgnoreCase))
                return s.UseWindowsAccent && s.PanelGlassBorder &&
                    (MatchesPanel(s, "#202020", 1.0, 8) || MatchesPanel(s, "#202020", 0.94, 8));
            if (string.Equals(name, "Dynamic (cover accent)", StringComparison.OrdinalIgnoreCase))
                return s.UseCoverAccent && !s.UseWindowsAccent && s.PanelGlassBorder &&
                    MatchesPanel(s, "#1A1D23", 1.0, 10);
            if (string.Equals(name, "Steam (default)", StringComparison.OrdinalIgnoreCase))
                return !s.UseWindowsAccent && !s.UseCoverAccent && s.PanelGlassBorder && Matches(s, "#66C0F4", "#1A1D23", 1.0, 10);
            if (string.Equals(name, "Steam Dark", StringComparison.OrdinalIgnoreCase))
                return !s.UseWindowsAccent && !s.UseCoverAccent && s.PanelGlassBorder && Matches(s, "#66C0F4", "#16181D", 1.0, 10);
            return false;
        }

        /// <summary>Preset the current values correspond to, or "Custom" once anything was changed by hand.</summary>
        private string DetectPreset()
        {
            var s = Settings;
            if (s == null) return "Custom";
            if (_customPinned) return "Custom";

            // Preserve a deliberately selected name when presets share the same appearance values.
            var selectedItem = ThemePresetCombo?.SelectedItem as ComboBoxItem;
            var selectedName = selectedItem?.Content as string;
            if (string.Equals(selectedItem?.Tag as string, SavedPresetItemTag, StringComparison.Ordinal))
            {
                var selectedPreset = s.SavedAppearancePresets?.FirstOrDefault(p => p != null &&
                    string.Equals(p.Name, selectedName, StringComparison.OrdinalIgnoreCase) && p.Matches(s));
                if (selectedPreset != null) return selectedPreset.Name;
            }
            else if (BuiltInPresetMatches(s, selectedName))
            {
                return selectedName;
            }

            var saved = s.SavedAppearancePresets?.FirstOrDefault(p => p != null &&
                !string.IsNullOrWhiteSpace(p.Name) && !IsReservedPresetName(p.Name) && p.Matches(s));
            if (saved != null) return saved.Name;

            if (BuiltInPresetMatches(s, "Windows 11")) return "Windows 11";
            if (BuiltInPresetMatches(s, "Dynamic (cover accent)")) return "Dynamic (cover accent)";
            // Legacy: older installs without UseWindowsAccent still match Win11 by live accent hex.
            if (!s.UseWindowsAccent && !s.UseCoverAccent && s.PanelGlassBorder &&
                (Matches(s, OverlayTheme.WindowsAccentHex(), "#202020", 1.0, 8) ||
                 Matches(s, OverlayTheme.WindowsAccentHex(), "#202020", 0.94, 8)))
                return "Windows 11";
            if (BuiltInPresetMatches(s, "Steam (default)")) return "Steam (default)";
            if (BuiltInPresetMatches(s, "Steam Dark")) return "Steam Dark";
            return "Custom";
        }

        private void RefreshSavedPresetItems()
        {
            if (ThemePresetCombo == null) return;
            for (int i = ThemePresetCombo.Items.Count - 1; i >= 0; i--)
            {
                var item = ThemePresetCombo.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, SavedPresetItemTag, StringComparison.Ordinal))
                    ThemePresetCombo.Items.RemoveAt(i);
            }

            int insertAt = ThemePresetCombo.Items.Count;
            for (int i = 0; i < ThemePresetCombo.Items.Count; i++)
            {
                var item = ThemePresetCombo.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(item.Content as string, "Custom", StringComparison.Ordinal))
                { insertAt = i; break; }
            }

            var presets = Settings?.SavedAppearancePresets;
            if (presets == null) return;
            foreach (var preset in presets.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name) && !IsReservedPresetName(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                ThemePresetCombo.Items.Insert(insertAt++, new ComboBoxItem
                {
                    Content = preset.Name.Trim(),
                    Tag = SavedPresetItemTag
                });
            }
        }

        private void SyncPresetCombo()
        {
            try
            {
                _syncingPreset = true;
                string name = DetectPreset();
                RefreshSavedPresetItems();
                ComboBoxItem selected = null;
                foreach (var obj in ThemePresetCombo.Items)
                {
                    var item = obj as ComboBoxItem;
                    if (item != null && string.Equals(item.Content as string, name, StringComparison.OrdinalIgnoreCase))
                    { selected = item; break; }
                }
                if (selected != null && !ReferenceEquals(ThemePresetCombo.SelectedItem, selected))
                    ThemePresetCombo.SelectedItem = selected;
                // Always available, so any look can be saved under a name.
                if (SavePresetPanel != null)
                    SavePresetPanel.Visibility = Visibility.Visible;
                if (DeleteAppearancePresetButton != null)
                {
                    DeleteAppearancePresetButton.IsEnabled = selected != null &&
                        string.Equals(selected.Tag as string, SavedPresetItemTag, StringComparison.Ordinal);
                }
            }
            catch { }
            finally { _syncingPreset = false; }
        }

        private void SaveAppearancePreset_Click(object sender, RoutedEventArgs e)
        {
            var s = Settings;
            string name = AppearancePresetNameBox?.Text?.Trim();
            if (s == null || string.IsNullOrWhiteSpace(name))
            {
                ShowPresetMessage("Enter a name for the preset first.", "Save appearance preset", MessageBoxImage.Information);
                AppearancePresetNameBox?.Focus();
                return;
            }
            if (IsReservedPresetName(name))
            {
                ShowPresetMessage("That name is reserved for a built-in preset. Choose another name.", "Save appearance preset", MessageBoxImage.Warning);
                return;
            }

            var presets = s.SavedAppearancePresets?.ToList() ?? new List<OverlayAppearancePreset>();
            var existing = presets.FirstOrDefault(p => p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var answer = ShowPresetConfirmation("Replace the saved preset ‘" + existing.Name + "’?", "Replace appearance preset");
                if (answer != MessageBoxResult.Yes) return;
                presets.Remove(existing);
            }
            presets.Add(OverlayAppearancePreset.FromSettings(name, s));
            s.SavedAppearancePresets = presets;
            _customPinned = false;
            AppearancePresetNameBox.Clear();
            SelectSavedPresetItem(name);
        }

        private void SelectSavedPresetItem(string name)
        {
            try
            {
                _syncingPreset = true;
                RefreshSavedPresetItems();
                foreach (var obj in ThemePresetCombo.Items)
                {
                    var item = obj as ComboBoxItem;
                    if (item != null && string.Equals(item.Tag as string, SavedPresetItemTag, StringComparison.Ordinal) &&
                        string.Equals(item.Content as string, name, StringComparison.OrdinalIgnoreCase))
                    {
                        ThemePresetCombo.SelectedItem = item;
                        if (DeleteAppearancePresetButton != null) DeleteAppearancePresetButton.IsEnabled = true;
                        break;
                    }
                }
            }
            catch { }
            finally { _syncingPreset = false; }
        }

        private void DeleteAppearancePreset_Click(object sender, RoutedEventArgs e)
        {
            var s = Settings;
            var selected = ThemePresetCombo?.SelectedItem as ComboBoxItem;
            var name = selected?.Content as string;
            if (s == null || selected == null ||
                !string.Equals(selected.Tag as string, SavedPresetItemTag, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(name)) return;

            if (ShowPresetConfirmation("Delete the saved preset ‘" + name + "’?", "Delete appearance preset") != MessageBoxResult.Yes)
                return;

            s.SavedAppearancePresets = (s.SavedAppearancePresets ?? new List<OverlayAppearancePreset>())
                .Where(p => p != null && !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            s.CaptureCustomAppearance();
            _customPinned = true;
            SyncPresetCombo();
        }

        private MessageBoxResult ShowPresetConfirmation(string message, string title)
        {
            var owner = Window.GetWindow(this);
            return owner == null
                ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        }

        private void ShowPresetMessage(string message, string title, MessageBoxImage icon)
        {
            var owner = Window.GetWindow(this);
            if (owner == null) MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            else MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        }

        /// <summary>Redraws the sample panel from the current Appearance settings.</summary>
        private BitmapImage _previewBackdropBitmap;
        private string _previewBackdropBitmapPath;

        /// <summary>Live preview of the fullscreen-game backdrop (blur, size, fit, dim).</summary>
        private void UpdateBackdropPreview(OverlaySettings s)
        {
            try
            {
                if (PreviewBackdropImage == null) return;
                string path = s.UseGameBackgroundBackdrop ? s.GetPreviewBackdropPath() : null;
                if (string.IsNullOrEmpty(path))
                {
                    PreviewBackdropImage.Visibility = Visibility.Collapsed;
                    PreviewBackdropBanner.Visibility = Visibility.Collapsed;
                    PreviewBackdropScrim.Visibility = Visibility.Collapsed;
                    return;
                }

                if (_previewBackdropBitmap == null || _previewBackdropBitmapPath != path)
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 960;
                    bi.EndInit();
                    bi.Freeze();
                    _previewBackdropBitmap = bi;
                    _previewBackdropBitmapPath = path;
                }

                // The preview is ~330 px wide vs a ~1920 px screen: scale the blur to match.
                double blur = Math.Max(0, Math.Min(100, s.BackdropBlur)) * (330.0 / 1920.0);
                double zoom = Math.Max(50, Math.Min(200, s.BackdropImageScale)) / 100.0;
                string mode = s.BackdropImageMode ?? "Auto";
                // Decide against a 16:9 screen, as the real overlay would on a normal monitor.
                bool banner = BackdropLayout.UseBanner(mode, _previewBackdropBitmap.PixelWidth, _previewBackdropBitmap.PixelHeight, 1920, 1080);

                PreviewBackdropImage.Source = _previewBackdropBitmap;
                PreviewBackdropImage.Stretch = BackdropLayout.FillStretch(mode, banner);
                PreviewBackdropImage.Effect = blur > 0.05
                    ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
                    : null;
                PreviewBackdropImage.Margin = new Thickness(-blur * 1.5);
                PreviewBackdropImage.RenderTransform = banner ? Transform.Identity : new ScaleTransform(zoom, zoom);
                PreviewBackdropImage.Visibility = Visibility.Visible;

                double dim = Math.Max(0, Math.Min(0.85, s.BackdropDim));
                double alpha = Math.Min(0.8, 0.45 + dim * 0.4);
                PreviewBackdropScrim.Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0));
                PreviewBackdropScrim.Visibility = Visibility.Visible;

                if (banner)
                {
                    bool fadeVertical = BackdropLayout.IsWiderThan(_previewBackdropBitmap.PixelWidth, _previewBackdropBitmap.PixelHeight, 1920, 1080);
                    PreviewBackdropBanner.Source = _previewBackdropBitmap;
                    PreviewBackdropBanner.OpacityMask = BackdropLayout.FadeMask(fadeVertical);
                    PreviewBackdropBanner.Opacity = Math.Max(0.35, 0.9 - dim * 0.5);
                    PreviewBackdropBanner.RenderTransform = new ScaleTransform(zoom, zoom);
                    PreviewBackdropBanner.Visibility = Visibility.Visible;
                }
                else
                {
                    PreviewBackdropBanner.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                try
                {
                    PreviewBackdropImage.Visibility = Visibility.Collapsed;
                    PreviewBackdropBanner.Visibility = Visibility.Collapsed;
                    PreviewBackdropScrim.Visibility = Visibility.Collapsed;
                }
                catch { }
            }
        }

        private void UpdatePreview()
        {
            try
            {
                var s = Settings;
                if (s == null || PreviewPanel == null) return;
                var t = OverlayTheme.Compute(s, _previewCoverImagePath);
                var accent = new System.Windows.Media.SolidColorBrush(t.Accent);
                var panelBrush = OverlayTheme.CreatePanelBrush(t);
                var radius = new CornerRadius(t.PanelRadius);
                var overlayControlRadius = new CornerRadius(6);

                // Shared panel chrome
                var borderBrush = OverlayTheme.CreatePanelBorderBrush(t);
                var borderThickness = new Thickness(t.PanelBorderThickness);
                PreviewPanel.Background = panelBrush;
                PreviewPanel.CornerRadius = radius;
                PreviewPanel.BorderBrush = borderBrush;
                PreviewPanel.BorderThickness = borderThickness;
                if (PreviewFpsPanel != null)
                {
                    PreviewFpsPanel.Background = panelBrush;
                    PreviewFpsPanel.CornerRadius = radius;
                    PreviewFpsPanel.BorderBrush = borderBrush;
                    PreviewFpsPanel.BorderThickness = borderThickness;
                }
                UpdateBackdropPreview(s);
                // With the artwork shown, the scrim above already darkens it; keep the dim preview for the plain gradient.
                PreviewDim.Opacity = (PreviewBackdropImage != null && PreviewBackdropImage.Visibility == Visibility.Visible) ? 0 : t.Dim;
                // Inner surfaces reuse the exact theme brushes used by the live overlay panels.
                Resources["PreviewSurfaceBrush"] = OverlayTheme.CreateSurfaceBrush(t, t.Surface);
                PreviewListItem.Background = OverlayTheme.CreateSelectedBrush(t);

                if (PreviewFpsPresetFocused != null)
                {
                    PreviewFpsPresetFocused.CornerRadius = overlayControlRadius;
                    PreviewFpsPresetFocused.Background = OverlayTheme.CreateSelectedBrush(t);
                    PreviewFpsPresetFocused.BorderBrush = OverlayTheme.CreateFocusRimBrush(t);
                    PreviewFpsPresetFocused.BorderThickness = new Thickness(t.Glass ? 1 : 2);
                    if (PreviewFpsPresetText != null)
                        PreviewFpsPresetText.Foreground = new System.Windows.Media.SolidColorBrush(t.SelectedForeground);
                }
                if (PreviewFpsDisable != null)
                {
                    PreviewFpsDisable.CornerRadius = overlayControlRadius;
                    PreviewFpsDisable.Background = new System.Windows.Media.SolidColorBrush(
                        t.Glass ? t.Surface : System.Windows.Media.Color.FromRgb(0x2A, 0x22, 0x22));
                    PreviewFpsDisable.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
                    PreviewFpsDisable.BorderThickness = new Thickness(1);
                    if (PreviewFpsDisableText != null)
                        PreviewFpsDisableText.Foreground = new System.Windows.Media.SolidColorBrush(t.Glass
                            ? System.Windows.Media.Colors.White
                            : System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x80));
                }

                // Which mock panels are visible
                ApplyPreviewPanelVisibility();

                // The settings sliders below follow the chosen accent too.
                Resources["AccentBrush"] = accent;
                Resources["PanelBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(t.Panel);
            }
            catch { }
        }

        private void PreviewPanelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPreviewPanelVisibility();
        }

        /// <summary>Shows only the panel(s) selected in Preview panel combo.</summary>
        private void ApplyPreviewPanelVisibility()
        {
            try
            {
                if (PreviewPanel == null) return;

                string choice = "All";
                var item = PreviewPanelCombo?.SelectedItem as ComboBoxItem;
                if (item?.Content is string name && !string.IsNullOrEmpty(name))
                    choice = name;

                bool all = string.Equals(choice, "All", StringComparison.OrdinalIgnoreCase);
                bool volume = all || string.Equals(choice, "Volume", StringComparison.OrdinalIgnoreCase);
                bool fps = all || string.Equals(choice, "FPS Limiter", StringComparison.OrdinalIgnoreCase);

                PreviewPanel.Visibility = volume ? Visibility.Visible : Visibility.Collapsed;
                if (PreviewFpsPanel != null)
                    PreviewFpsPanel.Visibility = fps ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void ThemePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPreset) return;
            var s = Settings;
            var item = ThemePresetCombo.SelectedItem as ComboBoxItem;
            if (s == null || item == null) return;

            string name = item.Content as string;
            if (string.IsNullOrEmpty(name)) return;

            // Leaving Custom → remember current colors so we can restore them later
            string current = DetectPreset();
            _customPinned = string.Equals(name, "Custom", StringComparison.Ordinal);
            if (string.Equals(current, "Custom", StringComparison.Ordinal) &&
                !string.Equals(name, "Custom", StringComparison.Ordinal))
            {
                s.CaptureCustomAppearance();
            }

            _applyingPreset = true;
            try
            {
                if (string.Equals(name, "Custom", StringComparison.Ordinal))
                {
                    s.RestoreCustomAppearance();
                }
                else if (s.SavedAppearancePresets?.FirstOrDefault(p => p != null &&
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is OverlayAppearancePreset savedPreset)
                {
                    savedPreset.ApplyTo(s);
                }
                else if (string.Equals(name, "Windows 11", StringComparison.Ordinal))
                {
                    // Dynamic Windows 11: live system accent + Win11 panel chrome
                    s.PanelGlassBorder = true;
                    s.UseWindowsAccent = true;
                    s.UseCoverAccent = false;
                    s.AccentColor = OverlayTheme.WindowsAccentHex();
                    s.PanelBackgroundColor = "#202020";
                    s.PanelOpacity = 1.0;
                    s.PanelCornerRadius = 8;
                    s.PanelGlassStrength = 0.5;
                }
                else if (string.Equals(name, "Dynamic (cover accent)", StringComparison.Ordinal))
                {
                    s.PanelGlassBorder = true;
                    s.UseWindowsAccent = false;
                    s.UseCoverAccent = true;
                    s.AccentColor = "#66C0F4"; // fallback when the current game has no local cover
                    s.PanelBackgroundColor = "#1A1D23";
                    s.PanelOpacity = 1.0;
                    s.PanelCornerRadius = 10;
                    s.PanelGlassStrength = 0.5;
                }
                else if (string.Equals(name, "Steam Dark", StringComparison.Ordinal))
                {
                    s.PanelGlassBorder = true;
                    s.UseWindowsAccent = false;
                    s.UseCoverAccent = false;
                    s.AccentColor = "#66C0F4";
                    s.PanelBackgroundColor = "#16181D";
                    s.PanelOpacity = 1.0;
                    s.PanelCornerRadius = 10;
                    s.PanelGlassStrength = 0.5;
                }
                else // Steam (default)
                {
                    s.PanelGlassBorder = true;
                    s.UseWindowsAccent = false;
                    s.UseCoverAccent = false;
                    s.AccentColor = "#66C0F4";
                    s.PanelBackgroundColor = "#1A1D23";
                    s.PanelOpacity = 1.0;
                    s.PanelCornerRadius = 10;
                    s.PanelGlassStrength = 0.5;
                }
            }
            finally { _applyingPreset = false; }
            SyncPresetCombo();
            UpdatePreview();
        }

        private void AppearanceReset_Click(object sender, RoutedEventArgs e)
        {
            var s = Settings;
            if (s == null) return;
            s.UseWindowsAccent = false;
            s.UseCoverAccent = false;
            s.AccentColor = "#66C0F4";
            s.PanelBackgroundColor = "#1A1D23";
            s.PanelOpacity = 1.0;
            s.PanelCornerRadius = 10;
            s.PanelGlassBorder = true;   // frosted panels are the default
            s.PanelGlassStrength = 0.5;
            s.BackdropDim = 0;
            s.CaptureCustomAppearance();
            _customPinned = false;
            SyncPresetCombo();
        }

        private void RefreshHotkeyButtonLabels()
        {
            var s = Settings;
            if (s == null) return;
            if (BtnHotkeyOverlay != null)
                BtnHotkeyOverlay.Content = FormatHotkeyDisplay(s.HotkeyOverlayToggle);
            if (BtnHotkeyForceClose != null)
                BtnHotkeyForceClose.Content = FormatHotkeyDisplay(s.HotkeyForceClose);
            if (BtnHotkeySuspend != null)
                BtnHotkeySuspend.Content = FormatHotkeyDisplay(s.HotkeySuspend);
            if (BtnHotkeyVolUp != null)
                BtnHotkeyVolUp.Content = FormatHotkeyDisplay(s.HotkeyVolumeUp);
            if (BtnHotkeyVolDown != null)
                BtnHotkeyVolDown.Content = FormatHotkeyDisplay(s.HotkeyVolumeDown);
            if (BtnHotkeyVolMute != null)
                BtnHotkeyVolMute.Content = FormatHotkeyDisplay(s.HotkeyVolumeMute);
            if (BtnHotkeyGameSettings != null)
                BtnHotkeyGameSettings.Content = FormatHotkeyDisplay(s.HotkeyGameSettings);
            if (BtnHotkeyGlobalSettings != null)
                BtnHotkeyGlobalSettings.Content = FormatHotkeyDisplay(s.HotkeyGlobalSettings);
            if (BtnHotkeyScanLocal != null)
                BtnHotkeyScanLocal.Content = FormatHotkeyDisplay(s.HotkeyScanLocalGames);
                if (BtnPrintScreenRemap != null) BtnPrintScreenRemap.Content = HotkeysLayout.FormatSendKeys(s.PrintScreenRemapCombo); // hotkeys-layout
            RefreshPadHotkeyLabels();
        }

        private void LocalStoreBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Choose the folder that contains your local games";
                    dlg.ShowNewFolderButton = false;
                    var current = Settings?.LocalGamesFolder;
                    if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
                        dlg.SelectedPath = current;
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && Settings != null)
                        Settings.LocalGamesFolder = dlg.SelectedPath;
                }
            }
            catch { }
        }

        private void LocalStoreScanNow_Click(object sender, RoutedEventArgs e)
        {
            try { LocalGamesStore.Instance?.RequestScanManual("settings button"); } catch { }
        }

        // ---- Controller hotkey recording ---------------------------------------------------

        private System.Windows.Threading.DispatcherTimer _padCaptureTimer;
        private Button _padCaptureButton;
        private string _padCaptureSlot;
        private int _padCapturePeak;
        private DateTime _padCaptureStart;

        private static string PadLabel(string combo) =>
            string.IsNullOrWhiteSpace(combo) ? "Not set" : HotkeysLayout.NormalizePad(combo).Replace("+", " + "); // hotkeys-layout

        private string GetPadCombo(string slot)
        {
            var s = Settings;
            if (s == null) return "";
            switch (slot)
            {
                case "PadOverlay": return s.ControllerHotkeyOverlay;
                case "PadScreenshot": return s.ControllerHotkeyScreenshot;
                case "PadSuspend": return s.ControllerHotkeySuspend;
                case "PadMute": return s.ControllerHotkeyMute;
                case "PadVolDown": return s.ControllerHotkeyVolumeDown; // hotkeys-layout
                case "PadVolUp": return s.ControllerHotkeyVolumeUp; // hotkeys-layout
            }
            return "";
        }

        private void SetPadCombo(string slot, string combo)
        {
            var s = Settings;
            if (s == null) return;
            switch (slot)
            {
                case "PadOverlay": s.ControllerHotkeyOverlay = combo; break;
                case "PadScreenshot": s.ControllerHotkeyScreenshot = combo; break;
                case "PadSuspend": s.ControllerHotkeySuspend = combo; break;
                case "PadMute": s.ControllerHotkeyMute = combo; break;
                case "PadVolDown": s.ControllerHotkeyVolumeDown = combo; break; // hotkeys-layout
                case "PadVolUp": s.ControllerHotkeyVolumeUp = combo; break; // hotkeys-layout
            }
        }

        private void RefreshPadHotkeyLabels()
        {
            if (_padCaptureTimer != null) return; // a recording is in progress, it owns the label
            // hotkeys-layout: Controller: Overlay row removed from this page
            if (BtnPadScreenshot != null) BtnPadScreenshot.Content = PadLabel(GetPadCombo("PadScreenshot"));
            if (BtnPadSuspend != null) BtnPadSuspend.Content = PadLabel(GetPadCombo("PadSuspend"));
            if (BtnPadMute != null) BtnPadMute.Content = PadLabel(GetPadCombo("PadMute"));
            if (BtnPadVolDown != null) BtnPadVolDown.Content = PadLabel(GetPadCombo("PadVolDown")); // hotkeys-layout
            if (BtnPadVolUp != null) BtnPadVolUp.Content = PadLabel(GetPadCombo("PadVolUp")); // hotkeys-layout
        }

        private void StopPadCapture(bool commit)
        {
            var timer = _padCaptureTimer;
            _padCaptureTimer = null;
            if (timer != null) timer.Stop();
            ControllerHotkeys.CaptureActive = false;

            if (commit && _padCapturePeak != 0 && !string.IsNullOrEmpty(_padCaptureSlot))
                SetPadCombo(_padCaptureSlot, ControllerHotkeys.Format(_padCapturePeak));

            _padCaptureButton = null;
            _padCaptureSlot = null;
            _padCapturePeak = 0;
            RefreshPadHotkeyLabels();
        }

        private void PadCapture_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn)) return;
            CancelCapture(restoreLabel: true);   // stop any keyboard recording
            StopPadCapture(commit: false);       // and any previous controller recording

            _padCaptureSlot = btn.Tag as string;
            _padCaptureButton = btn;
            _padCapturePeak = 0;
            _padCaptureStart = DateTime.UtcNow;
            ControllerHotkeys.CaptureActive = true;
            btn.Content = "Hold the buttons, then let go…";

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _padCaptureTimer = timer;
            timer.Tick += (_, __) =>
            {
                if (!ReferenceEquals(_padCaptureTimer, timer)) { timer.Stop(); return; }
                int mask = ControllerHotkeys.CurrentMask;
                if (mask != 0)
                {
                    _padCapturePeak |= mask;
                    if (_padCaptureButton != null)
                        _padCaptureButton.Content = ControllerHotkeys.Format(_padCapturePeak).Replace("+", " + ") + " …";
                }
                else if (_padCapturePeak != 0)
                {
                    StopPadCapture(commit: true);   // everything released: take the recorded combo
                }
                else if ((DateTime.UtcNow - _padCaptureStart).TotalSeconds > 10)
                {
                    StopPadCapture(commit: false);  // nothing pressed: cancel
                }
            };
            timer.Start();
            e.Handled = true;
        }

        private void PadClear_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn)) return;
            StopPadCapture(commit: false);
            SetPadCombo(btn.Tag as string, "");
            RefreshPadHotkeyLabels();
        }

        private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn))
                return;

            // Cancel previous capture
            CancelCapture(restoreLabel: true);

            _capturingSlot = btn.Tag as string;
            _capturingButton = btn;
            btn.Content = "Press keys… (Esc to cancel)";
            if (_capturingSlot == "PrintScreenRemap") HotkeysLayout.WatchFunctionKeys(() => _capturingSlot == "PrintScreenRemap", () => Settings, () => CancelCapture(restoreLabel: true)); // hotkeys-layout
            btn.Focus();
            e.Handled = true;
        }

        private void ResetPanelLayout_Click(object sender, RoutedEventArgs e)
        {
            try { Settings?.ResetPanelLayout(); } catch { }
        }

        private void HotkeyReset_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn))
                return;
            var slot = btn.Tag as string;
            var s = Settings;
            if (s == null || string.IsNullOrEmpty(slot))
                return;

            CancelCapture(restoreLabel: false);
            if (slot == "PrintScreenRemap") s.PrintScreenRemapCombo = "^+{F12}"; // hotkeys-layout
            switch (slot)
            {
                case "Overlay":
                    s.HotkeyOverlayToggle = "Alt+Oem3";
                    break;
                case "ForceClose":
                    s.HotkeyForceClose = "Alt+F4";
                    break;
                case "Suspend":
                    s.HotkeySuspend = "Pause";
                    break;
                case "VolUp":
                    s.HotkeyVolumeUp = "";
                    break;
                case "VolDown":
                    s.HotkeyVolumeDown = "";
                    break;
                case "VolMute":
                    s.HotkeyVolumeMute = "";
                    break;
                case "GameSettings":
                    s.HotkeyGameSettings = "";
                    break;
                case "GlobalSettings":
                    s.HotkeyGlobalSettings = "";
                    break;
                case "ScanLocal":
                    s.HotkeyScanLocalGames = "";
                    break;
            }
            RefreshHotkeyButtonLabels();
        }

        private void CancelCapture(bool restoreLabel)
        {
            _capturingSlot = null;
            if (restoreLabel)
                RefreshHotkeyButtonLabels();
            _capturingButton = null;
        }

        private void OverlaySettingsView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (string.IsNullOrEmpty(_capturingSlot) || _capturingButton == null)
                return;

            // Ignore pure modifiers
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.System)
            {
                e.Handled = true;
                return;
            }

            if (key == Key.Escape)
            {
                CancelCapture(restoreLabel: true);
                e.Handled = true;
                return;
            }

            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            string stored = BuildHotkeyString(key, alt, shift, ctrl);
            if (_capturingSlot == "PrintScreenRemap" && Settings != null) { var sk = HotkeysLayout.ToSendKeys(key, alt, shift, ctrl); if (sk != null) Settings.PrintScreenRemapCombo = sk; } // hotkeys-layout
            var s = Settings;
            if (s != null)
            {
                switch (_capturingSlot)
                {
                    case "Overlay":
                        s.HotkeyOverlayToggle = stored;
                        break;
                    case "ForceClose":
                        s.HotkeyForceClose = stored;
                        break;
                    case "Suspend":
                        s.HotkeySuspend = stored;
                        break;
                    case "VolUp":
                        s.HotkeyVolumeUp = stored;
                        break;
                    case "VolDown":
                        s.HotkeyVolumeDown = stored;
                        break;
                    case "VolMute":
                        s.HotkeyVolumeMute = stored;
                        break;
                    case "GameSettings":
                        s.HotkeyGameSettings = stored;
                        break;
                    case "GlobalSettings":
                        s.HotkeyGlobalSettings = stored;
                        break;
                    case "ScanLocal":
                        s.HotkeyScanLocalGames = stored;
                        break;
                }
            }

            if (_capturingButton != null)
                _capturingButton.Content = FormatHotkeyDisplay(stored);
                if (_capturingButton != null && _capturingSlot == "PrintScreenRemap" && Settings != null) _capturingButton.Content = HotkeysLayout.FormatSendKeys(Settings.PrintScreenRemapCombo); // hotkeys-layout

            _capturingSlot = null;
            _capturingButton = null;
            e.Handled = true;
        }

        /// <summary>Storage format compatible with HotkeyMatches (Forms Keys names).</summary>
        internal static string BuildHotkeyString(Key key, bool alt, bool shift, bool ctrl)
        {
            var forms = WpfKeyToFormsKey(key);
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(forms);
            return string.Join("+", parts);
        }

        internal static string FormatHotkeyDisplay(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return "(not set)";
            return stored
                .Replace("Oem3", "`")
                .Replace("Oemtilde", "`")
                .Replace("+", " + ");
        }

        /// <summary>Map WPF Key to System.Windows.Forms.Keys name string.</summary>
        internal static string WpfKeyToFormsKey(Key key)
        {
            // Common special cases
            switch (key)
            {
                case Key.OemTilde: // same value as Oem3 (146)
                    return "Oem3";
                case Key.Back: return "Back";
                case Key.Return: return "Enter";
                case Key.Escape: return "Escape";
                case Key.Space: return "Space";
                case Key.Prior: return "PageUp";
                case Key.Next: return "PageDown";
                case Key.Snapshot: return "PrintScreen";
                case Key.OemMinus: return "OemMinus";
                case Key.OemPlus: return "OemPlus";
                case Key.OemComma: return "Oemcomma";
                case Key.OemPeriod: return "OemPeriod";
                case Key.OemQuestion: return "OemQuestion";
                case Key.OemQuotes: return "OemQuotes";
                case Key.OemOpenBrackets: return "OemOpenBrackets";
                case Key.OemCloseBrackets: return "OemCloseBrackets";
                case Key.OemPipe: return "OemPipe";
                case Key.OemSemicolon: return "OemSemicolon";
                case Key.D0: case Key.D1: case Key.D2: case Key.D3: case Key.D4:
                case Key.D5: case Key.D6: case Key.D7: case Key.D8: case Key.D9:
                    return key.ToString(); // D0-D9 match Forms
                case Key.NumPad0: case Key.NumPad1: case Key.NumPad2: case Key.NumPad3:
                case Key.NumPad4: case Key.NumPad5: case Key.NumPad6: case Key.NumPad7:
                case Key.NumPad8: case Key.NumPad9:
                    return key.ToString();
                case Key.Multiply: return "Multiply";
                case Key.Add: return "Add";
                case Key.Subtract: return "Subtract";
                case Key.Divide: return "Divide";
                case Key.Decimal: return "Decimal";
            }

            // F1-F24, A-Z — same names as Forms
            if (key >= Key.F1 && key <= Key.F24)
                return key.ToString();
            if (key >= Key.A && key <= Key.Z)
                return key.ToString();

            return key.ToString();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void Slider_Loaded(object sender, RoutedEventArgs e)
        {
            var slider = sender as System.Windows.Controls.Slider;
            if (slider != null) SmoothSlider.Attach(slider);
        }

        private void HookSmoothSliders()
        {
            try
            {
                foreach (var s in FindVisualChildren<System.Windows.Controls.Slider>(this))
                    SmoothSlider.Attach(s);
            }
            catch { }
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            if (parent == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T matched) yield return matched;
                foreach (var g in FindVisualChildren<T>(child))
                    yield return g;
            }
        }

    }

    /// <summary>Names the Controller shortcut options after the pad in use (Xbox or PlayStation).</summary>
    public class ShortcutLabelConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length == 0 || !(values[0] is ControllerShortcut))
                return "";
            var shortcut = (ControllerShortcut)values[0];
            var style = values.Length > 1 && values[1] is ButtonPromptStyle ? (ButtonPromptStyle)values[1] : ButtonPromptStyle.Auto;
            if (PadPrompts.IsSony(style))
            {
                if (shortcut == ControllerShortcut.StartBack) return "Share + Options (Back + Start)";
                if (shortcut == ControllerShortcut.Guide) return "PS Button (Guide Button)";
            }
            else
            {
                if (shortcut == ControllerShortcut.StartBack) return "View + Menu (Back + Start)";
                if (shortcut == ControllerShortcut.Guide) return "Xbox Button (Guide Button)";
            }
            return "None (Disabled)";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class EnumDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Enum enumValue)
            {
                return GetEnumDescription(enumValue);
            }
            if (value != null)
            {
                return value.ToString();
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Enum.Parse(targetType, value.ToString());
        }

        private string GetEnumDescription(Enum value)
        {
            FieldInfo field = value.GetType().GetField(value.ToString());
            DescriptionAttribute attribute = field?.GetCustomAttribute<DescriptionAttribute>();

            return attribute?.Description ?? value.ToString();
        }

    }
}
