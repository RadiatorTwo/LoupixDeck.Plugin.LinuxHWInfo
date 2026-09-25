namespace LoupixDeck.Plugin.LinuxHwInfo;

/// <summary>
/// Physical quantity a reading represents. Drives formatting, gauge range and alert rule in
/// <see cref="Telemetry.SensorMetrics"/>. Values are never persisted — the stable identity of a
/// sensor is its <see cref="LinuxHwInfoSensor.Id"/> string.
/// </summary>
public enum LinuxHwInfoReadingType
{
    Other = 0,
    Temperature,
    Fan,
    Voltage,
    Current,
    Power,
    Clock,
    Usage,
    Data,
    Throughput
}
