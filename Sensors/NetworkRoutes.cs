using System.Globalization;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Finds the interface that carries the default route, read from <c>/proc/net/route</c>. It is the
/// one whose traffic the NET page shows: bridges, containers and VPN helpers come and go, the
/// default route is what the machine actually talks to the internet through.
/// </summary>
internal static class NetworkRoutes
{
    private const string RouteTable = "/proc/net/route";

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
