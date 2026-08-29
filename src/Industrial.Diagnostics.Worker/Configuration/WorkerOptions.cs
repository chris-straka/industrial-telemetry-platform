using System.ComponentModel.DataAnnotations;

namespace Industrial.Diagnostics.Worker.Configuration;

public class KafkaOptions
{
    public const string Section = "Kafka";

    [Required(AllowEmptyStrings = false)]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string GroupId { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string TopicName { get; set; } = string.Empty;
}

public class GeminiOptions
{
    public const string Section = "Gemini";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = string.Empty;
}
