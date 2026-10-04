using System.Text.RegularExpressions;
using LoupixDeck.Plugin.LinuxHwInfo.Rendering.Pixel;
using LoupixDeck.Plugin.LinuxHwInfo.Rendering.Tiles;
using LoupixDeck.Plugin.LinuxHwInfo.Sensors;

namespace LoupixDeck.Plugin.LinuxHwInfo.Rendering;

/// <summary>
/// The labels a LinuxHwInfo.Sensor tile shows, derived from the names the menu gives the sensors
/// (<see cref="SensorMenu.Name"/>), so the tile and the menu speak the same language. Each label
/// is the component ("CPU", "GPU", …) followed by the menu's entry name: CPU core 0 and the GPU
/// temperature read "CPU Core 0" and "GPU Temp" instead of both "Core 0". The quantity is left to
/// the unit printed beside the value.
///
/// <para>A tile header takes about eleven characters, a row label of a multi-reading tile about
/// seven, so the row label is abbreviated ("CPU C0", "Disk Rd"). Labels are unique among the
/// readings that share a unit; where the words alone would collide, a running number is added.</para>
/// </summary>
internal static partial class TileLabels
{
    public sealed record Labels(string Header, string Short);

    private sealed record Cache(IReadOnlyList<LinuxHwInfoSensor> Sensors, Dictionary<string, Labels> Labels);

    /// <summary>Width of a single-reading tile's header text (the header band minus its margins).</summary>
    private const int HeaderRoom = TileDrawing.W - 2;

    /// <summary>Characters a row label keeps beside a two-digit value at 2×; longer labels are cut
    /// when drawn, so <see cref="Shorten"/> makes the part that tells them apart fit first.</summary>
    private const int ShortBudget = 8;

    private static volatile Cache? _cache;

    private static readonly Dictionary<string, string> ComponentTags = new()
    {
        ["CPU"] = "CPU",
        ["GPU"] = "GPU",
        ["Memory"] = "RAM",
        ["Storage"] = "Disk",
        ["Network"] = "Net",
        ["Battery"] = "Bat"
    };

    /// <summary>Words that only repeat the quantity. Dropped when something else is left.</summary>
    private static readonly Dictionary<string, string[]> QuantityWords = new()
    {
        ["Temperature"] = ["Temperature", "Temp"],
        ["Power"] = ["Power"],
        ["Load"] = ["Load", "Usage"]
    };

    /// <summary>Row-label abbreviations, applied as whole words, case-insensitively.</summary>
    private static readonly (string Word, string Short)[] Abbreviations =
    [
        ("Temperature", "Temp"),
        ("Clock", "Clk"),
        ("Memory", "Mem"),
        ("controller", "Ctrl"),
        ("Composite", "Comp"),
        ("Calibration", "Cal"),
        ("Power", "Pwr"),
        ("Total", "Tot"),
        ("System", "Sys"),
        ("Average", "Avg"),
        ("Maximum", "Max"), ("Minimum", "Min"),
        ("Usage", "Load"),
        ("read", "Rd"), ("write", "Wr"), ("down", "Dn")
    ];

    /// <summary>The labels of the sensor with <paramref name="id"/>, or null when the menu does not
    /// offer it. Computed once per sensor snapshot.</summary>
    public static Labels? For(IReadOnlyList<LinuxHwInfoSensor> sensors, string id)
    {
        Cache? cache = _cache;
        if (cache is null || !ReferenceEquals(cache.Sensors, sensors))
            _cache = cache = new Cache(sensors, Compute(sensors));

        return cache.Labels.GetValueOrDefault(id);
    }

    private static Dictionary<string, Labels> Compute(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        List<SensorName> named = SensorMenu.Name(sensors);
        Context context = new(named);

        List<string> headers = [];
        List<string> shorts = [];
        foreach (SensorName name in named)
        {
            string tag = ComponentTags.GetValueOrDefault(name.Component, string.Empty);
            string entry = EntryWords(name, context);
            string header = Join(tag, entry);
            string compact = Join(tag, Abbreviate(entry));

            // The interface or block device ("enp5s0 down") says more than the component tag when
            // not both fit.
            if (PixelFont.Measure(compact) > HeaderRoom && name.Sensor.Source is SensorSourceKind.Network or SensorSourceKind.Disk)
                compact = Abbreviate(entry);

            headers.Add(PixelFont.Measure(header) > HeaderRoom ? compact : header);
            shorts.Add(ShortLabel(tag, entry, name, context));
        }

        Number(headers, named);
        Number(shorts, named);

        Dictionary<string, Labels> labels = [];
        for (int i = 0; i < named.Count; i++)
            labels[named[i].Sensor.Id] = new Labels(headers[i], shorts[i]);

        return labels;
    }

    /// <summary>What the snapshot holds beyond one sensor: how many devices report rates and
    /// temperatures, so a label names the device only when there is more than one.</summary>
    private sealed class Context(List<SensorName> named)
    {
        public int Interfaces { get; } = DistinctCount(named, SensorSourceKind.Network, n => Segment(n.Sensor.Id, 1));

