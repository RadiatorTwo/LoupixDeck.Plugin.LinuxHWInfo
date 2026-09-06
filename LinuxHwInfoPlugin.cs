using LoupixDeck.Plugin.LinuxHwInfo.Sensors;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo;

/// <summary>
/// Reads hardware sensors straight from the Linux kernel interfaces — <c>/sys/class/hwmon</c>,
/// <c>/proc</c> and, for NVIDIA cards, NVML — and renders them as live tiles on touch buttons.
/// The Linux counterpart to the HWiNFO and Argus Monitor plugins.
/// </summary>
public sealed class LinuxHwInfoPlugin : LoupixPlugin, IMenuContributor, IPluginSettingsPage
{
    public const string TransparentBackgroundKey = "background.transparent";
    public const string PollIntervalKey = "poll.intervalSeconds";

    private const int DefaultPollIntervalSeconds = 2;

    private LinuxHwInfoService? _service;
    private List<IPluginCommand> _commands = [];
    private IPluginHost? _host;
    private IReadOnlyList<PluginSettingAction>? _settingsActions;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "linuxhwinfo",
        Name = "LinuxHwInfo",
        Version = new Version(1, 0, 0),
        SdkVersion = new Version(1, 21, 0),
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

        _commands = [new LinuxHwInfoSensorCommand(_service)];
        _service.Start();
    }

    public override void Shutdown() => _service?.Stop();

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    // ── Dynamic menu ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the sensor picker: category → device → reading. Nothing is filtered out — every channel the
    /// kernel exposes is offered, including implausible ones, because only the user can tell which of a
    /// board's Super-I/O channels are actually wired up.
    /// </summary>
    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (target != ButtonTargets.TouchButton || _service == null)
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        IReadOnlyList<LinuxHwInfoSensor> sensors = _service.Sensors;
        if (sensors.Count == 0)
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        List<MenuNode> categories = [];
        foreach (string category in Categories.Order)
        {
            List<MenuNode> devices = [];

            foreach (IGrouping<string, LinuxHwInfoSensor> device in sensors
                         .Where(s => s.Category == category)
                         .GroupBy(s => s.Group)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<MenuNode> readings = device
                    .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new MenuNode
                    {
                        Name = s.Label,
                        CommandName = LinuxHwInfoSensorCommand.Name,
                        Parameters = new Dictionary<string, string> { { "Sensor", s.Id } }
                    })
                    .ToList();

                devices.Add(new MenuNode { Name = device.Key, Children = readings });
            }

            if (devices.Count > 0)
                categories.Add(new MenuNode { Name = category, Children = devices });
        }

        return Task.FromResult<IReadOnlyList<MenuNode>>(
            [new MenuNode { Name = "LinuxHwInfo", Children = categories }]);
    }

    // ── Settings page ──────────────────────────────────────────────────────────

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new PluginSettingDescriptor
        {
            Key = TransparentBackgroundKey,
            Label = "Transparent background",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = false,
            Description = "Draw sensor tiles without an opaque background so the page wallpaper shows through."
        },
        new PluginSettingDescriptor
        {
            Key = PollIntervalKey,
            Label = "Poll interval (seconds)",
            Kind = PluginSettingKind.Number,
            DefaultValue = DefaultPollIntervalSeconds,
            Description = "How often sensors are read. Clamped to 1–60 seconds."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions => _settingsActions ??=
    [
        new PluginSettingAction
        {
            Label = "Show Status",
            Invoke = () => Task.FromResult(_service?.Diagnostics ?? "not initialized")
        }
    ];

    public void OnSettingsSaved()
    {
        if (_host == null)
            return;

        if (_service != null)
            _service.IntervalSeconds = ReadPollInterval(_host);

        // Repaint immediately rather than waiting for the next poll tick.
        _host.RequestButtonRefresh(LinuxHwInfoSensorCommand.Name);
    }

    /// <summary>Number settings come back as <c>long</c> from the JSON store.</summary>
    private static int ReadPollInterval(IPluginHost host)
    {
        long seconds = host.Settings.Get<long>(PollIntervalKey, DefaultPollIntervalSeconds);
        return seconds <= 0 ? DefaultPollIntervalSeconds : (int)seconds;
    }
}
