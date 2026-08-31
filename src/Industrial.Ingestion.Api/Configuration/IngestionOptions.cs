using System.ComponentModel.DataAnnotations;

namespace Industrial.Ingestion.Api.Configuration;

public class KafkaOptions
{
    public const string Section = "Kafka";

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    // A literal here leaves the producer behind when the topic changes in config, and nothing
    // throws: the broker invents the new topic and make verify reports missing readings
    [Required(AllowEmptyStrings = false)]
    public string EventsTopic { get; set; } = string.Empty;
}
