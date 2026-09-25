using System.Text.RegularExpressions;
using LoupixDeck.Plugin.LinuxHwInfo.Sensors;
using LoupixDeck.Plugin.LinuxHwInfo.Telemetry;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo;

/// <summary>
/// Builds the <c>LinuxHwInfo.Sensor</c> part of the editor menu: sensors are sorted by component
/// (CPU, GPU, Memory, Storage, Mainboard, Network, Other) and then by quantity (Temperature, Load,
/// Clock, …), and every entry gets a name that is unique within its submenu.
///
/// <para>Only the presentation is new. Each entry still stores the sensor's stable id, exactly as
/// <see cref="Rendering.LinuxReadingBuilder"/> resolves it, so saved buttons keep loading. Nothing
/// is filtered out: every channel the kernel exposes is offered, including implausible ones,
/// because only the user can tell which of a board's Super-I/O channels are actually wired up.</para>
/// </summary>
internal static partial class SensorMenu
{
    private static readonly string[] ComponentOrder = ["CPU", "GPU", "Memory", "Storage", "Mainboard", "Network", "Other"];

    /// <summary>Quantities in menu order. "Fans" is the mainboard's plural of "Fan".</summary>
    private static readonly string[] SectionOrder =
    [
        "Temperature", "Load", "Clock", "Memory", "Fan", "Fans", "Power", "Voltage", "Current", "Transfer rate", "Other"
    ];

    /// <summary>Words that repeat the component and are dropped from the labels ("CPU Total"
    /// under CPU → "Total").</summary>
    private static readonly Dictionary<string, string[]> ComponentWords = new()
    {
        ["CPU"] = ["CPU"],
        ["GPU"] = ["GPU"],
        ["Memory"] = ["RAM"]
    };

    /// <summary>Words that repeat the quantity; a label that starts or ends with one loses it
    /// ("Clock" in "VRAM Clock", "Usage" in "GPU Usage").</summary>
    private static readonly Dictionary<string, string[]> SectionWords = new()
    {
        ["Load"] = ["Load", "Usage"],
        ["Clock"] = ["Clock"],
        ["Power"] = ["Power"],
        ["Temperature"] = ["Temperature", "Temp"],
        ["Fan"] = ["Fan"]
    };

    private sealed record Entry(LinuxHwInfoSensor Sensor, string Device, int Rank, string BaseName);

    public static List<MenuNode> Build(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        List<SensorName> named = Name(sensors);

        List<MenuNode> components = [];
        foreach (string component in ComponentOrder)
        {
            List<SensorName> ofComponent = named.Where(n => n.Component == component).ToList();
            List<IGrouping<string, SensorName>> sections = ofComponent.GroupBy(n => n.Section).ToList();

            List<MenuNode> children = [];
            foreach (IGrouping<string, SensorName> section in sections)
            {
                List<MenuNode> nodes = section.Select(n => new MenuNode
                {
                    Name = n.MenuName,
                    CommandName = LinuxHwInfoSensorCommand.Name,
                    Parameters = new Dictionary<string, string> { { "Sensor", n.Sensor.Id } }
                }).ToList();

                if (sections.Count == 1 || nodes.Count == 1)
                    children.AddRange(nodes);  // no extra level for a lone quantity or a lone entry
                else
                    children.Add(new MenuNode { Name = section.Key, Children = nodes });
            }

            if (children.Count > 0)
                components.Add(new MenuNode { Name = component, Children = children });
        }

        return components;
    }

    /// <summary>
    /// Names every sensor the menu offers, in menu order. Tile labels are derived from these names
    /// so the menu and the tile call a sensor the same thing.
    /// </summary>
    public static List<SensorName> Name(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        Dictionary<LinuxHwInfoSensor, string> devices = DeviceNames(sensors);
        Dictionary<(string Component, string Section), List<LinuxHwInfoSensor>> bySection = [];
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            (string, string) key = (Component(sensor), Section(sensor));
            if (!bySection.TryGetValue(key, out List<LinuxHwInfoSensor>? members))
                bySection[key] = members = [];

            members.Add(sensor);
        }

        List<SensorName> named = [];
        foreach (string component in ComponentOrder)
        {
            List<KeyValuePair<(string Component, string Section), List<LinuxHwInfoSensor>>> sections = bySection
                .Where(pair => pair.Key.Component == component)
                .OrderBy(pair => SectionRank(pair.Key.Section))
                .ToList();

            foreach (((_, string section), List<LinuxHwInfoSensor> members) in sections)
            {
                List<SensorName> names = SectionNames(component, section, members, devices);

                // A submenu with one entry is noise: the entry takes the submenu's place and name
                // (unless it is the component's only quantity, which gets no submenu either).
                if (sections.Count > 1 && names.Count == 1)
                    names[0] = names[0] with { MenuName = section };

                named.AddRange(names);
            }
        }

