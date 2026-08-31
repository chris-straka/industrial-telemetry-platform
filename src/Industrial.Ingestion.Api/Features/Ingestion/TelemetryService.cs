using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;
using Grpc.Core;
using Industrial.Ingestion.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// Cloud-side terminus of the store-and-forward path. Accepts a batch of readings
/// from an Edge Gateway and hands each one to Kafka.
/// </summary>
public class TelemetryService(
    IProducer<string, string> kafkaProducer,
    IValidator<TelemetryReading> validator,
    IOptions<KafkaOptions> kafkaOptions,
    ILogger<TelemetryService> logger
) : TelemetryIngestion.TelemetryIngestionBase
{
    private readonly string _topic = kafkaOptions.Value.EventsTopic;

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

        // A broker fault stops the batch, because the produce after it fails the same way
        // Readings we never reach go unnamed, which is how the gateway is told to keep them
        var faulted = false;

        // Launched before any is awaited, so librdkafka fills one broker request with the batch
        // Awaiting each in turn holds its queue at one message, a round trip and a linger per read
        // Produce() with a delivery callback is faster still, but callback-shaped for no gain here
        var inflight = new List<(string MessageId, Task<DeliveryResult<string, string>> Delivery)>(
            request.Readings.Count
        );

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

            try
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

                inflight.Add(
                    (
                        reading.MessageId,
                        kafkaProducer.ProduceAsync(_topic, message, context.CancellationToken)
                    )
                );
            }
            catch (KafkaException ex)
            {
                // The broker or the local queue, not this reading (ProduceException derives from it)
                faulted = true;
                logger.LogError(
                    ex,
                    "Batch stopped at reading {MessageId}. Gateway will retry the remainder.",
                    reading.MessageId
                );
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deterministic for this reading, so it fails the same way on every resend
                // Refusing it is the only answer that does not stall the queue behind it
                logger.LogError(
                    ex,
                    "Rejecting reading {MessageId} from {EquipmentId}: it cannot be produced.",
                    reading.MessageId,
                    reading.EquipmentId
                );
                rejected.Add(reading.MessageId);
            }
        }

        try
        {
            await Task.WhenAll(inflight.Select(x => x.Delivery));
        }
        catch
        {
            // WhenAll rethrows one exception and never says which message it belonged to
            // The await is still what finishes the produces, only its throw is unusable
        }

        // Our own shutdown cancelled the deliveries rather than the broker failing them.
        context.CancellationToken.ThrowIfCancellationRequested();

        var undelivered = 0;
        Exception? firstFailure = null;

        foreach (var (messageId, delivery) in inflight)
        {
            // A task completes only once the broker acknowledged, so these are DURABLE writes
            // rather than enqueued ones, which is the whole basis of the gateway's delete
            if (delivery.IsCompletedSuccessfully)
            {
                accepted.Add(messageId);
                continue;
            }

            faulted = true;
            undelivered++;
            firstFailure ??= delivery.Exception?.GetBaseException();
        }

        // One line with a count, because an outage fails all 200 and buries the log
        if (undelivered > 0)
        {
            logger.LogError(
                firstFailure,
                "{Undelivered} of {Inflight} deliveries failed. Gateway will send them again.",
                undelivered,
                inflight.Count
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
