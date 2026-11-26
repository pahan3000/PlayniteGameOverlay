using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteGameOverlay
{
    /// <summary>All colors/radii the overlay (and the settings preview) derive from the Appearance settings.</summary>
    internal sealed class OverlayThemeValues
    {
        public Color Accent, AccentDark, Panel, PanelWithAlpha, Surface, SurfaceHover, PanelBorder;
        public Color SelectedSurface, SelectedForeground;
        public double PanelRadius, ControlRadius, Dim, PanelBorderThickness, GlassStrength;
        public bool Glass;
    }

    internal static class OverlayTheme
    {
        public static readonly Color DefaultAccent = Color.FromRgb(0x66, 0xC0, 0xF4);
        public static readonly Color DefaultPanel = Color.FromRgb(0x1A, 0x1D, 0x23);
        private static readonly object CoverAccentCacheLock = new object();
        private static readonly Dictionary<string, Tuple<long, string>> CoverAccentCache =
            new Dictionary<string, Tuple<long, string>>(StringComparer.OrdinalIgnoreCase);

        public static OverlayThemeValues Compute(OverlaySettings s, string coverImagePath = null)
        {
            // Dynamic presets follow the active game's cover, falling back to the saved accent
            // when no local cover image is available.
            Color accent = s != null && s.UseCoverAccent
                ? Parse(ExtractCoverAccentHex(coverImagePath, s.AccentColor), DefaultAccent)
                : (s != null && s.UseWindowsAccent
                    ? Parse(WindowsAccentHex(), DefaultAccent)
                    : Parse(s?.AccentColor, DefaultAccent));
            Color panel = Parse(s?.PanelBackgroundColor, DefaultPanel);
            double opacity = Math.Max(0.3, Math.Min(1.0, s?.PanelOpacity ?? 1.0));
            double radius = Math.Max(0, Math.Min(28, s?.PanelCornerRadius ?? 10));
            double dim = Math.Max(0, Math.Min(0.85, s?.BackdropDim ?? 0));
            double glassStrength = Math.Max(0, Math.Min(1, s?.PanelGlassStrength ?? 0.5));

            bool defaultAccent = accent.R == DefaultAccent.R && accent.G == DefaultAccent.G && accent.B == DefaultAccent.B;
            Color accentDark = defaultAccent
                ? Color.FromRgb(0x15, 0x65, 0xC0)
                : Color.FromRgb((byte)(accent.R * 0.6), (byte)(accent.G * 0.6), (byte)(accent.B * 0.6));

            // Glass effect: subtle light control surfaces and a hairline border.
            bool glass = s != null && s.PanelGlassBorder;
            if (glass) accentDark = Color.FromRgb(0x6A, 0x6F, 0x7B);
            return new OverlayThemeValues
            {
                Accent = accent,
                AccentDark = accentDark,
                Panel = panel,
                PanelWithAlpha = Color.FromArgb((byte)Math.Round(opacity * 255), panel.R, panel.G, panel.B),
                // Cards/buttons sit slightly lighter than the panel, hover/focus lighter still
                // (offsets match the original #1A1D23 -> #1E2028 -> #2A2D35 palette).
                Surface = glass ? Color.FromArgb(ScaleAlpha(0x1C, glassStrength), 0xFF, 0xFF, 0xFF) : Offset(panel, 4, 3, 5),
                SurfaceHover = glass ? Color.FromArgb(ScaleAlpha(0x30, glassStrength), 0xFF, 0xFF, 0xFF) : Offset(panel, 16, 16, 18),
                Glass = glass,
                // Selection is a neutral surface lift rather than an accent wash, so controller
                // focus stays readable without turning every selected item blue.
                SelectedSurface = glass
                    ? Color.FromArgb(ScaleAlpha(0x30, glassStrength), 0xFF, 0xFF, 0xFF)
                    : Offset(panel, 16, 16, 18),
                SelectedForeground = Colors.White,
                PanelBorder = glass ? Color.FromArgb(ScaleAlpha(0x42, glassStrength), 0xFF, 0xFF, 0xFF) : Color.FromArgb(0, 0, 0, 0),
                PanelBorderThickness = glass ? 1 : 0,
                PanelRadius = radius,
                ControlRadius = Math.Round(radius * 0.8),
                Dim = dim,
                GlassStrength = glassStrength
            };
        }

        /// <summary>Finds a vivid, representative accent in a game cover and brightens it for UI contrast.</summary>
        public static string ExtractCoverAccentHex(string imagePath, string fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    return fallback;

                string path = Path.GetFullPath(imagePath);
                long modified = File.GetLastWriteTimeUtc(path).Ticks;
                lock (CoverAccentCacheLock)
                {
                    if (CoverAccentCache.TryGetValue(path, out var cached) && cached.Item1 == modified)
                        return cached.Item2;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 64;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var pixelsBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int width = pixelsBitmap.PixelWidth;
                int height = pixelsBitmap.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                pixelsBitmap.CopyPixels(pixels, stride, 0);

                var scores = new Dictionary<int, double>();
                var redTotals = new Dictionary<int, double>();
                var greenTotals = new Dictionary<int, double>();
                var blueTotals = new Dictionary<int, double>();
                for (int y = 0; y < height; y += 2)
                {
                    for (int x = 0; x < width; x += 2)
                    {
                        int i = y * stride + x * 4;
                        if (pixels[i + 3] < 96) continue;
                        int b = pixels[i];
                        int g = pixels[i + 1];
                        int r = pixels[i + 2];
                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));
                        double saturation = max == 0 ? 0 : (max - min) / (double)max;
                        double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                        if (saturation < 0.18 || luminance < 0.12 || luminance > 0.94) continue;

                        // Group nearby shades by hue so gradients and texture still form one accent cluster.
                        double hue;
                        if (max == r) hue = 60.0 * (g - b) / (max - min);
                        else if (max == g) hue = 60.0 * (2.0 + (b - r) / (max - min));
                        else hue = 60.0 * (4.0 + (r - g) / (max - min));
                        if (hue < 0) hue += 360.0;
                        int hueBin = ((int)Math.Floor(hue / 15.0)) % 24;
                        int saturationBin = Math.Min(3, (int)(saturation * 4));
                        int lightBin = luminance < 0.34 ? 0 : (luminance < 0.68 ? 1 : 2);

                        // Give midtones and saturated hues priority; ignore near-black and washed-out art.
                        double tone = 1.0 - Math.Min(1.0, Math.Abs(luminance - 0.52) / 0.52);
                        double weight = Math.Max(0.15, tone) * saturation * saturation;
                        int key = hueBin * 12 + saturationBin * 3 + lightBin;
                        scores[key] = scores.TryGetValue(key, out var score) ? score + weight : weight;
                        redTotals[key] = redTotals.TryGetValue(key, out var red) ? red + r * weight : r * weight;
                        greenTotals[key] = greenTotals.TryGetValue(key, out var green) ? green + g * weight : g * weight;
                        blueTotals[key] = blueTotals.TryGetValue(key, out var blue) ? blue + b * weight : b * weight;
                    }
                }

                if (scores.Count == 0)
                    return fallback;

                int best = 0;
                double bestScore = double.MinValue;
                foreach (var entry in scores)
                {
                    if (entry.Value > bestScore)
                    {
                        best = entry.Key;
                        bestScore = entry.Value;
                    }
                }

                double total = scores[best];
                int outR = (int)Math.Round(redTotals[best] / total);
                int outG = (int)Math.Round(greenTotals[best] / total);
                int outB = (int)Math.Round(blueTotals[best] / total);
                int outMax = Math.Max(outR, Math.Max(outG, outB));
                if (outMax > 0 && outMax < 224)
                {
                    double lift = 224.0 / outMax;
                    outR = (int)Math.Min(255, Math.Round(outR * lift));
                    outG = (int)Math.Min(255, Math.Round(outG * lift));
                    outB = (int)Math.Min(255, Math.Round(outB * lift));
                }
                string accentHex = ToHex(Color.FromRgb((byte)outR, (byte)outG, (byte)outB));

                lock (CoverAccentCacheLock)
                {
                    if (CoverAccentCache.Count > 96) CoverAccentCache.Clear();
                    CoverAccentCache[path] = Tuple.Create(modified, accentHex);
                }
                return accentHex;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Creates the shared panel fill, with a restrained vertical sheen for glass presets.</summary>
        public static Brush CreatePanelBrush(OverlayThemeValues theme)
        {
            return CreatePanelBrush(theme, theme != null && theme.Glass, 0);
        }

        /// <summary>
        /// Panel fill with an explicit glass sheen and a minimum alpha. Surfaces that carry a lot of
        /// text (achievements, menus) use a high minimum so a low panel opacity can't make them unreadable.
        /// </summary>
        public static Brush CreatePanelBrush(OverlayThemeValues theme, bool glass, byte minAlpha)
        {
            if (theme == null) return new SolidColorBrush(DefaultPanel);
            var baseColor = theme.PanelWithAlpha;
            if (baseColor.A < minAlpha) baseColor = Color.FromArgb(minAlpha, baseColor.R, baseColor.G, baseColor.B);
            if (!glass)
            {
                var solid = new SolidColorBrush(baseColor);
                solid.Freeze();
                return solid;
            }

            // Same recipe as the glass settings menus, so every preset (Steam, Steam Dark, Windows 11, custom)
            // gets that steel-tinted pane: a soft wash of the accent colour in the top-left falling off to a
            // deeper base at the bottom-right. The sheen changes colour, not opacity; panel opacity stays
            // controlled solely by the user's setting.
            Color accent = theme.Accent;
            double strength = theme.GlassStrength;
            var brush = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Shift(Tint(baseColor, accent, 0.14 * strength),
                    (int)Math.Round(4 * strength), (int)Math.Round(4 * strength), (int)Math.Round(5 * strength)), 0.0),
                new GradientStop(Tint(baseColor, accent, 0.05 * strength), 0.5),
                new GradientStop(Tint(baseColor, Colors.Black, 0.10 * strength), 1.0)
            }, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Panel border: a smooth top-lit hairline for glass (brightest along the top edge, fading evenly
        /// down the sides). A diagonal rim looked "gnawed" because each edge changed brightness at a
        /// different point; a vertical fade keeps every edge uniform. Plain solid colour otherwise.
        /// </summary>
        public static Brush CreatePanelBorderBrush(OverlayThemeValues theme)
        {
            if (theme == null || !theme.Glass)
            {
                var solid = new SolidColorBrush(theme != null ? theme.PanelBorder : Color.FromArgb(0, 0, 0, 0));
                solid.Freeze();
                return solid;
            }
            Func<byte, Color> white = a => Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
            var rim = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(white(ScaleAlpha(0x40, theme.GlassStrength)), 0.0),
                new GradientStop(white(ScaleAlpha(0x26, theme.GlassStrength)), 0.4),
                new GradientStop(white(ScaleAlpha(0x1A, theme.GlassStrength)), 1.0)
            }, new Point(0.5, 0), new Point(0.5, 1));
            rim.Freeze();
            return rim;
        }

        /// <summary>
        /// Fill for the selected/focused item. On glass it is a neutral pane that is a little
        /// brighter at the top (like light catching the glass) instead of a flat slab.
        /// </summary>
        public static Brush CreateSelectedBrush(OverlayThemeValues theme)
        {
            if (theme == null) return new SolidColorBrush(Colors.Transparent);
            var c = theme.SelectedSurface;
            if (!theme.Glass)
            {
                var solid = new SolidColorBrush(c);
                solid.Freeze();
                return solid;
            }
            // Diagonal (top-left lit -> bottom-right deeper) like the panel sheen, not a vertical "convex" fade.
            byte lift = ScaleAlpha(0x26, theme.GlassStrength);
            byte falloff = ScaleAlpha(0x08, theme.GlassStrength);
            var pane = new LinearGradientBrush(
                Color.FromArgb((byte)Math.Min(255, c.A + lift), c.R, c.G, c.B),
                Color.FromArgb((byte)Math.Max(0, c.A - falloff), c.R, c.G, c.B),
                new Point(0, 0), new Point(1, 1));
            pane.Freeze();
            return pane;
        }

        /// <summary>
        /// Fill for an "active" item (selected preset, current mode): a diagonal accent pane, lighter in the
        /// top-left and deeper bottom-right. Translucent on glass presets, opaque otherwise.
        /// </summary>
        public static Brush CreateAccentSelectedBrush(OverlayThemeValues theme)
        {
            Color accent = theme != null ? theme.Accent : DefaultAccent;
            bool glass = theme != null && theme.Glass;
            double gs = theme != null ? theme.GlassStrength : 0.5;
            Color top, bottom;
            if (glass)
            {
                // Restrained: the accent only tints the frosted pane, it never floods it.
                top = Color.FromArgb((byte)Math.Round(0x48 + 0x28 * gs), accent.R, accent.G, accent.B);
                bottom = Color.FromArgb((byte)Math.Round(0x1C + 0x14 * gs), accent.R, accent.G, accent.B);
            }
            else
            {
                Color surf = theme != null ? theme.Surface : Color.FromRgb(0x1E, 0x20, 0x28);
                top = Tint(Color.FromArgb(0xFF, surf.R, surf.G, surf.B), accent, 0.30);
                bottom = Tint(Color.FromArgb(0xFF, surf.R, surf.G, surf.B), accent, 0.12);
            }
            var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        /// <summary>Hairline rim for an active item: bright specular edge top-left, a hint of accent, fading out.</summary>
        public static Brush CreateAccentRimBrush(OverlayThemeValues theme)
        {
            Color accent = theme != null ? theme.Accent : DefaultAccent;
            Color hi = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            Color mid = Tint(Color.FromArgb(0x58, accent.R, accent.G, accent.B), Colors.White, 0.35);
            Color lo = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
            var brush = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(hi, 0.0),
                new GradientStop(mid, 0.5),
                new GradientStop(lo, 1.0)
            }, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Hover/focus rim for buttons. Use a neutral, panel-contrast color so selection follows each
        /// appearance preset without inheriting a saturated (often blue) accent.
        /// </summary>
        public static Brush CreateFocusRimBrush(OverlayThemeValues theme)
        {
            if (theme == null)
            {
                var solid = new SolidColorBrush(Color.FromRgb(0xD6, 0xD9, 0xDE));
                solid.Freeze();
                return solid;
            }
            var rimColor = GetNeutralFocusColor(theme.Panel);
            if (!theme.Glass)
            {
                var solid = new SolidColorBrush(rimColor);
                solid.Freeze();
                return solid;
            }
            var rim = new LinearGradientBrush(
                Color.FromArgb(ScaleAlpha(0x70, theme.GlassStrength), rimColor.R, rimColor.G, rimColor.B),
                Color.FromArgb(ScaleAlpha(0x24, theme.GlassStrength), rimColor.R, rimColor.G, rimColor.B),
                new Point(0.5, 0), new Point(0.5, 1));
            rim.Freeze();
            return rim;
        }

        private static Color GetNeutralFocusColor(Color panel)
        {
            // WCAG relative luminance gives a stable choice for dark, light, and custom-colored
            // panel presets. Neutral gray keeps the focus indicator distinct from the accent.
            double r = LinearChannel(panel.R / 255.0);
            double g = LinearChannel(panel.G / 255.0);
            double b = LinearChannel(panel.B / 255.0);
            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            return luminance < 0.42
                ? Color.FromRgb(0xD6, 0xD9, 0xDE)
                : Color.FromRgb(0x4A, 0x4E, 0x55);
        }

        private static double LinearChannel(double value)
        {
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// Fill for buttons/cards inside panels. Deliberately flat: a vertical light-to-dark gradient on every
        /// button makes them look puffy/convex. Depth comes from the panel sheen and rim instead.
        /// </summary>
        public static Brush CreateSurfaceBrush(OverlayThemeValues theme, Color color)
        {
            var solid = new SolidColorBrush(color);
            solid.Freeze();
            return solid;
        }

        /// <summary>Mixes <paramref name="other"/> into <paramref name="c"/> by <paramref name="amount"/>, keeping c's alpha.</summary>
        private static Color Tint(Color c, Color other, double amount)
        {
            Func<byte, byte, byte> mix = (x, y) => (byte)Math.Round(x * (1 - amount) + y * amount);
            return Color.FromArgb(c.A, mix(c.R, other.R), mix(c.G, other.G), mix(c.B, other.B));
        }

        private static Color Shift(Color c, int dr, int dg, int db)
        {
            return Color.FromArgb(c.A,
                (byte)Math.Max(0, Math.Min(255, c.R + dr)),
                (byte)Math.Max(0, Math.Min(255, c.G + dg)),
                (byte)Math.Max(0, Math.Min(255, c.B + db)));
        }

        /// <summary>Composites a translucent overlay color onto an opaque base and returns the opaque result.</summary>
        public static Color Blend(Color opaqueBase, Color overlay)
        {
            double a = overlay.A / 255.0;
            return Color.FromRgb(
                (byte)Math.Round(opaqueBase.R * (1 - a) + overlay.R * a),
                (byte)Math.Round(opaqueBase.G * (1 - a) + overlay.G * a),
                (byte)Math.Round(opaqueBase.B * (1 - a) + overlay.B * a));
        }

        private static byte ScaleAlpha(byte alpha, double strength)
        {
            return (byte)Math.Round(alpha * Math.Max(0, Math.Min(1, strength)));
        }

        public static Color Parse(string hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch { }
            return fallback;
        }

        public static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>The user's actual Windows accent color, lightened so it reads on a dark surface.</summary>
        public static string WindowsAccentHex()
        {
            try
            {
                Color c = SystemParameters.WindowGlassColor;
                double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                double mix = lum < 0.45 ? 0.40 : 0.15;
                Color lit = Color.FromRgb(
                    (byte)Math.Round(c.R + (255 - c.R) * mix),
                    (byte)Math.Round(c.G + (255 - c.G) * mix),
                    (byte)Math.Round(c.B + (255 - c.B) * mix));
                return ToHex(lit);
            }
            catch { return "#60CDFF"; }
        }

        private static Color Offset(Color c, int dr, int dg, int db)
        {
            return Color.FromRgb(
                (byte)Math.Max(0, Math.Min(255, c.R + dr)),
                (byte)Math.Max(0, Math.Min(255, c.G + dg)),
                (byte)Math.Max(0, Math.Min(255, c.B + db)));
        }
    }
}
