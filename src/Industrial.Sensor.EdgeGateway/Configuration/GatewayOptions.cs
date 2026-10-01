using System.ComponentModel.DataAnnotations;

namespace Industrial.Sensor.EdgeGateway.Configuration;

public class CloudOptions
{
    public const string Section = "Cloud";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string ApiUrl { get; set; } = string.Empty;
}

public sealed class TransportSecurityOptions : IValidatableObject
{
    public const string Section = "TransportSecurity";

    public bool Enabled { get; set; }

    public string ClientCertificatePath { get; set; } = string.Empty;

    // Real deployments should inject this as a secret. The Compose development certificate uses
    // an empty password because its private key lives in an isolated volume.
    public string? ClientCertificatePassword { get; set; }

    public string TrustedServerCaPath { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
            yield break;

        if (string.IsNullOrWhiteSpace(ClientCertificatePath))
        {
            yield return new ValidationResult(
                "TransportSecurity:ClientCertificatePath is required when mTLS is enabled.",
                [nameof(ClientCertificatePath)]
            );
        }

        if (string.IsNullOrWhiteSpace(TrustedServerCaPath))
        {
            yield return new ValidationResult(
                "TransportSecurity:TrustedServerCaPath is required when mTLS is enabled.",
                [nameof(TrustedServerCaPath)]
            );
        }
    }
}

public class BufferOptions
{
    public const string Section = "Buffer";

    [Required(AllowEmptyStrings = false)]
    public string Path { get; set; } = string.Empty;

    [Range(1, 100_000_000)]
    public int MaxDepth { get; set; }

    // A retry after this window is treated as a new submission. Sensor retries span seconds, so
    // a window of hours covers them while keeping the edge database bounded.
    [Range(1, 8_760)]
    public int SettledIdRetentionHours { get; set; }

    // Rejected payloads are kept for inspection only, so both age and row count are bounded.
    [Range(1, 8_760)]
    public int QuarantineRetentionHours { get; set; }

    [Range(1, 1_000_000)]
    public int QuarantineMaxRows { get; set; }
}

/// <summary>
/// Sensor-to-edge mutual TLS settings.
/// </summary>
/// <remarks>
/// Each device has its own client certificate whose subject name is its equipment ID, so one
/// compromised device cannot impersonate another. Disabled by default; the Compose stack enables
/// it.
/// </remarks>
public sealed class SensorSecurityOptions : IValidatableObject
{
    public const string Section = "SensorSecurity";

    public bool Enabled { get; set; }

    // Plaintext HTTP listener for health and buffer inspection. Any Kestrel endpoint configured
    // in code suppresses the default URL binding, so this port is bound explicitly. Keep it
    // aligned with the published port and healthchecks.
    [Range(1, 65535)]
    public int OpsListenPort { get; set; } = 8080;

    // HTTPS listener for sensor traffic. Health and buffer inspection stay on plaintext HTTP
    // because they accept no readings and the Compose healthchecks call them.
    [Range(1, 65535)]
    public int ListenPort { get; set; } = 8443;

    public string ServerCertificatePath { get; set; } = string.Empty;

    // Same empty-password development convention as the cloud hop. The private key lives in an
    // isolated volume rather than in configuration.
    public string? ServerCertificatePassword { get; set; }

    public string TrustedSensorCaPath { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
            yield break;

        if (string.IsNullOrWhiteSpace(ServerCertificatePath))
        {
            yield return new ValidationResult(
                "SensorSecurity:ServerCertificatePath is required when sensor mTLS is enabled.",
                [nameof(ServerCertificatePath)]
            );
        }

        if (string.IsNullOrWhiteSpace(TrustedSensorCaPath))
        {
            yield return new ValidationResult(
                "SensorSecurity:TrustedSensorCaPath is required when sensor mTLS is enabled.",
                [nameof(TrustedSensorCaPath)]
            );
        }
    }
}

public class UploaderOptions
{
    public const string Section = "Uploader";

    // Field bounds make 1,000 readings comfortably smaller than gRPC's default 4 MiB limit.
    [Range(1, 1_000)]
    public int BatchSize { get; set; }

    [Range(1, 60_000)]
    public int IdleDelayMs { get; set; }

    [Range(1, 3_600)]
    public int BaseBackoffSeconds { get; set; }

    [Range(1, 3_600)]
    public int MaxBackoffSeconds { get; set; }

    // Caps one upload call so a connection that dies without an RST cannot stall the uploader.
    // TCP keepalive is an OS-wide setting and cannot detect a server that accepted the bytes and
    // then stopped answering.
    [Range(1, 600)]
    public int UploadTimeoutSeconds { get; set; }
}
