using System.Diagnostics;

namespace Industrial.Diagnostics.Worker.Infrastructure;

/// <summary>
/// The worker's ActivitySource, the .NET equivalent of an OpenTelemetry tracer.
/// </summary>
/// <remarks>
/// Kafka auto-instrumentation is still pre-release, so the consumer starts its own spans and links
/// them to the trace context carried in each record.
/// </remarks>
public static class WorkerTracing
{
    public const string SourceName = "Industrial.Diagnostics.Worker";
    public static readonly ActivitySource Source = new(SourceName);
}
