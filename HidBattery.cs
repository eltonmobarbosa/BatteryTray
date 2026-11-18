using System.Runtime.InteropServices;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray;

// Any brand: devices that publish their battery the standard HID way, in a feature or input report.
// Same usages and scaling as the Linux kernel: Generic Device Controls "Battery Strength" and Battery System
// "Absolute State Of Charge", normalized over the item's logical range.
static class HidBattery
{
    static readonly uint[] BatteryUsages = [0x00060020, 0x00850065];
    static readonly Dictionary<string, Report[]> Cache = new(); // device path -> reports carrying a battery usage

    public static IEnumerable<Reading> Read()
    {
        var all = DeviceList.Local.GetHidDevices().ToList();
        var result = new List<Reading>();
        foreach (var dev in all)
        {
            if (!Cache.TryGetValue(dev.DevicePath, out var reports)) Cache[dev.DevicePath] = reports = BatteryReports(dev);
            foreach (var report in reports)
            {
                var level = ReadLevel(dev, report);
                if (level is null) continue;
                var siblings = all.Where(d => d.VendorID == dev.VendorID && d.ProductID == dev.ProductID);
                result.Add(new($"usb:{dev.VendorID:X4}:{dev.ProductID:X4}", Name(dev), DeviceKinds.FromHid(siblings), level.Value));
                break;
            }
        }
        return result;
    }

    static Report[] BatteryReports(HidDevice dev)
    {
        try
        {
            var descriptor = dev.GetReportDescriptor();
            return descriptor.FeatureReports.Concat(descriptor.InputReports)
                .Where(r => r.DataItems.Any(i => i.IsVariable && BatteryUsages.Any(u => i.Usages.ContainsValue(u))))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    static int? ReadLevel(HidDevice dev, Report report)
    {
        var feature = report.ReportType == ReportType.Feature;
        var buf = new byte[feature ? dev.GetMaxFeatureReportLength() : dev.GetMaxInputReportLength()];
        buf[0] = report.ReportID;
        // Asks the device for the report now; an input report would otherwise only arrive when the device decides to send it.
        if (!Get(dev.DevicePath, buf, feature, 0) && !Get(dev.DevicePath, buf, feature, GenericRead | GenericWrite)) return null;

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

    // Raw handle instead of HidSharp: access 0 still allows report requests on mouse/keyboard collections Windows keeps for itself.
    static bool Get(string path, byte[] buf, bool feature, uint access)
    {
        using var h = CreateFileW(path, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;
        return feature ? HidD_GetFeature(h, buf, buf.Length) : HidD_GetInputReport(h, buf, buf.Length);
    }

    static string Name(HidDevice d)
    {
        try { return d.GetProductName(); }
        catch { return $"{d.VendorID:X4}:{d.ProductID:X4}"; }
    }

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareReadWrite = 3, OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int length);
}
