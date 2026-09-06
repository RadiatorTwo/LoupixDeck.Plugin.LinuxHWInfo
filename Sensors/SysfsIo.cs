namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Read helpers for sysfs and procfs. Every method swallows I/O failures and reports them through the
/// return value: a sensor channel can disappear mid-poll (device detached, driver returns EIO on a
/// stale register) and that must never abort the surrounding walk.
/// </summary>
internal static class SysfsIo
{
    public static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryReadLong(string path, out long value)
    {
        value = 0;
        string? text = ReadText(path);
        return text != null && long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public static double? ReadScaled(string path, double divisor)
    {
        return TryReadLong(path, out long raw) ? raw / divisor : null;
    }

    public static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static IEnumerable<string> EnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Fully resolves a sysfs path the way <c>readlink -f</c> does, following symlinks in every path
    /// component and re-basing each relative target on the directory it was read from.
    /// </summary>
    /// <remarks>
    /// <c>Directory.ResolveLinkTarget</c> is not usable here. The sysfs class directories are themselves
    /// symlinks holding relative targets such as <c>../../devices/platform/nct6775.2592/hwmon/hwmon7</c>, so
    /// resolving only the final component against an unresolved parent yields a nonsense path
    /// (<c>/sys/nct6775.2592</c> instead of <c>/sys/devices/platform/nct6775.2592</c>). The bus keys that make
    /// sensor identifiers stable are derived from this path, so it has to be right.
    /// </remarks>
    public static string? RealPath(string path)
    {
        List<string> pending = [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        string current = string.Empty;
        int guard = 0;
        int index = 0;

        while (index < pending.Count)
        {
            // Bounds the work in the presence of a symlink cycle; sysfs never nests anywhere near this deep.
            if (++guard > 256)
                return null;

            string segment = pending[index];
            if (segment == ".")
            {
                index++;
                continue;
            }

            if (segment == "..")
            {
                int slash = current.LastIndexOf('/');
                current = slash <= 0 ? string.Empty : current[..slash];
                index++;
                continue;
            }

            string candidate = $"{current}/{segment}";
            string? target = LinkTargetOf(candidate);
            if (target == null)
            {
                current = candidate;
                index++;
                continue;
            }

            // Splice the link's own segments in where the link was, so they get resolved in turn.
            if (target.StartsWith('/'))
                current = string.Empty;

            pending.RemoveAt(index);
            pending.InsertRange(index, target.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        return current.Length == 0 ? "/" : current;
    }

    private static string? LinkTargetOf(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget ?? new FileInfo(path).LinkTarget;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
