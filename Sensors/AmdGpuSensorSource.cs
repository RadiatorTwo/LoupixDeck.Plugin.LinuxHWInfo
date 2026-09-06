using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// The two amdgpu metrics that live outside hwmon: GPU busy percentage and VRAM usage, read from
/// <c>/sys/class/drm/cardN/device</c>.
/// </summary>
/// <remarks>
/// amdgpu temperatures, fans, power and voltages already arrive through <see cref="HwmonSensorSource"/>;
/// only these DRM-only counters need extra code. They are keyed with the same PCI address the hwmon walker
/// derives, so both land in one device group in the menu.
/// <para>
/// <b>Not verified on AMD hardware</b> — the development machine is NVIDIA-only. Kept deliberately small and
/// fully self-contained so a fault here cannot reach the tested hwmon and NVML paths.
/// </para>
/// </remarks>
internal sealed partial class AmdGpuSensorSource
{
    private const string DrmRoot = "/sys/class/drm";

    public IEnumerable<LinuxHwInfoSensor> Poll()
    {
        List<LinuxHwInfoSensor> sensors = [];

        foreach (string cardDir in SysfsIo.EnumerateDirectories(DrmRoot))
        {
            // /sys/class/drm also holds connector directories such as card0-DP-1.
            if (!CardPattern().IsMatch(Path.GetFileName(cardDir)))
                continue;

            string deviceDir = Path.Combine(cardDir, "device");
            string? busy = SysfsIo.ReadText(Path.Combine(deviceDir, "gpu_busy_percent"));
            bool hasVram = SysfsIo.TryReadLong(Path.Combine(deviceDir, "mem_info_vram_total"), out long vramTotal);
            if (busy == null && !hasVram)
                continue;

            // Same PCI address the hwmon walker derives, so these land in the amdgpu chip's own device group.
            string? pci = BusKey.PciAddress(deviceDir);
            if (pci == null)
                continue;

            string deviceKey = $"pci/{pci}";
            string group = ChipNaming.PrettyName("amdgpu");

            if (busy != null && double.TryParse(busy, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double busyPercent))
                sensors.Add(Sensor(deviceKey, group, "drm.busy", "GPU Usage", LinuxHwInfoReadingType.Usage, "%",
                    busyPercent, 100.0));

            if (hasVram && vramTotal > 0)
            {
                double totalGb = vramTotal / 1024.0 / 1024.0 / 1024.0;
                sensors.Add(Sensor(deviceKey, group, "drm.vram_total", "VRAM Total", LinuxHwInfoReadingType.Data,
                    "GB", totalGb));

                if (SysfsIo.TryReadLong(Path.Combine(deviceDir, "mem_info_vram_used"), out long vramUsed))
                {
                    sensors.Add(Sensor(deviceKey, group, "drm.vram_used", "VRAM Used", LinuxHwInfoReadingType.Data,
                        "GB", vramUsed / 1024.0 / 1024.0 / 1024.0, totalGb));
                    sensors.Add(Sensor(deviceKey, group, "drm.vram_percent", "VRAM Fill", LinuxHwInfoReadingType.Usage,
                        "%", 100.0 * vramUsed / vramTotal, 100.0));
                }
            }
        }

        return sensors;
    }

    private static LinuxHwInfoSensor Sensor(string deviceKey, string group, string field, string label,
        LinuxHwInfoReadingType type, string unit, double value, double? max = null) => new(
        Id: SensorId.Hwmon(deviceKey, "amdgpu", field),
        Source: SensorSourceKind.Hwmon,
        Category: Categories.Gpu,
        Group: group,
        Label: label,
        Type: type,
        Unit: unit,
        Value: value,
        Max: max);

    [GeneratedRegex(@"^card\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CardPattern();
}
