using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo.Rendering;

/// <summary>
/// Turns a persisted <c>LinuxHwInfo.Sensor</c> command parameter and a live sensor snapshot into a
/// <see cref="SensorReading"/>. Owns the sensor→display mapping (label, unit scaling, value formatting,
/// gauge scaling, accent) so <see cref="SensorRenderer"/> stays a pure drawing component.
/// <para>
/// The model is <b>one reading per command</b>: the parameter is a stable sensor id such as
/// <c>hwmon/pci/0000:00:18.3/k10temp/temp1</c> and yields a single reading; a multi-sensor button is
/// composed by chaining several commands, which the renderer lays out as rows.
/// </para>
/// </summary>
public static class LinuxReadingBuilder
{
    private const string PluginLabel = "LinuxHwInfo";

    /// <summary>
    /// Builds the reading referenced by <paramref name="parameter"/>, or a placeholder when the service has
    /// no snapshot yet, the reference is empty, or the sensor is not currently present — a button pointing at
    /// a device that has since been removed must degrade, never throw.
    /// </summary>
    public static SensorReading Build(string? parameter, IReadOnlyList<LinuxHwInfoSensor> sensors, bool isAvailable)
    {
        if (!isAvailable)
            return Placeholder(PluginLabel, "N/A");

        if (string.IsNullOrWhiteSpace(parameter))
            return Placeholder(PluginLabel, "?");

        LinuxHwInfoSensor? sensor = null;
        foreach (LinuxHwInfoSensor candidate in sensors)
        {
            if (string.Equals(candidate.Id, parameter, StringComparison.Ordinal))
            {
                sensor = candidate;
                break;
            }
        }

        if (sensor is null)
            return Placeholder(PluginLabel, "?");

        (string value, string unit) = Format(sensor.Value, sensor.Unit);
        string header = sensor.Label;

        return new SensorReading(
            Header: header,
            Value: value,
            Unit: unit,
            Fraction: Fraction(sensor),
            Accent: Accent(sensor.Type),
            ShortHeader: ShortHeaderFrom(header));
    }

    private static SensorReading Placeholder(string header, string value) => new(header, value, string.Empty);

    // ── Value formatting ───────────────────────────────────────────────────────

    /// <summary>Scales a value into a more readable unit where that helps, and formats the number.</summary>
    private static (string Value, string Unit) Format(double value, string unit)
    {
        if (unit.Equals("MHz", StringComparison.OrdinalIgnoreCase) && Math.Abs(value) >= 1000.0)
        {
            value /= 1000.0;
            unit = "GHz";
        }

        return (FormatNumber(value), unit);
    }

    /// <summary>One decimal place, but a trailing ".0" is dropped so whole numbers read as integers
    /// (36 °C) while fractional readings keep their decimal (6.6 %).</summary>
    private static string FormatNumber(double value)
    {
        string text = value.ToString("F1", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal))
            text = text[..^2];
        return text;
    }

    // ── Gauge fill ─────────────────────────────────────────────────────────────

    // Nominal full-scale values for readings whose hardware reports no limit, matching the Windows sensor
    // plugins so bars fill comparably across them.
    private const double TempMaxC = 100.0;
    private const double PowerMaxW = 100.0;
    private const double FanRpmMax = 3000.0;
    private const double FreqMaxDefaultMhz = 6000.0;
    private const double ThroughputMaxMbPerSecond = 1000.0;

    /// <summary>
    /// 0..1 gauge fill, or null when the reading has no meaningful scale (no bar is drawn). A hardware-reported
    /// limit — a hwmon <c>_crit</c>/<c>_max</c>, NVML's enforced power limit, a total against its used value —
    /// always wins over the nominal per-type default. Voltage, current and "other" readings draw no bar.
    /// </summary>
    private static double? Fraction(LinuxHwInfoSensor sensor)
    {
        double? max = MaxFor(sensor);
        if (max is null or <= 0)
            return null;

        return Math.Clamp(sensor.Value / max.Value, 0.0, 1.0);
    }

    private static double? MaxFor(LinuxHwInfoSensor sensor)
    {
        if (sensor.Max is > 0)
            return sensor.Max;

        // Anything already expressed in percent is 0..100 regardless of its reading type.
        if (sensor.Unit == "%")
            return 100.0;

        return sensor.Type switch
        {
            LinuxHwInfoReadingType.Temperature => TempMaxC,
            LinuxHwInfoReadingType.Usage => 100.0,
            LinuxHwInfoReadingType.Power => PowerMaxW,
            LinuxHwInfoReadingType.Fan => FanRpmMax,
            LinuxHwInfoReadingType.Clock => FreqMaxDefaultMhz,
            LinuxHwInfoReadingType.Throughput => ThroughputMaxMbPerSecond,
            _ => null
        };
    }

    // ── Accents ────────────────────────────────────────────────────────────────

    private static readonly PluginColor AccentTemp = new(0xC0, 0x76, 0x40);       // muted orange
    private static readonly PluginColor AccentUsage = new(0x57, 0x9E, 0x63);      // muted green
    private static readonly PluginColor AccentClock = new(0x53, 0x6D, 0x9E);      // muted steel blue
    private static readonly PluginColor AccentPower = new(0xA8, 0x5C, 0x5C);      // muted red
    private static readonly PluginColor AccentFan = new(0xB0, 0x92, 0x42);        // muted amber
    private static readonly PluginColor AccentThroughput = new(0x4A, 0x8E, 0x96); // muted cyan
    private static readonly PluginColor AccentData = new(0x7E, 0x7E, 0x8C);       // neutral slate

    /// <summary>Muted accent tint for a reading kind, used only for the gauge fill. Null → the theme's
    /// neutral default bar colour.</summary>
    private static PluginColor? Accent(LinuxHwInfoReadingType type) => type switch
    {
        LinuxHwInfoReadingType.Temperature => AccentTemp,
        LinuxHwInfoReadingType.Usage => AccentUsage,
        LinuxHwInfoReadingType.Clock => AccentClock,
        LinuxHwInfoReadingType.Power => AccentPower,
        LinuxHwInfoReadingType.Fan => AccentFan,
        LinuxHwInfoReadingType.Throughput => AccentThroughput,
        LinuxHwInfoReadingType.Data => AccentData,
        _ => null
    };

    // ── Headers ────────────────────────────────────────────────────────────────

    /// <summary>Compact form of a header for use as a row label when several readings share the tile: drops a
    /// leading "CPU "/"GPU "/"VRAM " subsystem word so e.g. "CPU Clock Avg" fits beside its value as
    /// "Clock Avg". Returns the header unchanged when there is nothing to drop.</summary>
    private static string ShortHeaderFrom(string header)
    {
        foreach (string prefix in (string[])["CPU ", "GPU ", "RAM ", "VRAM "])
        {
            if (header.StartsWith(prefix, StringComparison.Ordinal) && header.Length > prefix.Length)
                return header[prefix.Length..];
        }

        return header;
    }
}
