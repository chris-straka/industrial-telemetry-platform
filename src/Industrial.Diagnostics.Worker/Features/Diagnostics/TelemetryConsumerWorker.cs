using System.Text.Json;
using Confluent.Kafka;
using Google.GenAI;
using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;
using Industrial.Diagnostics.Worker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

/// <summary>
/// The envelope produced by BOTH ingestion doors (gRPC TelemetryService and the legacy
/// REST endpoint), and it must stay in sync with them: System.Text.Json leaves unmatched
/// properties at their defaults, so a field renamed upstream does not throw here, it
/// silently becomes null or zero forever.
/// </summary>
public record TelemetryDto(
    string MessageId,
    string EquipmentId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    double EngineTemperature,
    double OilPressure
);

public class TelemetryConsumerWorker(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<TelemetryConsumerWorker> logger,
    IOptions<KafkaOptions> kafkaOptions,
    IProducer<string, string> producer,
    ModelEngine modelEngine,
    Client client
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kafka = kafkaOptions.Value;

        // AutoOffsetReset only applies when the broker has no saved offset for this
        // GroupId: a new or changed group, or a gap longer than the retention window.
        //
        // Crashing after the Postgres write but before the offset commit replays the message
        // Preventing that would need a distributed transaction across Postgres and Kafka
        // At-least-once plus the unique index on MessageId gets effectively-once without one

        var config = new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = kafka.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest, // read/offset from the beginning
            MetadataMaxAgeMs = 5000,
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
        consumer.Subscribe(kafka.EventsTopic);

        logger.LogInformation(
            "Subscribed to Kafka topic {Topic} on {Servers}",
            kafka.EventsTopic,
            kafka.BootstrapServers
        );

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Consume is blocking, but respects the stoppingToken
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message?.Value == null)
                        continue;

                    var data = JsonSerializer.Deserialize<TelemetryDto>(consumeResult.Message.Value);
                    // TryParse covers missing AND malformed: both are ids we cannot
                    // dedupe on, so both take the same discard path.
                    if (data is null || !Guid.TryParse(data.MessageId, out var messageId))
                    {
                        // A message with no idempotency key cannot be deduplicated, so
                        // accepting it would quietly break the guarantee. Poison-pill
                        // handling: log and move on rather than crash the consumer.
                        // TODO (TODO.md): route these to a telemetry-errors DLQ topic.
                        logger.LogWarning(
                            "Discarding message with no usable MessageId at offset {Offset}",
                            consumeResult?.Offset
                        );
                        continue;
                    }

                    await ProcessAsync(data, messageId, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in consumer loop");
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
        finally
        {
            // Close() commits final offsets and leaves the consumer group cleanly.
            // Without it the broker waits out session.timeout.ms before rebalancing,
            // so every deploy costs you a stall.
            consumer.Close();
        }
    }

    private async Task ProcessAsync(
        TelemetryDto data,
        Guid messageId,
        CancellationToken stoppingToken
    )
    {
        using var db = await contextFactory.CreateDbContextAsync(stoppingToken);

        // Dedupe BEFORE inference, because TimeSeriesPredictionEngine is STATEFUL: every
        // Predict() updates the detector's window, so a replayed duplicate would change
        // the verdict for later, legitimate readings.
        //
        // This SELECT is only an optimisation. It rarely races because Kafka keys by
        // EquipmentId, so one MessageId lands on one partition and one consumer, but the
        // unique index in the catch below is the actual guarantee.
        if (await db.TelemetryReadings.AnyAsync(r => r.MessageId == messageId, stoppingToken))
        {
            logger.LogDebug("Duplicate MessageId {MessageId} skipped.", messageId);
            return;
        }

        var result = modelEngine.Inspect((float)data.EngineTemperature);

        logger.LogInformation(
            "ML Debug - ID: {Id}, Seq: {Seq}, Temp: {Temp:F1}, IsAnomaly: {IsAnomaly}, Score: {Score:F4}, P-Value: {PVal:F4}, Lag: {Lag:F1}s",
            data.EquipmentId,
            data.SequenceNumber,
            data.EngineTemperature,
            result.IsAnomaly,
            result.Score,
            result.PValue,
            (data.ReceivedAt - data.OccurredAt).TotalSeconds
        );

        var reading = new TelemetryReading
        {
            MessageId = messageId,
            EquipmentId = data.EquipmentId,
            SequenceNumber = data.SequenceNumber,
            OccurredAt = data.OccurredAt, // sensor clock -- chart against this
            ReceivedAt = data.ReceivedAt, // cloud clock
            EngineTemperature = data.EngineTemperature,
            OilPressure = data.OilPressure,
            IsAnomaly = result.IsAnomaly,
        };

        db.TelemetryReadings.Add(reading);

        try
        {
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // 23505 = unique_violation. The AnyAsync check above missed it, which means
            // something concurrent beat us to it. Not an error -- it is the constraint
            // doing precisely its job, and the reason correctness does not depend on
            // that earlier SELECT.
            logger.LogDebug("Duplicate MessageId {MessageId} rejected by index.", messageId);
            return;
        }

        if (result.IsAnomaly)
            await HandleAnomalyAsync(data, stoppingToken);
    }

    private async Task HandleAnomalyAsync(TelemetryDto data, CancellationToken ct)
    {
        logger.LogWarning("ANOMALY: {Id}. Requesting AI analysis...", data.EquipmentId);

        string aiAdvice;
        try
        {
            var prompt =
                $"Equipment {data.EquipmentId} anomaly. Temp: {data.EngineTemperature:F1}C. Provide 3 steps.";

            var res = await client.Models.GenerateContentAsync(
                model: "gemini-flash-lite-latest",
                contents: prompt
            );

            aiAdvice = res.Text ?? throw new Exception("Could not fetch from AI");
        }
        catch (Exception ex)
        {
            // A third-party API being slow or down must not stop telemetry processing.
            // The reading is already committed above; the advice is best-effort.
            logger.LogError(ex, "AI call failed");
            aiAdvice = "AI unavailable";
        }

        var alert = new Message<string, string>
        {
            Key = data.EquipmentId,
            Value = JsonSerializer.Serialize(
                new
                {
                    data.MessageId,
                    data.EquipmentId,
                    data.OccurredAt,
                    data.EngineTemperature,
                    Diagnostics = aiAdvice,
                }
            ),
        };
        await producer.ProduceAsync(kafkaOptions.Value.AlertsTopic, alert, ct);
    }
}
