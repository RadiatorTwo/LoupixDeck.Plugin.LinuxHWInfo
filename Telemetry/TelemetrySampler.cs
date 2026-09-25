using LoupixDeck.Plugin.LinuxHwInfo.Sensors;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo.Telemetry;

/// <summary>
/// Turns every sensor snapshot into a <see cref="TelemetryFrame"/>: every sensor and every
/// <see cref="PageMetrics"/> metric gets a 72-sample history (one chart column per poll), clock
/// and load readings are smoothed with an EMA (α 0.4) so noise doesn't read as motion, and alert
/// states are evaluated with hysteresis. Runs on the service's poll thread, off the host's render
/// lock; render calls only read the latest published frame.
/// </summary>
/// <remarks>
/// Sampling follows the poll instead of a timer of its own: rates are deltas between two polls,
/// so a faster sampler would only repeat the same value and flatten the chart into steps.
/// </remarks>
internal sealed class TelemetrySampler(LinuxHwInfoService service, Func<double> tjMax, IPluginLogger? logger = null)
    : IDisposable
{
    /// <summary>Samples kept per metric — the width of the design's 72-px chart.</summary>
    public const int HistoryLength = 72;

    private const double EmaAlpha = 0.4;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Track> _tracks = [];
    private volatile TelemetryFrame _frame = TelemetryFrame.Unavailable;
    private bool _started;
    private string? _networkInterface;

    public TelemetryFrame Frame => _frame;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
                return;

            _started = true;
        }

        service.SnapshotUpdated += Sample;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
                return;

            _started = false;
            _tracks.Clear();
            _networkInterface = null;
            _frame = TelemetryFrame.Unavailable;
        }

        service.SnapshotUpdated -= Sample;
    }

    public void Dispose() => Stop();

    private void Sample()
    {
        try
        {
            lock (_gate)
            {
                // The poll thread may still run a handler list captured before Stop unsubscribed.
                if (_started)
                    _frame = BuildFrame();
            }
        }
        catch (Exception ex)
        {
            logger?.Error("Telemetry sample failed.", ex);
        }
    }

    private TelemetryFrame BuildFrame()
    {
        if (!service.IsAvailable)
        {
            _tracks.Clear();
            return TelemetryFrame.Unavailable;
        }

        IReadOnlyList<LinuxHwInfoSensor> sensors = service.Sensors;
        double tj = tjMax();

        List<(string Key, double Value, MetricInfo Info, LinuxHwInfoSensor? Sensor)> inputs = [];
        foreach (LinuxHwInfoSensor sensor in sensors)
            inputs.Add((MetricKeys.ForSensor(sensor), SensorMetrics.NativeValue(sensor), SensorMetrics.Describe(sensor, tj), sensor));

        string? networkInterface = NetworkRoutes.Preferred(PageMetrics.Interfaces(sensors));
        if (networkInterface != _networkInterface)
        {
            // The NET page now follows another interface (VPN up, LAN/Wi-Fi switch): its history
            // must not continue the previous interface's traffic.
            _tracks.Remove(PageMetrics.NetDown);
            _tracks.Remove(PageMetrics.NetUp);
            _networkInterface = networkInterface;
        }

        PageMetrics.Context context = new(sensors, tj, networkInterface);
        foreach (PageMetrics.Definition definition in PageMetrics.All)
        {
            if (definition.Read(context) is { } reading)
                inputs.Add((definition.Id, reading.Value, reading.Info, null));
        }

        Dictionary<string, string> gpuTemperatures = GpuTemperatures(sensors);

        // Fan rules read the temperature state they watch, so temperatures go first.
        Dictionary<string, MetricSnapshot> metrics = [];
        foreach ((string key, double value, MetricInfo info, LinuxHwInfoSensor? sensor) in
                 inputs.OrderBy(i => IsFanRule(i.Info.Threshold)))
        {
            if (!_tracks.TryGetValue(key, out Track? track))
                _tracks[key] = track = new Track();

            double smoothed = info.Smooth && !double.IsNaN(track.Last)
                ? track.Last + (EmaAlpha * (value - track.Last))
                : value;
            track.Push(smoothed);

            MetricState companion = info.Threshold switch
            {
                ThresholdKind.CpuFanStall => metrics.GetValueOrDefault(PageMetrics.CpuTemp)?.State ?? MetricState.Ok,
                // A card's own fan watches that card's temperature; the page's fan the page's GPU.
                ThresholdKind.GpuFanStall => metrics.GetValueOrDefault(
                    sensor is not null && gpuTemperatures.TryGetValue(SensorMetrics.DeviceKey(sensor), out string? temperature)
                        ? temperature
                        : PageMetrics.GpuTemp)?.State ?? MetricState.Ok,
                _ => MetricState.Ok
            };
            track.State = Thresholds.Evaluate(info.Threshold, smoothed, tj, track.State, companion);

            double[] history = track.ToArray();
            double max = info.GrowToPeak ? Math.Max(info.Max, Peak(history)) : info.Max;
            metrics[key] = new MetricSnapshot(smoothed, track.State, history, info.Min, max,
                Thresholds.LimitsFor(info.Threshold, tj)?.Warn, info.Format, info.Unit);
        }

        // A metric that vanished (device unplugged, interface down) keeps a gap in its chart and is
        // dropped once its whole history has scrolled out.
        foreach ((string key, Track track) in _tracks.ToArray())
        {
            if (metrics.ContainsKey(key))
                continue;

            track.Push(double.NaN);
            if (++track.Missing >= HistoryLength)
                _tracks.Remove(key);
        }

        return new TelemetryFrame(true, sensors, metrics);
    }

    /// <summary>Per GPU (by <see cref="SensorMetrics.DeviceKey"/>) the id of its core temperature,
    /// or of its first temperature when it reports no core reading.</summary>
    private static Dictionary<string, string> GpuTemperatures(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        Dictionary<string, string> temperatures = [];
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            if (sensor.Category != Categories.Gpu || sensor.Type != LinuxHwInfoReadingType.Temperature)
                continue;

            string device = SensorMetrics.DeviceKey(sensor);
            if (SensorMetrics.IsGpuCoreTemperature(sensor) || !temperatures.ContainsKey(device))
                temperatures[device] = sensor.Id;
        }

        return temperatures;
    }

    private static bool IsFanRule(ThresholdKind kind) =>
        kind is ThresholdKind.CpuFanStall or ThresholdKind.GpuFanStall;

    private static double Peak(double[] history)
    {
        double peak = double.MinValue;
        foreach (double value in history)
        {
            if (!double.IsNaN(value) && value > peak)
                peak = value;
        }

        return peak;
    }

    /// <summary>Ring buffer of one metric's last <see cref="HistoryLength"/> samples.</summary>
    private sealed class Track
    {
        private readonly double[] _samples = new double[HistoryLength];
        private int _count;
        private int _next;

        public double Last { get; private set; } = double.NaN;

        public MetricState State { get; set; }

        public int Missing { get; set; }

        public void Push(double value)
        {
            _samples[_next] = value;
            _next = (_next + 1) % HistoryLength;
            _count = Math.Min(_count + 1, HistoryLength);
            Last = value;
            if (!double.IsNaN(value))
                Missing = 0;
        }

        public double[] ToArray()
        {
            double[] result = new double[_count];
            int start = (_next - _count + HistoryLength) % HistoryLength;
            for (int i = 0; i < _count; i++)
                result[i] = _samples[(start + i) % HistoryLength];

            return result;
        }
    }
}
