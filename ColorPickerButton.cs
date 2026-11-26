using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Click the swatch to open a color picker (saturation/brightness square + hue strip).
    /// ColorHex is a two-way bindable "#RRGGBB" string.
    /// </summary>
    public class ColorPickerButton : UserControl
    {
        public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
            nameof(ColorHex), typeof(string), typeof(ColorPickerButton),
            new FrameworkPropertyMetadata("#000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorHexChanged));

        public string ColorHex
        {
            get { return (string)GetValue(ColorHexProperty); }
            set { SetValue(ColorHexProperty, value); }
        }

        private const double SvW = 200, SvH = 140, HueW = 200, HueH = 14;

        private readonly Border _swatch;
        private readonly TextBlock _label;
        private readonly Popup _popup;
        private readonly Rectangle _hueBase;
        private readonly Canvas _svCanvas, _hueCanvas;
        private readonly Ellipse _svMarker;
        private readonly Border _hueMarker;
        private readonly Grid _svArea, _hueArea;

        private double _h, _s = 1, _v = 1;
        private bool _updating;
        private DateTime _closedAtUtc = DateTime.MinValue;

        public ColorPickerButton()
        {
            var root = new StackPanel { Orientation = Orientation.Horizontal };

            _swatch = new Border
            {
                Width = 48,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x80, 0x80, 0x80))
            };
            var button = new Button
            {
                Content = _swatch,
                Padding = new Thickness(3),
                Cursor = Cursors.Hand,
                ToolTip = "Click to choose a color"
            };
            _label = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7
            };
            root.Children.Add(button);
            root.Children.Add(_label);
            Content = root;

            // --- popup content
            _hueBase = new Rectangle();
            var white = new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
            var black = new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
            _svMarker = new Ellipse
            {
                Width = 14,
                Height = 14,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9 },
                IsHitTestVisible = false
            };
            _svCanvas = new Canvas { IsHitTestVisible = false };
            _svCanvas.Children.Add(_svMarker);
            _svArea = new Grid { Width = SvW, Height = SvH, Cursor = Cursors.Cross, ClipToBounds = true, Background = Brushes.Transparent };
            _svArea.Children.Add(_hueBase);
            _svArea.Children.Add(white);
            _svArea.Children.Add(black);
            _svArea.Children.Add(_svCanvas);

            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            Color[] stops =
            {
                Color.FromRgb(255, 0, 0), Color.FromRgb(255, 255, 0), Color.FromRgb(0, 255, 0),
                Color.FromRgb(0, 255, 255), Color.FromRgb(0, 0, 255), Color.FromRgb(255, 0, 255), Color.FromRgb(255, 0, 0)
            };
            for (int i = 0; i < stops.Length; i++)
                rainbow.GradientStops.Add(new GradientStop(stops[i], i / 6.0));
            var hueBar = new Border { CornerRadius = new CornerRadius(3), Background = rainbow };
            _hueMarker = new Border
            {
                Width = 6,
                Height = HueH + 6,
                CornerRadius = new CornerRadius(3),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9 },
                IsHitTestVisible = false
            };
            _hueCanvas = new Canvas { IsHitTestVisible = false };
            _hueCanvas.Children.Add(_hueMarker);
            _hueArea = new Grid { Width = HueW, Height = HueH, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 12, 0, 0) };
            _hueArea.Children.Add(hueBar);
            _hueArea.Children.Add(_hueCanvas);

            var stack = new StackPanel();
            stack.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = _svArea });
            stack.Children.Add(_hueArea);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4C, 0x55)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 14, 14),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 },
                Child = stack
            };

            _popup = new Popup
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = card
            };
            _popup.Closed += (s, e) => _closedAtUtc = DateTime.UtcNow;
            button.Click += (s, e) =>
            {
                // The click that just closed the popup (StaysOpen=false) must not reopen it.
                if ((DateTime.UtcNow - _closedAtUtc).TotalMilliseconds < 250) return;
                _popup.IsOpen = true;
            };
            Unloaded += (s, e) => _popup.IsOpen = false;

            _svArea.MouseLeftButtonDown += (s, e) => { _svArea.CaptureMouse(); PickSv(e.GetPosition(_svArea)); e.Handled = true; };
            _svArea.MouseMove += (s, e) => { if (_svArea.IsMouseCaptured) PickSv(e.GetPosition(_svArea)); };
            _svArea.MouseLeftButtonUp += (s, e) => _svArea.ReleaseMouseCapture();
            _hueArea.MouseLeftButtonDown += (s, e) => { _hueArea.CaptureMouse(); PickHue(e.GetPosition(_hueArea)); e.Handled = true; };
            _hueArea.MouseMove += (s, e) => { if (_hueArea.IsMouseCaptured) PickHue(e.GetPosition(_hueArea)); };
            _hueArea.MouseLeftButtonUp += (s, e) => _hueArea.ReleaseMouseCapture();

            LoadFromHex(ColorHex);
        }

        private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (ColorPickerButton)d;
            if (c._updating) return;
            c.LoadFromHex(e.NewValue as string);
        }

        private void LoadFromHex(string hex)
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(hex ?? "#000000"); }
            catch { return; }

            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            _v = max;
            _s = max <= 0 ? 0 : d / max;
            if (d > 1e-6)
            {
                double h;
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
                if (h < 0) h += 360;
                _h = h; // greys keep the previous hue so the hue strip doesn't jump
            }
            UpdateVisuals(color);
        }

        private void PickSv(Point p)
        {
            _s = Math.Max(0, Math.Min(1, p.X / SvW));
            _v = 1 - Math.Max(0, Math.Min(1, p.Y / SvH));
            Commit();
        }

        private void PickHue(Point p)
        {
            _h = Math.Max(0, Math.Min(1, p.X / HueW)) * 359.99;
            Commit();
        }

        private void Commit()
        {
            Color c = HsvToColor(_h, _s, _v);
            UpdateVisuals(c);
            _updating = true;
            try { ColorHex = OverlayTheme.ToHex(c); }
            finally { _updating = false; }
        }

        private void UpdateVisuals(Color c)
        {
            _swatch.Background = new SolidColorBrush(c);
            _label.Text = OverlayTheme.ToHex(c);
            _hueBase.Fill = new SolidColorBrush(HsvToColor(_h, 1, 1));
            Canvas.SetLeft(_svMarker, _s * SvW - 7);
            Canvas.SetTop(_svMarker, (1 - _v) * SvH - 7);
            Canvas.SetLeft(_hueMarker, (_h / 359.99) * HueW - 3);
            Canvas.SetTop(_hueMarker, -3);
        }

        private static Color HsvToColor(double h, double s, double v)
        {
            double c = v * s, hh = h / 60.0, x = c * (1 - Math.Abs(hh % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (hh < 1) { r = c; g = x; }
            else if (hh < 2) { r = x; g = c; }
            else if (hh < 3) { g = c; b = x; }
            else if (hh < 4) { g = x; b = c; }
            else if (hh < 5) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }
    }
}
