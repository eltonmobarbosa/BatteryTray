using HidSharp;
using HidSharp.Reports;

namespace BatteryTray;

// Any brand: devices that publish their battery the standard HID way, in a feature report.
// Same usages and scaling as the Linux kernel: Generic Device Controls "Battery Strength" and Battery System
// "Absolute State Of Charge", normalized over the item's logical range.
static class HidBattery
{
    static readonly uint[] BatteryUsages = [0x00060020, 0x00850065];
    static readonly Dictionary<string, Report[]> Cache = new(); // device path -> feature reports carrying a battery usage

    public static IEnumerable<Reading> Read()
    {
        var all = DeviceList.Local.GetHidDevices().ToList();
        var result = new List<Reading>();
        foreach (var dev in all)
        {
            if (dev.GetMaxFeatureReportLength() == 0) continue;
            if (!Cache.TryGetValue(dev.DevicePath, out var reports)) Cache[dev.DevicePath] = reports = BatteryReports(dev);
            if (reports.Length == 0 || !dev.TryOpen(out var s)) continue;
            using (s)
            {
                foreach (var report in reports)
                {
                    var level = ReadLevel(s, report, dev.GetMaxFeatureReportLength());
                    if (level is null) continue;
                    var siblings = all.Where(d => d.VendorID == dev.VendorID && d.ProductID == dev.ProductID);
                    result.Add(new($"usb:{dev.VendorID:X4}:{dev.ProductID:X4}", Name(dev), DeviceKinds.FromHid(siblings), level.Value));
                    break;
                }
            }
        }
        return result;
    }

    static Report[] BatteryReports(HidDevice dev)
    {
        try
        {
            return dev.GetReportDescriptor().FeatureReports
                .Where(r => r.DataItems.Any(i => i.IsVariable && BatteryUsages.Any(u => i.Usages.ContainsValue(u))))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    static int? ReadLevel(HidStream s, Report report, int length)
    {
        var buf = new byte[length];
        buf[0] = report.ReportID;
        try { s.GetFeature(buf); }
        catch (IOException) { return null; }

        int? level = null;
        report.Read(buf, 0, (b, bitOffset, item, _) =>
        {
            foreach (var usage in BatteryUsages)
            {
                if (level is null && item.LogicalRange > 0 && item.Usages.TryGetIndexFromValue(usage, out var index))
                    level = (item.ReadLogical(b, bitOffset, index) - item.LogicalMinimum) * 100 / item.LogicalRange;
            }
        });
        return level;
    }

    static string Name(HidDevice d)
    {
        try { return d.GetProductName(); }
        catch { return $"{d.VendorID:X4}:{d.ProductID:X4}"; }
    }
}
