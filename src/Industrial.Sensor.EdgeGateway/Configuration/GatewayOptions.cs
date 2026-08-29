using System.ComponentModel.DataAnnotations;

namespace Industrial.Sensor.EdgeGateway.Configuration;

public class CloudOptions
{
    public const string Section = "Cloud";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string ApiUrl { get; set; } = string.Empty;
}

public class BufferOptions
{
    public const string Section = "Buffer";

    [Required(AllowEmptyStrings = false)]
    public string Path { get; set; } = string.Empty;

    [Range(1, 100_000_000)]
    public int MaxDepth { get; set; }
}

public class UploaderOptions
{
    public const string Section = "Uploader";

    [Range(1, 10_000)]
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
