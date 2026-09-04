using System.ComponentModel.DataAnnotations;

namespace Industrial.Ingestion.Api.Configuration;

public class KafkaOptions : IValidatableObject
{
    public const string Section = "Kafka";
    public const int MaximumReadinessTimeoutSeconds = 30;

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    // Mutual TLS: the broker proves its identity via the dev CA (hostname
    // verification stays on), and this workload presents its own client certificate.
    // The broker requires a dev-CA-chained client cert before any API call.
    public bool UseTls { get; set; }

    public string SslCaLocation { get; set; } = string.Empty;

    public string SslCertificateLocation { get; set; } = string.Empty;

    public string SslKeyLocation { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string EventsTopic { get; set; } = string.Empty;

    [Range(1, MaximumReadinessTimeoutSeconds)]
    public int ReadinessTimeoutSeconds { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UseTls && string.IsNullOrWhiteSpace(SslCaLocation))
        {
            yield return new ValidationResult(
                "Kafka:SslCaLocation is required when Kafka TLS is enabled.",
                [nameof(SslCaLocation)]
            );
        }

        if (UseTls && string.IsNullOrWhiteSpace(SslCertificateLocation))
        {
            yield return new ValidationResult(
                "Kafka:SslCertificateLocation is required when Kafka TLS is enabled.",
                [nameof(SslCertificateLocation)]
            );
        }

        if (UseTls && string.IsNullOrWhiteSpace(SslKeyLocation))
        {
            yield return new ValidationResult(
                "Kafka:SslKeyLocation is required when Kafka TLS is enabled.",
                [nameof(SslKeyLocation)]
            );
        }
    }
}

public sealed class TransportSecurityOptions : IValidatableObject
{
    public const string Section = "TransportSecurity";

    public bool Enabled { get; set; }

    public string TrustedClientCaPath { get; set; } = string.Empty;

    public string AllowedClientFingerprintsPath { get; set; } = string.Empty;

    // How often ingestion re-reads the CA and allowlist files. Removing a gateway
    // fingerprint revokes it within one interval, without a restart.
    [Range(5, 3600)]
    public int AllowlistReloadIntervalSeconds { get; set; } = 30;

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
