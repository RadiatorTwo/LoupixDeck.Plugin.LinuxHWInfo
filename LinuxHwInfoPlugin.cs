using System.Runtime.CompilerServices;
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
public sealed class LinuxHwInfoPlugin : LoupixPlugin, IMenuContributor, IPluginSettingsPage, IPluginRequirements
{
    public const string TransparentBackgroundKey = "background.transparent";
    public const string PollIntervalKey = "poll.intervalSeconds";

    /// <summary>Settings key: the CPU's maximum junction temperature in °C. CPU warn/critical
    /// limits are TjMax − 15 / TjMax − 5; hwmon does not report TjMax reliably.</summary>
    public const string CpuTjMaxKey = "thresholds.cpuTjMax";

    private const int DefaultPollIntervalSeconds = 2;
    private const long DefaultTjMax = 100;

    // Release builds of the host send all console output, plugin log lines included, into its log
    // file. Like the host's LOUPIXDECK_DEBUG_* switches, logging is opt-in.
    private static readonly bool DebugLogging =
        Environment.GetEnvironmentVariable("LOUPIXDECK_DEBUG_LINUXHWINFO") == "1";

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
        SdkVersion = new Version(1, 28, 0),
        Author = "RadiatorTwo",
        Description = "Display live Linux hardware sensor readings (hwmon, /proc, NVIDIA NVML) on touch buttons",
        Icon = LoadIcon()
    };

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(LinuxHwInfoPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.LinuxHwInfo.icon.png");
        if (stream == null) return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public override void Initialize(IPluginHost host)
    {
        _host = host;

        // Without LOUPIXDECK_DEBUG_LINUXHWINFO=1 nothing is logged; Show Status still reports the
        // sensor state.
        IPluginLogger? logger = DebugLogging ? host.Logger : null;
        _service = new LinuxHwInfoService(logger)
        {
            IntervalSeconds = ReadPollInterval(host)
        };

        _telemetry = new TelemetrySampler(_service, ReadTjMax, logger);
        _commands = [new LinuxHwInfoSensorCommand(_telemetry), new LinuxHwInfoPagesCommand(_telemetry)];
        _telemetry.Start();
        _service.Start();
    }

    public override void Shutdown()
    {
        _telemetry?.Stop();
        _service?.Stop();
    }

    // ── Requirements ───────────────────────────────────────────────────────────

    /// <summary>
    /// Readable hwmon sensors, and NVML wherever an NVIDIA card is installed. Machines without an
    /// NVIDIA card meet the NVML requirement, so AMD and Intel systems get no notice for it. Names and
    /// hints are English keys the host translates through the strings files; a message carries the
    /// reason with its details, so it is translated here and the host shows it as it is.
    /// </summary>
    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        try
        {
            SensorDiagnostic? hwmon = _service is null ? LinuxHwInfoService.ProbeHwmon() : _service.HwmonProblem;
            bool nvmlMissing = OperatingSystem.IsLinux()
                               && !(_service?.NvmlAvailable ?? false)
                               && NvmlSensorSource.NvidiaGpuPresent();

            return
            [
                new PluginRequirement
                {
                    Id = "hwmon",
                    Name = "Hardware sensors (hwmon)",
                    IsMet = hwmon is null,
                    Message = hwmon?.Translate(Tr),
                    InstallHint = "Install lm-sensors and run sensors-detect to load the sensor drivers for your board."
                },
                new PluginRequirement
                {
                    Id = "nvml",
                    Name = "NVIDIA GPU readings (NVML)",
                    IsMet = !nvmlMissing,
                    Message = nvmlMissing
                        ? string.Format(Tr("An NVIDIA GPU is installed, but NVML is not usable ({0}) — its readings are missing."),
                            _service?.NvmlStatus ?? Tr("not initialized"))
                        : null,
                    InstallHint = "Install the proprietary NVIDIA driver, which provides libnvidia-ml.so.1."
                }
            ];
        }
        catch (Exception)
        {
            // Must never throw; the host would treat it as "no requirements" anyway.
            return [];
        }
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
        PagesNode("CPU summary", ComponentPages.Summary.Id),
        PagesNode("Power page", ComponentPages.Power.Id),
        PagesNode("VRAM page", ComponentPages.Vram.Id),
        PagesNode("Battery page", ComponentPages.Battery.Id)
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
            return _host is null ? english : HostTr(_host, english);
        }
        catch (MissingMethodException)
        {
            return english;
        }
    }

    // Kept out of line: the JIT resolves IPluginHost.Tr when it compiles this method, which throws
    // on a host without it — inside Tr's try block rather than in its caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string HostTr(IPluginHost host, string english) => host.Tr(english);

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
