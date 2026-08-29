using System.Diagnostics;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// The gateway's ActivitySource, for the one span auto-instrumentation cannot draw.
/// </summary>
/// <remarks>
/// The AspNetCore and HttpClient instrumentation only cover a request in flight
/// An upload is a disk read, N stream writes and a delete, so nothing emits a span for it
///
/// Program.cs subscribes by name with AddSource
/// Without that StartActivity returns null, which is why UploaderWorker calls it with ?.
/// </remarks>
public static class EdgeTracing
{
    public const string SourceName = "Industrial.Sensor.EdgeGateway";

    public static readonly ActivitySource Source = new(SourceName);
}
