using System.Diagnostics.Metrics;

namespace Industrial.Diagnostics.Worker.Infrastructure;

/// <summary>
/// The worker's Otel's instruments.
/// </summary>
public sealed class WorkerMetrics : IDisposable
{
    public const string MeterName = "Industrial.Diagnostics.Worker";

    private readonly Meter _meter;
    private readonly Counter<long> _duplicates;

    public WorkerMetrics()
    {
        _meter = new Meter(MeterName);

        Persisted = _meter.CreateCounter<long>(
            "worker.telemetry.persisted",
            unit: "{reading}",
            description: "Readings durably written to Postgres."
        );

        Discarded = _meter.CreateCounter<long>(
            "worker.telemetry.discarded",
            unit: "{message}",
            description: "Kafka messages dropped because they carried no usable MessageId."
        );

        Anomalies = _meter.CreateCounter<long>(
            "worker.anomaly.detected",
            unit: "{reading}",
            description: "Readings the model flagged as anomalous."
        );

        AiFailures = _meter.CreateCounter<long>(
            "worker.ai.failures",
            unit: "{call}",
            description: "Gemini calls that failed or timed out, leaving the alert without advice."
        );

        // A failed alert is not retried: the reading is already persisted, so a replay
        // dedupes before reaching the alert. This counter is the only trace of the loss.
        AlertFailures = _meter.CreateCounter<long>(
            "worker.alert.failures",
            unit: "{alert}",
            description: "Anomaly alerts that could not be produced to the alerts topic."
        );

        _duplicates = _meter.CreateCounter<long>(
            "worker.telemetry.duplicate",
            unit: "{reading}",
            description: "Readings skipped because the MessageId was already persisted."
        );

        // Distribution of per-reading sensor to DB times (bucketed)
        Lag = _meter.CreateHistogram<double>(
            "worker.telemetry.lag",
            unit: "s",
            description: "Seconds between the sensor taking a reading and this worker persisting it."
        );
    }

    public Counter<long> Persisted { get; }
    public Counter<long> Discarded { get; }
    public Counter<long> Anomalies { get; }
    public Counter<long> AiFailures { get; }
    public Counter<long> AlertFailures { get; }
    public Histogram<double> Lag { get; }

    // A method rather than a public counter, so every sample carries the tag
    public void RecordDuplicate(string detectedBy) =>
        _duplicates.Add(1, new KeyValuePair<string, object?>("detected_by", detectedBy));

    public void Dispose() => _meter.Dispose();
}
