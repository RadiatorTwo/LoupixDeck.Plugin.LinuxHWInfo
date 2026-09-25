using LoupixDeck.Plugin.LinuxHwInfo.Sensors;

namespace LoupixDeck.Plugin.LinuxHwInfo.Telemetry;

/// <summary>
/// The derived metrics the component pages show (CPU temperature, GPU clock, RAM free …). Each is
/// picked out of the snapshot by reading type, category and the stable sensor id, never by a label
/// the user sees: hwmon labels depend on the board and the driver, ids do not.
/// </summary>
internal static class PageMetrics
{
    public const string CpuTemp = "cpu.temp";
    public const string CpuClock = "cpu.clock";
    public const string CpuFan = "cpu.fan";
    public const string CpuLoad = "cpu.load";
    public const string CpuPower = "cpu.power";
    public const string GpuTemp = "gpu.temp";
    public const string GpuClock = "gpu.clock";
    public const string GpuFan = "gpu.fan";
    public const string GpuLoad = "gpu.load";
    public const string RamLoad = "ram.load";
    public const string RamUsed = "ram.used";
    public const string RamFree = "ram.free";
    public const string NetDown = "net.down";
    public const string NetUp = "net.up";
    public const string DiskTemp = "disk.temp";
    public const string DiskRead = "disk.read";
    public const string DiskWrite = "disk.write";

    /// <summary>What a definition reads from: the snapshot, the user's TjMax and the network
    /// interface the NET page follows (see <see cref="NetworkRoutes.Preferred"/>).</summary>
    public sealed record Context(IReadOnlyList<LinuxHwInfoSensor> Sensors, double TjMax, string? NetworkInterface);

    /// <summary>The interfaces that report transfer rates, in snapshot order.</summary>
    public static IReadOnlyList<string> Interfaces(IReadOnlyList<LinuxHwInfoSensor> sensors) => sensors
        .Where(s => s.Source == SensorSourceKind.Network && s.Type == LinuxHwInfoReadingType.Throughput)
        .Select(s => InterfaceOf(s.Id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>One derived metric: how to read it from a snapshot, and how to describe the value
    /// it read. Null when the snapshot has nothing for it.</summary>
    public sealed record Definition(string Id, Func<Context, (double Value, MetricInfo Info)?> Read);

    public static IReadOnlyList<Definition> All { get; } =
    [
        // k10temp reports Tdie only where Tctl carries an offset (Ryzen 1000/2000 X, Threadripper:
        // +10 to +27 °C), so Tdie is the real die temperature there. Otherwise the hottest CPU
        // reading: Tctl on AMD, the package on Intel.
        new(CpuTemp, c => Of(First(c.Sensors, s => s.Category == Categories.Cpu
                                                   && s.Type == LinuxHwInfoReadingType.Temperature
                                                   && s.Label.Equals("Tdie", StringComparison.OrdinalIgnoreCase))
                             ?? MaxBy(c.Sensors, s => s.Category == Categories.Cpu
                                                      && s.Type == LinuxHwInfoReadingType.Temperature),
            new MetricInfo(MetricFormat.Temperature, 30, c.TjMax, ThresholdKind.CpuTemperature))),
        new(CpuClock, c => Of(ById(c.Sensors, SensorId.Proc("cpu", "freq.max"))
                              ?? MaxBy(c.Sensors, s => s.Category == Categories.Cpu
                                                       && s.Type == LinuxHwInfoReadingType.Clock),
            new MetricInfo(MetricFormat.ClockMhz, 800, 5800, Smooth: true, GrowToPeak: true))),
        new(CpuFan, c => Of(CpuFanSensor(c.Sensors),
            new MetricInfo(MetricFormat.Rpm, 0, 2500, ThresholdKind.CpuFanStall, GrowToPeak: true))),
        new(CpuLoad, c => Of(ById(c.Sensors, SensorId.Proc("cpu", "total")),
            new MetricInfo(MetricFormat.Percent, 0, 100, Smooth: true))),
        new(CpuPower, c => Of(First(c.Sensors, s => s.Category == Categories.Cpu
                                                    && s.Type == LinuxHwInfoReadingType.Power),
            new MetricInfo(MetricFormat.Watt, 0, 100, GrowToPeak: true))),

        new(GpuTemp, c => Of(First(Gpu(c.Sensors), SensorMetrics.IsGpuCoreTemperature)
                             ?? First(Gpu(c.Sensors), s => s.Type == LinuxHwInfoReadingType.Temperature),
            new MetricInfo(MetricFormat.Temperature, 30, 95, ThresholdKind.GpuTemperature))),
        new(GpuClock, c => Of(First(Gpu(c.Sensors), s => s.Type == LinuxHwInfoReadingType.Clock
                                                         && !s.Id.EndsWith("/clock.mem", StringComparison.Ordinal)),
            new MetricInfo(MetricFormat.ClockMhz, 200, 3200, Smooth: true, GrowToPeak: true))),
        new(GpuFan, c => GpuFanReading(Gpu(c.Sensors))),
        new(GpuLoad, c => Of(First(Gpu(c.Sensors), s => s.Type == LinuxHwInfoReadingType.Usage
                                                        && !SensorMetrics.IsVram(s)
                                                        && !SensorMetrics.IsGpuFanPercent(s)),
            new MetricInfo(MetricFormat.Percent, 0, 100, Smooth: true))),

        new(RamLoad, c => Of(ById(c.Sensors, SensorId.Proc("mem", "percent")),
            new MetricInfo(MetricFormat.Percent, 0, 100, ThresholdKind.RamLoad))),
        new(RamUsed, c => Of(ById(c.Sensors, SensorId.Proc("mem", "used")),
            new MetricInfo(MetricFormat.Megabytes, 0, 0))),
        new(RamFree, c => ById(c.Sensors, SensorId.Proc("mem", "total")) is { } total
                          && ById(c.Sensors, SensorId.Proc("mem", "used")) is { } used
            ? (SensorMetrics.NativeValue(total) - SensorMetrics.NativeValue(used), new MetricInfo(MetricFormat.Megabytes, 0, 0))
            : null),

        // Receive is download, transmit is upload — the direction is part of the id.
        new(NetDown, c => Of(Network(c, "rx"), new MetricInfo(MetricFormat.BytesPerSecond, 0, 0))),
        new(NetUp, c => Of(Network(c, "tx"), new MetricInfo(MetricFormat.BytesPerSecond, 0, 0))),

        // The hottest drive by its main reading; any storage temperature only when no drive has one.
        new(DiskTemp, c => Of(MaxBy(c.Sensors, SensorMetrics.IsDriveTemperature)
                              ?? MaxBy(c.Sensors, s => s.Category == Categories.Storage
                                                       && s.Type == LinuxHwInfoReadingType.Temperature),
            new MetricInfo(MetricFormat.Temperature, 20, 80, ThresholdKind.StorageTemperature))),
        // All drives together: the page is about how busy storage is, not about one drive.
        new(DiskRead, c => DiskTotal(c.Sensors, "read")),
        new(DiskWrite, c => DiskTotal(c.Sensors, "write"))
    ];

    private static (double, MetricInfo)? Of(LinuxHwInfoSensor? sensor, MetricInfo info) =>
        sensor is null ? null : (SensorMetrics.NativeValue(sensor), info);

    private static LinuxHwInfoSensor? ById(IReadOnlyList<LinuxHwInfoSensor> sensors, string id) =>
        First(sensors, s => s.Id == id);

    private static LinuxHwInfoSensor? First(IEnumerable<LinuxHwInfoSensor> sensors, Func<LinuxHwInfoSensor, bool> match)
    {
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            if (match(sensor))
                return sensor;
        }

        return null;
    }

    private static LinuxHwInfoSensor? MaxBy(IReadOnlyList<LinuxHwInfoSensor> sensors, Func<LinuxHwInfoSensor, bool> match)
    {
        LinuxHwInfoSensor? max = null;
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            if (match(sensor) && (max is null || sensor.Value > max.Value))
                max = sensor;
        }

        return max;
    }

