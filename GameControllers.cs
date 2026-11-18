using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryTray;

// Any brand: wireless controllers Windows itself reports battery for (Xbox and other Windows.Gaming.Input devices).
static class GameControllers
{
    static bool primed;

    public static IEnumerable<Reading> Read()
    {
        // Windows fills the list asynchronously after first use; give it a moment once instead of missing controllers on the first read.
        if (!primed)
        {
            primed = true;
            for (int i = 0; i < 10 && RawGameController.RawGameControllers.Count == 0; i++) Thread.Sleep(50);
        }

        var result = new List<Reading>();
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
