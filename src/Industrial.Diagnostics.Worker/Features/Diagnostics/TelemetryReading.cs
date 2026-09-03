using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

/// <summary>
/// Reading stored in the DB.
/// </summary>
[Index(nameof(MessageId), IsUnique = true)]
[Index(nameof(EquipmentId), nameof(OccurredAt))]
public class TelemetryReading
{
    // See the sensor emulator for why v7 is better for the index
    public Guid Id { get; set; } = Guid.CreateVersion7();

    // idempotency key (sensor -> gateway -> ingestion -> kafka -> worker)
    public Guid MessageId { get; set; }
    public string EquipmentId { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset PersistedAt { get; set; } = DateTimeOffset.UtcNow;
    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }
    public bool IsAnomaly { get; set; }

    // Nullable only for rows created before the audit migration. Every new decision records all
    // four values so it can be explained without reconstructing ephemeral process state.
    public double? DetectorScore { get; set; }
    public double? DetectorPValue { get; set; }
    public int? DetectorHistoryCount { get; set; }
    public string? DetectorVersion { get; set; }
}
