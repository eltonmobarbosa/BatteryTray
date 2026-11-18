using HidSharp;

namespace BatteryTray;

// Compx chip (VID 25A7), sold under Redragon and other brands; same protocol on the 2.4G dongle and on the mouse's own cable.
// Captured from the official Redragon M711-PRO software: command = feature report 0x08 (17 bytes),
// reply = input report 0x09 (17 bytes); every packet sums to 0x55. Only devices with exactly that report layout are queried.
//   cmd 0x03 -> reply[6]: 1 = mouse linked, 0 = off (or on the cable; then the cable device answers instead)
//   cmd 0x04 -> reply[6]: battery in 10% steps (checked against the official app at 20% and 30%), reply[7]: 1 = charging
// Without the 0x03 check, a dongle keeps returning the last battery of a mouse that is already off.
static class Compx
{
    const int Vid = 0x25A7;
    const byte GetStatus = 0x03;
    public const byte GetBattery = 0x04;

    public static IEnumerable<Reading> Read()
    {
        var result = new List<Reading>();
        var all = DeviceList.Local.GetHidDevices(Vid).ToList();
        foreach (var (_, cmd, reply) in Dongles())
        {
            if (Query(cmd, reply, GetStatus) is not { } status || status[6] == 0) continue;
            if (Query(cmd, reply, GetBattery) is not { } battery) continue;
            var name = Name(cmd);
            // Keyed by model, not PID: the mouse has one PID on the dongle and another on the cable, and the icon must stay the same.
            // A second identical mouse gets "#2" (in a stable order), so it keeps its own icon.
            var id = $"compx:{name}";
            var same = result.Count(r => r.Id == id || r.Id.StartsWith(id + "#"));
            var kind = DeviceKinds.FromHid(all.Where(d => d.ProductID == cmd.ProductID));
            result.Add(new(same == 0 ? id : $"{id}#{same + 1}", name, kind, battery[6] * 10, battery[7] == 1));
        }
        return result;
    }

    // Command and reply collections of each device that matches the protocol's report layout, one entry per USB interface.
    internal static IEnumerable<(string Interface, HidDevice Cmd, HidDevice Reply)> Dongles()
    {
        foreach (var iface in DeviceList.Local.GetHidDevices(Vid).GroupBy(HidPaths.InterfaceKey).OrderBy(g => g.Key))
        {
            var cmd = iface.FirstOrDefault(d => d.GetMaxFeatureReportLength() == 17 && HasFeatureReport(d, 0x08));
            var reply = iface.FirstOrDefault(d => d.GetMaxInputReportLength() == 17);
            if (cmd is not null && reply is not null) yield return (iface.Key, cmd, reply);
        }
    }

    internal static byte[]? Query(HidDevice cmd, HidDevice reply, byte command)
    {
        if (!cmd.TryOpen(out var cmdStream)) return null;
        using (cmdStream)
        {
            if (!reply.TryOpen(out var replyStream)) return null;
            using (replyStream)
            {
                var packet = new byte[17];
                packet[0] = 0x08;
                packet[1] = command;
                packet[16] = Checksum(packet);
                replyStream.ReadTimeout = 1000;
                cmdStream.SetFeature(packet);

                var buf = new byte[17];
                var deadline = Environment.TickCount64 + 1000;
                while (Environment.TickCount64 < deadline)
                {
                    try { replyStream.Read(buf); }
                    catch (TimeoutException) { return null; }
                    if (buf[0] == 0x09 && buf[1] == command) return buf;
                }
                return null;
            }
        }
    }

    internal static byte Checksum(byte[] packet) => (byte)(0x55 - packet.Take(16).Sum(b => b));

    static bool HasFeatureReport(HidDevice d, byte id)
    {
        try { return d.GetReportDescriptor().FeatureReports.Any(r => r.ReportID == id); }
        catch { return false; }
    }

    static readonly Dictionary<int, string?> Models = new();

    static string Name(HidDevice d)
    {
        if (!Models.TryGetValue(d.ProductID, out var model)) Models[d.ProductID] = model = ModelFromVendorSoftware(d.ProductID);
        if (model is not null) return model;
        try { return d.GetProductName(); }
        catch { return $"{Vid:X4}:{d.ProductID:X4}"; }
    }

    // The device only reports "2.4G Wireless Receiver"; vendor apps for this chip install a Cfg.ini with the model title and its PIDs.
    static string? ModelFromVendorSoftware(int pid)
    {
        var roots = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(Environment.GetFolderPath).Where(Directory.Exists).Distinct();
        foreach (var dir in roots.SelectMany(Directory.EnumerateDirectories))
        {
            var ini = Path.Combine(dir, "Cfg.ini");
            if (!File.Exists(ini)) continue;
            var values = File.ReadLines(ini, System.Text.Encoding.Latin1)
                .Select(l => l.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .GroupBy(kv => kv[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            int Hex(string key) => values.TryGetValue(key, out var v) && v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(v, 16) : -1;
            if (Hex("VID") == Vid && (Hex("PID") == pid || Hex("PID2") == pid) && values.TryGetValue("Title", out var title) && title.Length > 0)
                return title;
        }
        return null;
    }
}
