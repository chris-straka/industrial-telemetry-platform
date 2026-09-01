using System.ComponentModel.DataAnnotations;

namespace Industrial.Ingestion.Api.Configuration;

public class KafkaOptions
{
    public const string Section = "Kafka";

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string EventsTopic { get; set; } = string.Empty;
}
