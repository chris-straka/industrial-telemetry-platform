using System.ComponentModel.DataAnnotations;

namespace Industrial.Diagnostics.Worker.Configuration;

public class KafkaOptions : IValidatableObject
{
    public const string Section = "Kafka";
    public const int MaximumReadinessTimeoutSeconds = 30;

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    // Enables mutual TLS. The broker is verified against the dev CA with hostname verification
    // on, and the broker requires this client's dev-CA-signed certificate before any API call.
    public bool UseTls { get; set; }

    public string SslCaLocation { get; set; } = string.Empty;

    public string SslCertificateLocation { get; set; } = string.Empty;

    public string SslKeyLocation { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [StringLength(200)]
    public string GroupId { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string EventsTopic { get; set; } = string.Empty;

    // Web.Api reads the same topic name from its own config. A hard-coded name here could leave
    // the dashboard subscribed to a topic nothing writes to, and nothing would fail loudly.
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string AlertsTopic { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string DeadLetterTopic { get; set; } = string.Empty;

    [Range(1, MaximumReadinessTimeoutSeconds)]
    public int ReadinessTimeoutSeconds { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var topics = new[] { EventsTopic, AlertsTopic, DeadLetterTopic };
        if (topics.Distinct(StringComparer.Ordinal).Count() != topics.Length)
        {
            yield return new ValidationResult(
                "Kafka event, alert, and dead-letter topics must be distinct.",
                [nameof(EventsTopic), nameof(AlertsTopic), nameof(DeadLetterTopic)]
            );
        }

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

public class GeminiOptions
{
    public const string Section = "Gemini";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Model { get; set; } = string.Empty;
}

public class ConsumerOptions
{
    public const string Section = "Consumer";

    [Range(1, 3_600)]
    public int BaseBackoffSeconds { get; set; }

    [Range(1, 3_600)]
    public int MaxBackoffSeconds { get; set; }
}

public class OutboxOptions
{
    public const string Section = "Outbox";

    [Range(10, 60_000)]
    public int IdleDelayMs { get; set; }

    [Range(1, 3_600)]
    public int BaseBackoffSeconds { get; set; }

    [Range(1, 3_600)]
    public int MaxBackoffSeconds { get; set; }

    // Only acknowledged rows expire. Pending rows remain until Kafka accepts them.
    [Range(1, 87_600)]
    public int PublishedRetentionHours { get; set; }

    [Range(5, 3_600)]
    public int MaintenanceIntervalSeconds { get; set; }
}
