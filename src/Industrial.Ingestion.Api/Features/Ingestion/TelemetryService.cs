using System.Text.Json;
using Confluent.Kafka;
using Grpc.Core;

namespace Industrial.Ingestion.Api.Features.Ingestion;

public class TelemetryService(
    IProducer<string, string> kafkaProducer,
    ILogger<TelemetryService> logger
) : TelemetryIngestion.TelemetryIngestionBase
{
    public override async Task<TelemetryResponse> StreamTelemetry(
        IAsyncStreamReader<TelemetryRequest> requestStream,
        ServerCallContext context
    )
    {
        int count = 0;

        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
        {
            var payload = new
            {
                MessageId = request.MessageId, // Idempotency key! Downstream ML can use this to ignore dupes.
                EquipmentId = request.EquipmentId,
                EngineTemperature = request.EngineTemperature,
                OilPressure = request.OilPressure,
            };

            var message = new Message<string, string>
            {
                Key = request.EquipmentId,
                Value = JsonSerializer.Serialize(payload),
            };

            await kafkaProducer.ProduceAsync(
                "telemetry-events",
                message,
                context.CancellationToken
            );
            count++;
        }

        logger.LogInformation(
            "Received {Count} telemetry events from Edge Gateway via gRPC Stream.",
            count
        );

        return new TelemetryResponse { Success = true, AcceptedCount = count };
    }
}
