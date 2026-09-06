namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Maps hwmon chip names onto a human-readable device label and a top-level menu category.
/// Unknown chips fall through to their raw driver name under "Other" rather than being hidden.
/// </summary>
internal static class ChipNaming
{
    public static string PrettyName(string chip)
    {
        if (chip.StartsWith("nct6", StringComparison.Ordinal)
            || chip.StartsWith("it8", StringComparison.Ordinal)
            || chip.StartsWith("w836", StringComparison.Ordinal)
            || chip.StartsWith("f71", StringComparison.Ordinal))
            return "Super I/O";

        if (chip.StartsWith("iwlwifi", StringComparison.Ordinal)
            || chip.StartsWith("ath", StringComparison.Ordinal)
            || chip.StartsWith("mt76", StringComparison.Ordinal))
            return "Wi-Fi";

        return chip switch
        {
            "k10temp" or "zenpower" or "coretemp" => "CPU",
            "nvme" => "NVMe",
            "drivetemp" => "Drive",
            "jc42" or "spd5118" => "DIMM",
            "amdgpu" => "GPU",
            "acpitz" => "ACPI",
            _ => chip
        };
    }

    public static string Category(string chip)
    {
        if (chip.StartsWith("nct6", StringComparison.Ordinal)
            || chip.StartsWith("it8", StringComparison.Ordinal)
            || chip.StartsWith("w836", StringComparison.Ordinal)
            || chip.StartsWith("f71", StringComparison.Ordinal))
            return Categories.Motherboard;

        if (chip.StartsWith("iwlwifi", StringComparison.Ordinal)
            || chip.StartsWith("ath", StringComparison.Ordinal)
            || chip.StartsWith("mt76", StringComparison.Ordinal)
            || chip.StartsWith("r816", StringComparison.Ordinal))
            return Categories.Network;

        return chip switch
        {
            "k10temp" or "zenpower" or "coretemp" => Categories.Cpu,
            "nvme" or "drivetemp" => Categories.Storage,
            "jc42" or "spd5118" => Categories.Memory,
            "amdgpu" or "i915" or "xe" => Categories.Gpu,
            "acpitz" => Categories.Motherboard,
            _ => Categories.Other
        };
    }
}

/// <summary>Top-level menu buckets, in the order they are presented.</summary>
internal static class Categories
{
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Motherboard = "Motherboard";
    public const string Storage = "Storage";
    public const string Memory = "Memory";
    public const string Network = "Network";
    public const string Other = "Other";

    public static readonly string[] Order = [Cpu, Gpu, Motherboard, Storage, Memory, Network, Other];
}
