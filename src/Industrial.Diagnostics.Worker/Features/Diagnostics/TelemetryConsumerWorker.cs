using System.Text.Json;
using Confluent.Kafka;
using Google.GenAI;
using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;
using Industrial.Diagnostics.Worker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

public record TelemetryDto(string EquipmentId, double EngineTemperature, double OilPressure);

public class TelemetryConsumerWorker(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<TelemetryConsumerWorker> logger,
    IConfiguration configuration,
    IProducer<string, string> producer,
    ModelEngine modelEngine,
    Client client
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers =
            configuration["Kafka:BootstrapServers"]
            ?? throw new Exception("Worker missing Kafka:BootstrapServers");
        var groupId =
            configuration["Kafka:GroupId"] ?? throw new Exception("Worker missing Kafka:GroupId");
        var topicName =
            configuration["Kafka:TopicName"]
            ?? throw new Exception("Worker missing Kafka:TopicName");

        // AutoOffsetReset is where the consumer starts reading from if the broker has no saved position/offset for that consumer's groupId
        // It's relevant in four situations
        // 1. Brand new GroupId -> 1) connecting for first time or 2) consumer changed their groupId
        // 2. Consumer stopped for longer than Kafka's offset retention window (default 7 days)
        // 3. Broker's offset points to data older than 7 days
        // 4. Manual reset

        // If the consumer crahes after saving to the DB but before it commits to the offset
        // It will save to the DB twice

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest, // read/offset from the beginning
            MetadataMaxAgeMs = 5000,
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
        consumer.Subscribe(topicName);

        logger.LogInformation($"📥 Subscribed to Kafka Topic: {topicName} on {bootstrapServers}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Consume is blocking, but respects the stoppingToken
                var consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult?.Message?.Value == null)
                    continue;

                var data = JsonSerializer.Deserialize<TelemetryDto>(consumeResult.Message.Value);
                if (data == null)
                    continue;

                var result = modelEngine.Inspect((float)data.EngineTemperature);

                logger.LogInformation(
                    "ML Debug - ID: {Id}, Temp: {Temp:F1}, IsAnomaly: {IsAnomaly}, Score: {Score:F4}, P-Value: {PVal:F4}",
                    data.EquipmentId,
                    data.EngineTemperature,
                    result.IsAnomaly,
                    result.Score,
                    result.PValue
                );

                var reading = new TelemetryReading
                {
                    EquipmentId = data.EquipmentId,
                    EngineTemperature = data.EngineTemperature,
                    OilPressure = data.OilPressure,
                    IsAnomaly = result.IsAnomaly,
                    Timestamp = DateTime.UtcNow,
                };

                using var db = await contextFactory.CreateDbContextAsync(stoppingToken);
                db.TelemetryReadings.Add(reading);
                await db.SaveChangesAsync(stoppingToken);

                if (result.IsAnomaly)
                {
                    logger.LogInformation("Anomaly detected");
                    await HandleAnomalyAsync(data, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "🔥 Error in Consumer Loop");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task HandleAnomalyAsync(TelemetryDto data, CancellationToken ct)
    {
        logger.LogWarning("⚠️ ANOMALY: {Id}. Requesting AI Analysis...", data.EquipmentId);

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
            logger.LogError(ex, "❌ AI Failed");
            aiAdvice = "AI failed";
        }

        var alert = new Message<string, string>
        {
            Key = data.EquipmentId,
            Value = JsonSerializer.Serialize(new { data.EquipmentId, Diagnostics = aiAdvice }),
        };
        await producer.ProduceAsync("telemetry-alerts", alert, ct);
    }
}
