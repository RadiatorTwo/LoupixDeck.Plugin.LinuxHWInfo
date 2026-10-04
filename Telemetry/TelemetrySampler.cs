using System.Collections.Concurrent;
using LoupixDeck.Plugin.LinuxHwInfo.Sensors;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo.Telemetry;

/// <summary>
/// Turns every sensor snapshot into a <see cref="TelemetryFrame"/>: every <see cref="PageMetrics"/>
/// metric, and every sensor a tile has asked for, gets a 72-sample history (one chart column per poll), clock
/// and load readings are smoothed with an EMA (α 0.4) so noise doesn't read as motion, and alert
/// states are evaluated with hysteresis. Runs on the service's poll thread, off the host's render
/// lock; render calls only read the latest published frame.
/// </summary>
/// <remarks>
/// Sampling follows the poll instead of a timer of its own: rates are deltas between two polls,
/// so a faster sampler would only repeat the same value and flatten the chart into steps.
/// </remarks>
internal sealed class TelemetrySampler(LinuxHwInfoService service, Func<TelemetrySettings> readSettings,
    IPluginLogger? logger = null)
    : IDisposable
{
    /// <summary>Samples kept per metric — the width of the design's 72-px chart.</summary>
    public const int HistoryLength = 72;

    private const double EmaAlpha = 0.4;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    // Metric keys a tile has asked for (TelemetryFrame.Get); written by render threads.
    private readonly ConcurrentDictionary<string, byte> _requested = new(StringComparer.Ordinal);

    // Reused on every sample, which runs under _gate.
    private readonly List<(string Key, double Value, MetricInfo Info, LinuxHwInfoSensor? Sensor)> _inputs = [];
    private readonly HashSet<string> _companions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gpuTemperatures = new(StringComparer.Ordinal);
    private readonly List<string> _gone = [];
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
        TelemetrySettings settings = readSettings();
        double tj = settings.TjMax;

        // Single sensors only once a tile has asked for them; the page metrics always, since the
        // pages and the fan rules read them.
        FillGpuTemperatures(sensors);
        _inputs.Clear();
        _companions.Clear();
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            string key = MetricKeys.ForSensor(sensor);
            if (!_requested.ContainsKey(key))
                continue;

            MetricInfo info = SensorMetrics.Describe(sensor, tj);
            _inputs.Add((key, SensorMetrics.NativeValue(sensor), info, sensor));

            // A card's fan watches that card's temperature, so it is tracked too.
            if (info.Threshold == ThresholdKind.GpuFanStall
                && _gpuTemperatures.TryGetValue(SensorMetrics.DeviceKey(sensor), out string? temperature)
                && !_requested.ContainsKey(temperature))
                _companions.Add(temperature);
        }

        if (_companions.Count > 0)
        {
            foreach (LinuxHwInfoSensor sensor in sensors)
            {
                string key = MetricKeys.ForSensor(sensor);
                if (_companions.Remove(key))
                    _inputs.Add((key, SensorMetrics.NativeValue(sensor), SensorMetrics.Describe(sensor, tj), sensor));
            }
        }

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
                _inputs.Add((definition.Id, reading.Value, reading.Info, null));
        }

        // Fan rules read the temperature state they watch, so temperatures go first.
        Dictionary<string, MetricSnapshot> metrics = new(_inputs.Count, StringComparer.Ordinal);
        foreach ((string key, double value, MetricInfo info, LinuxHwInfoSensor? sensor) in _inputs)
        {
            if (!IsFanRule(info.Threshold))
                Update(key, value, info, sensor, settings, metrics);
        }

        foreach ((string key, double value, MetricInfo info, LinuxHwInfoSensor? sensor) in _inputs)
        {
            if (IsFanRule(info.Threshold))
                Update(key, value, info, sensor, settings, metrics);
        }

        // A metric that vanished (device unplugged, interface down) keeps a gap in its chart and is
        // dropped once its whole history has scrolled out.
        _gone.Clear();
        foreach ((string key, Track track) in _tracks)
        {
            if (metrics.ContainsKey(key))
                continue;

            track.Push(double.NaN);
            if (++track.Missing >= HistoryLength)
                _gone.Add(key);
        }

        foreach (string key in _gone)
            _tracks.Remove(key);

        return new TelemetryFrame(true, sensors, metrics, _requested);
    }

    private void Update(string key, double value, MetricInfo info, LinuxHwInfoSensor? sensor,
        TelemetrySettings settings, Dictionary<string, MetricSnapshot> metrics)
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
                sensor is not null && _gpuTemperatures.TryGetValue(SensorMetrics.DeviceKey(sensor), out string? temperature)
                    ? temperature
                    : PageMetrics.GpuTemp)?.State ?? MetricState.Ok,
            _ => MetricState.Ok
        };
        track.State = Thresholds.Evaluate(info.Threshold, smoothed, settings, track.State, companion);

        ReadOnlyMemory<double> history = track.History();
        double max = info.GrowToPeak ? Math.Max(info.Max, Peak(history.Span)) : info.Max;
        // Only the text changes with the unit; value, history, bar and limits stay in °C.
        MetricFormat format = settings.Fahrenheit && info.Format == MetricFormat.Temperature
            ? MetricFormat.TemperatureFahrenheit
            : info.Format;
        metrics[key] = new MetricSnapshot(smoothed, track.State, history, info.Min, max,
            Thresholds.LimitsFor(info.Threshold, settings)?.Warn, format, info.Unit);
    }

    /// <summary>Per GPU (by <see cref="SensorMetrics.DeviceKey"/>) the id of its core temperature,
    /// or of its first temperature when it reports no core reading.</summary>
    private void FillGpuTemperatures(IReadOnlyList<LinuxHwInfoSensor> sensors)
    {
        _gpuTemperatures.Clear();
        foreach (LinuxHwInfoSensor sensor in sensors)
        {
            if (sensor.Category != Categories.Gpu || sensor.Type != LinuxHwInfoReadingType.Temperature)
                continue;

            string device = SensorMetrics.DeviceKey(sensor);
            if (SensorMetrics.IsGpuCoreTemperature(sensor) || !_gpuTemperatures.ContainsKey(device))
                _gpuTemperatures[device] = sensor.Id;
        }
    }

    private static bool IsFanRule(ThresholdKind kind) =>
        kind is ThresholdKind.CpuFanStall or ThresholdKind.GpuFanStall;

    private static double Peak(ReadOnlySpan<double> history)
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

        // The history handed to a frame, oldest to newest. Two buffers taken in turn: a frame's
        // buffer is rewritten only two samples later, long after every render has moved on to a
        // newer frame, so no sample allocates and no render reads a buffer being written.
        private readonly double[][] _views = [new double[HistoryLength], new double[HistoryLength]];
        private int _view;
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

        public ReadOnlyMemory<double> History()
        {
            _view ^= 1;
            double[] result = _views[_view];
            int start = (_next - _count + HistoryLength) % HistoryLength;
            for (int i = 0; i < _count; i++)
                result[i] = _samples[(start + i) % HistoryLength];

            return result.AsMemory(0, _count);
        }
    }
}
