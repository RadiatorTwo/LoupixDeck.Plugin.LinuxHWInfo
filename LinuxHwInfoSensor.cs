namespace LoupixDeck.Plugin.LinuxHwInfo;

/// <summary>Which subsystem a sensor was collected from — used for menu grouping.</summary>
public enum SensorSourceKind
{
    Hwmon,
    Proc,
    Nvml,
    Network,
    Disk
}

/// <summary>
/// One reading from one channel, flattened out of whichever Linux interface produced it.
/// </summary>
/// <param name="Id">Stable identifier persisted into user button configurations — see
/// <see cref="Sensors.SensorId"/>. Never change how this is built for an existing sensor.</param>
/// <param name="Source">Collecting subsystem.</param>
/// <param name="Category">Top-level menu bucket ("CPU", "GPU", "Motherboard", ...).</param>
/// <param name="Group">Device instance label ("NVMe 1", "DIMM A", "GeForce RTX 4090").</param>
/// <param name="Label">Channel label ("Tctl", "CPUTIN", "GPU Temp").</param>
/// <param name="Type">Physical quantity.</param>
/// <param name="Unit">Display unit ("°C", "RPM", "%", "W", "MHz", "V", "A", "GB", "MB/s").</param>
/// <param name="Value">Current value already scaled into <paramref name="Unit"/>.</param>
/// <param name="Max">Upper bound for the gauge when the hardware reports one (hwmon _max/_crit,
/// NVML enforced power limit), otherwise null — the builder falls back to a per-type default.</param>
public sealed record LinuxHwInfoSensor(
    string Id,
    SensorSourceKind Source,
    string Category,
    string Group,
    string Label,
    LinuxHwInfoReadingType Type,
    string Unit,
    double Value,
    double? Max = null);
