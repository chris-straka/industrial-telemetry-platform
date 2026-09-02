using System.Diagnostics;

namespace Industrial.Diagnostics.Worker.Infrastructure;

/// <summary>
/// The worker's ActivitySource (.NET's version of OTel's Tracer).
/// </summary>
/// <remarks>
/// I need this to grab the span from kafka so I can link to it
/// autoinstrumentation for kafka is still pre-release
/// </remarks>
public static class WorkerTracing
{
    public const string SourceName = "Industrial.Diagnostics.Worker";
    public static readonly ActivitySource Source = new(SourceName);
}
