using System.Text.Json;
using Confluent.Kafka;
using Grpc.Core;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// Cloud-side terminus of the store-and-forward path. Accepts a batch of readings
/// from an Edge Gateway and hands each one to Kafka.
/// </summary>
public class TelemetryService(
    IProducer<string, string> kafkaProducer,
    ILogger<TelemetryService> logger
) : TelemetryIngestion.TelemetryIngestionBase
{
    public override async Task<TelemetryResponse> UploadTelemetry(
        UploadTelemetryRequest request,
        ServerCallContext context
    )
    {
        // Counted from the START of the batch. The gateway deletes exactly this many
        // records off the front of the batch it sent, so we MUST stop at the first
        // failure rather than skipping it and continuing -- otherwise "the first N
        // succeeded" stops being true and the gateway drops a reading it never
        // delivered. Fail fast, keep the invariant, let the retry sort it out.
        int accepted = 0;
        bool faulted = false;

        try
        {
            foreach (var reading in request.Readings)
            {
                var payload = new
                {
                    reading.MessageId, // idempotency key -- the consumer dedupes on this
                    reading.EquipmentId,
                    reading.SequenceNumber, // lets the consumer detect gaps, i.e. loss
                    // EVENT TIME: the sensor's clock. Preserved end to end so that a
                    // batch drained after a 30 minute outage still reports when each
                    // reading actually happened, rather than when we got around to it.
                    OccurredAt = reading.OccurredAt.ToDateTimeOffset(),
                    // PROCESSING TIME: our clock. Carrying both is what makes the lag
                    // during an outage measurable instead of invisible.
                    ReceivedAt = DateTimeOffset.UtcNow,
                    reading.EngineTemperature,
                    reading.OilPressure,
                };

                var message = new Message<string, string>
                {
                    // Keying by EquipmentId puts all readings for one machine on one
                    // partition, which is what preserves their relative order. Keying
                    // by MessageId would spread them across partitions and lose it.
                    Key = reading.EquipmentId,
                    Value = JsonSerializer.Serialize(payload),
                };

                // ProduceAsync completes only once the broker acknowledges, so
                // incrementing after the await means `accepted` counts DURABLE writes,
                // not enqueued ones. That is the whole basis of the gateway's delete.
                await kafkaProducer.ProduceAsync(
                    "telemetry-events",
                    message,
                    context.CancellationToken
                );
                accepted++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Partial acceptance. Report what we durably took and let the gateway
            // retry the rest; it still holds every record we did not confirm.
            faulted = true;
            logger.LogError(
                ex,
                "Batch faulted after {Accepted} accepted readings. Gateway will retry the remainder.",
                accepted
            );
        }

        logger.LogInformation(
            "Accepted {Count} telemetry events from Edge Gateway via gRPC (faulted: {Faulted}).",
            accepted,
            faulted
        );

        // success=true with a short count is not a contradiction: it means "this many
        // are safely mine, the rest are still yours".
        return new TelemetryResponse { Success = accepted > 0, AcceptedCount = accepted };
    }
}
