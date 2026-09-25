using System.Globalization;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Picks the interface whose traffic the NET page shows: the one that carries the default route,
/// read from <c>/proc/net/route</c>. Bridges, containers and VPN helpers come and go; the default
/// route is what the machine actually talks to the internet through.
/// </summary>
internal static class NetworkRoutes
{
    private const string RouteTable = "/proc/net/route";
    private const string NetClassRoot = "/sys/class/net";

    /// <summary>
    /// Of <paramref name="interfaces"/>: the default-route interface; without one, the first
    /// physical interface (one backed by a device — not a bridge, veth or tunnel); else the first.
    /// </summary>
    public static string? Preferred(IReadOnlyList<string> interfaces)
    {
        if (interfaces.Count == 0)
            return null;

        if (DefaultInterface() is { } route && interfaces.Contains(route))
            return route;

        foreach (string iface in interfaces)
        {
            if (Directory.Exists(Path.Combine(NetClassRoot, iface, "device")))
                return iface;
        }

        return interfaces[0];
    }

    /// <summary>The default-route interface with the lowest metric, or null when there is none.</summary>
    public static string? DefaultInterface()
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(RouteTable);
        }
        catch (Exception)
        {
            return null;
        }

        string? best = null;
        long bestMetric = long.MaxValue;
        foreach (string line in lines.Skip(1))
        {
            // Iface Destination Gateway Flags RefCnt Use Metric Mask ...
            string[] fields = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8 || fields[1] != "00000000" || fields[7] != "00000000")
                continue;

            if (!long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long metric))
                continue;

            if (metric < bestMetric)
            {
                best = fields[0];
                bestMetric = metric;
            }
        }

        return best;
    }
}
