using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Direct pointer-to-value mapping for Slider. Avoids WPF Thumb drag, which is
    /// clunky inside a scaled Viewbox and with fully custom templates.
    /// </summary>
    internal static class SmoothSlider
    {
        private static readonly ConditionalWeakTable<Slider, Options> Opts =
            new ConditionalWeakTable<Slider, Options>();

        private static Slider _active;
        private static bool _dragging;
        private static double _grabOffset;

        private sealed class Options
        {
            public Action OnReleased;
            public bool Hooked;
        }

        public static void Attach(Slider slider, Action onReleased = null)
        {
            if (slider == null) return;
            Options o;
            if (!Opts.TryGetValue(slider, out o))
            {
                o = new Options();
                Opts.Add(slider, o);
            }
            o.OnReleased = onReleased;
            if (o.Hooked) return;
            o.Hooked = true;
            slider.PreviewMouseLeftButtonDown += OnDown;
            slider.PreviewMouseMove += OnMove;
            slider.PreviewMouseLeftButtonUp += OnUp;
            slider.LostMouseCapture += OnLostCapture;
        }

        private static void OnDown(object sender, MouseButtonEventArgs e)
        {
            var s = sender as Slider;
            if (s == null || !s.IsEnabled) return;
            _active = s;
            _dragging = true;
            s.CaptureMouse();
            // Grabbing the knob itself keeps the pointer's offset from its centre (like the
            // Windows 11 mixer); clicking elsewhere on the rail jumps the knob to the pointer.
            var pos = e.GetPosition(s);
            _grabOffset = 0;
            double range = s.Maximum - s.Minimum;
            double w = s.ActualWidth;
            if (w > 28 && range > 0)
            {
                double knobX = 14.0 + (s.Value - s.Minimum) / range * (w - 28.0);
                if (Math.Abs(pos.X - knobX) <= 14.0) _grabOffset = pos.X - knobX;
            }
            Apply(s, pos);
            try { s.Focus(); } catch { }
            e.Handled = true;
        }

        private static void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _active == null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                End();
                return;
            }
            Apply(_active, e.GetPosition(_active));
            e.Handled = true;
        }

        private static void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            if (_active != null)
                Apply(_active, e.GetPosition(_active));
            End();
            e.Handled = true;
        }

        private static void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_dragging) End();
        }

        private static void End()
        {
            var s = _active;
            _dragging = false;
            _active = null;
            try { if (s != null && s.IsMouseCaptured) s.ReleaseMouseCapture(); } catch { }
            Options o;
            if (s != null && Opts.TryGetValue(s, out o) && o.OnReleased != null)
            {
                try { o.OnReleased(); } catch { }
            }
        }

        private static void Apply(Slider s, Point p)
        {
            double w = s.ActualWidth;
            if (w < 1) return;
            double pad = 14;
            double usable = Math.Max(1.0, w - pad * 2.0);
            double x = p.X - _grabOffset - pad;
            if (x < 0) x = 0;
            if (x > usable) x = usable;
            double t = x / usable;
            double value = s.Minimum + t * (s.Maximum - s.Minimum);
            if (s.IsSnapToTickEnabled && s.TickFrequency > 0)
                value = Math.Round(value / s.TickFrequency) * s.TickFrequency;
            else
                value = Math.Round(value * 1000.0) / 1000.0;
            if (value < s.Minimum) value = s.Minimum;
            if (value > s.Maximum) value = s.Maximum;
            if (Math.Abs(s.Value - value) < 1e-9) return;
            s.Value = value;
        }
    }
}
