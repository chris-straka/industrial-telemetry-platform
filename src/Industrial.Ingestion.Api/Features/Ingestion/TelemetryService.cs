using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;
using Grpc.Core;
using Industrial.Ingestion.Api.Configuration;
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
    private readonly string _topic = kafkaOptions.Value.EventsTopic;

    public override async Task<TelemetryResponse> UploadTelemetry(
        UploadTelemetryRequest request,
        ServerCallContext context
    )
    {
        var accepted = new List<string>(request.Readings.Count);
        var rejected = new List<string>();
        var inflight = new List<(string MessageId, Task<DeliveryResult<string, string>> Delivery)>(
            request.Readings.Count
        );

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
                var payload = new
                {
                    reading.MessageId,
                    reading.EquipmentId,
                    reading.SequenceNumber,
                    OccurredAt = reading.OccurredAt.ToDateTimeOffset(),
                    ReceivedAt = DateTimeOffset.UtcNow,
                    reading.EngineTemperature,
                    reading.OilPressure,
                };

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
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Rejecting reading {MessageId} from {EquipmentId}: it cannot be produced.",
                    reading.MessageId,
                    reading.EquipmentId
                );
                rejected.Add(reading.MessageId);
            }
        }

        var undelivered = 0;
        var faulted = false;
        Exception? firstFailure = null;

        foreach (var (messageId, delivery) in inflight)
        {
            try
            {
                await delivery;
                accepted.Add(messageId);
            }
            catch (OperationCanceledException)
                when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                faulted = true;
                undelivered++;
                firstFailure ??= ex;
            }
        }

        // One line, because an outage fails all 200 and buries the log
        if (undelivered > 0)
        {
            logger.LogError(
                firstFailure,
                "{Undelivered} of {Inflight} deliveries failed. Gateway will re-send them.",
                undelivered,
                inflight.Count
            );
        }

        logger.LogInformation(
            "Batch: accepted {Accepted}, rejected {Rejected} of {Sent} (faulted: {Faulted}).",
            accepted.Count,
            rejected.Count,
            request.Readings.Count,
            faulted
        );

        // Success means we reached the end of the batch, not that we took any of it
        return new TelemetryResponse
        {
            Success = !faulted,
            AcceptedMessageIds = { accepted },
            RejectedMessageIds = { rejected },
        };
    }
}
