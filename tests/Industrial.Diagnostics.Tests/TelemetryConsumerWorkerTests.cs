using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Industrial.Shared;

namespace Industrial.Diagnostics.Tests;

public sealed class TelemetryConsumerWorkerTests
{
    [Fact]
    public void Kafka_timestamp_offsets_are_normalized_before_Postgres_persistence()
    {
        var occurredAt = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(-6));
        var receivedAt = new DateTimeOffset(2026, 9, 3, 18, 0, 0, TimeSpan.FromHours(2));
        var envelope = new TelemetryEnvelope(
            Guid.CreateVersion7().ToString("D"),
            "EQ-offset",
            1,
            occurredAt,
            receivedAt,
            80,
            40
        );

        var normalized = TelemetryConsumerWorker.NormalizeTimestamps(envelope);

        Assert.Equal(TimeSpan.Zero, normalized.OccurredAt.Offset);
        Assert.Equal(TimeSpan.Zero, normalized.ReceivedAt.Offset);
        Assert.Equal(occurredAt.UtcDateTime, normalized.OccurredAt.UtcDateTime);
        Assert.Equal(receivedAt.UtcDateTime, normalized.ReceivedAt.UtcDateTime);
    }
}
