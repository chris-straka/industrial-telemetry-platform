using System.ComponentModel.DataAnnotations;

namespace Industrial.Web.Api.Configuration;

public class KafkaOptions : IValidatableObject
{
    public const string Section = "Kafka";
    public const int MaximumReadinessTimeoutSeconds = 30;

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    // Server-only TLS (no client certificate): the broker proves its identity via the
    // dev CA. Hostname verification stays on, so the broker certificate needs a SAN
    // for every advertised listener the clients use.
    public bool UseTls { get; set; }

    public string SslCaLocation { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [StringLength(200)]
    public string GroupId { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string EventsTopic { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string AlertsTopic { get; set; } = string.Empty;

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
    }
}

public class CorsOptions
{
    public const string Section = "CORS";

    [Required(AllowEmptyStrings = false)]
    public string AllowedOrigins { get; set; } = string.Empty;

    public string[] Origins =>
        AllowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
