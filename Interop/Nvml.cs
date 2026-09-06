using System.Runtime.InteropServices;
using System.Text;

namespace LoupixDeck.Plugin.LinuxHwInfo.Interop;

/// <summary>
/// P/Invoke surface for the NVIDIA Management Library shipped with the proprietary driver.
/// </summary>
/// <remarks>
/// The proprietary driver publishes no hwmon node, so NVML is the only way to read an NVIDIA GPU on Linux.
/// Classic <c>DllImport</c> rather than <c>LibraryImport</c>: several entry points write C strings into a
/// caller-owned buffer, which marshals trivially with <see cref="StringBuilder"/> and keeps the project free
/// of <c>AllowUnsafeBlocks</c>. Every call site must tolerate <see cref="DllNotFoundException"/> — the
/// library is simply absent on AMD- and Intel-only machines.
/// </remarks>
internal static class Nvml
{
    private const string Library = "libnvidia-ml.so.1";

    public const int DeviceNameBufferSize = 96;
    public const int DeviceUuidBufferSize = 96;

    public enum NvmlReturn
    {
        Success = 0,
        Uninitialized = 1,
        InvalidArgument = 2,
        NotSupported = 3,
        NoPermission = 4,
        NotFound = 6,
        InsufficientSize = 7,
        DriverNotLoaded = 9,
        Unknown = 999
    }

    public enum NvmlTemperatureSensor
    {
        Gpu = 0
    }

    public enum NvmlClockType
    {
        Graphics = 0,
        Sm = 1,
        Memory = 2,
        Video = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    /// <summary>Byte counts. The current NVML header declares these as <c>unsigned long long</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>
    /// Version 2 of the memory struct, which reports driver-reserved memory as its own field.
    /// </summary>
    /// <remarks>
    /// Version 1's <c>Used</c> silently folds the reserved block in, so it reads several hundred MiB higher
    /// than the figure <c>nvidia-smi</c> shows — the number a user will compare a button against. The leading
    /// <c>Version</c> field must be filled in by the caller with <see cref="MemoryV2Version"/> before the call.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemoryV2
    {
        public uint Version;
        public ulong Total;
        public ulong Reserved;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>NVML's versioned-struct token: the struct size in the low bits, the version in bits 24+.</summary>
    public static readonly uint MemoryV2Version = (uint)Marshal.SizeOf<NvmlMemoryV2>() | (2u << 24);

    [DllImport(Library, EntryPoint = "nvmlInit_v2")]
    public static extern NvmlReturn Init();

    [DllImport(Library, EntryPoint = "nvmlShutdown")]
    public static extern NvmlReturn Shutdown();

    [DllImport(Library, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static extern NvmlReturn DeviceGetCount(out uint deviceCount);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern NvmlReturn DeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetUUID", CharSet = CharSet.Ansi)]
    public static extern NvmlReturn DeviceGetUuid(IntPtr device, StringBuilder uuid, uint length);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    public static extern NvmlReturn DeviceGetName(IntPtr device, StringBuilder name, uint length);

    /// <summary>Degrees Celsius.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature")]
    public static extern NvmlReturn DeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temp);

    /// <summary>Both fields are percentages.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static extern NvmlReturn DeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static extern NvmlReturn DeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    /// <summary>Only present on driver R510 and newer; callers must fall back to the v1 entry point.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetMemoryInfo_v2")]
    public static extern NvmlReturn DeviceGetMemoryInfoV2(IntPtr device, ref NvmlMemoryV2 memory);

    /// <summary>Milliwatts.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static extern NvmlReturn DeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    /// <summary>Milliwatts.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    public static extern NvmlReturn DeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);

    /// <summary>Megahertz.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static extern NvmlReturn DeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clockMhz);

    /// <summary>Percent of maximum. NVML exposes no RPM reading for consumer cards.</summary>
    [DllImport(Library, EntryPoint = "nvmlDeviceGetFanSpeed")]
    public static extern NvmlReturn DeviceGetFanSpeed(IntPtr device, out uint speedPercent);
}
