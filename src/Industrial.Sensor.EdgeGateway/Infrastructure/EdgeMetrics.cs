using System.Diagnostics.Metrics;

using Industrial.Sensor.EdgeGateway.Features.Buffer;

namespace Industrial.Sensor.EdgeGateway.Infrastructure;

/// <summary>
/// The gateway's OpenTelemetry instruments.
/// </summary>
/// <remarks>
/// Queue depth and oldest-message age are the gauges that show an outage. Both climb while the
/// cloud is unreachable and fall as the buffer drains.
///
/// Queue depth also drives load shedding, so it lives in <see cref="BufferDepth"/> rather than
/// here. Admission logic does not depend on the telemetry pipeline.
/// </remarks>
public sealed class EdgeMetrics : IDisposable
{
    public const string MeterName = "Industrial.Sensor.EdgeGateway";

    private readonly Meter _meter;
    private readonly Counter<long> _uploadFailures;
    private readonly Histogram<double> _uploadDuration;

    // NaN means nothing is buffered. Encoding that in the value keeps it to one volatile read;
    // a separate flag could disagree with the age.
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

        // A 400 never reaches edge.telemetry.received. Without this counter, a rejected reading
        // would look like one the sensor never sent.
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

        // These are 403s rather than 400s, because the reading may be well-formed while the
        // sender is not its device. No alert watches this counter. Unauthenticated traffic drives
        // it, so paging on it would let an attacker page the on-call. Check the logs instead.
        IdentityRejected = _meter.CreateCounter<long>(
            "edge.telemetry.identity_rejected",
            unit: "{reading}",
            description: "Readings refused because the sender presented no certificate or one not authorized for the claimed equipment ID."
        );

        // Tagged by outcome because an unreachable cloud and a cloud refusing this caller need
        // different fixes.
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

        // Observable gauges are sampled at collection time. BufferDepth owns the value.
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

        // A gauge rather than a log line so it can be graphed beside the queue depth.
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

    // Exposed as a method rather than a counter so every sample carries an outcome tag.
    public void RecordUploadFailure(string outcome) =>
        _uploadFailures.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordUploadDuration(double milliseconds, string outcome) =>
        _uploadDuration.Record(
            milliseconds,
            new KeyValuePair<string, object?>("outcome", outcome)
        );

    // Volatile because the gauge callbacks read these fields on OTel's collection thread. A plain
    // write might not become visible to that thread. Interlocked is unnecessary because these are
    // plain stores, not read-modify-write operations.
    public void SetOldestReadingAge(double seconds) =>
        Volatile.Write(ref _oldestAgeSeconds, seconds);

    // An empty buffer has no oldest reading, which differs from one that is zero seconds old.
    public void NoReadingsBuffered() => Volatile.Write(ref _oldestAgeSeconds, double.NaN);

    public void SetCloudReachable(bool reachable) =>
        Volatile.Write(ref _cloudReachable, reachable ? 1 : 0);

    // Yielding nothing leaves a gap in the series. A zero would look like a reading that just
    // arrived.
    private IEnumerable<Measurement<double>> ObserveOldestReadingAge()
    {
        var seconds = Volatile.Read(ref _oldestAgeSeconds);

        if (!double.IsNaN(seconds))
            yield return new Measurement<double>(seconds);
    }

    public void Dispose() => _meter.Dispose();
}
