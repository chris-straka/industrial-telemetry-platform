using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Google.GenAI;
using Industrial.Diagnostics.Worker.Configuration;
using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;
using Industrial.Diagnostics.Worker.Infrastructure;
using Industrial.Diagnostics.Worker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

public class TelemetryConsumerWorker(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<TelemetryConsumerWorker> logger,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<GeminiOptions> geminiOptions,
    IOptions<ConsumerOptions> consumerOptions,
    IProducer<string, string> producer,
    ModelEngine modelEngine,
    WorkerMetrics metrics,
    Client gemini
) : BackgroundService
{
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kafkaConfig = kafkaOptions.Value;

        var config = new ConsumerConfig
        {
            BootstrapServers = kafkaConfig.BootstrapServers,
            GroupId = kafkaConfig.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MetadataMaxAgeMs = 5000,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
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

                try
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message?.Value == null)
                        continue;

                    TelemetryDto? data;

                    try
                    {
                        data = JsonSerializer.Deserialize<TelemetryDto>(
                            consumeResult.Message.Value
                        );
                    }
                    catch (JsonException)
                    {
                        data = null;
                    }

                    if (data is null || !Guid.TryParse(data.MessageId, out var messageId))
                    {
                        logger.LogWarning(
                            "Discarding unreadable message at offset {Offset}",
                            consumeResult.Offset
                        );
                        metrics.Discarded.Add(1);
                        consumer.StoreOffset(consumeResult);
                        continue;
                    }

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
                    consumer.StoreOffset(consumeResult);
                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    logger.LogError(ex, "Error in consumer loop");
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
        TelemetryDto data,
        Guid messageId,
        CancellationToken stoppingToken
    )
    {
        using var db = await contextFactory.CreateDbContextAsync(stoppingToken);

        if (await db.TelemetryReadings.AnyAsync(r => r.MessageId == messageId, stoppingToken))
        {
            logger.LogDebug("Duplicate MessageId {MessageId} skipped.", messageId);
            metrics.RecordDuplicate("select");
            return;
        }

        var result = modelEngine.Inspect((float)data.EngineTemperature);

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
        };

        db.TelemetryReadings.Add(reading);

        try
        {
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            logger.LogDebug("Duplicate MessageId {MessageId} rejected by index.", messageId);
            metrics.RecordDuplicate("index");
            return;
        }

        metrics.Persisted.Add(1);

        metrics.Lag.Record((DateTimeOffset.UtcNow - data.OccurredAt).TotalSeconds);

        if (result.IsAnomaly)
        {
            metrics.Anomalies.Add(1);
            await HandleAnomalyAsync(data, stoppingToken);
        }
    }

    private async Task HandleAnomalyAsync(TelemetryDto data, CancellationToken ct)
    {
        logger.LogWarning("ANOMALY: {Id}. Requesting AI analysis...", data.EquipmentId);

        string aiAdvice;

        using var aiTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        aiTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var prompt =
                $"Equipment {data.EquipmentId} anomaly. Temp: {data.EngineTemperature:F1}C. Provide 3 steps.";

            var res = await gemini.Models.GenerateContentAsync(
                model: geminiOptions.Value.Model,
                contents: prompt,
                cancellationToken: aiTimeout.Token
            );

            aiAdvice = res.Text ?? throw new Exception("Could not fetch from AI");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "AI call failed");
            metrics.AiFailures.Add(1);
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

        if (Activity.Current?.Id is { } traceparent)
        {
            alert.Headers = [new Header("traceparent", Encoding.UTF8.GetBytes(traceparent))];
        }

        try
        {
            await producer.ProduceAsync(kafkaOptions.Value.AlertsTopic, alert, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Alert for {Id} was not published.", data.EquipmentId);
            metrics.AlertFailures.Add(1);
        }
    }
}

public record TelemetryDto(
    string MessageId,
    string EquipmentId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    double EngineTemperature,
    double OilPressure
);
