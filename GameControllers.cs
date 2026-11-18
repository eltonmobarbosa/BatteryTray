using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryTray;

// Any brand: wireless controllers Windows itself reports battery for (Xbox and other Windows.Gaming.Input devices).
static class GameControllers
{
    public static IEnumerable<Reading> Read()
    {
        var result = new List<Reading>();
        // shortcut: Windows fills this list asynchronously, so the very first call after start may be empty; the next refresh picks them up.
        foreach (var c in RawGameController.RawGameControllers)
        {
            var report = c.TryGetBatteryReport();
            // Wired controllers report no capacity.
            if (report?.RemainingCapacityInMilliwattHours is int remaining && report.FullChargeCapacityInMilliwattHours is int full && full > 0)
                result.Add(new($"gi:{c.NonRoamableId}", c.DisplayName, DeviceKind.Gamepad, remaining * 100 / full, report.Status == BatteryStatus.Charging));
        }
        return result;
    }
}
