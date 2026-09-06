using System.Globalization;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Readings that no hwmon chip exposes: CPU load, CPU frequency and memory usage.
/// </summary>
/// <remarks>
/// CPU load is a delta between two <c>/proc/stat</c> samples, so this source is stateful and must live for
/// the lifetime of the poll loop. The first poll after <see cref="Reset"/> has no baseline and deliberately
/// reports no load sensors at all rather than a bogus 0 %.
/// </remarks>
internal sealed class ProcSensorSource
{
    private const string CpuFreqRoot = "/sys/devices/system/cpu";

    private long[]? _previousIdle;
    private long[]? _previousTotal;

    public void Reset()
    {
        _previousIdle = null;
        _previousTotal = null;
    }

    public IEnumerable<LinuxHwInfoSensor> Poll()
    {
        List<LinuxHwInfoSensor> sensors = [];
        CollectCpuLoad(sensors);
        CollectCpuFrequency(sensors);
        CollectMemory(sensors);
        return sensors;
    }

    private void CollectCpuLoad(List<LinuxHwInfoSensor> sensors)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/stat");
        }
        catch (Exception)
        {
            return;
        }

        List<long> idle = [];
        List<long> total = [];
        foreach (string line in lines)
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
                continue;

            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
                continue;

            long sum = 0;
            long idleTicks = 0;
            for (int i = 1; i < fields.Length; i++)
            {
                if (!long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    continue;

                sum += value;
                // Field 4 is idle, field 5 is iowait — both count as "not doing work".
                if (i is 4 or 5)
                    idleTicks += value;
            }

            idle.Add(idleTicks);
            total.Add(sum);
        }

        long[] currentIdle = [.. idle];
        long[] currentTotal = [.. total];

        // No baseline (first poll, or the core count changed under us) — record and report nothing.
        if (_previousIdle == null || _previousIdle.Length != currentIdle.Length)
        {
            _previousIdle = currentIdle;
            _previousTotal = currentTotal;
            return;
        }

        for (int i = 0; i < currentIdle.Length; i++)
        {
            long deltaTotal = currentTotal[i] - _previousTotal![i];
            long deltaIdle = currentIdle[i] - _previousIdle[i];
            if (deltaTotal <= 0)
                continue;

            double percent = Math.Clamp(100.0 * (1.0 - ((double)deltaIdle / deltaTotal)), 0.0, 100.0);
            bool aggregate = i == 0;

            sensors.Add(new LinuxHwInfoSensor(
                Id: SensorId.Proc("cpu", aggregate ? "total" : (i - 1).ToString(CultureInfo.InvariantCulture)),
                Source: SensorSourceKind.Proc,
                Category: Categories.Cpu,
                Group: "CPU",
                Label: aggregate ? "CPU Total" : $"Core {i - 1}",
                Type: LinuxHwInfoReadingType.Usage,
                Unit: "%",
                Value: percent,
                Max: 100.0));
        }

        _previousIdle = currentIdle;
        _previousTotal = currentTotal;
    }

    private static void CollectCpuFrequency(List<LinuxHwInfoSensor> sensors)
    {
        List<double> megahertz = [];

        foreach (string dir in SysfsIo.EnumerateDirectories(CpuFreqRoot))
        {
            string leaf = Path.GetFileName(dir);
            if (!leaf.StartsWith("cpu", StringComparison.Ordinal) || !int.TryParse(leaf.AsSpan(3), out _))
                continue;

            // scaling_cur_freq is the governor's view; cpuinfo_cur_freq is the hardware's and is
            // root-only on some platforms, so it is only the fallback.
            double? khz = SysfsIo.ReadScaled(Path.Combine(dir, "cpufreq", "scaling_cur_freq"), 1000.0)
                          ?? SysfsIo.ReadScaled(Path.Combine(dir, "cpufreq", "cpuinfo_cur_freq"), 1000.0);
            if (khz is > 0)
                megahertz.Add(khz.Value);
        }

        if (megahertz.Count == 0)
            return;

        sensors.Add(Frequency("freq.avg", "CPU Clock Avg", megahertz.Average()));
        sensors.Add(Frequency("freq.max", "CPU Clock Max", megahertz.Max()));
    }

    private static LinuxHwInfoSensor Frequency(string field, string label, double value) => new(
        Id: SensorId.Proc("cpu", field),
        Source: SensorSourceKind.Proc,
        Category: Categories.Cpu,
        Group: "CPU",
        Label: label,
        Type: LinuxHwInfoReadingType.Clock,
        Unit: "MHz",
        Value: value);

    private static void CollectMemory(List<LinuxHwInfoSensor> sensors)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/meminfo");
        }
        catch (Exception)
        {
            return;
        }

        long totalKb = 0;
        long availableKb = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                totalKb = ParseKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                availableKb = ParseKb(line);
        }

        if (totalKb <= 0)
            return;

        // MemAvailable, not MemFree — MemFree ignores reclaimable page cache and overstates usage badly.
        double totalGb = totalKb / 1024.0 / 1024.0;
        double usedGb = (totalKb - availableKb) / 1024.0 / 1024.0;

        sensors.Add(Memory("used", "RAM Used", LinuxHwInfoReadingType.Data, "GB", usedGb, totalGb));
        sensors.Add(Memory("total", "RAM Total", LinuxHwInfoReadingType.Data, "GB", totalGb, null));
        sensors.Add(Memory("percent", "RAM Usage", LinuxHwInfoReadingType.Usage, "%", 100.0 * usedGb / totalGb, 100.0));
    }

    private static LinuxHwInfoSensor Memory(string field, string label, LinuxHwInfoReadingType type,
        string unit, double value, double? max) => new(
        Id: SensorId.Proc("mem", field),
        Source: SensorSourceKind.Proc,
        Category: Categories.Memory,
        Group: "System Memory",
        Label: label,
        Type: type,
        Unit: unit,
        Value: value,
        Max: max);

    private static long ParseKb(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb)
            ? kb
            : 0;
    }
}
