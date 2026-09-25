using LoupixDeck.Plugin.LinuxHwInfo.Rendering.Tiles;
using LoupixDeck.Plugin.LinuxHwInfo.Telemetry;

namespace LoupixDeck.Plugin.LinuxHwInfo.Rendering;

/// <summary>
/// Turns a persisted <c>LinuxHwInfo.Sensor</c> command parameter into the <see cref="SensorRow"/> a
/// tile draws. Owns the parameter grammar and picks the labels; values, units, history and alert state
/// come from the <see cref="TelemetrySampler"/>, which tracks every sensor under the same id the
/// parameter stores.
/// <para>
/// The model is <b>one reading per command</b>: the parameter is a stable sensor id such as
/// <c>hwmon/pci/0000:00:18.3/k10temp/temp1</c> and yields a single row; a multi-sensor button is
/// composed by chaining several commands, which the tile lays out as rows.
/// </para>
/// </summary>
internal static class LinuxReadingBuilder
{
    /// <summary>
    /// The row for <paramref name="parameter"/>. A reference that is empty or names a sensor that is
    /// not present right now (a removed device) degrades to a placeholder row, never an exception.
    /// </summary>
    public static SensorRow Build(string? parameter, IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return Placeholder("LinuxHwInfo");

        string id = parameter.Trim();
        LinuxHwInfoSensor? sensor = null;
        foreach (LinuxHwInfoSensor candidate in sensors)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                sensor = candidate;
                break;
            }
        }

        if (sensor is null)
            return Placeholder(HeaderFromId(id));

        // The menu's name for the sensor; the source label is only the fallback.
        if (TileLabels.For(sensors, id) is { } labels)
            return new SensorRow(labels.Header, labels.Short, MetricKeys.ForSensor(sensor));

        return new SensorRow(sensor.Label, ShortHeaderFrom(sensor.Label), MetricKeys.ForSensor(sensor));
    }

    private static SensorRow Placeholder(string header) => new(header, header, null);

    /// <summary>The channel part of an id ("hwmon/…/k10temp/temp1" → "temp1"), so a tile whose
    /// device is gone still says which reading it wanted.</summary>
    private static string HeaderFromId(string id)
    {
        int slash = id.LastIndexOf('/');
        return slash >= 0 && slash < id.Length - 1 ? id[(slash + 1)..] : id;
    }

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
