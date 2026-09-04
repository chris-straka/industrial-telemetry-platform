using System.Diagnostics.Metrics;

using Industrial.Sensor.EdgeGateway.Features.Buffer;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// The gateway's OpenTelemetry instruments.
/// </summary>
/// <remarks>
/// A Counter only climbs and is read as a rate; a Gauge is a level that moves both ways
///
/// Depth and oldest-message-age are the gauges that make an outage visible
/// They climb during an outage and drain once there's no more outage
///
/// Queue depth is the one value this class does not own, because it also steers load shedding
/// I don't want application logic to depend on OTel implementation details
/// It lives in <see cref="BufferDepth"/>, so losing this file costs dashboards, never the ceiling
/// </remarks>
public sealed class EdgeMetrics : IDisposable
{
    public const string MeterName = "Industrial.Sensor.EdgeGateway";

    private readonly Meter _meter;
    private readonly Counter<long> _uploadFailures;
    private readonly Histogram<double> _uploadDuration;

    // NaN means nothing is buffered, so there is no age to report
    // A companion bool would need two volatile reads that can disagree, and this needs one
    private double _oldestAgeSeconds = double.NaN;
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
            description: "Readings acknowledged as duplicates because the MessageId was buffered, recently settled, or quarantined."
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

        // A 400 never reaches edge.telemetry.received, so without this a rejected reading looks like one the sensor never sent
        Malformed = _meter.CreateCounter<long>(
            "edge.telemetry.malformed",
            unit: "{reading}",
            description: "Readings rejected with 400 because an identity, sequence, timestamp, or measurement was invalid."
        );

        Rejected = _meter.CreateCounter<long>(
            "edge.telemetry.rejected",
            unit: "{reading}",
            description: "Readings dropped because the cloud named them as permanently refused."
        );

        // 403, deliberately not 400: the reading may be well-formed while the sender is
        // simply not its device. No alert rule watches this counter: it is driven by
        // unauthenticated input, and paging on attacker-controlled traffic is a self-DoS
        // primitive. Investigate spikes in the gateway logs instead.
        IdentityRejected = _meter.CreateCounter<long>(
            "edge.telemetry.identity_rejected",
            unit: "{reading}",
            description: "Readings refused because the sender presented no certificate or one not authorized for the claimed equipment ID."
        );

        // Tagged by outcome, because an unreachable cloud and one refusing this caller
        // are one climbing line otherwise, and they need different people to fix them
        _uploadFailures = _meter.CreateCounter<long>(
            "edge.upload.failures",
            unit: "{attempt}",
            description: "Upload attempts that ended without a full acknowledgement."
        );

        _uploadDuration = _meter.CreateHistogram<double>(
            "edge.upload.duration",
            unit: "ms",
            description: "Duration of one logical gRPC batch attempt, tagged by classified outcome."
        );

        // Observable means OTel calls this at collection time instead of us pushing values
        // The depth stays owned by BufferDepth, so losing this file costs dashboards, never the ceiling
        _meter.CreateObservableGauge(
            "edge.queue.depth",
            () => bufferDepth.Current,
            unit: "{reading}",
            description: "Readings sitting in the local buffer awaiting cloud acknowledgement."
        );

        _meter.CreateObservableGauge(
            "edge.oldest.message.age",
            ObserveOldestReadingAge,
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
    public Counter<long> Rejected { get; }
    public Counter<long> Shed { get; }
    public Counter<long> Malformed { get; }
    public Counter<long> IdentityRejected { get; }

    // A method rather than a public counter, so every sample carries the tag
    // An untagged Add from somewhere else would land in the same metric with no outcome at all
    public void RecordUploadFailure(string outcome) =>
        _uploadFailures.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordUploadDuration(double milliseconds, string outcome) =>
        _uploadDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("outcome", outcome)
        );

    // Volatile because the gauge callbacks read these on OTel's collection thread
    // A plain write can sit in a register the reader never sees, so the dashboard freezes on a stale value
    // Not Interlocked, which buys indivisible read-modify-write that neither of these does (docs/Concurrency.md)
    public void SetOldestReadingAge(double seconds) =>
        Volatile.Write(ref _oldestAgeSeconds, seconds);

    // An empty buffer has no oldest reading, which is not the same as one that is zero seconds old
    public void NoReadingsBuffered() => Volatile.Write(ref _oldestAgeSeconds, double.NaN);

    public void SetCloudReachable(bool reachable) =>
        Volatile.Write(ref _cloudReachable, reachable ? 1 : 0);

    // Yielding nothing leaves a gap in the series
    // Reporting a zero would draw the same flat line as a reading that arrived this instant
    private IEnumerable<Measurement<double>> ObserveOldestReadingAge()
    {
        var seconds = Volatile.Read(ref _oldestAgeSeconds);

        if (!double.IsNaN(seconds))
            yield return new Measurement<double>(seconds);
    }

    public void Dispose() => _meter.Dispose();
}
