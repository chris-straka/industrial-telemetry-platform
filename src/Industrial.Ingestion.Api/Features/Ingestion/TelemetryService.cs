using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using FluentValidation;

using Grpc.Core;

using Industrial.Ingestion.Api.Configuration;
using Industrial.Shared;

using Microsoft.Extensions.Options;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// Accepts a batch of readings from an Edge Gateway and hands each one to Kafka.
/// </summary>
public class TelemetryService(
    IProducer<string, string> kafkaProducer,
    IValidator<TelemetryReading> validator,
    IOptions<KafkaOptions> kafkaOptions,
    ILogger<TelemetryService> logger
) : TelemetryIngestion.TelemetryIngestionBase
{
    private const int MaxBatchSize = 1_000;
    private readonly string _topic = kafkaOptions.Value.EventsTopic;

    public override async Task<TelemetryResponse> UploadTelemetry(
        UploadTelemetryRequest request,
        ServerCallContext context
    )
    {
        if (request.Readings.Count > MaxBatchSize)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"A batch cannot contain more than {MaxBatchSize} readings."
                )
            );
        }

        var accepted = new List<string>(request.Readings.Count);
        var rejected = new List<string>();
        var inflight = new List<(string MessageId, Task<DeliveryResult<string, string>> Delivery)>(
            request.Readings.Count
        );
        var undelivered = 0;
        Exception? firstFailure = null;

        foreach (var reading in request.Readings)
        {
            var validation = validator.Validate(reading);

            if (!validation.IsValid)
            {
                logger.LogWarning(
                    "Rejecting reading {MessageId} from {EquipmentId}: {Errors}.",
                    reading.MessageId,
                    reading.EquipmentId,
                    string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))
                );

                rejected.Add(reading.MessageId);
                continue;
            }

            try
            {
                var payload = new TelemetryEnvelope(
                    reading.MessageId,
                    reading.EquipmentId,
                    reading.SequenceNumber,
                    reading.OccurredAt.ToDateTimeOffset(),
                    DateTimeOffset.UtcNow,
                    reading.EngineTemperature,
                    reading.OilPressure
                );

                var kafkaMsg = new Message<string, string>
                {
                    Key = reading.EquipmentId,
                    Value = JsonSerializer.Serialize(payload),
                };

                // Without this there's no way to connect the sensor trace to the consumer's trace
                if (!string.IsNullOrEmpty(reading.Traceparent))
                {
                    kafkaMsg.Headers =
                    [
                        new Header("traceparent", Encoding.UTF8.GetBytes(reading.Traceparent)),
                    ];
                }

                // Produce now, await later: librdkafka batches msgs into one broker request.
                inflight.Add(
                    (
                        reading.MessageId,
                        kafkaProducer.ProduceAsync(_topic, kafkaMsg, context.CancellationToken)
                    )
                );
            }
            catch (OperationCanceledException)
                when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Validation failures are permanent and are the only readings named as rejected.
                // Producer failures are transient or internal: leaving this ID unnamed tells the
                // gateway to retain it and retry instead of deleting the only durable copy.
                logger.LogError(
                    ex,
                    "Could not queue reading {MessageId} from {EquipmentId}; gateway will retry it.",
                    reading.MessageId,
                    reading.EquipmentId
                );
                undelivered++;
                firstFailure ??= ex;
            }
        }

        foreach (var (messageId, delivery) in inflight)
        {
            try
            {
                var result = await delivery;
                if (result.Status == PersistenceStatus.Persisted)
                {
                    accepted.Add(messageId);
                }
                else
                {
                    // PossiblyPersisted is deliberately ambiguous. Naming it accepted would let
                    // the gateway delete its copy without a durable broker acknowledgement.
                    undelivered++;
                    firstFailure ??= new KafkaException(
                        new Error(
                            ErrorCode.Local_MsgTimedOut,
                            $"Kafka reported {result.Status} for {messageId}."
                        )
                    );
                }
            }
            catch (OperationCanceledException)
                when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                undelivered++;
                firstFailure ??= ex;
            }
        }

        // One line, because an outage fails all 200 and buries the log
        if (undelivered > 0)
        {
            logger.LogError(
                firstFailure,
                "{Undelivered} of {Sent} readings were not durably delivered. Gateway will re-send them.",
                undelivered,
                request.Readings.Count
            );
        }

        logger.LogInformation(
            "Batch: accepted {Accepted}, rejected {Rejected} of {Sent} (faulted: {Faulted}).",
            accepted.Count,
            rejected.Count,
            request.Readings.Count,
            undelivered > 0
        );

        // Success means we reached the end of the batch, not that we took any of it
        return new TelemetryResponse
        {
            Success = undelivered == 0,
            AcceptedMessageIds = { accepted },
            RejectedMessageIds = { rejected },
        };
    }
}
