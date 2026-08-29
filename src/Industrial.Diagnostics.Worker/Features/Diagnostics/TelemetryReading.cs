using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

/// <summary>
/// A reading as stored in Postgres. This is the system of record -- unlike the edge
/// buffer, nothing here is ever deleted after acknowledgement.
/// </summary>
[Index(nameof(MessageId), IsUnique = true)]
[Index(nameof(EquipmentId), nameof(OccurredAt))]
public class TelemetryReading
{
    // v7, not v4: a v7 is time-ordered, so inserts land on the index's rightmost
    // page instead of scattering random page splits across the whole B-tree.
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The sensor's idempotency key, carried unchanged: sensor -> gateway -> gRPC ->
    /// Kafka -> here. The unique index on this column is the single thing that makes
    /// "duplicates = 0" a fact rather than a hope.
    /// </summary>
    // Guid, not string: Postgres uuid is 16 bytes against text's 37, on the index
    // hit by every message. The wire stays a string (proto has no uuid type), so
    // the consumer parses at the boundary and rejects anything unparseable.
    public Guid MessageId { get; set; }

    public string EquipmentId { get; set; } = string.Empty;

    /// <summary>
    /// Monotonic per EquipmentId, assigned by the sensor. Lets you prove nothing was
    /// LOST as well as nothing duplicated:
    ///   SELECT "EquipmentId", MAX("SequenceNumber") + 1 - COUNT(*) AS missing
    ///   FROM "TelemetryReadings" GROUP BY "EquipmentId";
    /// A row of zeroes is the end-to-end result the whole demo is built to produce.
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>
    /// EVENT TIME -- when the sensor took the reading, on the sensor's clock.
    /// Chart against this, not ReceivedAt.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// PROCESSING TIME -- when the cloud accepted it. (ReceivedAt - OccurredAt) is
    /// end-to-end lag: flat in steady state, a mountain during an outage, flat again
    /// once the buffer drains.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When this row was written. Kept separate so replays stay honest.</summary>
    public DateTimeOffset PersistedAt { get; set; } = DateTimeOffset.UtcNow;

    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }
    public bool IsAnomaly { get; set; }
}
