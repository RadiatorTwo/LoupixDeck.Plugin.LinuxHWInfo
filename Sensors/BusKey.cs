using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Derives the stable bus key that anchors a sensor identifier to the hardware rather than to the kernel's
/// probe order.
/// </summary>
/// <remarks>
/// The path is scanned from the leaf backwards and the first recognisable segment wins, because the device a
/// hwmon node hangs off is the deepest one: an NVMe controller resolves to
/// <c>/sys/devices/pci0000:00/0000:00:01.1/0000:01:00.0/nvme/nvme0</c>, where the meaningful address is the
/// PCI function in the middle, and a DIMM sensor to <c>.../0000:00:14.0/i2c-7/7-001a</c>, where taking the
/// PCI function instead of the i2c client address would collapse both DIMMs onto one key.
/// </remarks>
internal static partial class BusKey
{
    public static string Resolve(string deviceLinkPath, string fallbackLeaf)
    {
        string? real = SysfsIo.RealPath(deviceLinkPath);
        if (real == null)
            return $"virtual/{fallbackLeaf}";

        string[] segments = real.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            string segment = segments[i];

            if (I2cPattern().IsMatch(segment))
                return $"i2c/{segment}";

            if (PciPattern().IsMatch(segment))
                return $"pci/{segment}";

            if (i > 0 && segments[i - 1] == "platform")
                return $"platform/{segment}";
        }

        return $"virtual/{segments[^1]}";
    }

    /// <summary>The PCI address of a device, e.g. <c>0000:01:00.0</c>, when one is present in its path.</summary>
    public static string? PciAddress(string deviceLinkPath)
    {
        string? real = SysfsIo.RealPath(deviceLinkPath);
        if (real == null)
            return null;

        string[] segments = real.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            if (PciPattern().IsMatch(segments[i]))
                return segments[i];
        }

        return null;
    }

    [GeneratedRegex(@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-9a-f]$", RegexOptions.CultureInvariant)]
    private static partial Regex PciPattern();

    [GeneratedRegex(@"^\d+-[0-9a-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex I2cPattern();
}
