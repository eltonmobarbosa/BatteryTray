using HidSharp;

namespace BatteryTray;

// Fantech Kanata WG9RD (VID B6A4). Protocol as documented in MarcoVilera/peripheral-battery:
// feature report 6 (41 bytes with id): request [3] = 0x80|cmd 1 (connection + battery), [4] = sequence, [8] = 3 bytes wanted;
// reply [8] = status, [9] = mouse linked, [10] = bit 7 charging + bits 0-6 percent.
static class Fantech
{
    const int Vid = 0xB6A4;
    static readonly Dictionary<int, string> Models = new() { [0x40A1] = "Fantech Kanata WG9RD", [0x40A0] = "Fantech Kanata WG9RD" };
    static byte sequence;

    public static IEnumerable<Reading> Read()
    {
        var result = new List<Reading>();
        foreach (var dev in DeviceList.Local.GetHidDevices(Vid))
        {
            if (!Models.TryGetValue(dev.ProductID, out var name) || dev.GetMaxFeatureReportLength() != 41 || !dev.TryOpen(out var s)) continue;
            using (s)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var req = new byte[41];
                    req[0] = 6;
                    req[3] = 0x81;
                    req[4] = ++sequence;
                    req[8] = 3;
                    s.SetFeature(req);
                    Thread.Sleep(80);
                    var resp = new byte[41];
                    resp[0] = 6;
                    s.GetFeature(resp);
                    if (resp[8] == 0) continue; // not ready yet
                    if (resp[9] != 0) // 0 = dongle without linked mouse
                        result.Add(new($"usb:{Vid:X4}:{dev.ProductID:X4}", name, DeviceKind.Mouse, Math.Min(resp[10] & 0x7F, 100), (resp[10] & 0x80) != 0));
                    break;
                }
            }
        }
        return result;
    }
}
