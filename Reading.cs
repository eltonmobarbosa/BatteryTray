namespace BatteryTray;

// One battery reading. Id must stay the same across runs and renames: it keys the tray icon (and its pinned state).
readonly record struct Reading(string Id, string Name, DeviceKind Kind, int Level, bool Charging = false);
