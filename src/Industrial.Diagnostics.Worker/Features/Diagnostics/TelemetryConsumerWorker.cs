using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;
using Industrial.Diagnostics.Worker.Infrastructure;
using Industrial.Diagnostics.Worker.Infrastructure.Data;
using Industrial.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

public class TelemetryConsumerWorker(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<TelemetryConsumerWorker> logger,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<ConsumerOptions> consumerOptions,
    IDeadLetterPublisher deadLetterPublisher,
    DiagnosticsConsumerReadiness readiness,
    ModelEngine modelEngine,
    WorkerMetrics metrics
) : BackgroundService
{
    private static readonly JsonSerializerOptions TelemetryJsonOptions = new()
    {
        // Positional-record constructor parameters are the Kafka schema. Treat an absent numeric
        // field as malformed instead of silently accepting its CLR default of zero.
        RespectRequiredConstructorParameters = true,
    };

    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kafkaConfig = kafkaOptions.Value;

        var config = new ConsumerConfig
        {
            BootstrapServers = kafkaConfig.BootstrapServers,
            SecurityProtocol = kafkaConfig.UseTls ? SecurityProtocol.Ssl : SecurityProtocol.Plaintext,
            SslCaLocation = kafkaConfig.UseTls ? kafkaConfig.SslCaLocation : null,
            GroupId = kafkaConfig.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MetadataMaxAgeMs = 5000,
            AllowAutoCreateTopics = false,
            // A record becomes durable in Kafka's consumer-group state only after its Postgres
            // transaction succeeds (or after Kafka persists a poison copy in the DLQ).
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
                readiness.PartitionsAssigned(partitions.Count)
            )
            .SetPartitionsRevokedHandler((_, _) => readiness.PartitionsRevoked())
            .SetPartitionsLostHandler((_, _) => readiness.PartitionsRevoked())
            .Build();
        consumer.Subscribe(kafkaConfig.EventsTopic);

        logger.LogInformation(
            "Subscribed to Kafka topic {Topic} on {Servers}",
            kafkaConfig.EventsTopic,
            kafkaConfig.BootstrapServers
        );

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Activity? activity = null;
                ConsumeResult<string, string>? consumeResult = null;

                try
                {
                    consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult is null)
                        continue;

                    metrics.Consumed.Add(1);

                    if (consumeResult.Message?.Value is null)
                    {
                        const string reason = "Kafka record is a tombstone";
                        await deadLetterPublisher.PublishAsync(
                            consumeResult,
                            reason,
                            stoppingToken
                        );
                        logger.LogWarning(
                            "Quarantined Kafka tombstone at {TopicPartitionOffset}.",
                            consumeResult.TopicPartitionOffset
                        );
                        consumer.Commit(consumeResult);
                        _consecutiveFailures = 0;
                        continue;
                    }

                    TelemetryEnvelope? data;

                    try
                    {
                        data = JsonSerializer.Deserialize<TelemetryEnvelope>(
                            consumeResult.Message.Value,
                            TelemetryJsonOptions
                        );
                    }
                    catch (JsonException)
                    {
                        data = null;
                    }

                    if (!TryValidate(data, out var messageId, out var validationError))
                    {
                        await deadLetterPublisher.PublishAsync(
                            consumeResult,
                            validationError,
                            stoppingToken
                        );
                        logger.LogWarning(
                            "Quarantined poison message at {TopicPartitionOffset}: {Reason}",
                            consumeResult.TopicPartitionOffset,
                            validationError
                        );
                        consumer.Commit(consumeResult);
                        _consecutiveFailures = 0;
                        continue;
                    }

                    // JSON permits an equivalent instant with any numeric UTC offset, but Npgsql
                    // accepts DateTimeOffset for timestamptz only when its offset is zero.
                    data = NormalizeTimestamps(data!);

                    activity = WorkerTracing.Source.StartActivity(
                        "telemetry.process",
                        ActivityKind.Consumer,
                        ReadTraceContext(consumeResult.Message.Headers),
                        tags:
                        [
                            new("messaging.system", "kafka"),
                            new("messaging.destination.name", consumeResult.Topic),
                            new("messaging.kafka.offset", consumeResult.Offset.Value),
                            new("messaging.kafka.partition", consumeResult.Partition.Value),
                            new("equipment.id", data.EquipmentId),
                        ]
                    );

                    await HandleReadingAsync(data, messageId, stoppingToken);
                    consumer.Commit(consumeResult);
                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    metrics.ProcessingFailures.Add(1);

                    if (consumeResult is not null)
                    {
                        try
                        {
                            // Consume advances the local fetch position before processing. Rewind
                            // it explicitly so a later success can never commit past this failure.
                            consumer.Seek(consumeResult.TopicPartitionOffset);
                        }
                        catch (Exception seekException)
                        {
                            throw new InvalidOperationException(
                                $"Could not rewind failed Kafka record {consumeResult.TopicPartitionOffset}; stopping prevents an offset skip.",
                                new AggregateException(ex, seekException)
                            );
                        }
                    }

                    logger.LogError(
                        ex,
                        consumeResult is null
                            ? "Kafka consume loop failed before returning a record."
                            : "Error processing Kafka record; it was rewound for retry."
                    );
                    await BackoffAsync(stoppingToken);
                }
                finally
                {
                    activity?.Dispose();
                }
            }
        }
        finally
        {
            readiness.PartitionsRevoked();
            consumer.Close();
        }
    }

    // Jittered, because every replica fails on the same Postgres at the same instant and a
    // fixed delay makes them retry in lockstep.
    private async Task BackoffAsync(CancellationToken cancellationToken)
    {
        _consecutiveFailures++;

        var consumerConfig = consumerOptions.Value;
        var baseBackoff = TimeSpan.FromSeconds(consumerConfig.BaseBackoffSeconds);
        var maxBackoff = TimeSpan.FromSeconds(consumerConfig.MaxBackoffSeconds);

        var exponential = baseBackoff * Math.Pow(2, Math.Min(_consecutiveFailures - 1, 10));
        var capped = exponential > maxBackoff ? maxBackoff : exponential;
        var jittered = TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * capped.TotalMilliseconds
        );

        await SafeDelayAsync(jittered, cancellationToken);
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static ActivityContext ReadTraceContext(Headers? headers)
    {
        if (headers is null || !headers.TryGetLastBytes("traceparent", out var raw) || raw is null)
            return default;

        return ActivityContext.TryParse(
            Encoding.UTF8.GetString(raw),
            null,
            isRemote: true,
            out var ctx
        )
            ? ctx
            : default;
    }

    private async Task HandleReadingAsync(
        TelemetryEnvelope data,
        Guid messageId,
        CancellationToken stoppingToken
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(stoppingToken);

        if (await db.TelemetryReadings.AnyAsync(r => r.MessageId == messageId, stoppingToken))
        {
            logger.LogDebug("Duplicate MessageId {MessageId} skipped.", messageId);
            metrics.RecordDuplicate("select");
            return;
        }

        var modelAdvanced = false;
        MachineHealthResult result;

        try
        {
            result = await modelEngine.InspectAsync(
                data.EquipmentId,
                (float)data.EngineTemperature,
                async cancellationToken =>
                {
                    var history = await db
                        .TelemetryReadings.Where(r => r.EquipmentId == data.EquipmentId)
                        // Reconstruct the order in which this stateful detector consumed rows.
                        // Sensor clocks may jump, so event time is not a safe processing order.
                        .OrderByDescending(r => r.PersistedAt)
                        .ThenByDescending(r => r.Id)
                        .Take(ModelEngine.HistoryLength)
                        .Select(r => (float)r.EngineTemperature)
                        .ToListAsync(cancellationToken);

                    history.Reverse();
                    return history;
                },
                stoppingToken
            );
            modelAdvanced = true;

            logger.LogDebug(
                "ML - ID: {Id}, Seq: {Seq}, Temp: {Temp:F1}, IsAnomaly: {IsAnomaly}, Score: {Score:F4}, P-Value: {PVal:F4}, Lag: {Lag:F1}s",
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
                OccurredAt = data.OccurredAt,
                ReceivedAt = data.ReceivedAt,
                EngineTemperature = data.EngineTemperature,
                OilPressure = data.OilPressure,
                IsAnomaly = result.IsAnomaly,
                DetectorScore = result.Score,
                DetectorPValue = result.PValue,
                DetectorHistoryCount = result.HistoryCount,
                DetectorVersion = result.DetectorVersion,
            };

            db.TelemetryReadings.Add(reading);

            if (result.IsAnomaly)
            {
                db.AlertOutboxMessages.Add(
                    new AlertOutboxMessage
                    {
                        MessageId = messageId,
                        EquipmentId = data.EquipmentId,
                        // Empty Diagnostics is an internal "needs enrichment" marker. The outbox
                        // publisher performs the optional network call after this telemetry row and
                        // outbox row have committed, so Gemini latency cannot stall source offsets.
                        Payload = BuildPendingAnomalyPayload(data),
                        TraceParent = Activity.Current?.Id,
                    }
                );
            }

            // EF wraps both inserts in one Postgres transaction. The Kafka offset is committed
            // only after this returns, and the outbox publisher owns the separate Kafka write.
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            if (modelAdvanced)
                modelEngine.Invalidate(data.EquipmentId);

            // Several unique indexes exist. Treat the exception as idempotent success only if
            // another transaction actually persisted this telemetry MessageId.
            await using var verificationDb = await contextFactory.CreateDbContextAsync(
                stoppingToken
            );
            if (
                await verificationDb.TelemetryReadings.AnyAsync(
                    r => r.MessageId == messageId,
                    stoppingToken
                )
            )
            {
                logger.LogDebug("Duplicate MessageId {MessageId} rejected by index.", messageId);
                metrics.RecordDuplicate("index");
                return;
            }

            throw;
        }
        catch
        {
            if (modelAdvanced)
                modelEngine.Invalidate(data.EquipmentId);
            throw;
        }

        metrics.Persisted.Add(1);

        metrics.Lag.Record((DateTimeOffset.UtcNow - data.OccurredAt).TotalSeconds);

        if (result.IsAnomaly)
        {
            metrics.Anomalies.Add(1);
        }
    }

    private static string BuildPendingAnomalyPayload(TelemetryEnvelope data) =>
        JsonSerializer.Serialize(
            new TelemetryAlertEnvelope(
                data.MessageId,
                data.EquipmentId,
                data.OccurredAt,
                data.EngineTemperature,
                Diagnostics: string.Empty
            )
        );

    private static bool TryValidate(
        TelemetryEnvelope? data,
        out Guid messageId,
        out string reason
    )
    {
        messageId = default;

        if (data is null)
        {
            reason = "payload is not valid telemetry JSON";
            return false;
        }

        if (!Guid.TryParseExact(data.MessageId, "D", out messageId))
        {
            reason = "MessageId is not a canonical GUID";
            return false;
        }

        if (
            string.IsNullOrWhiteSpace(data.EquipmentId)
            || data.EquipmentId.Length > 64
            || data.EquipmentId != data.EquipmentId.Trim()
            || !data.EquipmentId.All(IsEquipmentIdCharacter)
        )
        {
            reason = "EquipmentId is missing or too long";
            return false;
        }

        if (data.SequenceNumber <= 0)
        {
            reason = "SequenceNumber is not positive";
            return false;
        }

        if (data.OccurredAt == default || data.ReceivedAt == default)
        {
            reason = "an event or cloud timestamp is missing";
            return false;
        }

        if (
            !IsSupportedMeasurement(data.EngineTemperature)
            || !IsSupportedMeasurement(data.OilPressure)
        )
        {
            reason = "a measurement is non-finite or outside the detector's numeric range";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static TelemetryEnvelope NormalizeTimestamps(TelemetryEnvelope data) =>
        data with
        {
            OccurredAt = data.OccurredAt.ToUniversalTime(),
            ReceivedAt = data.ReceivedAt.ToUniversalTime(),
        };

    private static bool IsSupportedMeasurement(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;

    private static bool IsEquipmentIdCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or ':';
}
