using System.Drawing;
using Microsoft.Win32;

namespace BatteryTray;

// Colors that follow the Windows taskbar theme and accent color.
static class Theme
{
    const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool Light => Registry.GetValue(Personalize, "SystemUsesLightTheme", 0) is 1;

    public static Color Accent =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr
            ? Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF)
            : Color.FromArgb(0, 120, 212);

    public static Color Foreground(bool light) => light ? Color.FromArgb(27, 27, 27) : Color.White;
    public static Color Low(bool light) => light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 97);
    public const int LowThreshold = 20;
}
