namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// One reading held on the gateway's local disk until the cloud settles it.
/// </summary>
/// <remarks>
/// The sensor stamps OccurredAt and this gateway stamps BufferedAt. Only OccurredAt is forwarded;
/// the cloud stamps its own ReceivedAt. Without event time, readings drained after an outage would
/// all appear to happen at once.
/// </remarks>
public class TelemetryRecord
{
    // EF maps a long Id to SQLite's autoincrementing INTEGER PRIMARY KEY rowid, so the oldest
    // buffered reading has the smallest Id. The uploader drains in Id order.
    public long Id { get; set; }

    // Idempotency key minted by the sensor. The unique index catches a retry while the row is
    // queued, and SettledMessage catches one that arrives after the row is removed.
    public string MessageId { get; set; } = string.Empty;

    public string EquipmentId { get; set; } = string.Empty;

    // Assigned by the sensor, monotonic per EquipmentId, and forwarded unchanged.
    public long SequenceNumber { get; set; }

    // Event time from the sensor's clock at acquisition.
    public DateTimeOffset OccurredAt { get; set; }

    // Gateway clock when the reading reached disk. BufferedAt - OccurredAt covers the emulator
    // channel, HTTP retries, the network, and the gateway fsync.
    public DateTimeOffset BufferedAt { get; set; }

    // W3C traceparent of the delivering request. The sensor's trace ends at the 202, so storing it
    // lets the uploader link its batch span back to that trace. Null when the request was not
    // sampled.
    public string? TraceParent { get; set; }

    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }
}
