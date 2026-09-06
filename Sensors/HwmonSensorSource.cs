using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Walks <c>/sys/class/hwmon</c> and turns every readable channel into a <see cref="LinuxHwInfoSensor"/>.
/// </summary>
/// <remarks>
/// The <c>hwmonN</c> directory names are assigned in driver probe order and are <b>not</b> stable across
/// reboots, and several chips share the same <c>name</c> (three NVMe controllers, two jc42 DIMM sensors on
/// a typical desktop). Sensor identity is therefore derived from the bus topology behind the hwmon node —
/// the real path of its <c>device</c> symlink — which is built from immutable wiring (PCI slot, i2c bus +
/// address, Super-I/O base address) rather than enumeration order. See <see cref="ResolveDeviceKey"/>.
/// </remarks>
internal sealed partial class HwmonSensorSource
{
    private const string HwmonRoot = "/sys/class/hwmon";

    /// <summary>Channel prefixes we surface, with their sysfs raw unit divisor and display unit.</summary>
    private static readonly (string Prefix, LinuxHwInfoReadingType Type, double Divisor, string Unit)[] ChannelTypes =
    [
        ("temp", LinuxHwInfoReadingType.Temperature, 1000.0, "°C"),
        ("fan", LinuxHwInfoReadingType.Fan, 1.0, "RPM"),
        ("in", LinuxHwInfoReadingType.Voltage, 1000.0, "V"),
        ("power", LinuxHwInfoReadingType.Power, 1_000_000.0, "W"),
        ("curr", LinuxHwInfoReadingType.Current, 1000.0, "A")
    ];

    /// <summary>Number of hwmon chips seen during the most recent walk — surfaced in the status action.</summary>
    public int ChipCount { get; private set; }

    public IEnumerable<LinuxHwInfoSensor> Poll()
    {
        List<HwmonChip> chips = DiscoverChips();
        ChipCount = chips.Count;

        List<LinuxHwInfoSensor> sensors = [];
        foreach (HwmonChip chip in chips)
            CollectChannels(chip, sensors);

        return sensors;
    }

    /// <summary>
    /// Enumerates the hwmon nodes and assigns each one a display group. Chips whose <c>name</c> appears more
    /// than once get a trailing instance number, ordered by device key so the numbering is deterministic and
    /// independent of the kernel's probe order.
    /// </summary>
    private static List<HwmonChip> DiscoverChips()
    {
        List<HwmonChip> chips = [];

        foreach (string dir in SysfsIo.EnumerateDirectories(HwmonRoot))
        {
            string? name = SysfsIo.ReadText(Path.Combine(dir, "name"));
            if (string.IsNullOrEmpty(name))
                continue;

            string deviceKey = BusKey.Resolve(Path.Combine(dir, "device"), Path.GetFileName(dir));

            // A drive's model is far more useful than "NVMe 2", and it is the same string the block layer
            // reports, so a drive's temperatures and its throughput end up in one device group.
            string? model = SysfsIo.ReadText(Path.Combine(dir, "device", "model"));
            string displayName = string.IsNullOrWhiteSpace(model) ? ChipNaming.PrettyName(name) : model.Trim();

            chips.Add(new HwmonChip(dir, name, deviceKey, displayName));
        }

        chips.Sort((a, b) =>
        {
            int byName = string.CompareOrdinal(a.DisplayName, b.DisplayName);
            return byName != 0 ? byName : string.CompareOrdinal(a.DeviceKey, b.DeviceKey);
        });

        Dictionary<string, int> totals = new(StringComparer.Ordinal);
        foreach (HwmonChip chip in chips)
            totals[chip.DisplayName] = totals.GetValueOrDefault(chip.DisplayName) + 1;

        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < chips.Count; i++)
        {
            HwmonChip chip = chips[i];
            if (totals[chip.DisplayName] <= 1)
                continue;

            int index = seen.GetValueOrDefault(chip.DisplayName) + 1;
            seen[chip.DisplayName] = index;
            chips[i] = chip with { DisplayName = $"{chip.DisplayName} {index}" };
        }

        return chips;
    }

    private static void CollectChannels(HwmonChip chip, List<LinuxHwInfoSensor> sensors)
    {
        foreach ((string prefix, LinuxHwInfoReadingType type, double divisor, string unit) in ChannelTypes)
        {
            foreach (string inputPath in SysfsIo.EnumerateFiles(chip.Directory, $"{prefix}*_input"))
            {
                Match match = ChannelPattern(prefix).Match(Path.GetFileName(inputPath));
                if (!match.Success)
                    continue;

                int channel = int.Parse(match.Groups[1].Value);

                // A channel that fails to read is skipped on its own — the rest of the chip still reports.
                if (!SysfsIo.TryReadLong(inputPath, out long raw))
                    continue;

                string prefixed = $"{prefix}{channel}";
                string? label = SysfsIo.ReadText(Path.Combine(chip.Directory, $"{prefixed}_label"));
                double? max = PlausibleLimit(type,
                    SysfsIo.ReadScaled(Path.Combine(chip.Directory, $"{prefixed}_crit"), divisor)
                    ?? SysfsIo.ReadScaled(Path.Combine(chip.Directory, $"{prefixed}_max"), divisor));

                sensors.Add(new LinuxHwInfoSensor(
                    Id: SensorId.Hwmon(chip.DeviceKey, chip.Name, prefixed),
                    Source: SensorSourceKind.Hwmon,
                    Category: ChipNaming.Category(chip.Name),
                    Group: chip.DisplayName,
                    Label: string.IsNullOrEmpty(label) ? $"{chip.DisplayName} {prefix}{channel}" : label,
                    Type: type,
                    Unit: unit,
                    Value: raw / divisor,
                    Max: max));
            }
        }
    }

    /// <summary>
    /// Rejects limits the hardware clearly did not mean. Some NVMe controllers report a <c>temp*_max</c> of
    /// 65261800 m°C; taken at face value it would flatten every gauge on that drive to zero. Only the gauge
    /// scale is affected — the reading itself is always shown.
    /// </summary>
    private static double? PlausibleLimit(LinuxHwInfoReadingType type, double? max)
    {
        if (max is not > 0)
            return null;

        double ceiling = type switch
        {
            LinuxHwInfoReadingType.Temperature => 200.0,
            LinuxHwInfoReadingType.Fan => 30_000.0,
            LinuxHwInfoReadingType.Voltage => 30.0,
            LinuxHwInfoReadingType.Current => 500.0,
            LinuxHwInfoReadingType.Power => 2_000.0,
            _ => double.MaxValue
        };

        return max <= ceiling ? max : null;
    }

    private static Regex ChannelPattern(string prefix) => new($"^{prefix}(\\d+)_input$", RegexOptions.CultureInvariant);

    private readonly record struct HwmonChip(string Directory, string Name, string DeviceKey, string DisplayName);
}
