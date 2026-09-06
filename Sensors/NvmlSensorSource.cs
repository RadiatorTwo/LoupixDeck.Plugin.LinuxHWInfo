using System.Text;
using LoupixDeck.Plugin.LinuxHwInfo.Interop;

namespace LoupixDeck.Plugin.LinuxHwInfo.Sensors;

/// <summary>
/// NVIDIA GPU readings via NVML. Silent no-op when the driver library is absent.
/// </summary>
/// <remarks>
/// <c>nvmlInit</c> performs a driver handshake and is done exactly once, in <see cref="Start"/>; device
/// handles stay valid for the whole session and are cached alongside it. Only the per-field getters run on
/// each poll — they read cached driver state and do not block.
/// </remarks>
internal sealed class NvmlSensorSource
{
    private readonly List<NvmlDevice> _devices = [];
    private bool _initialized;

    /// <summary>Human-readable state for the settings page's status action.</summary>
    public string Status { get; private set; } = "not started";

    public bool IsAvailable => _initialized && _devices.Count > 0;

    public void Start()
    {
        if (_initialized)
            return;

        try
        {
            Nvml.NvmlReturn result = Nvml.Init();
            if (result != Nvml.NvmlReturn.Success)
            {
                Status = $"nvmlInit failed ({result})";
                return;
            }

            _initialized = true;
            EnumerateDevices();
            Status = _devices.Count > 0
                ? $"{_devices.Count} GPU(s): {string.Join(", ", _devices.Select(d => d.Name))}"
                : "initialized, no GPU reported";
        }
        catch (DllNotFoundException)
        {
            // Expected on any machine without the proprietary NVIDIA driver.
            Status = "libnvidia-ml.so.1 not present";
        }
        catch (EntryPointNotFoundException ex)
        {
            Status = $"driver too old for NVML entry point: {ex.Message}";
        }
    }

    public void Stop()
    {
        if (!_initialized)
            return;

        _initialized = false;
        _devices.Clear();
        try
        {
            Nvml.Shutdown();
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down.
        }
    }

    private void EnumerateDevices()
    {
        if (Nvml.DeviceGetCount(out uint count) != Nvml.NvmlReturn.Success)
            return;

        for (uint i = 0; i < count; i++)
        {
            if (Nvml.DeviceGetHandleByIndex(i, out IntPtr handle) != Nvml.NvmlReturn.Success)
                continue;

            // The UUID is burned into the board and survives reboots and slot changes, unlike the NVML
            // index, which is enumeration-order dependent exactly like hwmonN.
            StringBuilder uuidBuffer = new(Nvml.DeviceUuidBufferSize);
            if (Nvml.DeviceGetUuid(handle, uuidBuffer, Nvml.DeviceUuidBufferSize) != Nvml.NvmlReturn.Success)
                continue;

            StringBuilder nameBuffer = new(Nvml.DeviceNameBufferSize);
            string name = Nvml.DeviceGetName(handle, nameBuffer, Nvml.DeviceNameBufferSize) == Nvml.NvmlReturn.Success
                ? nameBuffer.ToString()
                : "NVIDIA GPU";

            _devices.Add(new NvmlDevice(handle, uuidBuffer.ToString(), name));
        }
    }

    public IEnumerable<LinuxHwInfoSensor> Poll()
    {
        if (!_initialized)
            return [];

        List<LinuxHwInfoSensor> sensors = [];
        foreach (NvmlDevice device in _devices)
        {
            try
            {
                CollectDevice(device, sensors);
            }
            catch (Exception)
            {
                // A GPU that fell off the bus must not take the rest of the snapshot with it.
            }
        }

        return sensors;
    }

