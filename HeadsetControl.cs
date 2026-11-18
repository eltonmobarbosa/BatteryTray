using System.Diagnostics;
using System.Text.Json;

namespace BatteryTray;

// Headsets (SteelSeries, Corsair, HyperX, Logitech G, Roccat, ...) through the HeadsetControl CLI, if installed.
// https://github.com/Sapd/HeadsetControl — put headsetcontrol.exe next to BatteryTray.exe or on PATH.
static class HeadsetControl
{
    public static IEnumerable<Reading> Read()
    {
        var exe = Find();
        if (exe is null) return [];

        using var p = Process.Start(new ProcessStartInfo(exe, "-b -o json")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(5000))
        {
            p.Kill();
            return [];
        }

        var result = new List<Reading>();
        using var json = JsonDocument.Parse(output.Result);
        if (!json.RootElement.TryGetProperty("devices", out var devices)) return result;
        foreach (var d in devices.EnumerateArray())
        {
            // Level is -1 when the headset is off (and on some models while charging).
            if (!d.TryGetProperty("battery", out var b) || !b.TryGetProperty("level", out var level) || level.GetInt32() < 0) continue;
            var name = d.GetProperty("device").GetString() ?? "Headset";
            var id = d.TryGetProperty("id_vendor", out var vendor) && d.TryGetProperty("id_product", out var product) ? $"hc:{vendor.GetString()}:{product.GetString()}" : $"hc:{name}";
            var charging = b.TryGetProperty("status", out var status) && status.GetString() == "BATTERY_CHARGING";
            result.Add(new(id, name, DeviceKind.Headset, level.GetInt32(), charging));
        }
        return result;
    }

    static string? Find()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "headsetcontrol.exe");
        if (File.Exists(local)) return local;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "headsetcontrol.exe"))
            .FirstOrDefault(File.Exists);
    }
}
