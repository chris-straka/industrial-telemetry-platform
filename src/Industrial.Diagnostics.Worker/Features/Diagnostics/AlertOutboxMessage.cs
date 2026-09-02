using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

/// <summary>
/// An anomaly alert waiting to be published to Kafka.
/// </summary>
/// <remarks>
/// The telemetry row and this row are committed by one Postgres transaction. Publishing happens
/// later and is at-least-once: a crash after Kafka acknowledges but before PublishedAt commits can
/// send a duplicate, so MessageId remains the consumer's idempotency key.
/// </remarks>
[Index(nameof(MessageId), IsUnique = true)]
[Index(nameof(PublishedAt), nameof(CreatedAt))]
public sealed class AlertOutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MessageId { get; set; }
    public string EquipmentId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? TraceParent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