    private static void CollectDevice(NvmlDevice device, List<LinuxHwInfoSensor> sensors)
    {
        // Each field is checked on its own so a single NVML_ERROR_NOT_SUPPORTED does not drop the whole GPU.
        if (Nvml.DeviceGetTemperature(device.Handle, Nvml.NvmlTemperatureSensor.Gpu, out uint temperature) == Nvml.NvmlReturn.Success)
            sensors.Add(Sensor(device, "temp", "GPU Temp", LinuxHwInfoReadingType.Temperature, "°C", temperature));

        if (Nvml.DeviceGetUtilizationRates(device.Handle, out Nvml.NvmlUtilization utilization) == Nvml.NvmlReturn.Success)
        {
            sensors.Add(Sensor(device, "util.gpu", "GPU Usage", LinuxHwInfoReadingType.Usage, "%", utilization.Gpu, 100.0));
            sensors.Add(Sensor(device, "util.mem", "VRAM Usage", LinuxHwInfoReadingType.Usage, "%", utilization.Memory, 100.0));
        }

        if (TryGetMemory(device.Handle, out ulong memoryTotal, out ulong memoryUsed) && memoryTotal > 0)
        {
            double totalGb = memoryTotal / 1024.0 / 1024.0 / 1024.0;
            sensors.Add(Sensor(device, "mem.used", "VRAM Used", LinuxHwInfoReadingType.Data, "GB",
                memoryUsed / 1024.0 / 1024.0 / 1024.0, totalGb));
            sensors.Add(Sensor(device, "mem.total", "VRAM Total", LinuxHwInfoReadingType.Data, "GB", totalGb));
            sensors.Add(Sensor(device, "mem.percent", "VRAM Fill", LinuxHwInfoReadingType.Usage, "%",
                100.0 * memoryUsed / memoryTotal, 100.0));
        }

        if (Nvml.DeviceGetPowerUsage(device.Handle, out uint milliwatts) == Nvml.NvmlReturn.Success)
        {
            double? limit = Nvml.DeviceGetEnforcedPowerLimit(device.Handle, out uint limitMilliwatts) == Nvml.NvmlReturn.Success
                ? limitMilliwatts / 1000.0
                : null;
            sensors.Add(Sensor(device, "power", "GPU Power", LinuxHwInfoReadingType.Power, "W", milliwatts / 1000.0, limit));
        }

        if (Nvml.DeviceGetClockInfo(device.Handle, Nvml.NvmlClockType.Graphics, out uint graphicsMhz) == Nvml.NvmlReturn.Success)
            sensors.Add(Sensor(device, "clock.graphics", "GPU Clock", LinuxHwInfoReadingType.Clock, "MHz", graphicsMhz));

        if (Nvml.DeviceGetClockInfo(device.Handle, Nvml.NvmlClockType.Memory, out uint memoryMhz) == Nvml.NvmlReturn.Success)
            sensors.Add(Sensor(device, "clock.mem", "VRAM Clock", LinuxHwInfoReadingType.Clock, "MHz", memoryMhz));

        if (Nvml.DeviceGetFanSpeed(device.Handle, out uint fanPercent) == Nvml.NvmlReturn.Success)
            sensors.Add(Sensor(device, "fan", "GPU Fan", LinuxHwInfoReadingType.Usage, "%", fanPercent, 100.0));
    }

    /// <summary>
    /// Reads VRAM usage, preferring the v2 struct so the figure matches what <c>nvidia-smi</c> reports.
    /// Version 1 counts driver-reserved memory as used and is only the fallback for pre-R510 drivers.
    /// </summary>
    private static bool TryGetMemory(IntPtr handle, out ulong total, out ulong used)
    {
        total = 0;
        used = 0;

        try
        {
            Nvml.NvmlMemoryV2 memoryV2 = new() { Version = Nvml.MemoryV2Version };
            if (Nvml.DeviceGetMemoryInfoV2(handle, ref memoryV2) == Nvml.NvmlReturn.Success)
            {
                total = memoryV2.Total;
                used = memoryV2.Used;
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Driver predates the v2 entry point — fall through to v1.
        }

        if (Nvml.DeviceGetMemoryInfo(handle, out Nvml.NvmlMemory memory) != Nvml.NvmlReturn.Success)
            return false;

        total = memory.Total;
        used = memory.Used;
        return true;
    }

    private static LinuxHwInfoSensor Sensor(NvmlDevice device, string field, string label,
        LinuxHwInfoReadingType type, string unit, double value, double? max = null) => new(
        Id: SensorId.Nvml(device.Uuid, field),
        Source: SensorSourceKind.Nvml,
        Category: Categories.Gpu,
        Group: device.Name,
        Label: label,
        Type: type,
        Unit: unit,
        Value: value,
        Max: max);

    private readonly record struct NvmlDevice(IntPtr Handle, string Uuid, string Name);
}
