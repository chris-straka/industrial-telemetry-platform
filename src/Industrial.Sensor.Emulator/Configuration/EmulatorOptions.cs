using System.ComponentModel.DataAnnotations;

namespace Industrial.Sensor.Emulator.Configuration;

public class GatewayOptions : IValidatableObject
{
    public const string Section = "Gateway";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Url { get; set; } = string.Empty;

    // Directory holding one device-EQ-N.pfx per simulated device plus the sensor CA.
    // Required when Url is https, because each device presents its own client certificate.
    public string ClientCertificateDirectory { get; set; } = string.Empty;

    public string TrustedSensorCaPath { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (
            !Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
            yield break;

        if (string.IsNullOrWhiteSpace(ClientCertificateDirectory))
        {
            yield return new ValidationResult(
                "Gateway:ClientCertificateDirectory is required for an https gateway URL.",
                [nameof(ClientCertificateDirectory)]
            );
        }

        if (string.IsNullOrWhiteSpace(TrustedSensorCaPath))
        {
            yield return new ValidationResult(
                "Gateway:TrustedSensorCaPath is required for an https gateway URL.",
                [nameof(TrustedSensorCaPath)]
            );
        }
    }
}

public class EmulatorOptions
{
    public const string Section = "Emulator";

    [Range(1, 100_000)]
    public int DeviceCount { get; set; }

    [Range(1, 3_600)]
    public int IntervalSeconds { get; set; }

    // Channel capacity before TryWrite starts dropping readings. The range only validates; the
    // configured value lives in appsettings.json and Compose.
    [Range(1, 10_000_000)]
    public int BufferCapacity { get; set; }

    [Required]
    [Range(0, 10_000)]
    public int? ReplicaId { get; set; }
}
