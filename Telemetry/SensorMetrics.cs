using LoupixDeck.Plugin.LinuxHwInfo.Sensors;

namespace LoupixDeck.Plugin.LinuxHwInfo.Telemetry;

/// <summary>
/// Describes a single Linux sensor as a metric: which native value to track, how to format it and
/// which alert rule applies. Used for every sensor, so any sensor a user puts on a tile gets a
/// history and a state. Everything is derived from the reading type, the unit, the category and
/// the stable id — never from a label.
/// </summary>
internal static class SensorMetrics
{
    private const double MegabytesPerGigabyte = 1024.0;

    /// <summary>The value to track, in the unit <see cref="Describe"/> formats: transfer rates are
    /// normalized to bytes per second, sizes to megabytes, everything else is the sensor's own
    /// value.</summary>
    public static double NativeValue(LinuxHwInfoSensor sensor) => sensor.Type switch
    {
        LinuxHwInfoReadingType.Throughput => MetricFormatter.ToBytesPerSecond(sensor.Value, sensor.Unit) ?? sensor.Value,
        LinuxHwInfoReadingType.Data when IsGigabytes(sensor) => sensor.Value * MegabytesPerGigabyte,
        _ => sensor.Value
    };

    public static MetricInfo Describe(LinuxHwInfoSensor sensor, double tjMax)
    {
        bool percent = sensor.Unit.Trim() == "%";

        return sensor.Type switch
        {
            LinuxHwInfoReadingType.Temperature when sensor.Category == Categories.Cpu =>
                new MetricInfo(MetricFormat.Temperature, 30, tjMax, ThresholdKind.CpuTemperature),
            // Only the GPU core has the design's 80/88 limits; hot-spot and memory run hotter by design.
            LinuxHwInfoReadingType.Temperature when sensor.Category == Categories.Gpu =>
                new MetricInfo(MetricFormat.Temperature, 30, 95,
                    IsGpuCoreTemperature(sensor) ? ThresholdKind.GpuTemperature : ThresholdKind.None),
            // Only a drive's main reading has the storage limits; NVMe controller sensors run hotter.
            LinuxHwInfoReadingType.Temperature when sensor.Category == Categories.Storage =>
                new MetricInfo(MetricFormat.Temperature, 20, 80,
                    IsDriveTemperature(sensor) ? ThresholdKind.StorageTemperature : ThresholdKind.None),
            LinuxHwInfoReadingType.Temperature =>
                new MetricInfo(MetricFormat.Temperature, 20, 100),

            _ when sensor.Id == SensorId.Proc("mem", "percent") =>
                new MetricInfo(MetricFormat.Percent, 0, 100, ThresholdKind.RamLoad),
            LinuxHwInfoReadingType.Usage when percent && !IsVram(sensor) && !IsGpuFanPercent(sensor)
                                              && sensor.Category is Categories.Cpu or Categories.Gpu =>
                new MetricInfo(MetricFormat.Percent, 0, 100, Smooth: true),

            LinuxHwInfoReadingType.Fan =>
                new MetricInfo(MetricFormat.Rpm, 0, 3000, FanRule(sensor), GrowToPeak: true),

            // A hardware limit (NVML's enforced power limit) is the natural full scale.
            LinuxHwInfoReadingType.Power when sensor.Max is > 0 =>
                new MetricInfo(MetricFormat.Watt, 0, sensor.Max.Value),
            LinuxHwInfoReadingType.Power =>
                new MetricInfo(MetricFormat.Watt, 0, 100, GrowToPeak: true),

            LinuxHwInfoReadingType.Clock when sensor.Category == Categories.Gpu =>
                new MetricInfo(MetricFormat.ClockMhz, 0, 3200, Smooth: true, GrowToPeak: true),
            LinuxHwInfoReadingType.Clock =>
                new MetricInfo(MetricFormat.ClockMhz, 0, 6000, Smooth: true, GrowToPeak: true),

            LinuxHwInfoReadingType.Data when IsGigabytes(sensor) =>
                new MetricInfo(MetricFormat.Megabytes, 0, (sensor.Max ?? 0) * MegabytesPerGigabyte),

            LinuxHwInfoReadingType.Throughput =>
                new MetricInfo(MetricFormat.BytesPerSecond, 0, 0),

            _ when percent => new MetricInfo(MetricFormat.Percent, 0, 100),
            _ => new MetricInfo(MetricFormat.Generic, 0, 0, Unit: sensor.Unit)
        };
    }

    /// <summary>The GPU core temperature: NVML's GPU temperature, or amdgpu's "edge" channel —
    /// a kernel driver label, not a user-facing one.</summary>
    public static bool IsGpuCoreTemperature(LinuxHwInfoSensor sensor) =>
        sensor.Type == LinuxHwInfoReadingType.Temperature
        && ((sensor.Source == SensorSourceKind.Nvml && sensor.Id.EndsWith("/temp", StringComparison.Ordinal))
            || (sensor.Source == SensorSourceKind.Hwmon && sensor.Category == Categories.Gpu
                && sensor.Label.Equals("edge", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A drive's main temperature: hwmon channel temp1, which is NVMe's "Composite" — the value the
    /// drive's own warning limits refer to — and drivetemp's only channel. NVMe "Sensor 1/2"
    /// (controller, flash) routinely run 10–20 °C hotter within spec.
    /// </summary>
    public static bool IsDriveTemperature(LinuxHwInfoSensor sensor) =>
        sensor.Type == LinuxHwInfoReadingType.Temperature
        && sensor.Category == Categories.Storage
        && sensor.Id.EndsWith("/temp1", StringComparison.Ordinal);

    /// <summary>Video memory readings — NVML's memory fields and amdgpu's VRAM counters.</summary>
    public static bool IsVram(LinuxHwInfoSensor sensor) =>
        sensor.Category == Categories.Gpu
        && (EndsWithField(sensor, "mem.used", "mem.total", "mem.percent", "util.mem")
            || sensor.Id.Contains("/drm.vram_", StringComparison.Ordinal));

    /// <summary>NVML reports its fan as a duty cycle in percent rather than RPM.</summary>
    public static bool IsGpuFanPercent(LinuxHwInfoSensor sensor) =>
        sensor.Source == SensorSourceKind.Nvml && EndsWithField(sensor, "fan");

    /// <summary>A mainboard fan header that names the CPU ("CPU_FAN", "CPU Fan").</summary>
    public static bool IsCpuFan(LinuxHwInfoSensor sensor) =>
        sensor.Type == LinuxHwInfoReadingType.Fan
        && sensor.Category is Categories.Motherboard or Categories.Cpu
        && sensor.Label.Contains("CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only fans known to cool the CPU or GPU watch a temperature. Every other header could be an
    /// unused channel reading 0 RPM, which must not raise an alarm.
    /// </summary>
    private static ThresholdKind FanRule(LinuxHwInfoSensor sensor)
    {
        if (IsCpuFan(sensor))
            return ThresholdKind.CpuFanStall;

        return sensor.Category == Categories.Gpu ? ThresholdKind.GpuFanStall : ThresholdKind.None;
    }

    private static bool IsGigabytes(LinuxHwInfoSensor sensor) =>
        sensor.Unit.Trim().Equals("GB", StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithField(LinuxHwInfoSensor sensor, params string[] fields)
    {
        foreach (string field in fields)
        {
            if (sensor.Id.EndsWith("/" + field, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
