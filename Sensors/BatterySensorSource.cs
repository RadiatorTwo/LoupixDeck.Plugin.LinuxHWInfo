namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// The charge level of the batteries that power the machine, read from
/// <c>/sys/class/power_supply/&lt;name&gt;/capacity</c>. Silent on desktops, which have none.
/// </summary>
/// <remarks>
/// Only supplies of type <c>Battery</c> count. Peripherals report their batteries there too (a wireless
/// mouse, a game pad) and mark them with scope <c>Device</c>; those are skipped.
/// </remarks>
/// <param name="root">The power-supply class directory; only tests pass another one.</param>
internal sealed class BatterySensorSource(string root = "/sys/class/power_supply")
{
    public IEnumerable<LinuxHwInfoSensor> Poll()
    {
        List<LinuxHwInfoSensor> sensors = [];
        foreach (string dir in SysfsIo.EnumerateDirectories(root))
        {
            if (SysfsIo.ReadText(Path.Combine(dir, "type")) != "Battery"
                || string.Equals(SysfsIo.ReadText(Path.Combine(dir, "scope")), "Device", StringComparison.OrdinalIgnoreCase)
                || !SysfsIo.TryReadLong(Path.Combine(dir, "capacity"), out long capacity))
                continue;

            // The supply name (BAT0, BAT1) is assigned by ACPI and stays the same across reboots.
            string name = Path.GetFileName(dir);
            sensors.Add(new LinuxHwInfoSensor(
                Id: SensorId.Battery(name, "capacity"),
                Source: SensorSourceKind.Battery,
                Category: Categories.Battery,
                Group: name,
                Label: "Charge",
                Type: LinuxHwInfoReadingType.Usage,
                Unit: "%",
                Value: Math.Clamp(capacity, 0, 100),
                Max: 100.0));
        }

        return sensors;
    }
}
