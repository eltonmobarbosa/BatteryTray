using Windows.Devices.Enumeration;

namespace BatteryTray;

// Battery that Windows itself reads: BLE Battery Service and Classic headsets (HFP).
static class Bluetooth
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery
    const string ContainerKey = "System.Devices.ContainerId";
    const string AepConnected = "System.Devices.Aep.IsConnected";
    const string AepContainer = "System.Devices.Aep.ContainerId";
    const string CodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    const string CodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    const string LeCategory = "System.Devices.Aep.Bluetooth.Le.Appearance.Category";
    const string LeSubcategory = "System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory";
    const string ClassicOrLe =
        "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
        "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";

    public static IEnumerable<Reading> Read() => ReadAsync().GetAwaiter().GetResult();

    static async Task<List<Reading>> ReadAsync()
    {
        // Windows keeps the last battery value of paired devices that are off, so only trust connected ones.
        var endpoints = await DeviceInformation.FindAllAsync(ClassicOrLe,
            [AepConnected, AepContainer, CodMajor, CodMinor, LeCategory, LeSubcategory], DeviceInformationKind.AssociationEndpoint);
        var connected = new Dictionary<Guid, DeviceKind>();
        foreach (var e in endpoints)
        {
            if (e.Properties.TryGetValue(AepConnected, out var c) && c is true && e.Properties.TryGetValue(AepContainer, out var id) && id is Guid g)
                connected[g] = KindOf(e.Properties);
        }

        var devices = await DeviceInformation.FindAllAsync("", [BatteryKey, ContainerKey], DeviceInformationKind.Device);
        var result = new List<Reading>();
        foreach (var d in devices)
        {
            if (d.Properties.TryGetValue(BatteryKey, out var v) && v is not null
                && d.Properties.TryGetValue(ContainerKey, out var c) && c is Guid g && connected.TryGetValue(g, out var kind))
                result.Add(new($"bt:{g}", d.Name, kind, Convert.ToInt32(v)));
        }
        return result;
    }

    static DeviceKind KindOf(IReadOnlyDictionary<string, object> p)
    {
        int Get(string key) => p.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : -1;

        // BLE appearance: category 0x0F = HID (subcategory 1 keyboard, 2 mouse, 3/4 joystick/gamepad); 0x21-0x25 = audio.
        var category = Get(LeCategory);
        if (category == 0x0F)
            return Get(LeSubcategory) switch { 1 => DeviceKind.Keyboard, 2 => DeviceKind.Mouse, 3 or 4 => DeviceKind.Gamepad, _ => DeviceKind.Other };
        if (category is >= 0x21 and <= 0x25) return DeviceKind.Headset;

        // Classic Class of Device: major 4 = audio/video, major 5 = peripheral (minor bit 4 keyboard, bit 5 pointing).
        var major = Get(CodMajor);
        if (major == 4) return DeviceKind.Headset;
        if (major == 5)
        {
            var minor = Get(CodMinor);
            if ((minor & 0x20) != 0 && (minor & 0x10) == 0) return DeviceKind.Mouse;
            if ((minor & 0x10) != 0) return DeviceKind.Keyboard;
            if ((minor & 0x0F) is 1 or 2) return DeviceKind.Gamepad;
        }
        return DeviceKind.Other;
    }
}
