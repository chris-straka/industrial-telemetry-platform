namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// One reading parked on the gateway's local disk until the cloud confirms it.
/// </summary>
/// <remarks>
/// Two clocks stamp every reading: OccurredAt from the sensor, BufferedAt from this gateway
/// Only OccurredAt travels on, and the cloud stamps its own ReceivedAt on arrival
/// Without event time, readings drained after an outage would all look simultaneous
/// </remarks>
public class TelemetryRecord
{
    // EF will map `Id` to SQLite's INTEGER PRIMARY KEY rowid (autoincrements)
    // Can't use GUID for rowid because it's not an int and can't autoincrement
    // The oldest buffered readings will hold the smallest Id (what we use to drain)
    public long Id { get; set; }

    // Sensor mints this idempotency key. The live-table unique index catches a retry while queued;
    // SettledMessage catches one that arrives after this row has been removed.
    public string MessageId { get; set; } = string.Empty;

    public string EquipmentId { get; set; } = string.Empty;

    // Sensor assigned, monotonic per EquipmentId, forwarded untouched to the cloud
    public long SequenceNumber { get; set; }

    // EVENT TIME -> sensor's clock at acquisition
    public DateTimeOffset OccurredAt { get; set; }

    // BUFFER TIME, gateway's clock when the reading hit disk
    // BufferedAt - OccuredAt = acquisition -> durability
    // time in emulator channel, Polly retries, network, gateway fsync
    public DateTimeOffset BufferedAt { get; set; }

    // The W3C traceparent of the delivering request
    // The sensor's own trace ends at the 202, storing its id outlives the request
    // The uploader links this as part of its batch span when draining
    // Nullable because a reading can arrive unsampled
    public string? TraceParent { get; set; }

    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }
}
