using System;
using Eto.Drawing;
using Rhino.ApplicationSettings;

namespace StructuredViews
{
    /// <summary>
    /// Colours for everything the panel draws itself. On Mac Eto's system colours follow Rhino's light or
    /// dark appearance. On Windows they report the Windows theme, not Rhino's dark mode (black text on a
    /// dark panel), so the text colour comes from Rhino's own paint colours there, checked for contrast
    /// against the panel background.
    /// </summary>
    public static class Theme
    {
        public static Color Text => Rhino.Runtime.HostUtils.RunningOnWindows ? WindowsText() : SystemColors.ControlText;

        public static Color Accent => SystemColors.Highlight;

        public static Color AccentText => Colors.White;

        static Color WindowsText()
        {
            try
            {
                var background = Paint(PaintColor.PanelBackground);
                var text = Paint(PaintColor.TextEnabled);
                var dark = background.A > 0 ? Luminance(background) < 0.5f : Rhino.Runtime.HostUtils.RunningInDarkMode;
                if (text.A > 0 && Math.Abs(Luminance(text) - (background.A > 0 ? Luminance(background) : dark ? 0.15f : 0.9f)) > 0.4f)
                    return text;
                return dark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(28, 28, 28);
            }
            catch (Exception)
            {
                return Rhino.Runtime.HostUtils.RunningInDarkMode ? Color.FromArgb(232, 232, 232) : SystemColors.ControlText;
            }
        }

        static Color Paint(PaintColor which)
        {
            var c = AppearanceSettings.GetPaintColor(which, true);
            return Color.FromArgb(c.R, c.G, c.B, c.A);
        }

        static float Luminance(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
    }
}