        return named;
    }

    /// <summary>The menu component of a sensor, from the category its source assigned.</summary>
    private static string Component(LinuxHwInfoSensor sensor) => sensor.Category switch
    {
        Categories.Motherboard => "Mainboard",
        Categories.Cpu or Categories.Gpu or Categories.Memory or Categories.Storage or Categories.Network => sensor.Category,
        _ => "Other"
    };

    /// <summary>The quantity submenu of a sensor, from its reading type and id.</summary>
    private static string Section(LinuxHwInfoSensor sensor)
    {
        if (SensorMetrics.IsVram(sensor))
            return "Memory";
        if (SensorMetrics.IsGpuFanPercent(sensor))
            return "Fan";

        return sensor.Type switch
        {
            LinuxHwInfoReadingType.Temperature => "Temperature",
            LinuxHwInfoReadingType.Usage when sensor.Category == Categories.Memory => "Memory",
            LinuxHwInfoReadingType.Usage => "Load",
            LinuxHwInfoReadingType.Clock => "Clock",
            LinuxHwInfoReadingType.Data => "Memory",
            LinuxHwInfoReadingType.Fan => sensor.Category == Categories.Motherboard ? "Fans" : "Fan",
            LinuxHwInfoReadingType.Power => "Power",
            LinuxHwInfoReadingType.Voltage => "Voltage",
            LinuxHwInfoReadingType.Current => "Current",
            LinuxHwInfoReadingType.Throughput => "Transfer rate",
            _ => "Other"
        };
    }

    private static int SectionRank(string section)
    {
        int rank = Array.IndexOf(SectionOrder, section);
        return rank >= 0 ? rank : int.MaxValue;
    }

    private static List<SensorName> SectionNames(string component, string section, List<LinuxHwInfoSensor> members,
        Dictionary<LinuxHwInfoSensor, string> devices)
    {
        // Which device a reading belongs to only needs saying when the section spans several.
        bool severalDevices = members.Select(s => devices[s]).Distinct(StringComparer.Ordinal).Count() > 1;

        List<Entry> ordered = members
            .Select(s => new Entry(s, devices[s], Rank(s), BaseName(s, component, section, severalDevices)))
            .OrderBy(e => e.Rank)
            .ThenBy(e => e.Device, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => NaturalKey(e.BaseName), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Readings two devices label alike ("Composite" on each NVMe drive) take their device name,
        // and so does every reading of a machine with two GPUs, whose labels rarely say which card.
        if (severalDevices && (component == "GPU"
                               || ordered.GroupBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase)
                                   .Any(g => g.Select(e => e.Device).Distinct(StringComparer.Ordinal).Count() > 1)))
        {
            ordered = ordered.Select(e => e with { BaseName = WithDevice(e, section) }).ToList();
        }

        // Keep the percent and absolute reading of the same thing next to each other
        // ("Used (GB)" beside "Used (%)"): stable grouping by name, in order of first appearance.
        ordered = ordered.GroupBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase).SelectMany(g => g).ToList();

        // Name the unit only where it tells entries apart: "Used (%)" beside "Used (GB)", but
        // plain "Tctl" among °C-only readings.
        bool showUnit = ordered.Select(e => e.Sensor.Unit.Trim()).Where(u => u.Length > 0).Distinct().Count() > 1;

        List<string> tags = ordered.Select(e => showUnit ? e.Sensor.Unit.Trim() : "").ToList();
        List<string> baseNames = ordered.Select(e => e.BaseName).ToList();

        // Readings labelled identically within one unit get a running number: "Fan 1", "Fan 2".
        foreach (IGrouping<string, int> clash in Enumerable.Range(0, ordered.Count)
                     .GroupBy(i => Compose(baseNames[i], tags[i]), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            int number = 1;
            foreach (int i in clash)
                baseNames[i] = $"{ordered[i].BaseName} {number++}";
        }

        List<string> names = Enumerable.Range(0, ordered.Count).Select(i => Compose(baseNames[i], tags[i])).ToList();

        // Last resort for anything still ambiguous: the channel the id ends in.
        foreach (IGrouping<string, int> clash in Enumerable.Range(0, ordered.Count)
                     .GroupBy(i => names[i], StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (int i in clash)
            {
                string channel = Channel(ordered[i].Sensor.Id);
                names[i] = $"{names[i]} #{channel}";
                baseNames[i] = $"{baseNames[i]} #{channel}";
            }
        }

        return Enumerable.Range(0, ordered.Count)
            .Select(i => new SensorName(ordered[i].Sensor, ordered[i].Device, component, section, baseNames[i], names[i]))
            .ToList();
    }

    /// <summary>Aggregates come before the per-core readings they summarize.</summary>
    private static int Rank(LinuxHwInfoSensor sensor) =>
        sensor.Id == SensorId.Proc("cpu", "total") ? 0 : 1;

    private static string Compose(string name, string unitTag) =>
        unitTag.Length == 0 ? name : $"{name} ({unitTag})";

    /// <summary>
    /// The entry name before units and numbering: the source's label with the component, device and
    /// quantity words removed, or a fixed name where the id says more than the label.
    /// </summary>
    private static string BaseName(LinuxHwInfoSensor sensor, string component, string section, bool severalDevices)
    {
        string id = sensor.Id;
        if (id == SensorId.Proc("cpu", "freq.avg"))
            return "Clock avg";
        if (id == SensorId.Proc("cpu", "freq.max"))
            return "Clock max";
        if (id == SensorId.Proc("mem", "percent"))
            return "Used";

        switch (sensor.Source)
        {
            // The direction is part of the id: net/<iface>/rx, disk/<device>/write.
            case SensorSourceKind.Network:
                return $"{Channel(id, 1)} {(id.EndsWith("/rx", StringComparison.Ordinal) ? "down" : "up")}";
            case SensorSourceKind.Disk:
                return $"{Channel(id, 1)} {Channel(id)}";
        }

        if (SensorMetrics.IsVram(sensor))
        {
            return Channel(id) switch
            {
                "mem.used" or "mem.percent" or "drm.vram_used" or "drm.vram_percent" => "VRAM used",
                "mem.total" or "drm.vram_total" => "VRAM total",
                // NVML's memory utilization is how busy the memory controller is, not how full VRAM is.
                "util.mem" => "Memory controller",
                _ => sensor.Label
            };
        }

        string label = sensor.Label.Trim();

        // hwmon names unlabelled channels after their device ("Super I/O fan2"); within one device
        // the device name says nothing.
        if (!severalDevices && label.StartsWith(sensor.Group + " ", StringComparison.OrdinalIgnoreCase))
            label = label[(sensor.Group.Length + 1)..];

        if (ComponentWords.TryGetValue(component, out string[]? words))
        {
            foreach (string word in words)
                label = Regex.Replace(label, $@"\b{Regex.Escape(word)}\b", "", RegexOptions.IgnoreCase);
        }

        label = Whitespace().Replace(label, " ").Trim();

        // "Clock Avg" under Clock → "Avg"; "VRAM Clock" under Clock → "VRAM".
        if (SectionWords.TryGetValue(section, out string[]? quantity))
        {
            foreach (string word in quantity)
            {
                if (label.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase))
                    label = label[(word.Length + 1)..];
                else if (label.EndsWith(" " + word, StringComparison.OrdinalIgnoreCase))
                    label = label[..^(word.Length + 1)];
                else if (label.Equals(word, StringComparison.OrdinalIgnoreCase))
                    label = string.Empty;
            }
        }

        return label.Length == 0 ? section : label;
    }

    /// <summary>The name with the device in front, unless the name already carries it. A name that
    /// is only the quantity ("Temperature" under Temperature) becomes the device name.</summary>
    private static string WithDevice(Entry entry, string section)
    {
        string device = entry.Device;
        if (entry.Sensor.Source is SensorSourceKind.Network or SensorSourceKind.Disk
            || entry.BaseName.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            return entry.BaseName;

        return entry.BaseName == section ? device : $"{device} {entry.BaseName}";
    }

    /// <summary>
    /// The device name of every sensor: its group, except for GPUs that share one — every AMD card
    /// is "GPU", two NVIDIA cards of one model have the same name. Those are told apart by
    /// <see cref="SensorMetrics.DeviceKey"/> and numbered in snapshot order ("GPU 1", "GPU 2").
    /// </summary>
    private static Dictionary<LinuxHwInfoSensor, string> DeviceNames(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        Dictionary<string, List<string>> keysByGroup = [];
        foreach (LinuxHwInfoSensor sensor in sensors.Where(s => s.Category == Categories.Gpu))
        {
            if (!keysByGroup.TryGetValue(sensor.Group, out List<string>? keys))
                keysByGroup[sensor.Group] = keys = [];

            string key = SensorMetrics.DeviceKey(sensor);
            if (!keys.Contains(key))
                keys.Add(key);
        }

        Dictionary<LinuxHwInfoSensor, string> names = [];
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            names[sensor] = sensor.Category == Categories.Gpu && keysByGroup[sensor.Group] is { Count: > 1 } keys
                ? $"{sensor.Group} {keys.IndexOf(SensorMetrics.DeviceKey(sensor)) + 1}"
                : sensor.Group;
        }

        return names;
    }

    /// <summary>A segment of an id counted from the end: 0 is the channel ("temp1"), 1 the one
    /// before it (the interface or block device of a rate).</summary>
    private static string Channel(string id, int fromEnd = 0)
    {
        string[] parts = id.Split('/');
        return parts.Length > fromEnd ? parts[^(fromEnd + 1)] : id;
    }

    /// <summary>Sort key that orders numbers by value: "Core 2" before "Core 10".</summary>
    private static string NaturalKey(string name) => Digits().Replace(name, m => m.Value.PadLeft(8, '0'));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

/// <summary>
/// How the menu names a sensor. <paramref name="Name"/> is the entry name without the unit that
/// sets it apart from its neighbours ("Used"); <paramref name="MenuName"/> is what the menu shows
/// ("Used (%)", or the quantity's name when the entry replaces a one-entry submenu).
/// <paramref name="Device"/> is the name of the device the reading belongs to, unique per device.
/// </summary>
internal sealed record SensorName(LinuxHwInfoSensor Sensor, string Device, string Component, string Section,
    string Name, string MenuName);