        public int Disks { get; } = DistinctCount(named, SensorSourceKind.Disk, n => Segment(n.Sensor.Id, 1));

        /// <summary>The storage devices that report a temperature, in menu order.</summary>
        public List<string> Drives { get; } = named
            .Where(n => n.Component == "Storage" && n.Sensor.Type == LinuxHwInfoReadingType.Temperature)
            .Select(n => n.Sensor.Group)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        /// <summary>The GPUs, in menu order.</summary>
        public List<string> Gpus { get; } = named
            .Where(n => n.Component == "GPU")
            .Select(n => n.Device)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        private static int DistinctCount(List<SensorName> named, SensorSourceKind source, Func<SensorName, string> key) =>
            named.Where(n => n.Sensor.Source == source).Select(key).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>The entry part of the label: the menu name, minus words the tile does not need.</summary>
    private static string EntryWords(SensorName name, Context context)
    {
        LinuxHwInfoSensor sensor = name.Sensor;
        string id = sensor.Id;
        switch (sensor.Source)
        {
            // "enp5s0 down" → "down" on the usual one-adapter machine.
            case SensorSourceKind.Network:
                return context.Interfaces > 1 ? name.Name : Segment(name.Name, 0, ' ');
            // "nvme0n1 read" → "read", or "nvme0 read" beside other drives.
            case SensorSourceKind.Disk:
                return context.Disks > 1
                    ? $"{NamespaceSuffix().Replace(Segment(id, 1), "")} {Segment(id, 0)}"
                    : Segment(id, 0);
        }

        // A drive model is far too long for a tile: number the drives instead ("1 Comp").
        if (name.Component == "Storage" && sensor.Type == LinuxHwInfoReadingType.Temperature)
        {
            string label = HwmonChannel().Replace(sensor.Label, "").Trim();
            if (label.Length == 0)
                label = "Temp";

            return context.Drives.Count > 1 ? $"{context.Drives.IndexOf(sensor.Group) + 1} {label}" : label;
        }

        // "GPU Used" would not say what is used.
        if (Segment(id, 0) is "mem.used" or "mem.percent" or "drm.vram_used" or "drm.vram_percent")
            return GpuNumber(name, context) + "VRAM";

        // An entry that replaced its one-entry submenu is named after the quantity ("GPU > Fan").
        string entry = name.MenuName == name.Section ? name.Section : name.Name;

        // Beside a second GPU the menu names the card ("NVIDIA GeForce RTX 4090 Temperature"),
        // far too long for a tile: number the cards instead ("GPU 2 Temp").
        if (name.Component == "GPU" && context.Gpus.Count > 1)
        {
            if (entry.StartsWith(name.Device, StringComparison.OrdinalIgnoreCase))
                entry = entry[name.Device.Length..].Trim();

            entry = GpuNumber(name, context) + (entry.Length == 0 ? name.Section : entry);
        }

        entry = Whitespace().Replace(entry.Replace('(', ' ').Replace(")", ""), " ").Replace(" ,", ",").Trim();

        // "DIMM 1 temp1" → "DIMM 1": the hwmon channel name adds nothing once a device is named.
        // The menu's last-resort "#temp2" is replaced by the tile's own numbering.
        entry = ChannelSuffix().Replace(entry, "");
        string withoutChannel = Whitespace().Replace(HwmonChannel().Replace(entry, ""), " ").Trim();
        if (withoutChannel.Any(char.IsLetter))
            entry = withoutChannel;

        if (QuantityWords.TryGetValue(name.Section, out string[]? words))
        {
            string stripped = entry;
            foreach (string word in words)
                stripped = ReplaceWord(stripped, word, "");

            stripped = Whitespace().Replace(stripped, " ").Trim();
            // Keep the word where nothing but a number would be left.
            if (stripped.Any(char.IsLetter))
                entry = stripped;
        }

        return entry;
    }

    /// <summary>"2 " for the second of several GPUs, empty on a one-GPU machine.</summary>
    private static string GpuNumber(SensorName name, Context context) =>
        context.Gpus.Count > 1 ? $"{context.Gpus.IndexOf(name.Device) + 1} " : string.Empty;

