using System.ComponentModel.DataAnnotations;

namespace Industrial.Ingestion.Api.Configuration;

public class KafkaOptions
{
    public const string Section = "Kafka";
    public const int MaximumReadinessTimeoutSeconds = 30;

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string EventsTopic { get; set; } = string.Empty;

    [Range(1, MaximumReadinessTimeoutSeconds)]
    public int ReadinessTimeoutSeconds { get; set; }
}

public sealed class TransportSecurityOptions : IValidatableObject
{
    public const string Section = "TransportSecurity";

    public bool Enabled { get; set; }

    public string TrustedClientCaPath { get; set; } = string.Empty;

    public string AllowedClientFingerprintsPath { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
            yield break;

        if (string.IsNullOrWhiteSpace(TrustedClientCaPath))
        {
            yield return new ValidationResult(
                "TransportSecurity:TrustedClientCaPath is required when mTLS is enabled.",
                [nameof(TrustedClientCaPath)]
            );
        }

        if (string.IsNullOrWhiteSpace(AllowedClientFingerprintsPath))
        {
            yield return new ValidationResult(
                "TransportSecurity:AllowedClientFingerprintsPath is required when mTLS is enabled.",
                [nameof(AllowedClientFingerprintsPath)]
            );
        }
    }
}
