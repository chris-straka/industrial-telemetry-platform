using System.Text.Json;

using Industrial.Diagnostics.Worker.Features.Diagnostics;

namespace Industrial.Diagnostics.Tests;

public sealed class AlertOutboxPublisherWorkerTests
{
    [Fact]
    public void Own_pending_marker_with_identity_is_enriched()
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                MessageId = Guid.CreateVersion7().ToString("D"),
                EquipmentId = "EQ-1",
                OccurredAt = DateTimeOffset.UtcNow,
                EngineTemperature = 80.0,
                Diagnostics = string.Empty,
            }
        );

        Assert.True(
            AlertOutboxPublisherWorker.TryReadPendingEnrichment(payload, out var alert)
        );
        Assert.NotNull(alert);
        Assert.Equal("EQ-1", alert.EquipmentId);
    }

    [Fact]
    public void Already_enriched_payload_is_published_unchanged()
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                MessageId = Guid.CreateVersion7().ToString("D"),
                EquipmentId = "EQ-1",
                OccurredAt = DateTimeOffset.UtcNow,
                EngineTemperature = 80.0,
                Diagnostics = "AI unavailable",
            }
        );

        Assert.False(
            AlertOutboxPublisherWorker.TryReadPendingEnrichment(payload, out _)
        );
    }

    [Fact]
    public void Legacy_casing_payload_is_not_rewritten_to_null_identity()
    {
        // Lowercase keys do not bind to TelemetryAlertEnvelope under the default
        // case-sensitive serializer options. Enriching this row would republish it
        // with MessageId:null, destroying the hop-wide idempotency key.
        var payload =
            "{\"messageId\":\"77777777-7777-4777-8777-777777777777\","
            + "\"equipmentId\":\"E2E-AMBIG\","
            + "\"occurredAt\":\"2026-09-04T00:00:00Z\","
            + "\"engineTemperature\":80.0,"
            + "\"diagnostics\":\"\"}";

        Assert.False(
            AlertOutboxPublisherWorker.TryReadPendingEnrichment(payload, out _)
        );
    }

    [Fact]
    public void Malformed_payload_is_published_unchanged()
    {
        Assert.False(
            AlertOutboxPublisherWorker.TryReadPendingEnrichment(
                "{\"token\":\"not-an-envelope\"",
                out _
            )
        );
    }
}
