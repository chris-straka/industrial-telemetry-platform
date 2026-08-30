using System.Diagnostics.Metrics;
using Industrial.Sensor.EdgeGateway.Features.Buffer;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// The gateway's OpenTelemetry instruments.
/// </summary>
/// <remarks>
/// Names are dotted here and underscored in PromQL (edge.queue.depth -> edge_queue_depth)
/// A Counter only climbs and is read as a rate; a Gauge is a level that moves both ways
/// Depth and oldest-message-age are the gauges that make an outage visible: climb, then drain
/// Queue depth is the one value this class does not own, because it also steers load shedding
/// It lives in <see cref="BufferDepth"/>, so losing this file costs dashboards, never the ceiling
/// </remarks>
public sealed class EdgeMetrics : IDisposable
{
    public const string MeterName = "Industrial.Sensor.EdgeGateway";

    private readonly Meter _meter;
    private readonly Counter<long> _uploadFailures;
    private double _oldestAgeSeconds;
    private int _cloudReachable = 1;

    public EdgeMetrics(BufferDepth bufferDepth)
    {
        _meter = new Meter(MeterName);

        Received = _meter.CreateCounter<long>(
            "edge.telemetry.received",
            unit: "{reading}",
            description: "Readings durably written to the local buffer."
        );

        Duplicates = _meter.CreateCounter<long>(
            "edge.telemetry.duplicate",
            unit: "{reading}",
            description: "Readings rejected on receive because the MessageId was already buffered."
        );

        Uploaded = _meter.CreateCounter<long>(
            "edge.telemetry.uploaded",
            unit: "{reading}",
            description: "Readings the cloud durably acknowledged."
        );

        Shed = _meter.CreateCounter<long>(
            "edge.telemetry.shed",
            unit: "{reading}",
            description: "Readings rejected with 429 because the local buffer hit its ceiling."
        );

        // A rejection nobody counts is the silent failure this whole file exists to avoid
        Malformed = _meter.CreateCounter<long>(
            "edge.telemetry.malformed",
            unit: "{reading}",
            description: "Readings rejected with 400 for a missing MessageId or EquipmentId."
        );

        // A reading the cloud can never take, dropped so the queue can drain
        Poisoned = _meter.CreateCounter<long>(
            "edge.telemetry.poisoned",
            unit: "{reading}",
            description: "Readings dropped because the cloud can never accept them."
        );

        // Tagged by outcome, because an unreachable cloud and one refusing this caller
        // are one climbing line otherwise, and they need different people to fix them
        _uploadFailures = _meter.CreateCounter<long>(
            "edge.upload.failures",
            unit: "{attempt}",
            description: "Upload attempts that ended without a full acknowledgement."
        );

        // Observable means OTel calls this at collection time instead of us pushing values
        // That lets the depth stay owned by BufferDepth rather than mirrored into this class
        //
        // Every gauge here is a sample, so a spike between two collections is never seen
        // Counters carry what happened in between, gauges only report the instant they are read
        // See docs/Observability.md
        _meter.CreateObservableGauge(
            "edge.queue.depth",
            () => bufferDepth.Current,
            unit: "{reading}",
            description: "Readings sitting in the local buffer awaiting cloud acknowledgement."
        );

        _meter.CreateObservableGauge(
            "edge.oldest.message.age",
            () => Volatile.Read(ref _oldestAgeSeconds),
            unit: "s",
            description: "Age of the oldest unacknowledged reading. This is the real SLO."
        );

        // A gauge rather than a log line so it can be graphed beside the queue depth
        _meter.CreateObservableGauge(
            "edge.cloud.reachable",
            () => Volatile.Read(ref _cloudReachable),
            description: "1 when the last upload attempt reached the cloud, 0 otherwise."
        );
    }

    public Counter<long> Received { get; }
    public Counter<long> Duplicates { get; }
    public Counter<long> Uploaded { get; }
    public Counter<long> Poisoned { get; }
    public Counter<long> Shed { get; }
    public Counter<long> Malformed { get; }

    // A method rather than a public counter, so every sample carries the tag
    // An untagged Add from somewhere else would land in the same metric with no outcome at all
    public void RecordUploadFailure(string outcome) =>
        _uploadFailures.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void SetOldestAgeSeconds(double seconds) =>
        Volatile.Write(ref _oldestAgeSeconds, seconds);

    public void SetCloudReachable(bool reachable) =>
        Volatile.Write(ref _cloudReachable, reachable ? 1 : 0);

    public void Dispose() => _meter.Dispose();
}
