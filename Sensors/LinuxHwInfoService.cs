using System.Globalization;
using System.Text;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// Owns the single background poll loop and publishes the merged sensor snapshot.
/// </summary>
/// <remarks>
/// One loop covers every source: the sysfs reads are kernel-memory reads costing well under a millisecond in
/// total, and the NVML getters read cached driver state, so splitting them across threads would only add
/// merge complexity. The snapshot is swapped in one assignment to a <c>volatile</c> field, which is all the
/// synchronisation a lock-free single-writer / many-reader hand-off needs.
/// </remarks>
public sealed class LinuxHwInfoService : IDisposable
{
    private const int MinimumIntervalSeconds = 1;
    private const int MaximumIntervalSeconds = 60;

    /// <summary>Shown by the status action, and logged, wherever the plugin is not on Linux.</summary>
    public const string NotLinux = "LinuxHwInfo reads Linux kernel interfaces only — no sensors on this system.";

    private readonly HwmonSensorSource _hwmon = new();
    private readonly ProcSensorSource _proc = new();
    private readonly ThroughputSensorSource _throughput = new();
    private readonly NvmlSensorSource _nvml = new();
    private readonly AmdGpuSensorSource _amdGpu = new();

    private readonly IPluginLogger? _logger;

    private volatile IReadOnlyList<LinuxHwInfoSensor> _sensors = [];
    private volatile SensorDiagnostic? _lastError;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private DateTime _lastPollUtc;
    private int _intervalSeconds = 2;

    public LinuxHwInfoService(IPluginLogger? logger = null) => _logger = logger;

    public IReadOnlyList<LinuxHwInfoSensor> Sensors => _sensors;

    /// <summary>True once the loop has produced at least one snapshot.</summary>
    public bool IsAvailable => _lastPollUtc != default;

    /// <summary>
    /// Why hwmon yields no readings, or null when it yields some. Before the first poll the hwmon tree
    /// is walked right away, so a caller asking early gets no false alarm.
    /// </summary>
    internal SensorDiagnostic? HwmonProblem => IsAvailable ? _hwmon.Problem : ProbeHwmon();

    /// <summary>Walks the hwmon tree right now; see <see cref="HwmonSensorSource.Probe"/>.</summary>
    internal static SensorDiagnostic? ProbeHwmon() =>
        OperatingSystem.IsLinux() ? HwmonSensorSource.Probe() : new SensorDiagnostic(NotLinux, []);

    /// <summary>True once NVML is initialized and reports at least one GPU.</summary>
    internal bool NvmlAvailable => _nvml.IsAvailable;

    /// <summary>NVML's state as the driver reports it (technical, not translated).</summary>
    internal string NvmlStatus => _nvml.Status;

    public event Action? SnapshotUpdated;

    /// <summary>Poll cadence in seconds; clamped to a sane range. Takes effect on the next tick.</summary>
    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = Math.Clamp(value, MinimumIntervalSeconds, MaximumIntervalSeconds);
    }

    public void Start()
    {
        if (_pollTask != null)
            return;

        if (!OperatingSystem.IsLinux())
        {
            _logger?.Warn(NotLinux);
            return;
        }

        _nvml.Start();
        _logger?.Info($"NVML: {_nvml.Status}");

        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_cts.Token), _cts.Token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _pollTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutdown is best-effort; a stuck poll must not block application exit.
        }

        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        _nvml.Stop();
        _proc.Reset();
        _throughput.Reset();
        _lastPollUtc = default;
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                List<LinuxHwInfoSensor> snapshot = new(128);
                snapshot.AddRange(_hwmon.Poll());
                snapshot.AddRange(_proc.Poll());
                snapshot.AddRange(_throughput.Poll());
                snapshot.AddRange(_nvml.Poll());
                snapshot.AddRange(_amdGpu.Poll());

                _sensors = snapshot;
                _lastPollUtc = DateTime.UtcNow;
                SnapshotUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                // One bad poll must never kill the loop — record it and try again on the next tick.
                _lastError = new SensorDiagnostic("Sensor poll failed ({0}: {1}).", [ex.GetType().Name, ex.Message]);
                _logger?.Error("Sensor poll failed.", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Short human-readable state for the settings page's status action: the counts, why hwmon yields
    /// nothing (no chips, no access) and the last poll and NVML errors. <paramref name="tr"/> translates
    /// the English text; the NVML status is technical and stays as the driver reports it.
    /// </summary>
    public string Diagnostics(Func<string, string> tr)
    {
        if (!OperatingSystem.IsLinux())
            return tr(NotLinux);

        IReadOnlyList<LinuxHwInfoSensor> sensors = _sensors;
        string age = _lastPollUtc == default
            ? tr("never polled")
            : string.Format(tr("last poll {0}s ago"),
                (DateTime.UtcNow - _lastPollUtc).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture));

        StringBuilder text = new(string.Format(tr("{0} sensor(s) from {1} hwmon chip(s) — NVML: {2} — {3}"),
            sensors.Count, _hwmon.ChipCount, _nvml.Status, age));

        if (_lastPollUtc != default && _hwmon.Problem is { } problem)
            text.Append('\n').Append(problem.Translate(tr));

        foreach (SensorDiagnostic? error in (SensorDiagnostic?[])[_lastError, _nvml.LastError])
        {
            if (error is not null)
                text.Append('\n').Append(string.Format(tr("Last error: {0}"), error.Translate(tr)));
        }

        return text.ToString();
    }

    public void Dispose() => Stop();
}

/// <summary>A status message of the sensor sources: an English format string and its arguments,
/// translated by the plugin when shown.</summary>
internal sealed record SensorDiagnostic(string Format, object[] Args)
{
    public string Translate(Func<string, string> tr) => string.Format(CultureInfo.InvariantCulture, tr(Format), Args);
}
