using System.Diagnostics;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// The gateway's ActivitySource, the .NET equivalent of an OpenTelemetry tracer.
/// </summary>
/// <remarks>
/// StartActivity returns null when no listener subscribes to this source name, which is why
/// callers use ?. on the result.
///
/// HTTP client instrumentation sees only the gRPC call, not the read-and-upload cycle, so the
/// uploader starts its own batch trace from this source.
/// </remarks>
public static class EdgeTracing
{
    public const string SourceName = "Industrial.Sensor.EdgeGateway";

    public static readonly ActivitySource Source = new(SourceName);
}
