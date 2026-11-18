using System.Drawing;
using System.Drawing.Drawing2D;
using HidSharp;

namespace BatteryTray;

enum DeviceKind { Mouse, Keyboard, Headset, Gamepad, Other }

static class DeviceKinds
{
    static readonly FontFamily Icons = new("Segoe MDL2 Assets"); // ships with Windows 10/11

    static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Headset => "",
        DeviceKind.Gamepad => "",
        _ => "",
    };

    public static string Label(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Mouse",
        DeviceKind.Keyboard => "Teclado",
        DeviceKind.Headset => "Headset",
        DeviceKind.Gamepad => "Controle",
        _ => "Dispositivo",
    };

    // Glyph as a vector path scaled to fill the box, so font metrics and line height never clip it.
    public static void DrawGlyph(Graphics g, DeviceKind kind, RectangleF box, Color color)
    {
        using var path = new GraphicsPath();
        path.AddString(Glyph(kind), Icons, 0, 100, PointF.Empty, StringFormat.GenericDefault);
        var b = path.GetBounds();
        var scale = Math.Min(box.Width / b.Width, box.Height / b.Height);
        using var m = new Matrix();
        m.Translate(box.X + (box.Width - b.Width * scale) / 2, box.Y + (box.Height - b.Height * scale) / 2);
        m.Scale(scale, scale);
        m.Translate(-b.X, -b.Y);
        path.Transform(m);
        using var brush = new SolidBrush(color);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = smoothing;
    }

    // The first USB interface (MI_00) carries the device's main function; extra interfaces are macro/vendor channels.
    public static DeviceKind FromHid(IEnumerable<HidDevice> siblings)
    {
        var primary = siblings.Where(d =>
            !d.DevicePath.Contains("&mi_", StringComparison.OrdinalIgnoreCase) ||
            d.DevicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase));
        foreach (var d in primary)
        {
            uint usage;
            try { usage = d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).FirstOrDefault(); }
            catch { continue; }
            switch (usage)
            {
                case 0x00010002: return DeviceKind.Mouse;
                case 0x00010006: return DeviceKind.Keyboard;
                case 0x00010004 or 0x00010005: return DeviceKind.Gamepad;
            }
        }
        return DeviceKind.Other;
    }
}
