using System.Diagnostics;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// Gateway's ActivitySource (.NET's version of OTel's Tracer)
/// </summary>
/// <remarks>
/// This is a named factory for creating Activities.
///
/// Listeners listen to it by name/str (activities aren't created if no listeners)
/// You can turn off OTel, making all activities null as well
/// That's why UploaderWorker calls it with ?.
///
/// I need this because there's no auto-instrumentation for what the uploader does
/// HttpClientInstrumentation() only sees the gRPC call, not the read-upload
/// I need to create a new trace for the batch send I make to the cloud
/// </remarks>
public static class EdgeTracing
{
    public const string SourceName = "Industrial.Sensor.EdgeGateway";

    public static readonly ActivitySource Source = new(SourceName);
}
