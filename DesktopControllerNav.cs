using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Controller navigation for Playnite Desktop.
    ///
    /// Normal mode:
    ///   Up/Down  - move through games
    ///   Left     - previous cell in Grid view (no longer touches groups)
    ///   Right    - next cell in Grid view / into the details panel (never expands a group)
    ///   Square   - collapse the selected game's group (enters header mode); again expands it
    ///   A        - launch/install selected game
    ///
    /// Group-header mode:
    ///   Up/Down  - move between visible group headers
    ///   Square   - expand current group and enter its first game (leaves header mode)
    ///   Right    - nothing (use Square to expand)
    ///   A        - toggle current group without leaving header mode
    ///   B        - optional escape from header mode
    ///
    /// Playnite's grouped library is made of WPF GroupItem containers with an Expander,
    /// not TreeViewItem controls. This targets those actual group containers.
    /// </summary>
    /// <summary>
    /// Which device is in use. The controller's selection outlines are for the controller only: as soon as the mouse
    /// or keyboard is used they disappear, and they come back with the next controller press.
    /// </summary>
    internal static class PadInput
    {
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out PadPoint p);
        [StructLayout(LayoutKind.Sequential)] private struct PadPoint { public int X, Y; }

        private static volatile bool _active;
        private static long _ignoreUntilTicks;
        private static bool _hooked;
        private static int _lastX = int.MinValue, _lastY = int.MinValue;

        /// <summary>True while the controller was the last thing used.</summary>
        public static bool Active { get { return _active; } }

        /// <summary>Called by every controller action.</summary>
        public static void NotePad()
        {
            EnsureHook();
            _active = true;
        }

        /// <summary>The extension itself moves the cursor / sends keys for a moment: that must not count as mouse use.</summary>
        public static void Suppress(int milliseconds)
        {
            _ignoreUntilTicks = DateTime.UtcNow.AddMilliseconds(milliseconds).Ticks;
        }

        private static void EnsureHook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                var app = Application.Current;
                if (app == null) { _hooked = false; return; }
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { InputManager.Current.PreProcessInput += OnInput; } catch { }
                }));
            }
            catch { _hooked = false; }
        }

        private static void OnInput(object sender, PreProcessInputEventArgs e)
        {
            try
            {
                if (!_active) return;
                if (DateTime.UtcNow.Ticks < _ignoreUntilTicks) return;
                var input = e.StagingItem != null ? e.StagingItem.Input : null;
                if (input == null) return;

                bool realUse = false;
                if (input is KeyEventArgs || input is TextCompositionEventArgs ||
                    input is MouseButtonEventArgs || input is MouseWheelEventArgs)
                    realUse = true;
                else if (input is MouseEventArgs)
                {
                    // WPF also raises mouse moves when content scrolls under a resting cursor: only a real
                    // cursor movement counts.
                    PadPoint p;
                    if (GetCursorPos(out p))
                    {
                        // The first event only records where the cursor rests.
                        bool moved = _lastX != int.MinValue &&
                            (Math.Abs(p.X - _lastX) >= 3 || Math.Abs(p.Y - _lastY) >= 3);
                        _lastX = p.X; _lastY = p.Y;
                        if (moved) realUse = true;
                    }
                }
                if (!realUse) return;

                _active = false;
                try { Breadcrumb.Write("PadInput: mouse/keyboard in use, controller outlines hidden"); } catch { }
                DesktopControllerNav.HideForKeyboardMouse();
                SettingsPadNav.HideForKeyboardMouse();
            }
            catch { }
        }
    }

    public static class DesktopControllerNav
    {
        private static IPlayniteAPI _api;
        private static bool _groupHeaderMode;
        private static GroupItem _currentGroup;

        // Strong visual marker for controller-selected group headers.
        // We remember the header's local values so the active Playnite theme is restored exactly.
        private static ToggleButton _highlightedHeader;
        private static object _oldBackground = DependencyProperty.UnsetValue;
        private static object _oldForeground = DependencyProperty.UnsetValue;
        private static object _oldBorderBrush = DependencyProperty.UnsetValue;
        private static object _oldBorderThickness = DependencyProperty.UnsetValue;

        public static void Initialize(IPlayniteAPI api)
        {
            _api = api;
            _groupHeaderMode = false;
            _currentGroup = null;
            _highlightedHeader = null;
        }

        /// <summary>The mouse / keyboard took over: remove every controller outline and forget the controller's position.</summary>
        internal static void HideForKeyboardMouse()
        {
            try { ExitTopBarMode(); } catch { }
            try { ExitDetailsMode(); } catch { }
            try { ExitGroupHeaderMode(); } catch { }
            try { ClearHeaderHighlight(); } catch { }
        }

        private static int _selfPid;
        private static string _selfName;

        public static bool IsPlayniteDesktopForeground()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;

                GetWindowThreadProcessId(hwnd, out uint fgPid);
                if (fgPid == 0) return false;

                if (_selfName == null)
                {
                    // Process objects hold OS handles: look this process up once, and dispose it.
                    using (var self = Process.GetCurrentProcess())
                    {
                        _selfPid = self.Id;
                        _selfName = (self.ProcessName ?? "").ToLowerInvariant();
                    }
                }

                if ((int)fgPid == _selfPid)
                {
                    if (_selfName.IndexOf("fullscreen", StringComparison.Ordinal) >= 0)
                        return false;
                    if (_selfName.IndexOf("playnite", StringComparison.Ordinal) >= 0)
                        return true;
                }

                using (var p = Process.GetProcessById((int)fgPid))
                {
                    string name = (p.ProcessName ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(name)) return false;
                    if (name.IndexOf("fullscreen", StringComparison.Ordinal) >= 0)
                        return false;
                    return name.IndexOf("playnite", StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>One trace line per library pad action: how long it took and what state it left (selection count, mode).</summary>
        private static void Traced(string name, Action core)
        {
            if (!Breadcrumb.Enabled) { core(); return; }
            long t0 = Stopwatch.GetTimestamp();
            try { core(); }
            finally
            {
                try
                {
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    Breadcrumb.Write("Nav " + name + " " + ms.ToString("0.0") + "ms" + (ms > 40 ? " SLOW" : "") + " " + LibraryState());
                }
                catch { }
            }
        }

        private static string LibraryState()
        {
            string sel = "?", name = "-";
            try
            {
                var games = _api?.MainView?.SelectedGames;
                int n = games != null ? games.Count() : 0;
                sel = n.ToString();
                var first = games != null ? games.FirstOrDefault() : null;
                if (first != null) name = first.Name;
                int rows = _navList != null ? _navList.SelectedItems.Count : -1;
                if (rows != n) sel += "/rows" + rows;      // model and list disagree
            }
            catch { }
            bool grid = false; try { grid = IsGridView(); } catch { }
            return "sel=" + sel + " game=\"" + name + "\" grid=" + grid + " headerMode=" + _groupHeaderMode + " topBar=" + _topBarMode;
        }

        public static void Up() { Traced("Up", UpCore); }

        private static void UpCore()
        {
            PadInput.NotePad();
            if (MenuMove(-1)) return;
            if (DetailsNav(0, -1)) return;
            ExitTopBarMode();
            if (_groupHeaderMode && MoveFromCollapsedHeader(-1))
                return;
            if (_groupHeaderMode && TryMoveGroupHeader(-1))
                return;

            if (IsGridView())
            {
                var r = MoveGrid(0, -1);
                if (r != GridMove.Unavailable) return;
            }
            MoveSelection(-1, vertical: true);
        }

        public static void Down() { Traced("Down", DownCore); }

        private static void DownCore()
        {
            PadInput.NotePad();
            if (MenuMove(1)) return;
            if (DetailsNav(0, 1)) return;
            ExitTopBarMode();
            if (_groupHeaderMode && MoveFromCollapsedHeader(1))
                return;
            if (_groupHeaderMode && TryMoveGroupHeader(1))
                return;

            if (IsGridView())
            {
                var r = MoveGrid(0, 1);
                if (r != GridMove.Unavailable) return;
            }
            MoveSelection(1, vertical: true);
        }

        public static void Left() { Traced("Left", LeftCore); }

        private static void LeftCore()
        {
            PadInput.NotePad();
            if (MenuLeft()) return;
            if (DetailsNav(-1, 0)) return;
            if (_topBarMode && TopBarStep(-1)) return;
            try
            {
                // Collapsing / expanding a group moved to the Square button (ToggleGroup); Left no longer does it.
                if (_groupHeaderMode)
                    return;

                // Grid view: Left moves to the previous cell in the same row.
                GridMove gridResult = GridMove.Unavailable;
                if (IsGridView())
                {
                    gridResult = MoveGrid(-1, 0);
                    if (gridResult == GridMove.Moved)
                        return;
                }

                // Do NOT turn Left into another previous-game button in list/details view.
                // Index-based stepping is only a fallback when the grid layout could not be read.
                if (IsGridView() && gridResult == GridMove.Unavailable)
                    MoveSelection(-1, vertical: false);
            }
            catch
            {
                ExitGroupHeaderMode();
            }
        }

        public static void Right() { Traced("Right", RightCore); }

        private static void RightCore()
        {
            PadInput.NotePad();
            if (MenuRight()) return;
            if (DetailsNav(1, 0)) return;
            if (_topBarMode && TopBarStep(1)) return;
            try
            {
                // Expanding a collapsed group is Square's job (ToggleGroup): Right no longer expands anything.
                if (_groupHeaderMode)
                    return;

                if (IsGridView())
                {
                    var gr = MoveGrid(1, 0);
                    if (gr == GridMove.Unavailable)
                        MoveSelection(1, vertical: false);
                    else if (gr == GridMove.Edge)
                        EnterDetails();          // right edge of the grid: continue into the details panel
                }
                else
                {
                    EnterDetails();              // list view: Right goes into the details panel
                }
            }
            catch
            {
                ExitGroupHeaderMode();
            }
        }

        /// <summary>
        /// Square: collapse the selected game's group (and park on its header), or - when that group is already
        /// collapsed - expand it and step into its first game.
        /// </summary>
        public static void ToggleGroup() { Traced("ToggleGroup", ToggleGroupCore); }

        private static void ToggleGroupCore()
        {
            PadInput.NotePad();
            if (ResolveOpenMenu() != null) return;
            if (_detailsMode || _topBarMode) return;
            try
            {
                GroupItem group = _groupHeaderMode ? ResolveCurrentGroup() : null;
                if (group == null)
                {
                    var selected = _api?.MainView?.SelectedGames?.FirstOrDefault();
                    if (selected == null) return;
                    group = FindGroupContainingGame(selected.Id);
                    if (group == null) return;      // ungrouped library: nothing to collapse
                }

                var expander = GetGroupExpander(group);
                if (expander == null) return;

                if (expander.IsExpanded)
                {
                    _groupHeaderMode = true;
                    _currentGroup = group;
                    SetGroupExpanded(group, false);
                    FocusGroupHeader(group);
                    return;
                }

                expander.IsExpanded = true;
                try { group.UpdateLayout(); } catch { }
                Guid firstId;
                if (TryGetFirstGameId(group.DataContext, out firstId))
                {
                    _api?.MainView?.SelectGame(firstId);
                    ExitGroupHeaderMode();
                }
                else
                {
                    _groupHeaderMode = true;
                    _currentGroup = group;
                    FocusGroupHeader(group);
                }
            }
            catch
            {
                ExitGroupHeaderMode();
            }
        }

        public static void Confirm() { Traced("Confirm", ConfirmCore); }

        private static void ConfirmCore()
        {
            PadInput.NotePad();
            if (MenuConfirm()) return;
            if (DetailsConfirm()) return;
            if (TopBarConfirm()) return;
            try
            {
                if (_groupHeaderMode)
                {
                    var group = ResolveCurrentGroup();
                    var expander = GetGroupExpander(group);
                    if (expander != null)
                    {
                        expander.IsExpanded = !expander.IsExpanded;
                        group.UpdateLayout();
                        FocusGroupHeader(group);
                    }
                    return;
                }

                if (_api?.MainView == null) return;
                var game = _api.MainView.SelectedGames?.FirstOrDefault();
                if (game == null) return;
                // StartGame / InstallGame can open a modal dialog (e.g. Playnite's "Select action" picker when a game has
                // several play actions) and do not return until it is closed. Called straight from the pad poll tick that
                // blocked the poller for the whole time the dialog was open, so the pad was dead inside it
                // (breadcrumbs: "Nav Confirm 156776 ms SLOW" + "PadPoll: slow tick"). Run it after the tick has returned.
                var gameId = game.Id;
                bool installed = game.IsInstalled;
                var api = _api;
                Defer(() =>
                {
                    if (installed) api.StartGame(gameId);
                    else api.InstallGame(gameId);
                });
            }
            catch { }
        }

        /// <summary>
        /// B: closes the context menu (or its submenu) when one is open, otherwise opens the
        /// context menu of the selected game. In group-header mode B still leaves header mode.
        /// </summary>
        public static void Back() { Traced("Back", BackCore); }

        private static void BackCore()
        {
            PadInput.NotePad();
            try
            {
                if (MenuBack()) return;
                if (_detailsMode) { ExitDetailsMode(); return; }
                if (_topBarMode) { ExitTopBarMode(); return; }

                if (_groupHeaderMode)
                {
                    ExitGroupHeaderMode();
                    return;
                }

                OpenContextMenu();
            }
            catch { }
        }

        // ---- Top bar (LB / RB step through the icons, A presses, B / Up / Down leave) ----------------

        private static bool _topBarMode;
        private static ButtonBase _topBarButton;
        private static AdornerLayer _topBarLayer;
        private static Adorner _topBarAdorner;

        private sealed class TopBarHighlight : Adorner
        {
            public TopBarHighlight(UIElement el) : base(el) { IsHitTestVisible = false; }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                var r = new Rect(-2, -2, AdornedElement.RenderSize.Width + 4, AdornedElement.RenderSize.Height + 4);
                var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
                dc.DrawRoundedRectangle(fill, null, r, 4, 4);
            }
        }

        private static List<ButtonBase> CollectTopBarButtons(Window window)
        {
            var list = new List<ButtonBase>();
            double width = window.ActualWidth;
            foreach (var b in FindVisualChildren<ButtonBase>(window))
            {
                if (!IsUsable(b) || !b.IsEnabled) continue;
                var pos = GetPosition(b, window);
                if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)) continue;
                if (pos.Y + b.ActualHeight / 2 > 60) continue;           // only the top bar
                if (pos.X + b.ActualWidth / 2 > width - 110) continue;   // minimize / maximize / close
                list.Add(b);
            }
            return list.OrderBy(b => GetPosition(b, window).X).ToList();
        }

        private static void SetTopBarHighlight(ButtonBase button)
        {
            ClearTopBarHighlight();
            if (button == null || !PadInput.Active) return;
            try
            {
                var layer = AdornerLayer.GetAdornerLayer(button);
                if (layer == null) return;
                _topBarAdorner = new TopBarHighlight(button);
                layer.Add(_topBarAdorner);
                _topBarLayer = layer;
            }
            catch { }
        }

        private static void ClearTopBarHighlight()
        {
            try { if (_topBarLayer != null && _topBarAdorner != null) _topBarLayer.Remove(_topBarAdorner); }
            catch { }
            _topBarLayer = null;
            _topBarAdorner = null;
        }

        private static void ExitTopBarMode()
        {
            if (!_topBarMode && _topBarButton == null) return;
            _topBarMode = false;
            _topBarButton = null;
            ClearTopBarHighlight();
        }

        /// <summary>Moves to the next (+1) / previous (-1) top bar icon. First press enters the top bar.</summary>
        public static bool TopBarStep(int delta)
        {
            PadInput.NotePad();
            try
            {
                var window = GetForegroundPlayniteWindow();
                if (window == null) return false;
                var buttons = CollectTopBarButtons(window);
                if (buttons.Count == 0) return false;
                ExitDetailsMode();

                int idx = _topBarButton != null ? buttons.IndexOf(_topBarButton) : -1;
                int next = idx < 0
                    ? (delta > 0 ? 0 : buttons.Count - 1)
                    : Math.Max(0, Math.Min(buttons.Count - 1, idx + delta));

                _topBarMode = true;
                _topBarButton = buttons[next];
                try { _topBarButton.Focus(); } catch { }
                SetTopBarHighlight(_topBarButton);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// RT (+1) / LT (-1): jump across the gap in the top bar. The icons split into a left group
        /// (back/forward, view switchers...) and a right group (extension icons) at the widest gap.
        /// RT goes to the first icon of the right group (or its last icon if you are already in it);
        /// LT goes to the last icon of the left group (or its first icon if you are already in it).
        /// </summary>
        public static bool TopBarJump(int direction)
        {
            PadInput.NotePad();
            try
            {
                var window = GetForegroundPlayniteWindow();
                if (window == null) return false;
                var buttons = CollectTopBarButtons(window);
                if (buttons.Count == 0) return false;

                ExitDetailsMode();
                // Widest horizontal gap between neighbouring icons = the split between the groups.
                int split = -1;
                double widest = 100; // ignore normal icon spacing
                for (int i = 0; i + 1 < buttons.Count; i++)
                {
                    var a = GetPosition(buttons[i], window);
                    var b = GetPosition(buttons[i + 1], window);
                    double gap = b.X - (a.X + buttons[i].ActualWidth);
                    if (gap > widest) { widest = gap; split = i + 1; }
                }

                int cur = _topBarButton != null ? buttons.IndexOf(_topBarButton) : -1;
                int target;
                if (split < 0)
                {
                    // One single group: just go to its end.
                    target = direction > 0 ? buttons.Count - 1 : 0;
                }
                else if (direction > 0)
                    target = (cur >= split) ? buttons.Count - 1 : split;
                else
                    target = (cur >= 0 && cur < split) ? 0 : split - 1;

                _topBarMode = true;
                _topBarButton = buttons[target];
                try { _topBarButton.Focus(); } catch { }
                SetTopBarHighlight(_topBarButton);
                return true;
            }
            catch { return false; }
        }

        private static bool TopBarConfirm()
        {
            try
            {
                if (!_topBarMode || _topBarButton == null) return false;
                var button = _topBarButton;
                ExitTopBarMode();
                if (!IsUsable(button) || !button.IsEnabled) return true;

                // ButtonBase.OnClick: Click handlers, Command, and the toggle for ToggleButtons.
                Defer(() =>
                {
                    var onClick = typeof(ButtonBase).GetMethod("OnClick",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (onClick != null) onClick.Invoke(button, null);
                    else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                });
                return true;
            }
            catch { return false; }
        }

        // ---- Details panel (Right from the library enters it; D-pad moves between its buttons) --------
        //
        //   Right (from the game list)   enter the details panel on its first button (Play)
        //   Left / Right                 previous / next button in the same row; Left past the first = back to the list
        //   Up / Down                    row above / below (Play row, link row ...), nearest button horizontally
        //   A                            press the selected button / link
        //   B                            back to the game list

        private static bool _detailsMode;
        private static FrameworkElement _detailsStop;
        private static AdornerLayer _detailsLayer;
        private static Adorner _detailsAdorner;

        private sealed class DetailsHighlight : Adorner
        {
            private readonly Rect _content;

            public DetailsHighlight(UIElement el, Rect content) : base(el) { IsHitTestVisible = false; _content = content; }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                double aw = AdornedElement.RenderSize.Width, ah = AdornedElement.RenderSize.Height;
                var r = new Rect(-2, -2, aw + 4, ah + 4);
                try
                {
                    // A transparent click-catcher laid over visible content: outline that content.
                    if (!_content.IsEmpty)
                    {
                        var oc = _content;
                        oc.Inflate(8, 5);
                        oc.Intersect(new Rect(0, 0, aw, ah));
                        if (!oc.IsEmpty && oc.Width > 8 && oc.Height > 8)
                            r = new Rect(oc.X - 2, oc.Y - 2, oc.Width + 4, oc.Height + 4);
                    }
                    else
                    // Only a button stretched across a whole row (the achievements strip) is outlined around its
                    // content; normal buttons keep their own outline.
                    if (aw > 360)
                    {
                        var c = ContentBoundsOf(AdornedElement as FrameworkElement);
                        if (!c.IsEmpty && c.Width < aw * 0.6)
                        {
                            c.Inflate(8, 5);
                            c.Intersect(new Rect(0, 0, aw, ah));
                            if (!c.IsEmpty && c.Width > 8 && c.Height > 8)
                                r = new Rect(c.X - 2, c.Y - 2, c.Width + 4, c.Height + 4);
                        }
                    }
                }
                catch { }
                var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
                dc.DrawRoundedRectangle(fill, null, r, 4, 4);
            }
        }

        private static double EffectiveOpacity(DependencyObject d)
        {
            double o = 1;
            for (int guard = 0; d != null && guard < 256; guard++)
            {
                var ui = d as UIElement;
                if (ui != null) o *= ui.Opacity;
                DependencyObject next = null;
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                {
                    try { next = VisualTreeHelper.GetParent(d); } catch { }
                }
                if (next == null)
                {
                    try { next = LogicalTreeHelper.GetParent(d); } catch { }
                }
                d = next;
            }
            return o;
        }

        /// <summary>Union of the visible content (text, icons, bars) inside a button, in the button's coordinates.</summary>
        private static Rect ContentBoundsOf(FrameworkElement root)
        {
            Rect u = Rect.Empty;
            if (root == null) return u;
            foreach (var e in FindVisualChildren<FrameworkElement>(root))
            {
                if (!(e is TextBlock || e is Image || e is ProgressBar || e is System.Windows.Shapes.Shape)) continue;
                if (!IsUsable(e) || EffectiveOpacity(e) <= 0.05) continue;
                try
                {
                    double width = e.ActualWidth;
                    var tb = e as TextBlock;
                    if (tb != null)
                    {
                        if (string.IsNullOrWhiteSpace(tb.Text)) continue;
                        // A stretched TextBlock is as wide as its cell: measure the text itself.
                        var ft = new System.Windows.Media.FormattedText(tb.Text,
                            System.Globalization.CultureInfo.CurrentCulture, tb.FlowDirection,
                            new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                            tb.FontSize, System.Windows.Media.Brushes.Black);
                        width = Math.Min(width, ft.WidthIncludingTrailingWhitespace + 2);
                    }
                    var b = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, width, e.ActualHeight));
                    u.Union(b);
                }
                catch { }
            }
            return u;
        }

        /// <summary>
        /// Visible content (text, icons, bars) drawn underneath a button that has none of its own - a transparent
        /// click-catcher laid over a panel. Only content lying fully inside the button counts, so banners and
        /// backgrounds behind it are ignored. Result is in the button's coordinates.
        /// </summary>
        private sealed class ContentRef { public FrameworkElement E; public Point P; }
        private static bool _scanActive;                 // true while CollectDetailRows runs
        private static List<ContentRef> _contentCache;   // content elements of the window, built once per scan

        private static List<ContentRef> BuildContentCache(Window window)
        {
            var list = new List<ContentRef>();
            foreach (var e in FindVisualChildren<FrameworkElement>(window))
            {
                if (!(e is TextBlock || e is Image || e is ProgressBar || e is System.Windows.Shapes.Shape)) continue;
                if (!IsUsable(e) || EffectiveOpacity(e) <= 0.05) continue;
                var pos = GetPosition(e, window);
                if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)) continue;
                list.Add(new ContentRef { E = e, P = pos });
            }
            return list;
        }

        private static Rect OverlayContentBounds(Window window, FrameworkElement btn)
        {
            Rect u = Rect.Empty;
            try
            {
                if (window == null || btn == null) return u;
                var origin = GetPosition(btn, window);
                if (double.IsNaN(origin.X) || double.IsNaN(origin.Y)) return u;
                var area = new Rect(origin.X - 2, origin.Y - 2, btn.ActualWidth + 4, btn.ActualHeight + 4);

                List<ContentRef> content;
                if (_scanActive)
                {
                    if (_contentCache == null) _contentCache = BuildContentCache(window);
                    content = _contentCache;
                }
                else content = BuildContentCache(window);

                foreach (var cr in content)
                {
                    var e = cr.E;
                    if (ReferenceEquals(e, btn)) continue;
                    // Cheap reject: the content's full box does not even touch the button area.
                    if (cr.P.X > area.Right || cr.P.Y > area.Bottom ||
                        cr.P.X + e.ActualWidth < area.Left || cr.P.Y + e.ActualHeight < area.Top) continue;
                    try
                    {
                        double width = e.ActualWidth;
                        var tb = e as TextBlock;
                        if (tb != null)
                        {
                            if (string.IsNullOrWhiteSpace(tb.Text)) continue;
                            var ft = new System.Windows.Media.FormattedText(tb.Text,
                                System.Globalization.CultureInfo.CurrentCulture, tb.FlowDirection,
                                new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                                tb.FontSize, System.Windows.Media.Brushes.Black);
                            width = Math.Min(width, ft.WidthIncludingTrailingWhitespace + 2);
                        }
                        var p = cr.P;
                        var r = new Rect(p.X, p.Y, width, e.ActualHeight);
                        // Centre inside the button; not a banner / background that merely passes behind it.
                        if (!area.Contains(new Point(r.X + r.Width / 2, r.Y + r.Height / 2))) continue;
                        if (r.Width > area.Width + 4 || r.Height > area.Height * 1.5) continue;
                        r.Intersect(area);
                        if (r.IsEmpty) continue;
                        u.Union(r);
                    }
                    catch { }
                }
                if (!u.IsEmpty) u.Offset(-origin.X, -origin.Y);
            }
            catch { }
            return u;
        }

        /// <summary>False for transparent hit-areas with nothing under them and faded-out buttons: nothing on screen to select.</summary>
        private static bool IsVisuallyPresent(FrameworkElement e, Window window)
        {
            try
            {
                // A fully transparent button is the usual way to put a click area over a custom panel (the
                // achievements strip), so opacity alone must not exclude it: it counts when visible content lies
                // underneath it. Without content (a hidden settings gear ...) there is nothing to select.
                if (!ContentBoundsOf(e).IsEmpty) return true;
                if (!OverlayContentBounds(window, e).IsEmpty) return true;
                if (EffectiveOpacity(e) <= 0.05) return false;
                var bg = (e as Control)?.Background;
                if (bg == null) return false;
                var solid = bg as System.Windows.Media.SolidColorBrush;
                return !(solid != null && solid.Color.A == 0);
            }
            catch { return true; }
        }

        private static void ClearDetailsHighlight()
        {
            try { if (_detailsLayer != null && _detailsAdorner != null) _detailsLayer.Remove(_detailsAdorner); }
            catch { }
            _detailsLayer = null;
            _detailsAdorner = null;
        }

        private static void ExitDetailsMode()
        {
            if (!_detailsMode && _detailsStop == null) return;
            _detailsMode = false;
            _detailsStop = null;
            ClearDetailsHighlight();
        }

        private static void SetDetailsStop(FrameworkElement el)
        {
            ClearDetailsHighlight();
            _detailsStop = el;
            if (el == null) return;
            try { el.BringIntoView(); } catch { }
            try
            {
                // Layout (scrolling into view) has to settle before the outline is placed.
                el.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!PadInput.Active || !_detailsMode || !ReferenceEquals(_detailsStop, el) || !IsUsable(el)) return;
                        ClearDetailsHighlight();
                        var layer = AdornerLayer.GetAdornerLayer(el);
                        if (layer == null) return;
                        Rect overlay = Rect.Empty;
                        if (ContentBoundsOf(el).IsEmpty) overlay = OverlayContentBounds(Window.GetWindow(el), el);
                        _detailsAdorner = new DetailsHighlight(el, overlay);
                        layer.Add(_detailsAdorner);
                        _detailsLayer = layer;
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        private static DependencyObject DetailsParent(DependencyObject d)
        {
            DependencyObject next = null;
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            {
                try { next = VisualTreeHelper.GetParent(d); } catch { }
            }
            if (next == null)
            {
                try { next = LogicalTreeHelper.GetParent(d); } catch { }
            }
            return next;
        }

        /// <summary>Game id of a data context: a Game, a list entry, or a details view model exposing a "Game" entry.</summary>
        private static bool TryGetGameIdDeep(object value, out Guid id)
        {
            id = Guid.Empty;
            if (value == null) return false;
            if (TryGetGameId(value, out id)) return true;
            try
            {
                var p = value.GetType().GetProperty("Game", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    var inner = p.GetValue(value, null);
                    if (inner != null && !ReferenceEquals(inner, value) && TryGetGameId(inner, out id)) return true;
                }
            }
            catch { }
            id = Guid.Empty;
            return false;
        }

        /// <summary>A row of the game list / grid (or a platform group): these are not part of the details panel.</summary>
        private static bool IsLibraryItemContainer(DependencyObject d)
        {
            if (d is GroupItem) return true;
            if (d is ListBoxItem || d is TreeViewItem)
            {
                var fe = (FrameworkElement)d;
                Guid g;
                return fe.DataContext is CollectionViewGroup || TryGetGameId(fe.DataContext, out g);
            }
            return false;
        }

        private static bool InsideLibraryItem(DependencyObject d)
        {
            for (int guard = 0; d != null && guard < 256; guard++)
            {
                d = DetailsParent(d);
                if (d != null && IsLibraryItemContainer(d)) return true;
            }
            return false;
        }

        /// <summary>The element, or an ancestor below the window, is bound to the selected game.</summary>
        private static bool BelongsToGame(FrameworkElement el, Guid gameId, Window window)
        {
            DependencyObject d = el;
            for (int guard = 0; d != null && guard < 256 && !ReferenceEquals(d, window); guard++)
            {
                var fe = d as FrameworkElement;
                Guid id;
                if (fe != null && TryGetGameIdDeep(fe.DataContext, out id) && id == gameId) return true;
                d = DetailsParent(d);
            }
            return false;
        }

        /// <summary>The details panel's pressable elements, grouped in rows (top to bottom), each row left to right.</summary>
        private static List<List<FrameworkElement>> CollectDetailRows(Window window)
        {
            var rows = new List<List<FrameworkElement>>();
            _scanActive = true;
            _contentCache = null;
            try
            {
                var selected = _api?.MainView?.SelectedGames?.FirstOrDefault();
                if (selected == null || window == null) return rows;

                var candidates = new List<FrameworkElement>();
                foreach (var b in FindVisualChildren<ButtonBase>(window))
                {
                    if (b is RepeatButton) continue;                         // scrollbar arrows
                    if (!b.IsEnabled) continue;
                    candidates.Add(b);
                }
                // Link lists drawn as list items (not game rows).
                foreach (var li in FindVisualChildren<ListBoxItem>(window))
                {
                    if (IsLibraryItemContainer(li)) continue;
                    if (FindVisualChildren<ButtonBase>(li).Any()) continue;   // its button is already a stop
                    candidates.Add(li);
                }

                var usable = new List<FrameworkElement>();
                foreach (var e in candidates)
                {
                    if (!IsUsable(e)) continue;
                    var pos = GetPosition(e, window);
                    if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)) continue;
                    if (pos.Y + e.ActualHeight / 2 <= 60) continue;           // top bar
                    if (FindVisualParent<ScrollBar>(e) != null) continue;
                    if (InsideLibraryItem(e)) continue;
                    if (!IsVisuallyPresent(e, window)) continue;                       // invisible hit-areas
                    usable.Add(e);
                }

                // Prefer what is bound to the selected game (play button, links, achievements);
                // if the theme binds it differently, fall back to every non-library button.
                var bound = usable.Where(e => BelongsToGame(e, selected.Id, window)).ToList();
                var stops = bound.Count > 0 ? bound : usable;
                if (bound.Count > 0)
                {
                    // Plugin controls (achievements ...) often have their own DataContext: keep them when they sit in
                    // the same row as a bound button and to the right of the first one.
                    double leftEdge = bound.Min(e => GetPosition(e, window).X) - 10;
                    var rowCenters = bound.Select(e => GetPosition(e, window).Y + e.ActualHeight / 2).ToList();
                    foreach (var e in usable)
                    {
                        if (bound.Contains(e)) continue;
                        var q = GetPosition(e, window);
                        double cy = q.Y + e.ActualHeight / 2;
                        if (q.X >= leftEdge && rowCenters.Any(y => Math.Abs(y - cy) <= 18))
                            stops.Add(e);
                    }
                }
                if (stops.Count == 0) return rows;

                var ordered = stops
                    .Select(e => new { E = e, P = GetPosition(e, window), H = e.ActualHeight })
                    .OrderBy(x => x.P.Y + x.H / 2).ThenBy(x => x.P.X).ToList();

                double rowY = double.NaN;
                List<FrameworkElement> row = null;
                foreach (var x in ordered)
                {
                    double cy = x.P.Y + x.H / 2;
                    if (row == null || Math.Abs(cy - rowY) > 18)
                    {
                        row = new List<FrameworkElement>();
                        rows.Add(row);
                        rowY = cy;
                    }
                    row.Add(x.E);
                }
                foreach (var r in rows) r.Sort((a, b) => GetPosition(a, window).X.CompareTo(GetPosition(b, window).X));

                if (Breadcrumb.Enabled) try
                {
                    Breadcrumb.Write("DetailsNav: " + (bound.Count > 0 ? "bound" : "fallback") + " " +
                        string.Join(" | ", rows.Select(r => string.Join(",", r.Select(e =>
                            {
                                var q = GetPosition(e, window);
                                return e.GetType().Name + "@" + (int)q.X + "," + (int)q.Y + " " + (int)e.ActualWidth + "x" + (int)e.ActualHeight;
                            })))));
                }
                catch { }
            }
            catch { }
            finally { _scanActive = false; _contentCache = null; }
            return rows;
        }

        /// <summary>Right from the game list: select the first button of the details panel.</summary>
        private static void DumpDetailsCandidates(Window window)
        {
            try
            {
                var selected = _api?.MainView?.SelectedGames?.FirstOrDefault();
                var sb = new System.Text.StringBuilder("DetailsNav candidates:");
                int n = 0;
                foreach (var b in FindVisualChildren<ButtonBase>(window))
                {
                    if (n++ >= 60) break;
                    var q = GetPosition(b, window);
                    if (q.Y + b.ActualHeight / 2 <= 60) continue;
                    bool lib = InsideLibraryItem(b);
                    sb.Append("\n  ").Append(b.GetType().Name).Append(" @").Append((int)q.X).Append(",").Append((int)q.Y)
                      .Append(" ").Append((int)b.ActualWidth).Append("x").Append((int)b.ActualHeight)
                      .Append(" enabled=").Append(b.IsEnabled).Append(" usable=").Append(IsUsable(b))
                      .Append(" lib=").Append(lib).Append(" op=").Append(EffectiveOpacity(b).ToString("0.##"))
                      .Append(" present=").Append(IsVisuallyPresent(b, window))
                      .Append(" own=").Append(!ContentBoundsOf(b).IsEmpty)
                      .Append(" overlay=").Append(!OverlayContentBounds(window, b).IsEmpty)
                      .Append(" game=").Append(selected != null && BelongsToGame(b, selected.Id, window))
                      .Append(" dc=").Append(b.DataContext == null ? "null" : b.DataContext.GetType().Name);
                }
                Breadcrumb.Write(sb.ToString());
            }
            catch (Exception ex) { try { Breadcrumb.Write("DetailsNav dump failed: " + ex.Message); } catch { } }
        }

        private static List<List<FrameworkElement>> _detailRowsCache;
        private static Window _detailRowsWindow;
        private static long _detailRowsTicks;

        private static bool EnterDetails()
        {
            _detailRowsCache = null;
            try
            {
                var window = GetForegroundPlayniteWindow();
                if (window == null) return false;
                if (Breadcrumb.Enabled) DumpDetailsCandidates(window);   // diagnostic only: it walks up to 60 buttons
                var rows = CollectDetailRows(window);
                if (rows.Count == 0 || rows[0].Count == 0) return false;

                ExitTopBarMode();
                ExitGroupHeaderMode();
                _detailsMode = true;
                SetDetailsStop(rows[0][0]);
                return true;
            }
            catch { return false; }
        }

        private static bool DetailsNav(int dx, int dy)
        {
            if (!_detailsMode) return false;
            try
            {
                var window = GetForegroundPlayniteWindow();
                // Scanning the whole visual tree on every D-pad press made Right feel laggy: reuse the last scan
                // for a moment (held-down repeats) as long as the current stop is still part of it.
                List<List<FrameworkElement>> rows = null;
                long nowTicks = DateTime.UtcNow.Ticks;
                if (window != null && _detailRowsCache != null && ReferenceEquals(_detailRowsWindow, window) &&
                    nowTicks - _detailRowsTicks < 600L * TimeSpan.TicksPerMillisecond &&
                    _detailRowsCache.Any(rw => rw.Contains(_detailsStop)))
                    rows = _detailRowsCache;
                else if (window != null)
                {
                    rows = CollectDetailRows(window);
                    _detailRowsCache = rows; _detailRowsWindow = window; _detailRowsTicks = nowTicks;
                }
                if (rows == null || rows.Count == 0 || !IsUsable(_detailsStop)) { _detailRowsCache = null; ExitDetailsMode(); return false; }

                int r = -1, c = -1;
                for (int i = 0; i < rows.Count && r < 0; i++)
                {
                    int j = rows[i].IndexOf(_detailsStop);
                    if (j >= 0) { r = i; c = j; }
                }
                if (r < 0) { ExitDetailsMode(); return false; }

                if (dx != 0)
                {
                    int nc = c + dx;
                    if (nc < 0) { ExitDetailsMode(); return true; }           // Left past the first button: back to the list
                    if (nc >= rows[r].Count) return true;
                    SetDetailsStop(rows[r][nc]);
                    return true;
                }

                int nr = r + dy;
                if (nr < 0 || nr >= rows.Count) return true;
                var cur = GetPosition(_detailsStop, window);
                double cx = cur.X + _detailsStop.ActualWidth / 2;
                FrameworkElement best = null;
                double bestDist = double.MaxValue;
                foreach (var e in rows[nr])
                {
                    var p = GetPosition(e, window);
                    double dist = Math.Abs(p.X + e.ActualWidth / 2 - cx);
                    if (dist < bestDist) { bestDist = dist; best = e; }
                }
                if (best != null) SetDetailsStop(best);
                return true;
            }
            catch { ExitDetailsMode(); return false; }
        }

        private static bool DetailsConfirm()
        {
            try
            {
                if (!_detailsMode || _detailsStop == null) return false;
                var el = _detailsStop;
                if (!IsUsable(el)) { ExitDetailsMode(); return false; }

                Defer(() =>
                {
                    var button = el as ButtonBase;
                    if (button != null)
                    {
                        if (!button.IsEnabled) return;
                        int sourcesBefore = CountPresentationSources();
                        // ButtonBase.OnClick: Click handlers, Command, and the toggle for ToggleButtons.
                        var onClick = typeof(ButtonBase).GetMethod("OnClick",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (onClick != null) onClick.Invoke(button, null);
                        else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

                        // The little arrow beside Play opens a menu; themes often open it from mouse events, which
                        // a plain Click does not produce. If nothing opened, retry the way a mouse would.
                        if (button is ToggleButton || (button.ActualWidth <= 48 && button.ActualHeight >= 20))
                            VerifyMenuOpened(button, sourcesBefore);
                        return;
                    }
                    var item = el as ListBoxItem;
                    if (item != null) item.IsSelected = true;
                });
                return true;
            }
            catch { return false; }
        }

        private static int CountPresentationSources()
        {
            try
            {
                // CurrentSources is a non-generic IEnumerable: no Count property.
                int n = 0;
                foreach (PresentationSource src in PresentationSource.CurrentSources) n++;
                return n;
            }
            catch { return 0; }
        }

        private static bool MenuOpenedBy(ButtonBase button, int sourcesBefore)
        {
            try
            {
                if (ResolveOpenMenu() != null) return true;
                if (CountPresentationSources() > sourcesBefore) return true;      // a popup window appeared
                var toggle = button as ToggleButton;
                return toggle != null && toggle.IsChecked == true;
            }
            catch { return false; }
        }

        /// <summary>A pressed dropdown arrow should open its menu. Check shortly after; if not, open its own menu, then click like a mouse.</summary>
        private static void VerifyMenuOpened(ButtonBase button, int sourcesBefore)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    if (MenuOpenedBy(button, sourcesBefore)) { FocusFirstMenuItemSoon(); return; }

                    // 1) The button carries its own context menu.
                    var menu = button.ContextMenu;
                    if (menu != null && menu.HasItems)
                    {
                        Breadcrumb.Write("DetailsNav: arrow fallback -> own ContextMenu");
                        menu.PlacementTarget = button;
                        menu.Placement = PlacementMode.Bottom;
                        menu.IsOpen = true;
                        _padMenu = menu;
                        FocusFirstMenuItemSoon();
                        return;
                    }

                    // 2) A real mouse click on the button.
                    Breadcrumb.Write("DetailsNav: arrow fallback -> mouse click");
                    SendLeftClick(button);
                    var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    again.Tick += (s2, e2) =>
                    {
                        again.Stop();
                        try { FocusFirstMenuItemSoon(); } catch { }
                    };
                    again.Start();
                }
                catch (Exception ex) { Breadcrumb.Write("DetailsNav: arrow fallback failed: " + ex.Message); }
            };
            t.Start();
        }

        // ---- Context menu (B opens it; D-pad / stick / A / B drive it) --------------------------

        private static ContextMenu _padMenu;

        private const byte VK_APPS = 0x5D;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NavPoint p);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct NavPoint { public int X, Y; }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        /// <summary>Opens the context menu of the selected game and focuses its first item.</summary>
        public static bool OpenContextMenu()
        {
            PadInput.NotePad();
            try
            {
                var window = GetForegroundPlayniteWindow();
                var game = _api?.MainView?.SelectedGames?.FirstOrDefault();
                Breadcrumb.Write("DesktopNav.OpenContextMenu window=" + (window != null) + " game=" + (game != null));
                if (window == null || game == null) return false;

                FrameworkElement target = FindSelectedGameElement(window, game.Id);
                Breadcrumb.Write("DesktopNav.OpenContextMenu target=" + (target == null ? "none" : target.GetType().Name) +
                                 " hasMenu=" + (target?.ContextMenu != null));
                if (target == null) target = Keyboard.FocusedElement as FrameworkElement;
                if (target == null) return false;

                try { target.BringIntoView(); } catch { }
                try { target.Focus(); Keyboard.Focus(target); } catch { }

                // 1) The item carries a ready-made menu: open it directly.
                var menu = target.ContextMenu;
                if (menu != null && menu.HasItems)
                {
                    // Beside the selected item (WPF shifts it to stay on screen), not centred on top of the list.
                    menu.PlacementTarget = target;
                    menu.Placement = PlacementMode.Right;
                    menu.IsOpen = true;
                    _padMenu = menu;
                }
                else
                {
                    // 2) Playnite builds its menu on a real right click: do exactly that on the item.
                    SendRightClick(target);
                }

                // 3) Still nothing a moment later: try the keyboard Menu key as a last resort.
                var verify = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                verify.Tick += (s, e) =>
                {
                    verify.Stop();
                    try
                    {
                        if (ResolveOpenMenu() == null)
                        {
                            Breadcrumb.Write("DesktopNav.OpenContextMenu fallback: Menu key");
                            try { target.Focus(); Keyboard.Focus(target); } catch { }
                            PadInput.Suppress(1500);
                            keybd_event(VK_APPS, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                            keybd_event(VK_APPS, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
                        }
                    }
                    catch { }
                    FocusFirstMenuItemSoon();
                };
                verify.Start();

                FocusFirstMenuItemSoon();
                return true;
            }
            catch (Exception ex)
            {
                Breadcrumb.Write("DesktopNav.OpenContextMenu failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Start: opens Playnite's main menu (the "Playnite" button at the left of the top bar), or closes it when it is
        /// already open. The menu is then driven like any other context menu (D-pad / A / B).
        /// </summary>
        public static bool ToggleMainMenu()
        {
            PadInput.NotePad();
            try
            {
                var open = ResolveOpenMenu();
                if (open != null)
                {
                    open.IsOpen = false;
                    _padMenu = null;
                    return true;
                }

                var window = GetForegroundPlayniteWindow();
                if (window == null) return false;

                FrameworkElement host = null;
                foreach (var fe in FindVisualChildren<FrameworkElement>(window))
                {
                    if (fe.Name == "PART_ElemMainMenu" && fe.ContextMenu != null) { host = fe; break; }
                }
                if (host == null || !IsUsable(host))
                {
                    Breadcrumb.Write("DesktopNav.ToggleMainMenu: main menu button not found / hidden (" + (host == null ? "missing" : "not visible") + ")");
                    return false;
                }

                ExitTopBarMode();
                ExitDetailsMode();
                var menu = host.ContextMenu;
                // Same thing Playnite's own left click does, placed under the button.
                menu.PlacementTarget = host;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                _padMenu = menu;
                FocusFirstMenuItemSoon();
                return true;
            }
            catch (Exception ex)
            {
                Breadcrumb.Write("DesktopNav.ToggleMainMenu failed: " + ex.Message);
                return false;
            }
        }

        private static void SendRightClick(FrameworkElement target)
        {
            try
            {
                if (!target.IsVisible) return;
                var p = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                NavPoint old;
                bool haveOld = GetCursorPos(out old);

                PadInput.Suppress(1500);
                SetCursorPos((int)p.X, (int)p.Y);
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);

                if (haveOld)
                {
                    // Put the pointer back once the menu is up so it does not hover-steal the selection.
                    var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                    back.Tick += (s, e) => { back.Stop(); try { SetCursorPos(old.X, old.Y); } catch { } };
                    back.Start();
                }
            }
            catch (Exception ex) { Breadcrumb.Write("DesktopNav.SendRightClick failed: " + ex.Message); }
        }

        private static void SendLeftClick(FrameworkElement target)
        {
            try
            {
                if (!target.IsVisible) return;
                var p = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                NavPoint old;
                bool haveOld = GetCursorPos(out old);

                PadInput.Suppress(1500);
                SetCursorPos((int)p.X, (int)p.Y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

                if (haveOld)
                {
                    var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                    back.Tick += (s, e) => { back.Stop(); try { SetCursorPos(old.X, old.Y); } catch { } };
                    back.Start();
                }
            }
            catch (Exception ex) { Breadcrumb.Write("DesktopNav.SendLeftClick failed: " + ex.Message); }
        }

        /// <summary>Menu items are generated after the popup opens: focus the first one then.</summary>
        private static void FocusFirstMenuItemSoon()
        {
            for (int i = 1; i <= 3; i++)
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120 * i + (i == 3 ? 300 : 0)) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    try
                    {
                        var m = ResolveOpenMenu();
                        if (m == null) return;
                        _padMenu = m;
                        var items = GetNavigableItems(m);
                        if (items.Count > 0 && !items.Any(x => x.IsKeyboardFocused || x.IsHighlighted))
                            items[0].Focus();
                    }
                    catch { }
                };
                t.Start();
            }
        }

        private static FrameworkElement FindSelectedGameElement(Window window, Guid gameId)
        {
            FrameworkElement fallback = null;
            foreach (var item in FindVisualChildren<ListBoxItem>(window))
            {
                if (!IsUsable(item)) continue;
                Guid id;
                if (!TryGetGameId(item.DataContext, out id) || id != gameId) continue;
                if (item.IsSelected) return item;
                if (fallback == null) fallback = item;
            }
            if (fallback != null) return fallback;

            foreach (var element in FindVisualChildren<FrameworkElement>(window))
            {
                if (!IsUsable(element) || element.ContextMenu == null) continue;
                Guid id;
                if (TryGetGameId(element.DataContext, out id) && id == gameId)
                    return element;
            }
            return null;
        }

        /// <summary>The currently open context menu, whether opened by us, the mouse or the Menu key.</summary>
        private static ContextMenu ResolveOpenMenu()
        {
            if (_padMenu != null && _padMenu.IsOpen) return _padMenu;
            _padMenu = null;

            // A menu opened by a real right click is not always keyboard-focused yet:
            // look for an open ContextMenu among the open popup roots.
            try
            {
                foreach (PresentationSource src in PresentationSource.CurrentSources)
                {
                    var root = src?.RootVisual as DependencyObject;
                    if (root == null || root is Window) continue; // popups only, never walk whole windows
                    foreach (var cm in FindVisualChildren<ContextMenu>(root))
                        if (cm.IsOpen) return cm;
                }
            }
            catch { }

            DependencyObject cur = Keyboard.FocusedElement as DependencyObject;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                var cm = cur as ContextMenu;
                if (cm != null) return cm.IsOpen ? cm : null;

                DependencyObject next = null;
                if (cur is Visual || cur is System.Windows.Media.Media3D.Visual3D)
                {
                    try { next = VisualTreeHelper.GetParent(cur); } catch { }
                }
                if (next == null)
                {
                    try { next = LogicalTreeHelper.GetParent(cur); } catch { }
                }
                if (next == null)
                {
                    var fe = cur as FrameworkElement;
                    next = fe?.TemplatedParent ?? fe?.Parent;
                }
                cur = next;
            }
            return null;
        }

        private static List<MenuItem> GetNavigableItems(ItemsControl host)
        {
            var list = new List<MenuItem>();
            if (host == null) return list;
            for (int i = 0; i < host.Items.Count; i++)
            {
                var mi = host.ItemContainerGenerator.ContainerFromIndex(i) as MenuItem;
                if (mi != null && mi.IsEnabled && mi.Visibility == Visibility.Visible)
                    list.Add(mi);
            }
            return list;
        }

        /// <summary>The innermost open submenu's owner, or the menu itself.</summary>
        private static ItemsControl DeepestHost(ContextMenu menu)
        {
            ItemsControl host = menu;
            for (int depth = 0; depth < 16; depth++)
            {
                var open = GetNavigableItems(host).FirstOrDefault(m => m.HasItems && m.IsSubmenuOpen);
                if (open == null) break;
                host = open;
            }
            return host;
        }

        private static bool MenuMove(int delta)
        {
            try
            {
                var menu = ResolveOpenMenu();
                if (menu == null) return false;
                var items = GetNavigableItems(DeepestHost(menu));
                if (items.Count == 0) return true;

                int cur = items.FindIndex(m => m.IsKeyboardFocused || m.IsHighlighted);
                int next = cur < 0 ? (delta > 0 ? 0 : items.Count - 1)
                                   : (cur + delta + items.Count) % items.Count;
                items[next].Focus();
                return true;
            }
            catch { return false; }
        }

        private static bool MenuRight()
        {
            try
            {
                var menu = ResolveOpenMenu();
                if (menu == null) return false;
                var host = DeepestHost(menu);
                var item = GetNavigableItems(host).FirstOrDefault(m => m.IsKeyboardFocused || m.IsHighlighted);
                if (item != null && item.HasItems)
                    OpenSubmenu(item);
                return true;
            }
            catch { return false; }
        }

        private static bool MenuLeft()
        {
            try
            {
                var menu = ResolveOpenMenu();
                if (menu == null) return false;
                CloseDeepestSubmenu(menu);
                return true;
            }
            catch { return false; }
        }

        private static bool MenuConfirm()
        {
            try
            {
                var menu = ResolveOpenMenu();
                if (menu == null) return false;
                var host = DeepestHost(menu);
                var item = GetNavigableItems(host).FirstOrDefault(m => m.IsKeyboardFocused || m.IsHighlighted);
                if (item == null) return true;

                if (item.HasItems)
                {
                    OpenSubmenu(item);
                    return true;
                }

                // MenuItem.OnClick runs Click handlers, toggles checkable items and executes the Command.
                // Deferred: the item may open a modal dialog (Edit game, extension windows...).
                var closeMenu = !item.StaysOpenOnClick;
                Defer(() =>
                {
                    var onClick = typeof(MenuItem).GetMethod("OnClick",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (onClick != null) onClick.Invoke(item, null);
                    else item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
                    if (closeMenu) { try { menu.IsOpen = false; } catch { } }
                });
                if (closeMenu) _padMenu = null;
                return true;
            }
            catch { return false; }
        }

        private static bool MenuBack()
        {
            try
            {
                var menu = ResolveOpenMenu();
                if (menu == null) return false;
                if (!CloseDeepestSubmenu(menu))
                {
                    menu.IsOpen = false;
                    _padMenu = null;
                }
                return true;
            }
            catch { return false; }
        }

        private static void OpenSubmenu(MenuItem item)
        {
            item.IsSubmenuOpen = true;
            try { item.ApplyTemplate(); item.UpdateLayout(); } catch { }
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    var first = GetNavigableItems(item).FirstOrDefault();
                    if (first != null) first.Focus();
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Closes the innermost open submenu. False when only the root menu is open.</summary>
        private static bool CloseDeepestSubmenu(ContextMenu menu)
        {
            ItemsControl host = menu;
            MenuItem owner = null;
            for (int depth = 0; depth < 16; depth++)
            {
                var open = GetNavigableItems(host).FirstOrDefault(m => m.HasItems && m.IsSubmenuOpen);
                if (open == null) break;
                owner = open;
                host = open;
            }
            if (owner == null) return false;
            owner.IsSubmenuOpen = false;
            owner.Focus();
            return true;
        }

        /// <summary>
        /// Runs a click after the current pad poll has returned. A click can open a modal dialog; run inline,
        /// its nested message loop would execute inside the poller's tick.
        /// </summary>
        internal static void Defer(Action action)
        {
            try
            {
                var d = Application.Current?.Dispatcher;
                if (d == null) { action(); return; }
                d.BeginInvoke(new Action(() => { try { action(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
            catch { }
        }

        public static void EnsureLibraryFocus()
        {
            if (_groupHeaderMode && !IsUsable(_currentGroup))
                ExitGroupHeaderMode();
        }

        private static GroupItem ResolveCurrentGroup()
        {
            if (IsUsable(_currentGroup))
                return _currentGroup;

            var selected = _api?.MainView?.SelectedGames?.FirstOrDefault();
            if (selected == null)
                return null;

            _currentGroup = FindGroupContainingGame(selected.Id);
            return _currentGroup;
        }

        private static GroupItem FindGroupContainingGame(Guid gameId)
        {
            var window = GetForegroundPlayniteWindow();
            if (window == null)
                return null;

            // Prefer exact DataContext membership. A GroupItem's DataContext is normally
            // a CollectionViewGroup; this still works for nested grouping.
            foreach (var group in FindVisualChildren<GroupItem>(window))
            {
                if (!IsUsable(group))
                    continue;

                try
                {
                    if (ContainsGameId(group.DataContext, gameId))
                        return group;
                }
                catch { }
            }

            // Fallback: locate the selected game's realized visual and walk up to GroupItem.
            foreach (var element in FindVisualChildren<FrameworkElement>(window))
            {
                if (!IsUsable(element))
                    continue;

                try
                {
                    Guid id;
                    if (TryGetGameId(element.DataContext, out id) && id == gameId)
                    {
                        var parent = FindVisualParent<GroupItem>(element);
                        if (parent != null)
                            return parent;
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool TryMoveGroupHeader(int delta)
        {
            try
            {
                var window = GetForegroundPlayniteWindow();
                if (window == null)
                    return false;

                var groups = FindVisualChildren<GroupItem>(window)
                    .Where(IsUsable)
                    .Select(g => new { Group = g, Pos = GetPosition(g, window) })
                    .Where(x => !double.IsNaN(x.Pos.Y))
                    .OrderBy(x => x.Pos.Y)
                    .ThenBy(x => x.Pos.X)
                    .Select(x => x.Group)
                    .ToList();

                if (groups.Count == 0)
                    return false;

                var current = ResolveCurrentGroup();
                int index = current == null ? -1 : groups.IndexOf(current);
                if (index < 0)
                    index = delta > 0 ? -1 : groups.Count;

                int next = index + delta;
                if (next < 0) next = 0;
                if (next >= groups.Count) next = groups.Count - 1;

                _currentGroup = groups[next];
                FocusGroupHeader(_currentGroup);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void FocusGroupHeader(GroupItem group)
        {
            if (group == null) return;

            try { group.BringIntoView(); } catch { }

            // The default Playnite grouped view uses an Expander header with a ToggleButton.
            // Add our own high-contrast selection marker because many desktop themes barely
            // show keyboard focus on group headers.
            try
            {
                var toggle = FindVisualChildren<ToggleButton>(group)
                    .FirstOrDefault(x => x.IsVisible && x.IsEnabled);
                if (toggle != null)
                {
                    HighlightHeader(toggle);
                    toggle.Focus();
                    Keyboard.Focus(toggle);
                    return;
                }
            }
            catch { }

            ClearHeaderHighlight();
            try
            {
                group.Focusable = true;
                group.Focus();
                Keyboard.Focus(group);
            }
            catch { }
        }

        private static void HighlightHeader(ToggleButton toggle)
        {
            if (toggle == null || !PadInput.Active)
                return;

            if (ReferenceEquals(_highlightedHeader, toggle))
                return;

            ClearHeaderHighlight();

            try
            {
                _highlightedHeader = toggle;
                _oldBackground = toggle.ReadLocalValue(Control.BackgroundProperty);
                _oldForeground = toggle.ReadLocalValue(Control.ForegroundProperty);
                _oldBorderBrush = toggle.ReadLocalValue(Control.BorderBrushProperty);
                _oldBorderThickness = toggle.ReadLocalValue(Control.BorderThicknessProperty);

                toggle.SetValue(Control.BackgroundProperty, SystemColors.HighlightBrush);
                toggle.SetValue(Control.ForegroundProperty, SystemColors.HighlightTextBrush);
                toggle.SetValue(Control.BorderBrushProperty, SystemColors.HighlightBrush);
                toggle.SetValue(Control.BorderThicknessProperty, new Thickness(2));
            }
            catch { }
        }

        private static void ClearHeaderHighlight()
        {
            var toggle = _highlightedHeader;
            _highlightedHeader = null;
            if (toggle == null)
                return;

            try { RestoreLocalValue(toggle, Control.BackgroundProperty, _oldBackground); } catch { }
            try { RestoreLocalValue(toggle, Control.ForegroundProperty, _oldForeground); } catch { }
            try { RestoreLocalValue(toggle, Control.BorderBrushProperty, _oldBorderBrush); } catch { }
            try { RestoreLocalValue(toggle, Control.BorderThicknessProperty, _oldBorderThickness); } catch { }

            _oldBackground = DependencyProperty.UnsetValue;
            _oldForeground = DependencyProperty.UnsetValue;
            _oldBorderBrush = DependencyProperty.UnsetValue;
            _oldBorderThickness = DependencyProperty.UnsetValue;
        }

        private static void RestoreLocalValue(DependencyObject obj, DependencyProperty property, object value)
        {
            if (value == DependencyProperty.UnsetValue)
                obj.ClearValue(property);
            else
                obj.SetValue(property, value);
        }

        private static Expander GetGroupExpander(GroupItem group)
        {
            if (group == null) return null;
            try
            {
                return FindVisualChildren<Expander>(group).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static void SetGroupExpanded(GroupItem group, bool expanded)
        {
            var expander = GetGroupExpander(group);
            if (expander == null)
                return;

            expander.IsExpanded = expanded;
            try { group.UpdateLayout(); } catch { }
        }

        private static bool ContainsGameId(object value, Guid gameId)
        {
            if (value == null)
                return false;

            Guid direct;
            if (TryGetGameId(value, out direct) && direct == gameId)
                return true;

            var group = value as CollectionViewGroup;
            if (group != null)
            {
                foreach (var item in group.Items)
                {
                    if (ContainsGameId(item, gameId))
                        return true;
                }
                return false;
            }

            return false;
        }

        private static bool TryGetFirstGameId(object value, out Guid id)
        {
            id = Guid.Empty;
            if (value == null)
                return false;

            if (TryGetGameId(value, out id))
                return true;

            var group = value as CollectionViewGroup;
            if (group != null)
            {
                foreach (var item in group.Items)
                {
                    if (TryGetFirstGameId(item, out id))
                        return true;
                }
            }

            return false;
        }

        private static bool TryGetGameId(object value, out Guid id)
        {
            id = Guid.Empty;
            if (value == null)
                return false;

            var game = value as Game;
            if (game != null)
            {
                id = game.Id;
                return true;
            }

            try
            {
                var t = value.GetType();

                // Playnite's game-list entry commonly exposes a Game property.
                var gameProp = t.GetProperty("Game", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (gameProp != null)
                {
                    var nestedGame = gameProp.GetValue(value, null) as Game;
                    if (nestedGame != null)
                    {
                        id = nestedGame.Id;
                        return true;
                    }
                }

                // Fallback for entries exposing Id directly.
                var idProp = t.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (idProp != null && idProp.PropertyType == typeof(Guid))
                {
                    id = (Guid)idProp.GetValue(value, null);
                    return id != Guid.Empty;
                }
            }
            catch { }

            return false;
        }

        private static bool IsGridView()
        {
            try
            {
                var view = _api?.MainView?.ActiveDesktopView;
                return view != null &&
                       view.ToString().IndexOf("Grid", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void ExitGroupHeaderMode()
        {
            _groupHeaderMode = false;
            ClearHeaderHighlight();
        }

        private static Window GetForegroundPlayniteWindow()
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                    return null;

                IntPtr foreground = GetForegroundWindow();
                foreach (Window w in app.Windows)
                {
                    if (w == null || !w.IsVisible)
                        continue;

                    try
                    {
                        var hwnd = new WindowInteropHelper(w).Handle;
                        if (hwnd != IntPtr.Zero && hwnd == foreground)
                            return w;
                    }
                    catch { }
                }

                // Fallback: choose the visible Playnite-owned window with the largest area.
                Window best = null;
                double bestArea = 0;
                foreach (Window w in app.Windows)
                {
                    if (w == null || !w.IsVisible)
                        continue;

                    double area = Math.Max(0, w.ActualWidth) * Math.Max(0, w.ActualHeight);
                    if (area > bestArea)
                    {
                        best = w;
                        bestArea = area;
                    }
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        internal static Point GetPosition(FrameworkElement element, Visual ancestor)
        {
            try
            {
                return element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
            }
            catch
            {
                return new Point(double.NaN, double.NaN);
            }
        }

        internal static bool IsUsable(FrameworkElement element)
        {
            return element != null && element.IsVisible && element.ActualHeight > 0 && element.ActualWidth > 0;
        }

        internal static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                yield break;

            // Iterative pre-order walk. The old recursive iterator re-yielded every node once per ancestor
            // level, which made whole-window scans (details panel, grid cells) slow on every D-pad press.
            var stack = new Stack<DependencyObject>();
            stack.Push(parent);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                int count = 0;
                try { count = VisualTreeHelper.GetChildrenCount(cur); }
                catch { count = 0; }

                for (int i = count - 1; i >= 0; i--)
                {
                    DependencyObject child = null;
                    try { child = VisualTreeHelper.GetChild(cur, i); }
                    catch { }
                    if (child != null)
                        stack.Push(child);
                }

                if (!ReferenceEquals(cur, parent))
                {
                    var typed = cur as T;
                    if (typed != null)
                        yield return typed;
                }
            }
        }

        internal static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            try
            {
                var current = child;
                while (current != null)
                {
                    current = VisualTreeHelper.GetParent(current);
                    var typed = current as T;
                    if (typed != null)
                        return typed;
                }
            }
            catch { }
            return null;
        }

        private enum GridMove { Moved, Edge, Unavailable }

        private sealed class GridCell
        {
            public FrameworkElement Element;
            public Guid Id;
            public double CenterX, CenterY, Height;
        }

        /// <summary>
        /// Moves the selection using the real on-screen positions of the grid cells instead of
        /// guessing a column count. Moved = selection changed, Edge = no cell in that direction
        /// (stay put), Unavailable = the grid layout could not be read (caller may fall back).
        /// </summary>
        private static GridMove MoveGrid(int dx, int dy)
        {
            try
            {
                if (_api?.MainView == null) return GridMove.Unavailable;
                var selected = _api.MainView.SelectedGames?.FirstOrDefault();
                if (selected == null) return GridMove.Unavailable;

                var window = GetForegroundPlayniteWindow();
                if (window == null) return GridMove.Unavailable;

                var cells = new List<GridCell>();
                foreach (var item in FindVisualChildren<ListBoxItem>(window))
                {
                    if (!IsUsable(item)) continue;
                    Guid id;
                    if (!TryGetGameId(item.DataContext, out id)) continue;
                    var pos = GetPosition(item, window);
                    if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)) continue;
                    cells.Add(new GridCell
                    {
                        Element = item,
                        Id = id,
                        CenterX = pos.X + item.ActualWidth / 2.0,
                        CenterY = pos.Y + item.ActualHeight / 2.0,
                        Height = item.ActualHeight
                    });
                }

                var cur = cells.FirstOrDefault(x => x.Id == selected.Id);
                if (cur == null) return GridMove.Unavailable;

                // Only compare cells scrolled together with the selected one (ignore other lists).
                var scroll = FindVisualParent<ScrollViewer>(cur.Element);
                cells = cells.Where(x => x != cur && FindVisualParent<ScrollViewer>(x.Element) == scroll).ToList();

                double tol = Math.Max(4.0, cur.Height * 0.35);
                GridCell best = null;

                if (dx != 0)
                {
                    best = cells
                        .Where(x => Math.Abs(x.CenterY - cur.CenterY) <= tol &&
                                    (dx > 0 ? x.CenterX > cur.CenterX + 1 : x.CenterX < cur.CenterX - 1))
                        .OrderBy(x => Math.Abs(x.CenterX - cur.CenterX))
                        .FirstOrDefault();
                }
                else
                {
                    var rows = cells.Where(x => dy > 0 ? x.CenterY - cur.CenterY > tol : cur.CenterY - x.CenterY > tol).ToList();
                    if (rows.Count > 0)
                    {
                        double rowY = dy > 0 ? rows.Min(x => x.CenterY) : rows.Max(x => x.CenterY);
                        best = rows
                            .Where(x => Math.Abs(x.CenterY - rowY) <= tol)
                            .OrderBy(x => Math.Abs(x.CenterX - cur.CenterX))
                            .FirstOrDefault();
                    }
                }

                if (best == null) return GridMove.Edge;
                _api.MainView.SelectGame(best.Id);
                return GridMove.Moved;
            }
            catch
            {
                return GridMove.Unavailable;
            }
        }

        private sealed class NavStop
        {
            public FrameworkElement Element;
            public Guid Id;          // game stop
            public GroupItem Group;  // collapsed-group header stop
            public double X, Y;
        }

        private static bool IsCollapsedGroup(GroupItem group)
        {
            var ex = GetGroupExpander(group);
            return ex != null && !ex.IsExpanded;
        }

        /// <summary>
        /// Everything the pad can stop on in list view, in on-screen order: the visible games plus the
        /// header of every COLLAPSED group (so a collapsed group can be reached and expanded without
        /// collapsing the group you are in). Games inside collapsed groups are not visible, so they are
        /// skipped. Expanded groups' headers are not stops: you flow straight into their first game.
        /// </summary>
        private static List<NavStop> CollectListStops(Window window, FrameworkElement anchor)
        {
            ScrollViewer scroll = anchor != null ? FindVisualParent<ScrollViewer>(anchor) : null;
            var all = new List<NavStop>();
            // PERF: walk only the list's own subtree. Walking the whole window on every D-pad step was the lag.
            DependencyObject scanRoot = scroll != null ? (DependencyObject)scroll : window;

            foreach (var item in FindVisualChildren<ListBoxItem>(scanRoot))
            {
                if (!IsUsable(item)) continue;   // inside a collapsed group => not visible
                Guid id;
                if (!TryGetGameId(item.DataContext, out id)) continue;
                if (FindVisualParent<ScrollViewer>(item) != scroll) continue;
                var pos = GetPosition(item, window);
                if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)) continue;
                all.Add(new NavStop { Element = item, Id = id, X = pos.X, Y = pos.Y });
            }

            foreach (var group in FindVisualChildren<GroupItem>(scanRoot))
            {
                if (!IsUsable(group) || !IsCollapsedGroup(group)) continue;
                if (FindVisualParent<ScrollViewer>(group) != scroll) continue;
                var pos = GetPosition(group, window);
                if (double.IsNaN(pos.X) || double.IsNaN(pos.Y)) continue;
                all.Add(new NavStop { Element = group, Group = group, X = pos.X, Y = pos.Y });
            }

            return all.OrderBy(x => x.Y).ThenBy(x => x.X).ToList();
        }

        /// <summary>
        /// List view: one step through the stops above. fromHeader = the collapsed group header the pad is
        /// currently on (header mode), otherwise the selected game is the start. False = could not decide,
        /// the caller falls back to plain collection order.
        /// </summary>
        private static bool MoveListVisible(int delta, GroupItem fromHeader = null)
        {
            long mlvStart = Stopwatch.GetTimestamp();
            try
            {
                if (_api?.MainView == null || delta == 0) return false;
                var window = GetForegroundPlayniteWindow();
                if (window == null) return false;

                Guid selectedId = Guid.Empty;
                if (fromHeader == null)
                {
                    var selected = _api.MainView.SelectedGames?.FirstOrDefault();
                    if (selected == null) return false;
                    selectedId = selected.Id;
                }

                ListBoxItem FindInRoot(DependencyObject root)
                {
                    ListBoxItem firstMatch = null;
                    foreach (var item in FindVisualChildren<ListBoxItem>(root))
                    {
                        Guid id;
                        if (!IsUsable(item) || !TryGetGameId(item.DataContext, out id) || id != selectedId) continue;
                        if (item.IsSelected) return item;          // the same game can be listed in several groups
                        if (firstMatch == null) firstMatch = item;
                    }
                    return firstMatch;
                }

                FrameworkElement FindAnchor()
                {
                    if (fromHeader != null) return IsUsable(fromHeader) ? fromHeader : null;
                    ListBoxItem found = null;
                    if (_navList != null && IsUsable(_navList)) found = FindInRoot(_navList);   // PERF: cached list
                    if (found == null) found = FindInRoot(window);
                    if (found != null) _navList = FindVisualParent<ListBox>(found);
                    return found;
                }

                int IndexOfCurrent(List<NavStop> stops, FrameworkElement cur)
                {
                    if (fromHeader != null) return stops.FindIndex(x => x.Group == fromHeader);
                    int exact = stops.FindIndex(x => x.Group == null && ReferenceEquals(x.Element, cur));
                    return exact >= 0 ? exact : stops.FindIndex(x => x.Group == null && x.Id == selectedId);
                }

                var anchor = FindAnchor();
                if (anchor == null) return false;
                var scrollViewer = FindVisualParent<ScrollViewer>(anchor);

                var ordered = CollectListStops(window, anchor);
                int idx = IndexOfCurrent(ordered, anchor);
                if (idx < 0) return false;
                int next = idx + delta;

                // End of what is currently realized: scroll a little so the virtualized list creates the
                // following items, then look again.
                if ((next < 0 || next >= ordered.Count) && scrollViewer != null)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (delta > 0) scrollViewer.LineDown(); else scrollViewer.LineUp();
                    }
                    try { scrollViewer.UpdateLayout(); } catch { }
                    anchor = FindAnchor();
                    if (anchor == null) return false;
                    ordered = CollectListStops(window, anchor);
                    idx = IndexOfCurrent(ordered, anchor);
                    if (idx < 0) return false;
                    next = idx + delta;
                }

                if (next < 0 || next >= ordered.Count)
                    return true; // genuinely the first / last stop: stay put

                var target = ordered[next];
                if (Breadcrumb.Enabled)
                {
                    int dupIds = ordered.Where(x => x.Group == null).GroupBy(x => x.Id).Count(g => g.Count() > 1);
                    Breadcrumb.Write("MoveListVisible: stops=" + ordered.Count + " idx=" + idx + " next=" + next +
                        " dupGameIds=" + dupIds + " scan=" + ((Stopwatch.GetTimestamp() - mlvStart) * 1000.0 / Stopwatch.Frequency).ToString("0.0") + "ms" +
                        " target=" + (target.Group != null ? "collapsed-group-header" : "game " + target.Id));
                }
                if (target.Group != null)
                {
                    // A collapsed group: stop on its header so it can be expanded (A / Right).
                    _currentGroup = target.Group;
                    _groupHeaderMode = true;
                    FocusGroupHeader(target.Group);
                    return true;
                }

                if (_groupHeaderMode) ExitGroupHeaderMode();
                long selT0 = Stopwatch.GetTimestamp();
                _api.MainView.SelectGame(target.Id);
                long selT1 = Stopwatch.GetTimestamp();
                try { target.Element.BringIntoView(); } catch { }
                long selT2 = Stopwatch.GetTimestamp();
                EnsureSingleSelection(target.Element as ListBoxItem);
                if (Breadcrumb.Enabled)
                {
                    double selMs = (selT1 - selT0) * 1000.0 / Stopwatch.Frequency, bivMs = (selT2 - selT1) * 1000.0 / Stopwatch.Frequency;
                    if (selMs + bivMs > 80) LogSlowSelect(target.Id, selMs, bivMs);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Why selecting this game was slow: Playnite loads and decodes its images on selection. Logs their sizes.</summary>
        private static void LogSlowSelect(Guid id, double selectMs, double bringMs)
        {
            try
            {
                var g = _api.Database.Games.Get(id);
                var sb = new StringBuilder();
                sb.Append("SlowSelect: SelectGame ").Append(selectMs.ToString("0")).Append(" ms, BringIntoView ").Append(bringMs.ToString("0"))
                  .Append(" ms for \"").Append(g != null ? g.Name : id.ToString()).Append("\"");
                if (g != null)
                {
                    foreach (var pair in new[] { new KeyValuePair<string, string>("background", g.BackgroundImage),
                                                 new KeyValuePair<string, string>("cover", g.CoverImage),
                                                 new KeyValuePair<string, string>("icon", g.Icon) })
                    {
                        if (string.IsNullOrEmpty(pair.Value)) { sb.Append(" | ").Append(pair.Key).Append(": none"); continue; }
                        string path = null;
                        try { path = _api.Database.GetFullFilePath(pair.Value); } catch { }
                        if (path == null || !System.IO.File.Exists(path)) { sb.Append(" | ").Append(pair.Key).Append(": not a local file"); continue; }
                        sb.Append(" | ").Append(pair.Key).Append(": ").Append(new System.IO.FileInfo(path).Length / 1024).Append(" KB");
                        try
                        {
                            var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path),
                                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation | System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                                System.Windows.Media.Imaging.BitmapCacheOption.None);
                            sb.Append(' ').Append(dec.Frames[0].PixelWidth).Append('x').Append(dec.Frames[0].PixelHeight);
                        }
                        catch { }
                    }
                }
                Breadcrumb.Write(sb.ToString());
            }
            catch { }
        }

        private static ListBox _navList;   // the games list found on the last step (avoids re-scanning the window)

        /// <summary>
        /// A pad step must leave exactly one game selected. The list can be in Extended mode, and a game listed in
        /// two groups (e.g. two platforms) gets both of its rows selected by SelectGame(id). Setting SelectedItem
        /// replaces the whole selection with just the row the pad is on.
        /// </summary>
        private static void EnsureSingleSelection(ListBoxItem item)
        {
            if (item == null) return;
            Defer(() =>
            {
                try
                {
                    var lb = FindVisualParent<ListBox>(item);
                    if (lb == null || lb.SelectedItems.Count <= 1) return;
                    lb.SelectedItem = item.DataContext;
                    Breadcrumb.Write("PadNav: collapsed multi-selection (" + lb.SelectedItems.Count + " rows) to one row");
                }
                catch { }
            });
        }

        /// <summary>Header mode on a collapsed group: Up/Down continue through games and other collapsed headers.</summary>
        private static bool MoveFromCollapsedHeader(int delta)
        {
            try
            {
                if (IsGridView()) return false;
                var group = ResolveCurrentGroup();
                if (group == null || !IsCollapsedGroup(group)) return false;
                return MoveListVisible(delta, group);
            }
            catch { return false; }
        }

        private static void MoveSelection(int delta, bool vertical)
        {
            try
            {
                if (_api?.MainView == null) return;

                if (!IsGridView() && MoveListVisible(delta)) return;
                Breadcrumb.Throttled("mlv-fallback", 500, "MoveSelection: list-view stepping unavailable, using collection-order fallback");

                List<Game> list = null;
                try { list = _api.MainView.FilteredGames; } catch { }
                if (list == null || list.Count == 0) return;

                int idx = 0;
                try
                {
                    var selected = _api.MainView.SelectedGames?.FirstOrDefault();
                    if (selected != null)
                    {
                        int found = -1;
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i].Id == selected.Id)
                            {
                                found = i;
                                break;
                            }
                        }
                        if (found >= 0)
                            idx = found;
                    }
                }
                catch { }

                int step = delta;
                try
                {
                    var view = _api.MainView.ActiveDesktopView;
                    if (vertical && view.ToString().IndexOf("Grid", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int cols = EstimateGridColumns();
                        step = delta * cols;
                    }
                }
                catch { }

                int next = idx + step;
                bool gridVertical = vertical && Math.Abs(step) > 1;
                if (next < 0) next = gridVertical ? idx : 0;                      // already on the top row
                if (next >= list.Count) next = gridVertical ? idx : list.Count - 1; // already on the last row

                _api.MainView.SelectGame(list[next].Id);
            }
            catch { }
        }

        private static int EstimateGridColumns()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out RECT rc))
                {
                    int width = rc.Right - rc.Left;
                    int content = Math.Max(400, width - 120);
                    int cols = content / 180;
                    if (cols < 3) cols = 3;
                    if (cols > 10) cols = 10;
                    return cols;
                }
            }
            catch { }
            return 5;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }
    }

    /// <summary>
    /// Gamepad control for window-style UI: this extension's settings windows (global and per-game) and,
    /// on Playnite Desktop, every other secondary Playnite window (Playnite settings, add-ons, edit game,
    /// message boxes, ...).
    ///   D-pad / stick  Up/Down: move between controls    Left/Right: slider value, or sideways to a neighbour
    ///                  Lists, trees and tab strips select the entry that receives focus
    ///   A: press / toggle / select / open a dropdown (again = confirm)
    ///   B: close dropdown, else press the window's Cancel button, else close the window
    ///   LB / RB: previous / next section header (or previous / next tab when the window has tabs)
    ///   LT / RT: jump to the top / to Save (or the default button)
    /// </summary>
    public static class SettingsPadNav
    {
        private static AdornerLayer _layer;
        private static Adorner _adorner;

        private sealed class FocusHighlight : Adorner
        {
            public FocusHighlight(UIElement el) : base(el) { IsHitTestVisible = false; }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                var r = new Rect(-2, -2, AdornedElement.RenderSize.Width + 4, AdornedElement.RenderSize.Height + 4);
                var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255));
                dc.DrawRoundedRectangle(fill, null, r, 4, 4);
            }
        }

        /// <summary>Rows of a long list: a soft fill only. The white outline is clipped by the list and shows as two stray lines.</summary>
        private sealed class RowHighlight : Adorner
        {
            private readonly double _radius;

            public RowHighlight(UIElement el, double radius = 0) : base(el) { IsHitTestVisible = false; _radius = radius; }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                var r = new Rect(0, 0, AdornedElement.RenderSize.Width, AdornedElement.RenderSize.Height);
                var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255));
                dc.DrawRoundedRectangle(fill, null, r, _radius, _radius);
            }
        }

        /// <summary>The window an element belongs to, also for elements inside a popup (dropdown, menu).</summary>
        private static Window OwnerWindow(DependencyObject d)
        {
            for (int guard = 0; d != null && guard < 128; guard++)
            {
                var win = d as Window;
                if (win != null) return win;

                DependencyObject next = null;
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                {
                    try { next = VisualTreeHelper.GetParent(d); } catch { }
                }
                if (next == null)
                {
                    try { next = LogicalTreeHelper.GetParent(d); } catch { }
                }
                if (next == null)
                {
                    var fe = d as FrameworkElement;
                    next = fe?.TemplatedParent ?? fe?.Parent;
                }
                var popup = d as System.Windows.Controls.Primitives.Popup;
                if (popup != null && next == null)
                    next = popup.PlacementTarget ?? popup.Parent;
                d = next;
            }
            return null;
        }

        /// <summary>
        /// The element with keyboard focus inside this window, or null. Focus that is stuck on something
        /// hidden, collapsed or disabled (a toggle just hid its section) counts as "no focus", so the next
        /// press re-seats the selection instead of silently doing nothing.
        /// </summary>
        private static DependencyObject FocusedIn(Window w)
        {
            var f = Keyboard.FocusedElement as DependencyObject;
            if (f == null) return null;
            try
            {
                if (!ReferenceEquals(OwnerWindow(f), w)) return null;
                // Keyboard focus often rests on the window itself (it takes it when activated, or when the focused
                // control disappears). There is nothing to press on it, and outlining it draws a white frame around
                // the whole window: treat it as "no focus" so the next press seats the selection on a real control.
                if (f is Window || f is UserControl || f is Page || f is Frame) return null;
                var fe = f as FrameworkElement;
                if (fe != null && !(fe is Window) && (!fe.IsVisible || !fe.IsEnabled)) return null;
                return f;
            }
            catch { return null; }
        }

        private static T SelfOrParent<T>(DependencyObject d) where T : DependencyObject
        {
            if (d == null) return null;
            var self = d as T;
            var found = self ?? DesktopControllerNav.FindVisualParent<T>(d);
            if (found != null) return found;

            // Items of an open dropdown live in a popup, outside the control's own visual tree.
            var item = d as ComboBoxItem ?? DesktopControllerNav.FindVisualParent<ComboBoxItem>(d);
            if (item != null) return ItemsControl.ItemsControlFromItemContainer(item) as T;
            return null;
        }

        private static ToggleButton HeaderOf(Expander ex)
        {
            return DesktopControllerNav.FindVisualChildren<ToggleButton>(ex).FirstOrDefault(t => t.IsVisible && t.IsEnabled);
        }

        private static void FocusElement(FrameworkElement el)
        {
            if (el == null) return;
            try { el.Focus(); Keyboard.Focus(el); } catch { }
            try { el.BringIntoView(); } catch { }
        }

        private static List<Button> UsableButtons(Window w)
        {
            return DesktopControllerNav.FindVisualChildren<Button>(w)
                .Where(b => DesktopControllerNav.IsUsable(b) && b.IsEnabled).ToList();
        }

        /// <summary>ButtonBase.OnClick: Click handlers, Command, and the check / toggle for CheckBox & friends.</summary>
        private static void Press(ButtonBase button)
        {
            if (button == null || !button.IsEnabled) return;
            DesktopControllerNav.Defer(() =>
            {
                var onClick = typeof(ButtonBase).GetMethod("OnClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (onClick != null) onClick.Invoke(button, null);
                else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            });
        }

        /// <summary>
        /// Windows with nothing to edit (message boxes, confirmations) start on their default button;
        /// everything else starts on the first section header or the first control.
        /// </summary>
        // ---- Info panels: not something to select ---------------------------------------------------
        // Big focusable containers (the game header in the achievements window ...) can take keyboard focus but
        // there is nothing to press on them; the outline around such a panel only looks wrong. The pad steps over them.

        private static bool IsPanelLike(DependencyObject d, ItemsControl contentList)
        {
            var fe = d as FrameworkElement;
            if (fe == null) return false;
            if (fe is TextBoxBase || fe is PasswordBox || fe is Selector || fe is ComboBox || fe is Slider ||
                fe is TreeView || fe is TreeViewItem || fe is TabItem || fe is Expander || fe is ToggleButton ||
                fe is ScrollViewer || fe is Window || fe is ListBoxItem || fe is DataGridRow || fe is GroupItem) return false;
            var win = Window.GetWindow(fe);
            if (win != null && win.GetType().Assembly == typeof(SettingsPadNav).Assembly) return false;   // this extension's own windows
            if (contentList != null && IsDescendantOf(fe, contentList)) return false;      // rows of the long list
            if (fe is ButtonBase && fe.ActualHeight < 80) return false;                    // ordinary buttons
            return fe.ActualWidth >= 300 && fe.ActualHeight >= 80;
        }

        /// <summary>Focus landed on an info panel: keep moving in the same direction (a few tries) to reach something usable.</summary>
        private static void SkipPanels(Window w, FocusNavigationDirection dir)
        {
            try
            {
                var list = FindContentList(w);
                for (int i = 0; i < 4; i++)
                {
                    var cur = FocusedIn(w);
                    if (cur == null || !IsPanelLike(cur, list)) return;
                    var ui = cur as UIElement;
                    if (ui == null || !ui.MoveFocus(new TraversalRequest(dir))) return;
                }
            }
            catch { }
        }

        private static void FocusTop(Window w)
        {
            ExitListMode();
            var ex = DesktopControllerNav.FindVisualChildren<Expander>(w).FirstOrDefault(e => DesktopControllerNav.IsUsable(e));
            var header = ex != null ? HeaderOf(ex) : null;
            if (header != null)
            {
                FocusElement(header);
                if (FocusedIn(w) != null) return;
            }

            // Tab strip: start on the selected tab. A tab-less TabControl (a page host) has no visible tab to land on:
            // focusing its hidden tab silently fails and used to leave the window itself focused.
            var tabs = DesktopControllerNav.FindVisualChildren<TabControl>(w).FirstOrDefault(t => DesktopControllerNav.IsUsable(t) && t.Items.Count > 1);
            if (tabs != null)
            {
                var tab = tabs.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, tabs.SelectedIndex)) as TabItem;
                if (tab != null && DesktopControllerNav.IsUsable(tab))
                {
                    FocusElement(tab);
                    if (FocusedIn(w) != null) return;
                }
            }

            // Settings-style window: start on the selected section of its section tree.
            var tree = FindSidebar(w) as TreeView;
            if (tree != null && FocusSidebar(w, tree)) return;

            bool hasInputs =
                DesktopControllerNav.FindVisualChildren<Selector>(w).Any(c => DesktopControllerNav.IsUsable(c)) ||
                DesktopControllerNav.FindVisualChildren<TreeView>(w).Any(c => DesktopControllerNav.IsUsable(c)) ||
                DesktopControllerNav.FindVisualChildren<TextBoxBase>(w).Any(c => DesktopControllerNav.IsUsable(c)) ||
                DesktopControllerNav.FindVisualChildren<Slider>(w).Any(c => DesktopControllerNav.IsUsable(c)) ||
                DesktopControllerNav.FindVisualChildren<ToggleButton>(w).Any(c => DesktopControllerNav.IsUsable(c) && c.IsEnabled);
            if (!hasInputs)
            {
                var def = UsableButtons(w).FirstOrDefault(b => b.IsDefault) ?? UsableButtons(w).FirstOrDefault();
                if (def != null) { FocusElement(def); return; }
            }

            try { w.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); } catch { }
            SkipPanels(w, FocusNavigationDirection.Next);

            // Last resort: never leave the pad without a selection.
            if (FocusedIn(w) == null) FocusAnyControl(w);
        }

        /// <summary>The topmost-leftmost control the pad can use (default / cancel buttons included).</summary>
        private static void FocusAnyControl(Window w)
        {
            try
            {
                FrameworkElement best = null;
                double bestY = double.MaxValue, bestX = double.MaxValue;
                foreach (var fe in DesktopControllerNav.FindVisualChildren<FrameworkElement>(w))
                {
                    if (!IsContentCandidate(fe, true)) continue;
                    var p = DesktopControllerNav.GetPosition(fe, w);
                    if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                    if (p.Y < bestY - 0.5 || (Math.Abs(p.Y - bestY) <= 0.5 && p.X < bestX))
                    {
                        best = fe; bestY = p.Y; bestX = p.X;
                    }
                }
                if (best != null) FocusElement(best);
            }
            catch { }
        }

        /// <summary>Menu-style rows (wide buttons, toggles, expander headers, list items): highlighted with a soft fill, no outline.</summary>
        private static bool IsMenuRow(FrameworkElement fe)
        {
            if (fe == null) return false;
            if (fe is ComboBox || fe is TextBoxBase || fe is PasswordBox || fe is Slider || fe is TabItem) return false;
            bool rowType = fe is ButtonBase || fe is Expander || fe is ListBoxItem || fe is TreeViewItem || fe is MenuItem;
            return rowType && fe.ActualWidth >= 200 && fe.ActualHeight >= 24;
        }

        private static void Highlight(Window w) { HighlightElement(w, null); }

        /// <summary>True for elements an outline must not be drawn around: the window, a page host, a whole-page scroller.</summary>
        private static bool IsOversizedOutline(FrameworkElement fe, Window w)
        {
            if (fe is Window || fe is ScrollViewer || fe is UserControl || fe is Page || fe is Frame || fe is TabControl) return true;
            if (fe is TextBoxBase || fe is Selector || fe is TreeView) return false;     // big on purpose (editors, lists)
            return w != null && w.ActualWidth > 0 && w.ActualHeight > 0 &&
                   fe.ActualWidth >= w.ActualWidth * 0.85 && fe.ActualHeight >= w.ActualHeight * 0.6;
        }

        private static void HighlightElement(Window w, FrameworkElement target, bool row = false)
        {
            // Layout (scrolling into view) has to settle before the outline is placed.
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Clear();
                    if (!PadInput.Active) return;
                    var fe = target ?? (FocusedIn(w) as FrameworkElement);
                    if (fe == null || !fe.IsVisible) return;
                    if (target == null && IsPanelLike(fe, FindContentList(w))) return;   // never outline an info panel
                    if (!row && IsOversizedOutline(fe, w)) return;                        // never frame a whole window / page
                    var layer = AdornerLayer.GetAdornerLayer(fe);
                    if (layer == null) return;
                    if (row) _adorner = new RowHighlight(fe);
                    else if (IsMenuRow(fe)) _adorner = new RowHighlight(fe, 6);
                    else _adorner = new FocusHighlight(fe);
                    layer.Add(_adorner);
                    _layer = layer;
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void Clear()
        {
            try { if (_layer != null && _adorner != null) _layer.Remove(_adorner); } catch { }
            _layer = null;
            _adorner = null;
        }

        /// <summary>The mouse / keyboard took over: remove the controller outline (on the UI thread).</summary>
        internal static void HideForKeyboardMouse()
        {
            try
            {
                var d = Application.Current != null ? Application.Current.Dispatcher : null;
                Action a = () => { try { Clear(); ExitListMode(); } catch { } };
                if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
            }
            catch { }
        }

        /// <summary>
        /// Settings-style sidebars (tab strips, single-select lists, trees) switch page when an entry is
        /// selected, and a pad cannot "click" them: select whatever just received focus.
        /// </summary>
        private static TreeViewItem _lastSelectedTvi;

        // Selecting a section makes the host window load that section's page, and the page often grabs keyboard
        // focus while it loads: the pad then suddenly drove the page without Right / A having been pressed.
        // While the pad rests on the section list, focus that wanders into the page is put back.
        private static TreeViewItem _holdTvi;

        private static void HoldSidebarFocus(Window w, TreeViewItem tvi)
        {
            _holdTvi = tvi;
            foreach (int ms in new[] { 60, 180, 400, 800, 1400 })
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    try
                    {
                        if (!ReferenceEquals(_holdTvi, tvi) || w == null || !w.IsVisible || !PadInput.Active) return;
                        var cur = FocusedIn(w);
                        var tree = DesktopControllerNav.FindVisualParent<TreeView>(tvi);
                        if (tree == null || !DesktopControllerNav.IsUsable(tvi)) return;
                        if (cur != null && IsDescendantOf(cur, tree)) return;      // still on the section list
                        FocusElement(tvi);
                        Highlight(w);
                    }
                    catch { }
                };
                t.Start();
            }
        }

        /// <summary>Raise a left click on the middle of the element's header, as a mouse would.</summary>
        private static void ClickLikeMouse(FrameworkElement el)
        {
            try
            {
                if (el == null || !DesktopControllerNav.IsUsable(el)) return;
                FrameworkElement host = null;
                var tvi = el as TreeViewItem;
                if (tvi != null)
                {
                    try { host = tvi.Template != null ? tvi.Template.FindName("PART_Header", tvi) as FrameworkElement : null; }
                    catch { }
                }
                bool header = host != null && DesktopControllerNav.IsUsable(host);
                if (!header) host = el;

                // An expanded tree item also contains its children: aim at its own header row.
                double y = header ? host.ActualHeight / 2 : Math.Min(host.ActualHeight / 2, 12);
                UIElement target = null;
                try { target = host.InputHitTest(new Point(Math.Min(host.ActualWidth / 2, 80), y)) as UIElement; } catch { }
                if (target == null) target = host;

                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = target };
                target.RaiseEvent(down);
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = target };
                target.RaiseEvent(up);
            }
            catch { }
        }

        private static void SelectFocused(Window w)
        {
            try
            {
                var f = FocusedIn(w);
                var tab = f as TabItem;
                if (tab != null) { tab.IsSelected = true; return; }

                var tvi = SelfOrParent<TreeViewItem>(f);
                if (tvi != null)
                {
                    bool changed = !ReferenceEquals(_lastSelectedTvi, tvi);
                    tvi.IsSelected = true;
                    if (changed)
                    {
                        _lastSelectedTvi = tvi;
                        try
                        {
                            Breadcrumb.Write("PadSettings: tree item " + tvi.GetType().Name + " focused=" + f.GetType().Name +
                                             " selected=" + tvi.IsSelected);
                        }
                        catch { }
                        // Playnite's settings tree switches the page from mouse input, which IsSelected alone does not
                        // reproduce: click the item's header as well.
                        DesktopControllerNav.Defer(() => ClickLikeMouse(tvi));
                    }
                    HoldSidebarFocus(w, tvi);
                    return;
                }

                var lbi = f as ListBoxItem;
                if (lbi != null)
                {
                    var lb = ItemsControl.ItemsControlFromItemContainer(lbi) as ListBox;
                    if (lb != null && lb.SelectionMode == SelectionMode.Single && lbi.IsEnabled)
                        lbi.IsSelected = true;
                }
            }
            catch { }
        }

        /// <summary>Nothing left to focus in that direction: scroll the window's main scroll area instead.</summary>
        /// <summary>
        /// Focus a real control inside a scroll area: coming from below (Up) the lowest one in view, otherwise the topmost.
        /// </summary>
        private static bool FocusInsideScrollViewer(Window w, ScrollViewer sv, FocusNavigationDirection dir)
        {
            try
            {
                var svPos = DesktopControllerNav.GetPosition(sv, w);
                var all = new List<FrameworkElement>();
                var inView = new List<FrameworkElement>();
                foreach (var fe in DesktopControllerNav.FindVisualChildren<FrameworkElement>(sv))
                {
                    if (!IsContentCandidate(fe, true)) continue;
                    all.Add(fe);
                    var p = DesktopControllerNav.GetPosition(fe, w);
                    if (double.IsNaN(p.Y)) continue;
                    if (p.Y >= svPos.Y - 1 && p.Y + fe.ActualHeight <= svPos.Y + sv.ActualHeight + 1) inView.Add(fe);
                }
                var pool = inView.Count > 0 ? inView : all;
                if (pool.Count == 0) return false;
                bool wantLowest = dir == FocusNavigationDirection.Up;
                FrameworkElement best = null;
                double bestY = wantLowest ? double.MinValue : double.MaxValue, bestX = double.MaxValue;
                foreach (var fe in pool)
                {
                    var p = DesktopControllerNav.GetPosition(fe, w);
                    if (double.IsNaN(p.Y)) continue;
                    bool better = wantLowest ? (p.Y > bestY + 0.5 || (Math.Abs(p.Y - bestY) <= 0.5 && p.X < bestX))
                                             : (p.Y < bestY - 0.5 || (Math.Abs(p.Y - bestY) <= 0.5 && p.X < bestX));
                    if (better) { best = fe; bestY = p.Y; bestX = p.X; }
                }
                if (best == null) return false;
                FocusElement(best);
                bool ok = FocusedIn(w) != null && !(FocusedIn(w) is ScrollViewer);
                if (Breadcrumb.Enabled) Breadcrumb.Write("FocusInsideScrollViewer " + dir + " -> " + Describe(best, w) + " ok=" + ok);
                return ok;
            }
            catch { return false; }
        }

        /// <summary>Nearest usable control strictly in the given direction (by centre), moving focus onto it.</summary>
        private static bool FocusNearest(Window w, FrameworkElement origin, FocusNavigationDirection dir)
        {
            try
            {
                var fp = DesktopControllerNav.GetPosition(origin, w);
                if (double.IsNaN(fp.X) || double.IsNaN(fp.Y)) return false;
                double cx = fp.X + origin.ActualWidth / 2, cy = fp.Y + origin.ActualHeight / 2;

                FrameworkElement best = null;
                double bestScore = double.MaxValue;
                foreach (var fe in DesktopControllerNav.FindVisualChildren<FrameworkElement>(w))
                {
                    if (ReferenceEquals(fe, origin) || IsDescendantOf(fe, origin) || IsDescendantOf(origin, fe)) continue;
                    if (!IsContentCandidate(fe, true)) continue;
                    var p = DesktopControllerNav.GetPosition(fe, w);
                    if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                    double dx = p.X + fe.ActualWidth / 2 - cx;
                    double dy = p.Y + fe.ActualHeight / 2 - cy;

                    double along, across;
                    switch (dir)
                    {
                        case FocusNavigationDirection.Up:    along = -dy; across = Math.Abs(dx); break;
                        case FocusNavigationDirection.Down:  along = dy;  across = Math.Abs(dx); break;
                        case FocusNavigationDirection.Left:  along = -dx; across = Math.Abs(dy); break;
                        case FocusNavigationDirection.Right: along = dx;  across = Math.Abs(dy); break;
                        default: return false;
                    }
                    if (along < 2) continue;                       // not in that direction
                    double score = along + across * 2.5;           // prefer what is straight ahead
                    if (score < bestScore) { bestScore = score; best = fe; }
                }
                if (Breadcrumb.Enabled)
                    Breadcrumb.Write("FocusNearest " + dir + " from " + Describe(origin, w) + " -> " + (best != null ? Describe(best, w) + " score=" + bestScore.ToString("0") : "no candidate"));
                if (best == null) return false;
                FocusElement(best);                                // also scrolls it into view
                return FocusedIn(w) != null && !ReferenceEquals(FocusedIn(w), origin);
            }
            catch { return false; }
        }

        private static void ScrollFallback(Window w, int sign)
        {
            try
            {
                var sv = DesktopControllerNav.FindVisualChildren<ScrollViewer>(w)
                    .FirstOrDefault(v => DesktopControllerNav.IsUsable(v) && v.ScrollableHeight > 0);
                if (sv == null) return;
                for (int i = 0; i < 3; i++)
                {
                    if (sign > 0) sv.LineDown(); else sv.LineUp();
                }
            }
            catch { }
        }

        /// <summary>
        /// Called once when a window opens while the pad is in use: puts the selection on the first
        /// control (or section header) and draws the outline, so no button press is needed to start.
        /// </summary>
        public static void SelectInitial(Window w)
        {
            PadInput.NotePad();
            try
            {
                if (w == null || !w.IsVisible) return;

                // Settings-style window (section list on the left, page on the right): start on the section list.
                // Starting in the page's list instead left the pad inside the page, and the section list only
                // answered after a D-pad Left press.
                var startSide = FindSidebar(w);
                if (startSide != null)
                {
                    _startedWindow = new WeakReference(w);
                    ExitListMode();
                    if (FocusSidebar(w, startSide)) { SelectFocused(w); Highlight(w); return; }
                }

                // Windows built around a long list (the achievements window ...): start on its first row,
                // not on the header / buttons above it.
                else if (TryStartInList(w)) return;
                if (startSide == null) ScheduleListStart(w, 0);

                var f = FocusedIn(w) as FrameworkElement;
                // Keep focus where the window put it only when it is on something worth navigating from.
                bool onNavigable = f is ButtonBase || f is Selector || f is ListBoxItem || f is TreeViewItem || f is TabItem || f is Expander || f is Slider;
                if (!onNavigable) FocusTop(w);
                else FocusElement(f);
                SelectFocused(w);
                Highlight(w);
            }
            catch { }
        }

        /// <summary>Put the cursor on the first visible row of the window's main list. False = no usable list (yet).</summary>
        private static WeakReference _startedWindow;

        private static bool StartedOn(Window w)
        {
            return _startedWindow != null && ReferenceEquals(_startedWindow.Target, w);
        }

        private static bool TryStartInList(Window w)
        {
            try
            {
                var list = FindContentList(w);
                if (list == null || list.Items.Count == 0)
                {
                    try { Breadcrumb.Throttled("padlist-none", 5000, "PadList: no content list in '" + w.GetType().Name + "' (yet)"); } catch { }
                    return false;
                }
                list.UpdateLayout();
                SetListRow(w, list, FirstVisibleRow(w, list));
                bool ok = ListModeActive(w);
                if (ok)
                {
                    _startedWindow = new WeakReference(w);
                    // The old start (FocusTop) also made the window the active one, which is what routes the pad
                    // to it. Starting in the list must do the same, or the pad keeps driving the library window
                    // until the window is clicked with the mouse.
                    try { if (!w.IsActive) w.Activate(); } catch { }
                    try
                    {
                        var row = RowContainer(list, _listIdx);
                        if (row != null && row.Focusable) FocusElement(row);
                        else
                        {
                            var inner = row != null ? DesktopControllerNav.FindVisualChildren<FrameworkElement>(row).FirstOrDefault(x => x.Focusable && x.IsVisible) : null;
                            if (inner != null) FocusElement(inner);
                            else if (list.Focusable) FocusElement(list);
                            else Keyboard.Focus(w);
                        }
                    }
                    catch { }
                    // Focus moves may repaint / re-place the outline: draw it for the row again.
                    try { SetListRow(w, list, _listIdx); } catch { }
                }
                try
                {
                    var chain = new System.Text.StringBuilder();
                    DependencyObject up = list;
                    for (int i = 0; i < 4 && up != null; i++)
                    {
                        up = VisualTreeHelper.GetParent(up);
                        if (up != null) chain.Append(" < ").Append(up.GetType().Name);
                    }
                    Breadcrumb.Write("PadList: " + list.GetType().Name + " rows=" + list.Items.Count +
                        " size=" + (int)list.ActualWidth + "x" + (int)list.ActualHeight + chain + " started=" + ok);
                }
                catch { }
                return ok;
            }
            catch { return false; }
        }

        /// <summary>
        /// First pad press in a window that has a long list (achievements ...): land on the first row instead of
        /// wherever the window happened to put focus, or the top bar. Only once per window.
        /// </summary>
        private static bool FirstTouchEnterList(Window w)
        {
            if (StartedOn(w)) return false;
            // A window with a section list on the left starts on that list (the caller then seats the selection).
            if (FindSidebar(w) != null) { _startedWindow = new WeakReference(w); return false; }
            return TryStartInList(w);
        }

        /// <summary>
        /// The list is often filled after the window opens (achievements load asynchronously). Retry for a few
        /// seconds, but only while the pad has not moved on its own, so it never steals the selection.
        /// </summary>
        private static void ScheduleListStart(Window w, int attempt)
        {
            if (attempt >= 8) return;
            try
            {
                var focusAtStart = FocusedIn(w);
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    try
                    {
                        if (w == null || !w.IsVisible || _listCtl != null) return;
                        if (FindSidebar(w) != null) return;                          // section list: never jump into the page
                        if (!ReferenceEquals(FocusedIn(w), focusAtStart)) return;   // user already navigated
                        if (TryStartInList(w)) return;
                        ScheduleListStart(w, attempt + 1);
                    }
                    catch { }
                };
                t.Start();
            }
            catch { }
        }

        // ---- Item lists (achievements, long result lists ...) ----------------------------------------
        // Rows of a long list are often not keyboard-focusable, so directional focus cannot walk them.
        // A virtual cursor does instead: Down from the control above enters the list, Up / Down step
        // through the rows, LB / RB jump 8 rows, A selects (and clicks) the row, B / Left / Right leave.

        private static ItemsControl _listCtl;
        private static int _listIdx;

        private static void ExitListMode() { _listCtl = null; _listIdx = 0; }

        private static bool ListModeActive(Window w)
        {
            if (_listCtl == null) return false;
            try
            {
                if (DesktopControllerNav.IsUsable(_listCtl) && ReferenceEquals(OwnerWindow(_listCtl), w) && _listCtl.Items.Count > 0)
                    return true;
            }
            catch { }
            ExitListMode();
            return false;
        }

        private static DependencyObject ParentOf(DependencyObject d)
        {
            DependencyObject n = null;
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
            {
                try { n = VisualTreeHelper.GetParent(d); } catch { }
            }
            if (n == null)
            {
                try { n = LogicalTreeHelper.GetParent(d); } catch { }
            }
            return n;
        }

        private static bool IsDescendantOf(DependencyObject d, DependencyObject ancestor)
        {
            for (int guard = 0; d != null && guard < 256; guard++)
            {
                if (ReferenceEquals(d, ancestor)) return true;
                d = ParentOf(d);
            }
            return false;
        }

        /// <summary>Index of the row of <paramref name="list"/> that contains <paramref name="d"/>, or -1.</summary>
        private static int IndexOfRow(ItemsControl list, DependencyObject d)
        {
            for (int guard = 0; d != null && guard < 256; guard++)
            {
                int i = list.ItemContainerGenerator.IndexFromContainer(d);
                if (i >= 0) return i;
                d = ParentOf(d);
            }
            return -1;
        }

        /// <summary>The window's main long list (largest ItemsControl with enough rows). Not used in this extension's own windows.</summary>
        private static ItemsControl FindContentList(Window w)
        {
            try
            {
                if (w.GetType().Assembly == typeof(SettingsPadNav).Assembly) return null;
                ItemsControl best = null;
                double bestArea = 0;
                foreach (var ic in DesktopControllerNav.FindVisualChildren<ItemsControl>(w))
                {
                    if (ic is ComboBox || ic is TabControl || ic is TreeView || ic is MenuBase || ic is ToolBar || ic is StatusBar) continue;
                    // Parts of a DataGrid (the cells of one row, the rows panel, the header strip) are ItemsControls
                    // too, but they are not the list: only the DataGrid itself is.
                    if (!(ic is DataGrid) && ic.GetType().Name.StartsWith("DataGrid", StringComparison.Ordinal)) continue;
                    // Anything inside a row of another list (tag chips, icons ...) is not the main list either.
                    if (DesktopControllerNav.FindVisualParent<DataGridRow>(ic) != null ||
                        DesktopControllerNav.FindVisualParent<ListBoxItem>(ic) != null ||
                        DesktopControllerNav.FindVisualParent<TreeViewItem>(ic) != null) continue;
                    if (!DesktopControllerNav.IsUsable(ic)) continue;
                    int n;
                    try { n = ic.Items.Count; } catch { continue; }
                    if (n < 6 || ic.ActualHeight < 150) continue;
                    double area = ic.ActualWidth * ic.ActualHeight;
                    if (area > bestArea) { best = ic; bestArea = area; }
                }
                return best;
            }
            catch { return null; }
        }

        private static FrameworkElement RowContainer(ItemsControl list, int idx)
        {
            var c = list.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
            if (c != null) return c;
            try
            {
                object item = list.Items[idx];
                var lb = list as ListBox;
                if (lb != null) lb.ScrollIntoView(item);
                else
                {
                    var dg = list as DataGrid;
                    if (dg != null) dg.ScrollIntoView(item);
                }
                list.UpdateLayout();
            }
            catch { }
            return list.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
        }

        private static void SetListRow(Window w, ItemsControl list, int idx)
        {
            int n = list.Items.Count;
            if (n == 0) return;
            idx = Math.Max(0, Math.Min(n - 1, idx));
            // Remember the row first: when its container is not built yet (fast scrolling), list mode must survive
            // and the outline be placed a moment later, not be lost.
            _listCtl = list;
            _listIdx = idx;
            var c = RowContainer(list, idx);
            if (c != null) { try { c.BringIntoView(); } catch { } }
            HighlightListRow(w, list, idx, 0);
        }

        private static int _rowHlSeq;

        /// <summary>
        /// Outline for a row of the long list, placed once layout has settled. It looks the row's container up when it
        /// runs (virtualization recycles containers while scrolling, so one captured earlier may now show another row
        /// or be gone), only the newest request runs, and the previous outline stays until the new one is ready.
        /// </summary>
        private static void HighlightListRow(Window w, ItemsControl list, int idx, int attempt)
        {
            int seq = ++_rowHlSeq;
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (seq != _rowHlSeq) return;                         // a newer step took over
                    if (!PadInput.Active || !ReferenceEquals(_listCtl, list) || _listIdx != idx) return;
                    var fe = list.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
                    if (fe == null || !fe.IsVisible)
                    {
                        if (attempt < 6)
                        {
                            try
                            {
                                var lb = list as ListBox;
                                if (lb != null) lb.ScrollIntoView(list.Items[idx]);
                                else { var dg = list as DataGrid; if (dg != null) dg.ScrollIntoView(list.Items[idx]); }
                            }
                            catch { }
                            HighlightListRow(w, list, idx, attempt + 1);
                        }
                        else Breadcrumb.Write("PadList: row " + idx + " never got a container (list has " + list.Items.Count + " rows)");
                        return;
                    }
                    if (_adorner != null && ReferenceEquals(_adorner.AdornedElement, fe)) return;   // already outlined
                    Clear();
                    var layer = AdornerLayer.GetAdornerLayer(fe);
                    if (layer == null) return;
                    _adorner = new RowHighlight(fe);
                    layer.Add(_adorner);
                    _layer = layer;
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static int FirstVisibleRow(Window w, ItemsControl list)
        {
            double top = DesktopControllerNav.GetPosition(list, w).Y;
            int n = list.Items.Count;
            for (int i = 0; i < n; i++)
            {
                var c = list.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (c == null || !DesktopControllerNav.IsUsable(c)) continue;
                var p = DesktopControllerNav.GetPosition(c, w);
                if (double.IsNaN(p.Y)) continue;
                if (p.Y + c.ActualHeight / 2 >= top) return i;
            }
            return 0;
        }

        /// <summary>Up / Down while the cursor is in a list. False = not in list mode.</summary>
        private static bool ListModeMove(Window w, int sign)
        {
            if (!ListModeActive(w)) return false;
            var list = _listCtl;
            int ni = _listIdx + sign;
            if (ni < 0)
            {
                // Above the first row: leave the list upwards.
                ExitListMode();
                try { list.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up)); } catch { }
                Highlight(w);
                return true;
            }
            if (ni >= list.Items.Count) return true;
            SetListRow(w, list, ni);
            return true;
        }

        /// <summary>After a directional focus move: did we run into the list (or stop right above it)? Then enter it.</summary>
        private static bool TryEnterList(Window w, ItemsControl list, DependencyObject from, bool moved, FocusNavigationDirection dir)
        {
            try
            {
                var nf = FocusedIn(w);
                bool intoList = nf != null && IsDescendantOf(nf, list);
                bool stuckAbove = false;
                if (!moved && dir == FocusNavigationDirection.Down)
                {
                    var fe = from as FrameworkElement;
                    if (fe != null)
                    {
                        var pf = DesktopControllerNav.GetPosition(fe, w);
                        var pl = DesktopControllerNav.GetPosition(list, w);
                        stuckAbove = pf.Y + fe.ActualHeight <= pl.Y + 8;
                    }
                }
                if (!intoList && !stuckAbove) return false;
                SetListRow(w, list, FirstVisibleRow(w, list));
                return true;
            }
            catch { return false; }
        }

        private static bool ListModeConfirm(Window w)
        {
            if (!ListModeActive(w)) return false;
            var list = _listCtl;
            int idx = _listIdx;
            var row = RowContainer(list, idx);
            if (row == null) return true;
            DesktopControllerNav.Defer(() =>
            {
                try
                {
                    var sel = list as Selector;
                    if (sel != null) sel.SelectedIndex = idx;
                    var item = row as ListBoxItem;
                    if (item != null) item.IsSelected = true;

                    // A real click on whatever sits in the middle of the row, for rows that react to the
                    // mouse (details, toggles) rather than to selection.
                    UIElement target = null;
                    try { target = row.InputHitTest(new Point(row.ActualWidth / 2, row.ActualHeight / 2)) as UIElement; } catch { }
                    if (target == null) target = row;
                    var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = target };
                    target.RaiseEvent(down);
                    var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = target };
                    target.RaiseEvent(up);
                }
                catch { }
            });
            return true;
        }

        /// <summary>LB / RB while the cursor is in a list: jump 8 rows.</summary>
        private static bool ListPage(Window w, int delta)
        {
            if (!ListModeActive(w)) return false;
            SetListRow(w, _listCtl, _listIdx + delta * 8);
            return true;
        }

        // ---- Section list (sidebar) <-> page ---------------------------------------------------------
        // Settings-style windows (Playnite's own settings, the add-ons window ...) have a list of sections on the left
        // (tree, list or tab strip) and the selected section's page on the right. WPF keeps directional focus
        // inside such a list ("Contained"), so the pad could walk the sections but Right / A never reached the page.
        // Right / A now hand the selection over to the first control of the page, and Left brings it back.

        /// <summary>A section list sits in the left part of the window and is clearly narrower than the page next to it.</summary>
        private static bool IsSidebarLike(Window w, FrameworkElement root)
        {
            try
            {
                if (root == null || w == null || !DesktopControllerNav.IsUsable(root) || w.ActualWidth < 200) return false;
                var p = DesktopControllerNav.GetPosition(root, w);
                if (double.IsNaN(p.X)) return false;
                return root.ActualWidth <= w.ActualWidth * 0.5 && (p.X + root.ActualWidth / 2) <= w.ActualWidth * 0.4;
            }
            catch { return false; }
        }

        /// <summary>The section list the focused element belongs to (tree, left tab strip, narrow list), or null.</summary>
        private static FrameworkElement SidebarRootOf(Window w, DependencyObject f)
        {
            try
            {
                if (f == null) return null;

                var tvi = SelfOrParent<TreeViewItem>(f);
                if (tvi != null)
                {
                    var tree = DesktopControllerNav.FindVisualParent<TreeView>(tvi);
                    return IsSidebarLike(w, tree) ? tree : null;
                }

                var tab = f as TabItem;
                if (tab != null)
                {
                    var tabControl = ItemsControl.ItemsControlFromItemContainer(tab) as TabControl;
                    if (tabControl != null && tabControl.TabStripPlacement == Dock.Left)
                    {
                        var panel = DesktopControllerNav.FindVisualParent<TabPanel>(tab);
                        if (panel != null && DesktopControllerNav.IsUsable(panel)) return panel;
                    }
                    return null;
                }

                var lbi = f as ListBoxItem;
                if (lbi != null)
                {
                    var lb = ItemsControl.ItemsControlFromItemContainer(lbi) as ListBox;
                    if (lb != null && !ReferenceEquals(lb, FindContentList(w)) && IsSidebarLike(w, lb)) return lb;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The window's section list (tree, left tab strip or narrow single-select list), or null.</summary>
        private static FrameworkElement FindSidebar(Window w)
        {
            try
            {
                foreach (var tree in DesktopControllerNav.FindVisualChildren<TreeView>(w))
                    if (tree.HasItems && IsSidebarLike(w, tree)) return tree;

                foreach (var tc in DesktopControllerNav.FindVisualChildren<TabControl>(w))
                {
                    if (!DesktopControllerNav.IsUsable(tc) || tc.TabStripPlacement != Dock.Left || tc.Items.Count < 2) continue;
                    var panel = DesktopControllerNav.FindVisualChildren<TabPanel>(tc).FirstOrDefault(x => DesktopControllerNav.IsUsable(x));
                    if (panel != null) return panel;
                }

                var content = FindContentList(w);
                foreach (var lb in DesktopControllerNav.FindVisualChildren<ListBox>(w))
                {
                    if (ReferenceEquals(lb, content) || lb.Items.Count == 0 || lb.SelectionMode != SelectionMode.Single) continue;
                    if (!IsSidebarLike(w, lb)) continue;
                    if (DesktopControllerNav.FindVisualParent<ListBoxItem>(lb) != null ||
                        DesktopControllerNav.FindVisualParent<TreeViewItem>(lb) != null) continue;
                    return lb;
                }
            }
            catch { }
            return null;
        }

        private static TreeViewItem FindSelectedTreeItem(ItemsControl host)
        {
            for (int i = 0; i < host.Items.Count; i++)
            {
                var c = host.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (c == null) continue;
                if (c.IsSelected) return c;
                if (c.IsExpanded && c.HasItems)
                {
                    var inner = FindSelectedTreeItem(c);
                    if (inner != null) return inner;
                }
            }
            return null;
        }

        /// <summary>
        /// The selectable entries of a tree in display order: expanded groups contribute their children,
        /// separators, hidden and disabled entries are left out.
        /// </summary>
        private static void CollectTreeEntries(ItemsControl host, List<TreeViewItem> into, int depth)
        {
            if (host == null || depth > 8) return;
            for (int i = 0; i < host.Items.Count; i++)
            {
                var c = host.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (c == null || c.Visibility != Visibility.Visible) continue;
                // A Separator placed straight into a TreeView is wrapped in a TreeViewItem of its own.
                bool selectable = !(c.Header is Separator) && c.IsEnabled && c.Focusable;
                if (selectable) into.Add(c);
                if (c.IsExpanded && c.HasItems) CollectTreeEntries(c, into, depth + 1);
            }
        }

        /// <summary>Up / Down in a tree: move to the previous / next visible entry. False = not handled here.</summary>
        private static bool StepTree(Window w, TreeViewItem from, int sign)
        {
            try
            {
                var tree = DesktopControllerNav.FindVisualParent<TreeView>(from);
                if (tree == null) return false;
                var entries = new List<TreeViewItem>();
                CollectTreeEntries(tree, entries, 0);
                int idx = entries.IndexOf(from);
                if (idx < 0)
                {
                    try { Breadcrumb.Write("PadSettings: tree step - focused entry not in the visible list (" + entries.Count + " entries)"); } catch { }
                    return false;
                }
                int ni = idx + sign;
                if (ni < 0 || ni >= entries.Count) return true;      // first / last entry: stay
                FocusElement(entries[ni]);
                if (FocusedIn(w) == null) return false;
                SelectFocused(w);
                Highlight(w);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Put the selection back on the section list (on its selected entry).</summary>
        private static bool FocusSidebar(Window w, FrameworkElement side)
        {
            try
            {
                FrameworkElement target = null;
                var tree = side as TreeView;
                if (tree != null)
                {
                    target = FindSelectedTreeItem(tree);
                    if (target == null || !DesktopControllerNav.IsUsable(target))
                        target = DesktopControllerNav.FindVisualChildren<TreeViewItem>(tree).FirstOrDefault(x => DesktopControllerNav.IsUsable(x));
                }
                else if (side is TabPanel)
                {
                    var tc = DesktopControllerNav.FindVisualParent<TabControl>(side);
                    if (tc != null) target = tc.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, tc.SelectedIndex)) as TabItem;
                }
                else
                {
                    var lb = side as ListBox;
                    if (lb != null)
                    {
                        FrameworkElement sel = lb.SelectedIndex >= 0
                            ? lb.ItemContainerGenerator.ContainerFromIndex(lb.SelectedIndex) as FrameworkElement
                            : null;
                        target = sel ?? (lb.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement);
                    }
                }

                if (target == null || !DesktopControllerNav.IsUsable(target)) return false;
                ExitListMode();
                FocusElement(target);
                if (FocusedIn(w) == null) return false;
                Highlight(w);
                return true;
            }
            catch { return false; }
        }

        /// <summary>A control the pad can move onto: buttons, toggles, boxes, lists, sliders - not parts of other controls.</summary>
        private static bool IsContentCandidate(FrameworkElement fe, bool includeDefaultButtons)
        {
            try
            {
                if (fe == null || !fe.Focusable || !fe.IsEnabled || !DesktopControllerNav.IsUsable(fe)) return false;
                if (fe is TabControl) return false;
                bool typeOk = fe is ButtonBase || fe is Selector || fe is TextBoxBase || fe is PasswordBox || fe is Slider || fe is TreeView;
                if (!typeOk) return false;
                if (fe is RepeatButton || fe is DataGridColumnHeader || fe is GridViewColumnHeader) return false;
                var btn = fe as Button;
                if (btn != null && !includeDefaultButtons && (btn.IsDefault || btn.IsCancel)) return false;
                if (!(fe is ComboBox) && DesktopControllerNav.FindVisualParent<ComboBox>(fe) != null) return false;
                if (DesktopControllerNav.FindVisualParent<ScrollBar>(fe) != null) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The first thing to select on the page right of the section list: its topmost control (or the window's long
        /// list, when that comes first). Save / Cancel are not page content.
        /// </summary>
        private static FrameworkElement FindContentTarget(Window w, FrameworkElement sidebar, out bool isList)
        {
            isList = false;
            var sp = DesktopControllerNav.GetPosition(sidebar, w);
            if (double.IsNaN(sp.X)) return null;
            double rightEdge = sp.X + sidebar.ActualWidth;
            var content = FindContentList(w);

            FrameworkElement best = null;
            double bestY = double.MaxValue, bestX = double.MaxValue;
            foreach (var fe in DesktopControllerNav.FindVisualChildren<FrameworkElement>(w))
            {
                bool isContentList = content != null && ReferenceEquals(fe, content);
                if (!isContentList)
                {
                    if (!IsContentCandidate(fe, false)) continue;
                    if (content != null && IsDescendantOf(fe, content)) continue;
                }
                if (IsDescendantOf(fe, sidebar)) continue;

                var p = DesktopControllerNav.GetPosition(fe, w);
                if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                if (p.X + fe.ActualWidth / 2 < rightEdge) continue;                 // not right of the section list
                if (p.Y + fe.ActualHeight < 0 || p.Y > w.ActualHeight) continue;   // scrolled out of view
                if (p.Y < bestY - 0.5 || (Math.Abs(p.Y - bestY) <= 0.5 && p.X < bestX))
                {
                    best = fe; bestY = p.Y; bestX = p.X; isList = isContentList;
                }
            }
            return best;
        }

        /// <summary>
        /// Move the selection from the section list onto its page. The page of a just-selected section is built a
        /// moment later, so this retries briefly - but never once the pad has moved on or the mouse took over.
        /// </summary>
        private static void EnterContentSoon(Window w, FrameworkElement sidebar, int attempt)
        {
            if (attempt == 0) _holdTvi = null;      // the user asked to go into the page
            try
            {
                var t = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(attempt == 0 ? 60 : 150) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    try
                    {
                        if (w == null || !w.IsVisible || !PadInput.Active) return;
                        var cur = FocusedIn(w);
                        if (cur != null && !IsDescendantOf(cur, sidebar)) return;   // already moved on

                        bool isList;
                        var target = FindContentTarget(w, sidebar, out isList);
                        if (target != null)
                        {
                            if (isList)
                            {
                                if (TryStartInList(w)) return;
                            }
                            else
                            {
                                FocusElement(target);
                                var landed = FocusedIn(w);
                                if (landed != null && !IsDescendantOf(landed, sidebar))
                                {
                                    ExitListMode();
                                    Highlight(w);
                                    return;
                                }
                            }
                        }
                        if (attempt < 6) EnterContentSoon(w, sidebar, attempt + 1);
                        else { try { Breadcrumb.Write("PadSettings: no control found on the page next to the section list"); } catch { } }
                    }
                    catch { }
                };
                t.Start();
            }
            catch { }
        }

        private static string _branch = "";

        /// <summary>Type, name, label, position, flags of a control - enough to recognise it in the log.</summary>
        private static string Describe(DependencyObject d, Window w)
        {
            if (d == null) return "none";
            try
            {
                var sb = new StringBuilder(d.GetType().Name);
                var fe = d as FrameworkElement;
                if (fe == null) return sb.ToString();
                if (!string.IsNullOrEmpty(fe.Name)) sb.Append('#').Append(fe.Name);
                var cc = fe as ContentControl;
                string txt = cc != null ? cc.Content as string : null;
                if (txt != null) sb.Append(" \"").Append(txt.Length > 30 ? txt.Substring(0, 30) : txt).Append('"');
                var p = w != null ? DesktopControllerNav.GetPosition(fe, w) : new Point(double.NaN, double.NaN);
                sb.Append(" @").Append(double.IsNaN(p.X) ? "?" : ((int)p.X) + "," + ((int)p.Y))
                  .Append(' ').Append((int)fe.ActualWidth).Append('x').Append((int)fe.ActualHeight);
                var btn = fe as Button;
                if (btn != null && btn.IsDefault) sb.Append(" default");
                if (btn != null && btn.IsCancel) sb.Append(" cancel");
                if (!fe.IsEnabled) sb.Append(" disabled");
                if (!fe.IsVisible) sb.Append(" hidden");
                return sb.ToString();
            }
            catch { return d.GetType().Name; }
        }

        /// <summary>Containers above a control that restrict arrow / tab navigation: the usual reason WPF cannot leave a footer.</summary>
        private static string NavChain(DependencyObject d)
        {
            var sb = new StringBuilder();
            try
            {
                for (int i = 0; d != null && i < 40; i++)
                {
                    var dir = KeyboardNavigation.GetDirectionalNavigation(d);
                    var tab = KeyboardNavigation.GetTabNavigation(d);
                    var ctl = KeyboardNavigation.GetControlTabNavigation(d);
                    if (dir != KeyboardNavigationMode.Continue || tab != KeyboardNavigationMode.Continue || ctl != KeyboardNavigationMode.Continue)
                        sb.Append(d.GetType().Name).Append("[dir=").Append(dir).Append(",tab=").Append(tab).Append("] ");
                    if (d is Window) break;
                    d = VisualTreeHelper.GetParent(d);
                }
            }
            catch { }
            return sb.Length == 0 ? "(all Continue)" : sb.ToString().TrimEnd();
        }

        private static void TracedWindowAction(string name, Window w, Action core)
        {
            if (!Breadcrumb.Enabled) { core(); return; }
            string before = "", title = "";
            try { before = Describe(FocusedIn(w), w); title = w.Title; } catch { }
            _branch = "";
            long t0 = Stopwatch.GetTimestamp();
            try { core(); }
            finally
            {
                try
                {
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    string after = Describe(FocusedIn(w), w);
                    Breadcrumb.Write("Pad " + name + " [" + w.GetType().Name + " \"" + title + "\"] " + ms.ToString("0.0") + "ms" + (ms > 40 ? " SLOW" : "") +
                        " via=" + (_branch.Length == 0 ? "?" : _branch) +
                        " | " + before + (before == after ? "  (focus unchanged)" : "  ->  " + after));
                }
                catch { }
            }
        }

        public static void Move(Window w, FocusNavigationDirection dir) { TracedWindowAction("Move" + dir, w, () => MoveCore(w, dir)); }

        private static void MoveCore(Window w, FocusNavigationDirection dir)
        {
            PadInput.NotePad();
            try
            {
                // Cursor inside a long list: Up / Down step through the rows, Left / Right leave the list.
                bool vert0 = dir == FocusNavigationDirection.Up || dir == FocusNavigationDirection.Down;
                if (vert0 && ListModeMove(w, dir == FocusNavigationDirection.Down ? 1 : -1)) { _branch = "list-mode-rows"; return; }
                if (!vert0 && ListModeActive(w))
                {
                    ExitListMode();
                    if (dir == FocusNavigationDirection.Left)
                    {
                        var sideFromList = FindSidebar(w);
                        if (sideFromList != null && FocusSidebar(w, sideFromList)) return;
                    }
                    Highlight(w);
                    return;
                }

                // Very first press in this window: start in the list, not on the header / buttons above it.
                if (FirstTouchEnterList(w)) { _branch = "first-touch-enter-list"; return; }

                var f = FocusedIn(w);
                if (f == null) { _branch = "no-focus->FocusTop"; FocusTop(w); SelectFocused(w); Highlight(w); return; }

                // A scroll area is not something to press. This window's content ScrollViewer is focusable, and WPF then
                // bounced between it and the Save / Cancel buttons forever (Up from Save = scroll area, Down = Save).
                // Make it non-focusable and go to a real control inside it.
                var sv0 = f as ScrollViewer;
                if (sv0 != null)
                {
                    sv0.Focusable = false;
                    _branch = "leave-scrollviewer";
                    if (FocusInsideScrollViewer(w, sv0, dir)) { SelectFocused(w); Highlight(w); return; }
                }

                bool vertical = dir == FocusNavigationDirection.Up || dir == FocusNavigationDirection.Down;
                int sign = (dir == FocusNavigationDirection.Down || dir == FocusNavigationDirection.Right) ? 1 : -1;

                // An open dropdown: Up/Down choose the entry.
                var combo = SelfOrParent<ComboBox>(f);
                if (combo != null && combo.IsDropDownOpen)
                {
                    _branch = "open-combo";
                    if (vertical)
                    {
                        int n = combo.Items.Count;
                        int i = combo.SelectedIndex;
                        int ni = i < 0 ? 0 : Math.Max(0, Math.Min(n - 1, i + sign));
                        if (n > 0 && ni != i) combo.SelectedIndex = ni;
                    }
                    return;
                }

                // Sliders: Left/Right change the value.
                var slider = SelfOrParent<Slider>(f);
                if (slider != null && !vertical)
                {
                    _branch = "slider";
                    double range = slider.Maximum - slider.Minimum;
                    double step = slider.SmallChange > 0 ? slider.SmallChange : range / 20.0;
                    if (step < range / 100.0) step = range / 100.0;
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, slider.Value + sign * step));
                    return;
                }

                // Tab strips: Left/Right walk the tabs.
                var tabItem = f as TabItem;
                if (tabItem != null && !vertical)
                {
                    var tc = ItemsControl.ItemsControlFromItemContainer(tabItem) as TabControl;
                    if (tc != null && tc.TabStripPlacement == Dock.Left)
                    {
                        // Vertical tab strip: Left / Right do not walk the tabs. Right goes into the page.
                        var tabSide = SidebarRootOf(w, tabItem);
                        if (tabSide != null)
                        {
                            if (sign > 0) EnterContentSoon(w, tabSide, 0);
                            return;
                        }
                    }
                    if (tc != null)
                    {
                        int n = tc.Items.Count;
                        int i = tc.Items.IndexOf(tc.ItemContainerGenerator.ItemFromContainer(tabItem));
                        int ni = Math.Max(0, Math.Min(n - 1, i + sign));
                        var next = tc.ItemContainerGenerator.ContainerFromIndex(ni) as TabItem;
                        if (next != null) { FocusElement(next); next.IsSelected = true; Highlight(w); return; }
                    }
                }

                // Tree items. Up / Down walk the visible entries one by one (directional focus search gives up at the
                // edge of the visible part of a long tree, which left the pad stuck part-way down the section list).
                // Right expands, Left collapses (like the keyboard does), Left on a child returns to its group.
                var treeItem = f as TreeViewItem;
                if (treeItem != null)
                {
                    _branch = "tree";
                    if (vertical)
                    {
                        if (StepTree(w, treeItem, sign)) return;
                    }
                    else
                    {
                        if (treeItem.HasItems && sign > 0 && !treeItem.IsExpanded) { treeItem.IsExpanded = true; return; }
                        if (treeItem.HasItems && sign < 0 && treeItem.IsExpanded) { treeItem.IsExpanded = false; return; }
                        if (sign < 0)
                        {
                            var parentGroup = ItemsControl.ItemsControlFromItemContainer(treeItem) as TreeViewItem;
                            if (parentGroup != null && DesktopControllerNav.IsUsable(parentGroup))
                            {
                                FocusElement(parentGroup);
                                SelectFocused(w);
                                Highlight(w);
                                return;
                            }
                        }
                    }
                }

                // Focus already on a row of the long list (clicked, or tabbed into): continue by rows.
                var list = vertical ? FindContentList(w) : null;
                if (list != null && IsDescendantOf(f, list))
                {
                    int row = IndexOfRow(list, f);
                    if (row >= 0) { _branch = "content-list-row"; SetListRow(w, list, row + sign); return; }
                }

                var ui = f as UIElement;
                bool moved = false;
                if (ui != null) moved = ui.MoveFocus(new TraversalRequest(dir));
                _branch = moved ? "wpf-directional" : "wpf-directional(failed)";
                if (moved)
                {
                    var landedSv = FocusedIn(w) as ScrollViewer;
                    if (landedSv != null)
                    {
                        landedSv.Focusable = false;      // never land on the scroll area again
                        _branch += "+skip-scrollviewer";
                        if (vertical && FocusInsideScrollViewer(w, landedSv, dir)) { SelectFocused(w); Highlight(w); return; }
                        if (!vertical) { FocusElement(f as FrameworkElement); moved = false; }   // sideways: stay where we were
                    }
                }
                if (moved) SkipPanels(w, dir);

                // Section list on the left, page on the right: WPF never lets directional focus leave the list, so
                // Right hands over to the page and Left (when nothing is further left) returns to the list.
                if (!vertical && !moved)
                {
                    if (sign > 0)
                    {
                        FrameworkElement sideRoot = SidebarRootOf(w, f);
                        if (sideRoot == null && f is TreeViewItem) sideRoot = DesktopControllerNav.FindVisualParent<TreeView>(f);
                        if (sideRoot != null) { EnterContentSoon(w, sideRoot, 0); return; }
                    }
                    else if (SidebarRootOf(w, f) == null)
                    {
                        var sideBack = FindSidebar(w);
                        if (sideBack != null && FocusSidebar(w, sideBack)) return;
                    }
                }

                // WPF's directional search gives up in several layouts (footer buttons in a container with
                // DirectionalNavigation=Contained, content in a ScrollViewer ...). Pick the nearest control by position.
                if (!moved || ReferenceEquals(FocusedIn(w), f))
                {
                    var stuckEl = f as FrameworkElement;
                    if (stuckEl != null && FocusNearest(w, stuckEl, dir)) { _branch += "+nearest"; SelectFocused(w); Highlight(w); return; }
                    _branch += "+nearest(none)";
                    if (Breadcrumb.Enabled)
                    {
                        try
                        {
                            Breadcrumb.Write("PadMove: STUCK dir=" + dir + " on " + Describe(f, w) + " in \"" + w.Title + "\" | nav restrictions above it: " + NavChain(f) +
                                " | scrollable=" + DesktopControllerNav.FindVisualChildren<ScrollViewer>(w).Count(v => DesktopControllerNav.IsUsable(v) && v.ScrollableHeight > 0));
                        }
                        catch { }
                    }
                }

                if (list != null && TryEnterList(w, list, f, moved, dir)) return;
                if (!moved && vertical) { _branch += "+scroll-fallback"; ScrollFallback(w, sign); }
                else if (!moved && !vertical && ui is FrameworkElement && !DesktopControllerNav.IsUsable((FrameworkElement)ui))
                    FocusTop(w);

                SelectFocused(w);
                Highlight(w);
            }
            catch { }
        }

        public static void Confirm(Window w) { TracedWindowAction("Confirm", w, () => ConfirmCore(w)); }

        private static void ConfirmCore(Window w)
        {
            PadInput.NotePad();
            try
            {
                if (ListModeConfirm(w)) return;
                var f = FocusedIn(w);
                if (f == null) { FocusTop(w); SelectFocused(w); Highlight(w); return; }

                var combo = SelfOrParent<ComboBox>(f);
                if (combo != null)
                {
                    combo.IsDropDownOpen = !combo.IsDropDownOpen;   // open, or confirm the highlighted entry
                    return;
                }

                var ex = f as Expander;
                if (ex != null) { ex.IsExpanded = !ex.IsExpanded; return; }

                var tab = f as TabItem;
                if (tab != null)
                {
                    tab.IsSelected = true;
                    var tabSide = SidebarRootOf(w, tab);          // a tab strip on the left: A opens the page
                    if (tabSide != null) EnterContentSoon(w, tabSide, 0);
                    return;
                }

                var tvi = f as TreeViewItem;
                if (tvi != null)
                {
                    tvi.IsSelected = true;
                    // Playnite's settings tree opens a page from mouse input, which IsSelected alone does not reproduce.
                    if (!ReferenceEquals(_lastSelectedTvi, tvi))
                    {
                        _lastSelectedTvi = tvi;
                        DesktopControllerNav.Defer(() => ClickLikeMouse(tvi));
                    }
                    if (tvi.HasItems) tvi.IsExpanded = !tvi.IsExpanded;     // a group of sections: open / close it
                    else
                    {
                        // A section: A moves the selection onto its page, where the settings are.
                        // A tree that is not recognised as a "sidebar" (wide, or not at the far left) used to do nothing here.
                        FrameworkElement treeSide = SidebarRootOf(w, tvi) ?? DesktopControllerNav.FindVisualParent<TreeView>(tvi);
                        if (treeSide != null) EnterContentSoon(w, treeSide, 0);
                    }
                    return;
                }

                var lbi = f as ListBoxItem;
                if (lbi != null && lbi.IsEnabled)
                {
                    var lb = ItemsControl.ItemsControlFromItemContainer(lbi) as ListBox;
                    if (lb != null && lb.SelectionMode != SelectionMode.Single)
                    {
                        // Multi-select lists: A toggles the entry (like Ctrl+click).
                        lbi.IsSelected = !lbi.IsSelected;
                        return;
                    }
                    lbi.IsSelected = true;   // single-select: choose the entry
                    return;
                }

                var button = f as ButtonBase;
                if (button != null && button.IsEnabled)
                    Press(button);
            }
            catch { }
        }

        public static void Back(Window w) { TracedWindowAction("Back", w, () => BackCore(w)); }

        private static void BackCore(Window w)
        {
            PadInput.NotePad();
            try
            {
                if (ListModeActive(w)) { ExitListMode(); Highlight(w); return; }
                var combo = SelfOrParent<ComboBox>(FocusedIn(w));
                if (combo != null && combo.IsDropDownOpen) { combo.IsDropDownOpen = false; return; }
                Clear();

                // Dialogs and message boxes mark their Cancel button with IsCancel: B means Cancel.
                var cancel = UsableButtons(w).FirstOrDefault(b => b.IsCancel);
                if (cancel != null) { Press(cancel); return; }

                DesktopControllerNav.Defer(() => { try { w.Close(); } catch { } });   // closing without Save is Cancel
            }
            catch { }
        }

        /// <summary>LB / RB: previous / next section header, or previous / next tab when there are no sections.</summary>
        public static void Section(Window w, int delta) { TracedWindowAction(delta > 0 ? "SectionNext" : "SectionPrev", w, () => SectionCore(w, delta)); }

        private static void SectionCore(Window w, int delta)
        {
            PadInput.NotePad();
            _holdTvi = null;
            try
            {
                if (ListPage(w, delta)) return;
                var sections = DesktopControllerNav.FindVisualChildren<Expander>(w)
                    .Where(e => DesktopControllerNav.IsUsable(e) && HeaderOf(e) != null).ToList();

                if (sections.Count == 0)
                {
                    var tabs = DesktopControllerNav.FindVisualChildren<TabControl>(w)
                        .FirstOrDefault(t => DesktopControllerNav.IsUsable(t) && t.Items.Count > 1);
                    if (tabs != null)
                    {
                        int n = tabs.Items.Count;
                        int i = tabs.SelectedIndex;
                        int ni = i < 0 ? 0 : Math.Max(0, Math.Min(n - 1, i + delta));
                        tabs.SelectedIndex = ni;
                        var item = tabs.ItemContainerGenerator.ContainerFromIndex(ni) as TabItem;
                        if (item != null) FocusElement(item);
                        Highlight(w);
                    }
                    return;
                }

                var f = FocusedIn(w);
                var cur = SelfOrParent<Expander>(f);
                int idx = cur != null ? sections.IndexOf(cur) : -1;
                int next = idx < 0 ? (delta > 0 ? 0 : sections.Count - 1)
                                   : Math.Max(0, Math.Min(sections.Count - 1, idx + delta));
                FocusElement(HeaderOf(sections[next]));
                Highlight(w);
            }
            catch { }
        }

        /// <summary>LT: back to the top. RT: down to the Save / default button (or the last control).</summary>
        public static void Jump(Window w, bool toBottom) { TracedWindowAction(toBottom ? "JumpBottom" : "JumpTop", w, () => JumpCore(w, toBottom)); }

        private static void JumpCore(Window w, bool toBottom)
        {
            PadInput.NotePad();
            _holdTvi = null;
            try
            {
                ExitListMode();
                if (!toBottom) { FocusTop(w); SelectFocused(w); Highlight(w); return; }

                var buttons = UsableButtons(w);
                var save = buttons.FirstOrDefault(b =>
                        string.Equals(b.Content as string, "Save", StringComparison.OrdinalIgnoreCase))
                    ?? buttons.FirstOrDefault(b => b.IsDefault);
                if (save != null) FocusElement(save);
                else w.MoveFocus(new TraversalRequest(FocusNavigationDirection.Last));
                Highlight(w);
            }
            catch { }
        }
    }
}
