using System.Globalization;
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

    private readonly HwmonSensorSource _hwmon = new();
    private readonly ProcSensorSource _proc = new();
    private readonly ThroughputSensorSource _throughput = new();
    private readonly NvmlSensorSource _nvml = new();
    private readonly AmdGpuSensorSource _amdGpu = new();

    private readonly IPluginLogger? _logger;

    private volatile IReadOnlyList<LinuxHwInfoSensor> _sensors = [];
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private DateTime _lastPollUtc;
    private int _intervalSeconds = 2;

    public LinuxHwInfoService(IPluginLogger? logger = null) => _logger = logger;

    public IReadOnlyList<LinuxHwInfoSensor> Sensors => _sensors;

    /// <summary>True once the loop has produced at least one snapshot.</summary>
    public bool IsAvailable => _lastPollUtc != default;

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
            _logger?.Warn("LinuxHwInfo only reads Linux kernel interfaces — no sensors will be reported.");
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
                // One bad poll must never kill the loop — log and try again on the next tick.
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

    /// <summary>Short human-readable state for the settings page's status action.</summary>
    public string Diagnostics
    {
        get
        {
            IReadOnlyList<LinuxHwInfoSensor> sensors = _sensors;
            string age = _lastPollUtc == default
                ? "never polled"
                : $"last poll {(DateTime.UtcNow - _lastPollUtc).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s ago";

            return $"{sensors.Count} sensor(s) from {_hwmon.ChipCount} hwmon chip(s) — NVML: {_nvml.Status} — {age}";
        }
    }

    public void Dispose() => Stop();
}
