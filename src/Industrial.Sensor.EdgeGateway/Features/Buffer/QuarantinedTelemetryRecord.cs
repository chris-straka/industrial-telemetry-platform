namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// A reading the cloud declared permanently invalid, retained briefly for operator inspection.
/// </summary>
/// <remarks>
/// Quarantine is not another delivery queue: the cloud has explicitly said it will never accept
/// this MessageId. Keeping the original payload makes admission drift diagnosable without letting
/// one poison reading block newer telemetry. Rows are bounded by both age and count.
/// </remarks>
public sealed class QuarantinedTelemetryRecord
{
    public string MessageId { get; set; } = string.Empty;
    public long OriginalQueueId { get; set; }
    public string EquipmentId { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset BufferedAt { get; set; }
    public string? TraceParent { get; set; }
    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }

    public DateTimeOffset RejectedAt { get; set; }
    public string RejectionCode { get; set; } = string.Empty;
    public string RejectionReason { get; set; } = string.Empty;
}
