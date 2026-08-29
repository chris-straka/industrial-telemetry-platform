using System.ComponentModel.DataAnnotations;

namespace Industrial.Web.Api.Configuration;

public class KafkaOptions
{
    public const string Section = "Kafka";

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string GroupId { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string EventsTopic { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string AlertsTopic { get; set; } = string.Empty;
}

public class CorsOptions
{
    public const string Section = "CORS";

    [Required(AllowEmptyStrings = false)]
    public string AllowedOrigins { get; set; } = string.Empty;

    public string[] Origins =>
        AllowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
