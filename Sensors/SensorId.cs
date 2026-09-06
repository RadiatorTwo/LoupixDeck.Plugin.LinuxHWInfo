namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Builds the stable sensor identifiers that get persisted into user button configurations.
/// </summary>
/// <remarks>
/// <para>
/// These strings are a permanent public contract. Once a version has shipped, neither the field order,
/// the <c>/</c> separator, nor the way a bus key is derived may change — an old configuration must keep
/// resolving to the same sensor. A future format change has to be additive (a new leading segment) with
/// the old form still understood.
/// </para>
/// <para>
/// <c>/</c> is the separator because none of the fields can contain one, while a PCI address such as
/// <c>0000:00:18.3</c> rules out <c>:</c>.
/// </para>
/// </remarks>
internal static class SensorId
{
    /// <summary>e.g. <c>hwmon/pci/0000:00:18.3/k10temp/temp1</c>.</summary>
    public static string Hwmon(string deviceKey, string chip, string channel) => $"hwmon/{deviceKey}/{chip}/{channel}";

    /// <summary>e.g. <c>nvml/GPU-5564d92f-.../temp</c>.</summary>
    public static string Nvml(string uuid, string field) => $"nvml/{uuid}/{field}";

    /// <summary>e.g. <c>proc/cpu/total</c>, <c>proc/mem/used</c>.</summary>
    public static string Proc(string group, string field) => $"proc/{group}/{field}";

    /// <summary>e.g. <c>net/enp5s0/rx</c>.</summary>
    public static string Net(string iface, string field) => $"net/{iface}/{field}";

    /// <summary>e.g. <c>disk/nvme0n1/read</c>.</summary>
    public static string Disk(string device, string field) => $"disk/{device}/{field}";
}
