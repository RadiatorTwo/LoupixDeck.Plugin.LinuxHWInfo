using LoupixDeck.Plugin.LinuxHwInfo.Rendering.Tiles;
using LoupixDeck.Plugin.LinuxHwInfo.Sensors;
using LoupixDeck.Plugin.LinuxHwInfo.Telemetry;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo;

/// <summary>
/// Reads hardware sensors straight from the Linux kernel interfaces — <c>/sys/class/hwmon</c>,
/// <c>/proc</c> and, for NVIDIA cards, NVML — samples them into histories and alert states, and renders
/// them as pixel tiles on touch buttons.
/// The Linux counterpart to the HWiNFO and Argus Monitor plugins.
/// </summary>
public sealed class LinuxHwInfoPlugin : LoupixPlugin, IMenuContributor, IPluginSettingsPage
{
    public const string TransparentBackgroundKey = "background.transparent";
    public const string PollIntervalKey = "poll.intervalSeconds";

    /// <summary>Settings key: the CPU's maximum junction temperature in °C. CPU warn/critical
    /// limits are TjMax − 15 / TjMax − 5; hwmon does not report TjMax reliably.</summary>
    public const string CpuTjMaxKey = "thresholds.cpuTjMax";

    private const int DefaultPollIntervalSeconds = 2;
    private const long DefaultTjMax = 100;

    private LinuxHwInfoService? _service;
    private TelemetrySampler? _telemetry;
    private List<IPluginCommand> _commands = [];
    private IPluginHost? _host;
    private IReadOnlyList<PluginSettingAction>? _settingsActions;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "linuxhwinfo",
        Name = "LinuxHwInfo",
        Version = new Version(1, 1, 0),
        SdkVersion = new Version(1, 26, 0),
        Author = "RadiatorTwo",
        Description = "Display live Linux hardware sensor readings (hwmon, /proc, NVIDIA NVML) on touch buttons"
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _service = new LinuxHwInfoService(host.Logger)
        {
            IntervalSeconds = ReadPollInterval(host)
        };

        _telemetry = new TelemetrySampler(_service, ReadTjMax, host.Logger);
        _commands = [new LinuxHwInfoSensorCommand(_telemetry), new LinuxHwInfoPagesCommand(_telemetry)];
        _telemetry.Start();
        _service.Start();
    }

    public override void Shutdown()
    {
        _telemetry?.Stop();
        _service?.Stop();
    }

    private double ReadTjMax()
    {
        long tjMax = _host?.Settings.Get(CpuTjMaxKey, DefaultTjMax) ?? DefaultTjMax;
        return Math.Clamp(tjMax, 60, 125);
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    // ── Dynamic menu ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the menu: the paging tiles, then every sensor sorted by component and quantity (one
    /// command each; chain several on a button for a multi-row tile). See <see cref="SensorMenu"/>.
    /// </summary>
    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (target != ButtonTargets.TouchButton || _service == null)
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        // The pages need no sensor list, so they are offered before the first poll has finished.
        List<MenuNode> children = [new MenuNode { Name = "Pages", Children = PageNodes() }];
        children.AddRange(SensorMenu.Build(_service.Sensors));

        return Task.FromResult<IReadOnlyList<MenuNode>>(
            [new MenuNode { Name = "LinuxHwInfo", Children = children }]);
    }

    /// <summary>The paging tile (every page, press for the next) and one fixed tile per page.</summary>
    private static List<MenuNode> PageNodes() =>
    [
        PagesNode("All pages (press to cycle)", ComponentPages.DefaultSelection),
        PagesNode("CPU page", ComponentPages.Cpu.Id),
        PagesNode("GPU page", ComponentPages.Gpu.Id),
        PagesNode("RAM page", ComponentPages.Ram.Id),
        PagesNode("Network page", ComponentPages.Net.Id),
        PagesNode("Disk page", ComponentPages.Disk.Id),
        PagesNode("CPU summary", ComponentPages.Summary.Id)
    ];

    private static MenuNode PagesNode(string name, string pages) => new()
    {
        Name = name,
        CommandName = LinuxHwInfoPagesCommand.CommandName,
        Parameters = new Dictionary<string, string> { { "Pages", pages } }
    };

    // ── Settings page ──────────────────────────────────────────────────────────

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new PluginSettingDescriptor
        {
            Key = TransparentBackgroundKey,
            Label = "Transparent background",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = false,
            Description = "Draw sensor tiles without an opaque background so the page wallpaper shows through. " +
                          "Text gets a 1-pixel shadow for legibility."
        },
        new PluginSettingDescriptor
        {
            Key = PollIntervalKey,
            Label = "Poll interval (seconds)",
            Kind = PluginSettingKind.Number,
            DefaultValue = DefaultPollIntervalSeconds,
            Description = "How often sensors are read. Clamped to 1–60 seconds."
        },
        new PluginSettingDescriptor
        {
            Key = CpuTjMaxKey,
            Label = "CPU TjMax (°C)",
            Kind = PluginSettingKind.Number,
            DefaultValue = DefaultTjMax,
            Description = "Maximum junction temperature of your CPU, from the vendor's spec sheet " +
                          "(typically 95 for AMD Ryzen, 100–105 for Intel). CPU temperature turns amber " +
                          "at TjMax − 15 and red at TjMax − 5."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions => _settingsActions ??=
    [
        new PluginSettingAction
        {
            Label = "Show Status",
            Invoke = () => Task.FromResult(_service?.Diagnostics(Tr) ?? Tr("not initialized"))
        }
    ];

    /// <summary>Translates runtime text through the plugin's strings files; hosts before SDK 1.24
    /// have no <see cref="IPluginHost.Tr"/> and get the English text.</summary>
    private string Tr(string english)
    {
        try
        {
            return _host?.Tr(english) ?? english;
        }
        catch (MissingMethodException)
        {
            return english;
        }
    }

    public void OnSettingsSaved()
    {
        if (_host == null)
            return;

        if (_service != null)
            _service.IntervalSeconds = ReadPollInterval(_host);

        // Tiles redraw several times a second and pick up the new settings on their own; this
        // only covers a host that drives them through the slower poll path.
        _host.RequestButtonRefresh(LinuxHwInfoSensorCommand.Name);
        _host.RequestButtonRefresh(LinuxHwInfoPagesCommand.CommandName);
    }

    /// <summary>Number settings come back as <c>long</c> from the JSON store.</summary>
    private static int ReadPollInterval(IPluginHost host)
    {
        long seconds = host.Settings.Get<long>(PollIntervalKey, DefaultPollIntervalSeconds);
        return seconds <= 0 ? DefaultPollIntervalSeconds : (int)seconds;
    }
}