    /// <summary>
    /// The CPU fan: the header whose label names the CPU. Boards whose Super-I/O exposes no labels
    /// get the first mainboard fan that is spinning — an unused header reads 0 RPM and would show
    /// a stalled fan that does not exist.
    /// </summary>
    private static LinuxHwInfoSensor? CpuFanSensor(IReadOnlyList<LinuxHwInfoSensor> sensors) =>
        First(sensors, SensorMetrics.IsCpuFan)
        ?? First(sensors, s => s.Type == LinuxHwInfoReadingType.Fan && s.Category == Categories.Motherboard && s.Value > 0);

    /// <summary>
    /// The readings of one GPU, so the page never mixes two cards: the first NVIDIA card, else the
    /// first other GPU. A GPU's readings share their id up to the last '/' (NVML by UUID, amdgpu by
    /// PCI address — hwmon and DRM alike).
    /// </summary>
    private static IEnumerable<LinuxHwInfoSensor> Gpu(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        LinuxHwInfoSensor? anchor = First(sensors, s => s.Category == Categories.Gpu && s.Source == SensorSourceKind.Nvml)
                                    ?? First(sensors, s => s.Category == Categories.Gpu);
        if (anchor is null)
            return [];

        string device = DevicePrefix(anchor.Id);
        return sensors.Where(s => s.Category == Categories.Gpu && DevicePrefix(s.Id) == device);
    }

    private static string DevicePrefix(string id)
    {
        int slash = id.LastIndexOf('/');
        return slash < 0 ? id : id[..slash];
    }

    /// <summary>The GPU fan in RPM where the driver reports it (amdgpu), else NVML's duty cycle.</summary>
    private static (double, MetricInfo)? GpuFanReading(IEnumerable<LinuxHwInfoSensor> gpu)
    {
        List<LinuxHwInfoSensor> sensors = gpu.ToList();
        if (First(sensors, s => s.Type == LinuxHwInfoReadingType.Fan) is { } rpm)
            return Of(rpm, new MetricInfo(MetricFormat.Rpm, 0, 3300, ThresholdKind.GpuFanStall, GrowToPeak: true));

        return Of(First(sensors, SensorMetrics.IsGpuFanPercent), new MetricInfo(MetricFormat.Percent, 0, 100));
    }

    /// <summary>
    /// A transfer rate of the interface the page follows. The direction is the id's last segment
    /// (<c>net/&lt;iface&gt;/rx</c>), so it never depends on the order the kernel lists the
    /// counters in.
    /// </summary>
    private static LinuxHwInfoSensor? Network(Context context, string direction) =>
        context.NetworkInterface is { } iface ? ById(context.Sensors, SensorId.Net(iface, direction)) : null;

    private static string InterfaceOf(string id)
    {
        string[] parts = id.Split('/');
        return parts.Length >= 3 ? parts[1] : string.Empty;
    }

    private static (double, MetricInfo)? DiskTotal(IReadOnlyList<LinuxHwInfoSensor> sensors, string direction)
    {
        double? total = null;
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            if (sensor.Source == SensorSourceKind.Disk && sensor.Id.EndsWith("/" + direction, StringComparison.Ordinal))
                total = (total ?? 0) + SensorMetrics.NativeValue(sensor);
        }

        return total is { } value ? (value, new MetricInfo(MetricFormat.BytesPerSecond, 0, 0)) : null;
    }
}
