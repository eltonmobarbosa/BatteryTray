using System.Drawing;
using Microsoft.Win32;

namespace BatteryTray;

// Colors that follow the Windows taskbar theme.
static class Theme
{
    const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool Light => Registry.GetValue(Personalize, "SystemUsesLightTheme", 0) is 1;

    public static Color Foreground(bool light) => light ? Color.FromArgb(27, 27, 27) : Color.White;

    public const int LowThreshold = 20;

    // Battery color, tuned per theme for contrast: blue charging, red <= 20%, amber <= 50%, green above.
    public static Color Level(int level, bool charging, bool light) =>
        charging ? (light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(76, 194, 255))
        : level <= LowThreshold ? (light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 97))
        : level <= 50 ? (light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(252, 196, 25))
        : light ? Color.FromArgb(16, 124, 16) : Color.FromArgb(108, 203, 95);

    public static Color Low(bool light) => Level(0, false, light);

    // Readable text on top of a colored badge.
    public static Color InkOn(Color background) =>
        background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 160 ? Color.Black : Color.White;
}
