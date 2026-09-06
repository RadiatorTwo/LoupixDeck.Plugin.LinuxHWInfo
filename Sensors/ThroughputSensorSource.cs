using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Network and disk transfer rates, derived from the byte counters in <c>/proc/net/dev</c> and
/// <c>/proc/diskstats</c>.
/// </summary>
/// <remarks>
/// Both are monotonic counters, so a rate needs the previous sample plus the time that elapsed since it was
/// taken — the elapsed time is measured rather than assumed, so a delayed poll does not inflate the rate.
/// The first poll after <see cref="Reset"/> establishes the baseline and reports nothing.
/// </remarks>
internal sealed partial class ThroughputSensorSource
{
    private const double BytesPerSector = 512.0;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly Dictionary<string, (long Rx, long Tx)> _previousNet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Read, long Write)> _previousDisk = new(StringComparer.Ordinal);
    private long _previousTimestamp;

    public void Reset()
    {
        _previousNet.Clear();
        _previousDisk.Clear();
        _previousTimestamp = 0;
    }

    public IEnumerable<LinuxHwInfoSensor> Poll()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = _previousTimestamp == 0
            ? 0.0
            : (double)(now - _previousTimestamp) / Stopwatch.Frequency;
        _previousTimestamp = now;

        List<LinuxHwInfoSensor> sensors = [];
        CollectNetwork(sensors, seconds);
        CollectDisk(sensors, seconds);
        return sensors;
    }

    private void CollectNetwork(List<LinuxHwInfoSensor> sensors, double seconds)
    {
        foreach (string line in ReadLines("/proc/net/dev"))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            string iface = line[..colon].Trim();
            if (iface.Length == 0 || iface == "lo")
                continue;

            string[] fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9
                || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rx)
                || !long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out long tx))
                continue;

            if (_previousNet.TryGetValue(iface, out (long Rx, long Tx) previous) && seconds > 0)
            {
                sensors.Add(Throughput(SensorId.Net(iface, "rx"), SensorSourceKind.Network, Categories.Network,
                    iface, $"{iface} RX", Rate(rx - previous.Rx, 1.0, seconds)));
                sensors.Add(Throughput(SensorId.Net(iface, "tx"), SensorSourceKind.Network, Categories.Network,
                    iface, $"{iface} TX", Rate(tx - previous.Tx, 1.0, seconds)));
            }

            _previousNet[iface] = (rx, tx);
        }
    }

    private void CollectDisk(List<LinuxHwInfoSensor> sensors, double seconds)
    {
        foreach (string line in ReadLines("/proc/diskstats"))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
                continue;

            string device = fields[2];
            // Whole devices only — partitions would double-count their parent's traffic.
            if (!WholeDevicePattern().IsMatch(device))
                continue;

            if (!long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long read)
                || !long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out long write))
                continue;

            if (_previousDisk.TryGetValue(device, out (long Read, long Write) previous) && seconds > 0)
            {
                string group = DiskGroup(device);
                sensors.Add(Throughput(SensorId.Disk(device, "read"), SensorSourceKind.Disk, Categories.Storage,
                    group, $"{device} Read", Rate(read - previous.Read, BytesPerSector, seconds)));
                sensors.Add(Throughput(SensorId.Disk(device, "write"), SensorSourceKind.Disk, Categories.Storage,
                    group, $"{device} Write", Rate(write - previous.Write, BytesPerSector, seconds)));
            }

            _previousDisk[device] = (read, write);
        }
    }

    /// <summary>
    /// Groups a block device under its model string, which is the same one the drive's hwmon node reports, so
    /// a drive's temperature and its throughput appear together. Falls back to the kernel name.
    /// </summary>
    private static string DiskGroup(string device)
    {
        string? model = SysfsIo.ReadText($"/sys/block/{device}/device/model");
        return string.IsNullOrWhiteSpace(model) ? device : model.Trim();
    }

    /// <summary>Counter delta to MB/s. A negative delta means the counter wrapped or the device was reset.</summary>
    private static double Rate(long delta, double bytesPerUnit, double seconds) =>
        delta <= 0 ? 0.0 : delta * bytesPerUnit / BytesPerMegabyte / seconds;

    private static LinuxHwInfoSensor Throughput(string id, SensorSourceKind source, string category,
        string group, string label, double value) => new(
        Id: id,
        Source: source,
        Category: category,
        Group: group,
        Label: label,
        Type: LinuxHwInfoReadingType.Throughput,
        Unit: "MB/s",
        Value: value);

    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^(nvme\d+n\d+|sd[a-z]+|hd[a-z]+|mmcblk\d+|vd[a-z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeDevicePattern();
}
