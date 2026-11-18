using System.Diagnostics;
using System.Text.Json;

namespace BatteryTray;

// Headsets (SteelSeries, Corsair, HyperX, Logitech G, Roccat, ...) through the HeadsetControl CLI, if installed.
// https://github.com/Sapd/HeadsetControl — embedded in the exe at build time; one next to BatteryTray.exe or on PATH wins.
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
            .FirstOrDefault(File.Exists) ?? Bundled();
    }

    // The copy embedded at build time, written once next to the log; a user-installed one above takes precedence.
    static string? bundled;
    static string? Bundled()
    {
        if (bundled is not null && File.Exists(bundled)) return bundled;
        using var res = typeof(HeadsetControl).Assembly.GetManifestResourceStream("headsetcontrol.exe");
        if (res is null) return null;
        var path = Path.Combine(Log.Folder, "headsetcontrol.exe");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
            {
                Directory.CreateDirectory(Log.Folder);
                var tmp = path + ".tmp";
                using (var f = File.Create(tmp)) res.CopyTo(f);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (IOException) { return File.Exists(path) ? path : null; } // another instance (--list) writing it, or it is running
        catch (UnauthorizedAccessException) { return null; }
        return bundled = path;
    }
}
