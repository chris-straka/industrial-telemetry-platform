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

    // Prefer an injected secret in real deployments. The Compose-only development certificate
    // deliberately uses an empty password because its private key lives in an isolated volume.
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

    // A retry after this window is treated as a new submission. The sensor's HTTP retry policy
    // is measured in seconds; retaining markers for hours leaves ample ambiguity coverage while
    // keeping this transient edge database bounded.
    [Range(1, 8_760)]
    public int SettledIdRetentionHours { get; set; }

    // Rejected payloads are forensic evidence, not an unbounded second queue.
    [Range(1, 8_760)]
    public int QuarantineRetentionHours { get; set; }

    [Range(1, 1_000_000)]
    public int QuarantineMaxRows { get; set; }
}

/// <summary>
/// Sensor-to-edge mutual TLS. Each device carries its own client certificate whose subject
/// name is its equipment ID, so one compromised device cannot impersonate another shard.
/// Disabled by default; the Compose stack enables it once the emulator presents certs.
/// </summary>
public sealed class SensorSecurityOptions : IValidatableObject
{
    public const string Section = "SensorSecurity";

    public bool Enabled { get; set; }

    // Plaintext HTTP listener for health and buffer inspection. Bound explicitly (rather
    // than the image default) because any code-configured Kestrel endpoint suppresses
    // default URL binding; keep this aligned with the published port and healthchecks.
    [Range(1, 65535)]
    public int OpsListenPort { get; set; } = 8080;

    // Dedicated HTTPS listener for sensor traffic. Health and buffer inspection stay on
    // plaintext HTTP: they never accept readings and the compose healthchecks use them.
    [Range(1, 65535)]
    public int ListenPort { get; set; } = 8443;

    public string ServerCertificatePath { get; set; } = string.Empty;

    // Same empty-password development convention as the cloud hop: the private key lives
    // in an isolated volume, never in configuration.
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

    // Ceiling on one upload stream, so a connection that dies without an RST cannot park the uploader forever.
    // A TCP keepalive would notice that eventually, but it is an OS-wide knob and it cannot see a server that took the bytes and then stopped answering.
    [Range(1, 600)]
    public int UploadTimeoutSeconds { get; set; }
}
