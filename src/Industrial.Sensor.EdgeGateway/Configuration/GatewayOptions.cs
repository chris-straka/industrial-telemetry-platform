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
