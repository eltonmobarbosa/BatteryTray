using System.Text;
using HidSharp;

namespace BatteryTray;

// Logitech HID++ 2.0 over the long report (0x11, 20 bytes): Unifying, Lightspeed, Bolt receivers and wired devices.
static class Logitech
{
    const int Vid = 0x046D;
    const byte SoftwareId = 0x0A;

    public static IEnumerable<Reading> Read()
    {
        var result = new List<Reading>();
        foreach (var dev in DeviceList.Local.GetHidDevices(Vid))
        {
            if (dev.GetMaxOutputReportLength() != 20 || dev.GetMaxInputReportLength() != 20) continue;
            if (!dev.TryOpen(out var s)) continue;
            using (s)
            {
                s.ReadTimeout = 300;
                // 0xFF = the device itself (wired/direct); 1..6 = receiver slots.
                // shortcut: old HID++ 1.0 devices (pre-2013 Unifying) answer nothing here and are skipped.
                foreach (byte slot in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    if (Battery(s, slot) is not var (level, charging)) continue;
                    var (name, kind) = Info(s, slot);
                    result.Add(new($"usb:{Vid:X4}:{dev.ProductID:X4}:{slot}", name ?? $"Logitech {slot}", kind, level, charging));
                }
            }
        }
        return result;
    }

    static (int Level, bool Charging)? Battery(HidStream s, byte slot)
    {
        var unified = Feature(s, slot, 0x1004);
        if (unified != 0 && Call(s, slot, unified, 1) is { } u)
            // Percentage 0 means the device reports only coarse levels: bit 3 full, 2 good, 1 low, 0 critical.
            // Charging status: 1 charging, 2 slow charging, 3 complete (still plugged).
            return (u[0] != 0 ? u[0] : (u[1] & 8) != 0 ? 100 : (u[1] & 4) != 0 ? 60 : (u[1] & 2) != 0 ? 20 : 5, u[2] is 1 or 2 or 3);

        var status = Feature(s, slot, 0x1000);
        // Status: 1 recharging, 2 almost full, 3 full, 4 slow recharge.
        if (status != 0 && Call(s, slot, status, 0) is { } b) return (b[0], b[2] is >= 1 and <= 4);

        var voltage = Feature(s, slot, 0x1001);
        // Flags byte: bit 7 = charging.
        if (voltage != 0 && Call(s, slot, voltage, 0) is { } v) return (FromMillivolts(v[0] << 8 | v[1]), (v[2] & 0x80) != 0);

        return null;
    }

    // Li-ion discharge curve (same points Solaar uses).
    static readonly (int Mv, int Pct)[] Curve =
        [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];

    internal static int FromMillivolts(int mv)
    {
        if (mv >= Curve[0].Mv) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            var (hiMv, hiPct) = Curve[i - 1];
            var (loMv, loPct) = Curve[i];
            if (mv >= loMv) return loPct + (mv - loMv) * (hiPct - loPct) / (hiMv - loMv);
        }
        return 0;
    }

    // Feature 0x0005 DeviceName: function 0 = name length, 1 = name chunk, 2 = device type.
    static (string?, DeviceKind) Info(HidStream s, byte slot)
    {
        var idx = Feature(s, slot, 0x0005);
        if (idx == 0 || Call(s, slot, idx, 0) is not { } count) return (null, DeviceKind.Other);
        var name = new StringBuilder();
        while (name.Length < count[0] && Call(s, slot, idx, 1, (byte)name.Length) is { } chunk)
            name.Append(Encoding.ASCII.GetString(chunk, 0, Math.Min(16, count[0] - name.Length)));
        var kind = Call(s, slot, idx, 2) is { } t ? t[0] switch
        {
            0 or 2 => DeviceKind.Keyboard,
            3 or 4 or 5 => DeviceKind.Mouse,
            8 => DeviceKind.Headset,
            11 or 12 => DeviceKind.Gamepad,
            _ => DeviceKind.Other,
        } : DeviceKind.Other;
        return (name.Length > 0 ? name.ToString() : null, kind);
    }

    static byte Feature(HidStream s, byte slot, ushort id) =>
        Call(s, slot, 0x00, 0, (byte)(id >> 8), (byte)id) is { } r ? r[0] : (byte)0;

    static byte[]? Call(HidStream s, byte slot, byte feature, int function, params byte[] args)
    {
        var req = new byte[20];
        req[0] = 0x11;
        req[1] = slot;
        req[2] = feature;
        req[3] = (byte)(function << 4 | SoftwareId);
        args.CopyTo(req, 4);
        try { s.Write(req); }
        catch (IOException) { return null; }

        var buf = new byte[20];
        var deadline = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < deadline)
        {
            try { s.Read(buf); }
            catch (TimeoutException) { return null; }
            if (buf[1] != slot) continue;
            if (buf[2] is 0xFF or 0x8F && buf[3] == feature && buf[4] == req[3]) return null; // error reply
            if (buf[0] == 0x11 && buf[2] == feature && buf[3] == req[3]) return buf[4..];
        }
        return null;
    }
}