    private static string Abbreviate(string entry)
    {
        // "Core 0" → "C0"; a "Core" in front of another word adds nothing.
        string text = CoreNumber().Replace(entry, "C$1");
        text = SensorNumber().Replace(text, "S$1");
        text = CoreBeforeWord().Replace(text, "");

        foreach ((string word, string abbreviation) in Abbreviations)
            text = ReplaceWord(text, word, abbreviation);

        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>
    /// Cuts a row label to <see cref="ShortBudget"/> characters word by word, so every word keeps a
    /// few letters instead of the tail being lost: "Vorne Unten" → "Vor Unte", "Mem-Modul 3" →
    /// "Mem-Mo 3". Never touches the component tag or trailing digits.
    /// </summary>
    /// <summary>
    /// The row label. An interface or block device name ("enp5s0", "nvme0") is never cut — "enp50"
    /// would read like another interface — so where it and the tag do not both fit, the tag goes.
    /// </summary>
    private static string ShortLabel(string tag, string entry, SensorName name, Context context)
    {
        string abbreviated = Aggregate().Replace(Abbreviate(entry), "$1");
        bool deviceWord = (name.Sensor.Source == SensorSourceKind.Network && context.Interfaces > 1)
                          || (name.Sensor.Source == SensorSourceKind.Disk && context.Disks > 1);

        string label = Join(tag, abbreviated);
        if (!deviceWord)
            return Shorten(label, tag.Length > 0);

        return label.Length > ShortBudget ? Shorten(abbreviated, hasTag: true) : label;
    }

    /// <param name="hasTag">Leaves the first word untouched: the component tag, or the device name
    /// of a rate.</param>
    private static string Shorten(string label, bool hasTag)
    {
        List<string> words = label.Split(' ').ToList();
        int first = hasTag ? 1 : 0;

        while (string.Join(' ', words).Length > ShortBudget)
        {
            // The longest word other than the last with more than three letters, else the last one.
            int pick = -1;
            for (int i = first; i < words.Count - 1; i++)
            {
                if (Letters(words[i]) > 3 && (pick < 0 || words[i].Length > words[pick].Length))
                    pick = i;
            }

            if (pick < 0 && words.Count - 1 >= first && Letters(words[^1]) > 3)
                pick = words.Count - 1;

            if (pick < 0)
                break;

            words[pick] = DropLetter(words[pick]);
        }

        return string.Join(' ', words);
    }

    private static int Letters(string word) => word.Count(char.IsLetter);

    /// <summary>Removes the last letter before any trailing digits ("NotConnected1" → "NotConnecte1").</summary>
    private static string DropLetter(string word)
    {
        int end = word.Length;
        while (end > 0 && !char.IsLetter(word[end - 1]))
            end--;

        return (word[..(end - 1)] + word[end..]).TrimEnd('-', '.', ',');
    }

    /// <summary>Appends a running number to labels that collide among readings of the same unit.</summary>
    /// <remarks>A number another label already carries is skipped, so the result is unique.</remarks>
    private static void Number(List<string> labels, List<SensorName> named)
    {
        HashSet<(string, string)> taken = Enumerable.Range(0, labels.Count)
            .Select(i => (labels[i].ToUpperInvariant(), named[i].Sensor.Unit))
            .ToHashSet();

        foreach (IGrouping<(string, string), int> clash in Enumerable.Range(0, labels.Count)
                     .GroupBy(i => (labels[i].ToUpperInvariant(), named[i].Sensor.Unit))
                     .Where(g => g.Count() > 1)
                     .ToList())
        {
            int number = 1;
            foreach (int i in clash)
            {
                string numbered;
                do
                    numbered = $"{labels[i]} {number++}";
                while (taken.Contains((numbered.ToUpperInvariant(), named[i].Sensor.Unit)));

                labels[i] = numbered;
                taken.Add((numbered.ToUpperInvariant(), named[i].Sensor.Unit));
            }
        }
    }

    private static string Join(string tag, string entry)
    {
        if (tag.Length == 0 || entry.Length == 0)
            return tag.Length == 0 ? entry : tag;

        // Labels sometimes start with the component already ("RAM Used" under Memory).
        return entry.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase) ? entry : $"{tag} {entry}";
    }

    /// <summary>A segment counted from the end: 0 is the last.</summary>
    private static string Segment(string text, int fromEnd, char separator = '/')
    {
        string[] parts = text.Split(separator);
        return parts.Length > fromEnd ? parts[^(fromEnd + 1)] : text;
    }

    private static string ReplaceWord(string text, string word, string replacement) =>
        Regex.Replace(text, $@"(?<!\w){Regex.Escape(word)}(?!\w)", replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Clk max" → "max": in a row label the unit already says clock.</summary>
    [GeneratedRegex(@"^(?:Clk)\s+(max|min|avg)$")]
    private static partial Regex Aggregate();

    [GeneratedRegex(@"\b[Cc]ore\s+(\d+)\b")]
    private static partial Regex CoreNumber();

    /// <summary>"Sensor 1" → "S1" (NVMe drives number their extra sensors).</summary>
    [GeneratedRegex(@"\b[Ss]ensor\s+(\d+)\b")]
    private static partial Regex SensorNumber();

    [GeneratedRegex(@"\b[Cc]ore\s+(?=\S)")]
    private static partial Regex CoreBeforeWord();

    /// <summary>hwmon's own channel names ("temp1", "fan2") that unlabelled channels carry.</summary>
    [GeneratedRegex(@"\b(?:temp|fan|in|power|curr)\d+\b")]
    private static partial Regex HwmonChannel();

    [GeneratedRegex(@"\s+#\S+$")]
    private static partial Regex ChannelSuffix();

    /// <summary>The namespace of an NVMe block device ("nvme0n1" → "nvme0").</summary>
    [GeneratedRegex(@"(?<=^nvme\d+)n\d+$")]
    private static partial Regex NamespaceSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
