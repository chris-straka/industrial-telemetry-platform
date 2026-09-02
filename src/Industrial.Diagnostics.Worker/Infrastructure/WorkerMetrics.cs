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

        Consumed = _meter.CreateCounter<long>(
            "worker.telemetry.consumed",
            unit: "{message}",
            description: "Kafka records returned to the diagnostics consumer."
        );

        ProcessingFailures = _meter.CreateCounter<long>(
            "worker.telemetry.processing_failures",
            unit: "{attempt}",
            description: "Processing attempts that failed and were rewound for retry."
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

        AlertFailures = _meter.CreateCounter<long>(
            "worker.alert.failures",
            unit: "{attempt}",
            description: "Outbox publication attempts that failed; the alert remains pending."
        );

        AlertsPublished = _meter.CreateCounter<long>(
            "worker.alert.published",
            unit: "{alert}",
            description: "Outbox alerts acknowledged by Kafka and marked published in Postgres."
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
    public Counter<long> Consumed { get; }
    public Counter<long> ProcessingFailures { get; }
    public Counter<long> Discarded { get; }
    public Counter<long> Anomalies { get; }
    public Counter<long> AiFailures { get; }
    public Counter<long> AlertFailures { get; }
    public Counter<long> AlertsPublished { get; }
    public Histogram<double> Lag { get; }

    // A method rather than a public counter, so every sample carries the tag
    public void RecordDuplicate(string detectedBy) =>
        _duplicates.Add(1, new KeyValuePair<string, object?>("detected_by", detectedBy));

    public void Dispose() => _meter.Dispose();
}
