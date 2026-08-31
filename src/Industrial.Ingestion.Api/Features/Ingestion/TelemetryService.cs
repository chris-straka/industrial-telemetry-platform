using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;
using Grpc.Core;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// Cloud-side terminus of the store-and-forward path. Accepts a batch of readings
/// from an Edge Gateway and hands each one to Kafka.
/// </summary>
public class TelemetryService(
    IProducer<string, string> kafkaProducer,
    IValidator<TelemetryReading> validator,
    ILogger<TelemetryService> logger
) : TelemetryIngestion.TelemetryIngestionBase
{
    public override async Task<TelemetryResponse> UploadTelemetry(
        UploadTelemetryRequest request,
        ServerCallContext context
    )
    {
        // Two lists rather than one count, because the batch has two independent ways of not
        // finishing and the gateway's response to each is opposite. A reading we durably took
        // is safe for the gateway to forget. A reading we refuse is one it must forget, since
        // resending it produces the same refusal forever. Everything named in neither list is
        // still the gateway's, and it will send it again.
        var accepted = new List<string>(request.Readings.Count);
        var rejected = new List<string>();

        // An infrastructure fault stops the batch where it stands. The readings after it are
        // untouched rather than skipped, so they keep their place in the gateway's queue and
        // stay in order behind the one that failed.
        var faulted = false;

        try
        {
            foreach (var reading in request.Readings)
            {
                var validation = validator.Validate(reading);

                if (!validation.IsValid)
                {
                    // Logged with the reasons, because this is the only side that knows them:
                    // the gateway is told which readings we refused, never why.
                    logger.LogWarning(
                        "Rejecting reading {MessageId} from {EquipmentId}: {Errors}.",
                        reading.MessageId,
                        reading.EquipmentId,
                        string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))
                    );

                    // An empty MessageId names nothing the gateway can match, so it would hold
                    // the reading forever. Its receiver answers 400 to a reading without one,
                    // which is what keeps such a row out of the buffer in the first place.
                    rejected.Add(reading.MessageId);
                    continue;
                }

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

                // ProduceAsync completes only once the broker acknowledges, so recording the
                // id after the await means this list holds DURABLE writes, not enqueued ones.
                // That is the whole basis of the gateway's delete.
                await kafkaProducer.ProduceAsync(
                    "telemetry-events",
                    message,
                    context.CancellationToken
                );
                accepted.Add(reading.MessageId);
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
                accepted.Count
            );
        }

        logger.LogInformation(
            "Accepted {Accepted} and rejected {Rejected} of {Sent} telemetry events from Edge Gateway via gRPC (faulted: {Faulted}).",
            accepted.Count,
            rejected.Count,
            request.Readings.Count,
            faulted
        );

        // success reports whether we got to the end of the batch, nothing more. A batch that
        // ran to completion and refused every reading in it still succeeded: the gateway
        // learned the fate of everything it sent, which is all this call promises.
        return new TelemetryResponse
        {
            Success = !faulted,
            AcceptedMessageIds = { accepted },
            RejectedMessageIds = { rejected },
        };
    }
}
