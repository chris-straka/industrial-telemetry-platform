using System.ComponentModel.DataAnnotations;

namespace Industrial.Sensor.Emulator.Configuration;

public class GatewayOptions
{
    public const string Section = "Gateway";

    // These annotations run at runtime (after environment vars are provided)
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Url { get; set; } = string.Empty;
}

public class EmulatorOptions
{
    public const string Section = "Emulator";

    [Range(1, 100_000)]
    public int DeviceCount { get; set; }

    [Range(1, 3_600)]
    public int IntervalSeconds { get; set; }

    // Number of TelemetryDto readings in the Channel before TryWrite starts dropping readings.
    // This is only a validation range, see appsettings.json / compose for the configured value
    [Range(1, 10_000_000)]
    public int BufferCapacity { get; set; }

    [Required]
    [Range(0, 10_000)]
    public int? ReplicaId { get; set; }
}
