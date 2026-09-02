using System.ComponentModel.DataAnnotations;

namespace Industrial.Ingestion.Api.Configuration;

public class KafkaOptions
{
    public const string Section = "Kafka";

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,249}$")]
    public string EventsTopic { get; set; } = string.Empty;
}
