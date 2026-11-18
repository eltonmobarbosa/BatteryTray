using HidSharp;

namespace BatteryTray;

// Razer 90-byte feature report protocol (as documented by OpenRazer). Command class 0x07 (power):
// id 0x80 = battery level (0-255), id 0x84 = charging (1/0).
static class Razer
{
    const int Vid = 0x1532;
    // Transaction id varies by model; try the known ones until one answers.
    static readonly byte[] TransactionIds = [0x1F, 0xFF, 0x3F, 0x9F];

    public static IEnumerable<Reading> Read()
    {
        var result = new List<Reading>();
        foreach (var dev in DeviceList.Local.GetHidDevices(Vid))
        {
            if (dev.GetMaxFeatureReportLength() != 91 || !dev.TryOpen(out var s)) continue;
            using (s)
            {
                foreach (var tid in TransactionIds)
                {
                    var level = Query(s, tid, 0x80);
                    if (level is null) continue;
                    var kind = DeviceKinds.FromHid(DeviceList.Local.GetHidDevices(Vid, dev.ProductID));
                    result.Add(new($"usb:{Vid:X4}:{dev.ProductID:X4}", dev.GetProductName(), kind, level.Value * 100 / 255, Query(s, tid, 0x84) == 1));
                    break;
                }
            }
        }
        return result;
    }

    static int? Query(HidStream s, byte transactionId, byte command)
    {
        // Byte 0 is the HID report id (0); the Razer packet starts at byte 1.
        var req = new byte[91];
        req[2] = transactionId;
        req[6] = 0x02;    // data size
        req[7] = 0x07;    // command class: power
        req[8] = command;
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= req[i];
        req[89] = crc;

        try
        {
            s.SetFeature(req);
            Thread.Sleep(60); // device needs time to fill the reply
            var resp = new byte[91];
            s.GetFeature(resp);
            // Status 0x02 = success; anything else (busy, offline, unsupported) means no reading.
            return resp[1] == 0x02 && resp[7] == 0x07 && resp[8] == command ? resp[10] : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
