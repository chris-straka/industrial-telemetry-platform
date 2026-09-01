using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;
using Industrial.Ingestion.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// Debug-only route to push one reading straight into Kafka (see the .http file).
/// Nothing in production calls it, and a broker outage loses the reading outright.
/// </summary>
public class TelemetryValidator : AbstractValidator<TelemetryDto>
{
    public TelemetryValidator()
    {
        RuleFor(x => x.EquipmentId).NotEmpty();
        RuleFor(x => x.EngineTemperature).InclusiveBetween(-50, 250);
    }
}

public static class IngestTelemetryEndpoint
{
    public static void MapDebugTelemetryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/debug/telemetry",
            async (
                TelemetryDto request,
                IValidator<TelemetryDto> validator,
                IProducer<string, string> kafkaProducer,
                IOptions<KafkaOptions> kafkaOptions
            ) =>
            {
                var validationResult = await validator.ValidateAsync(request);
                if (!validationResult.IsValid)
                    return Results.ValidationProblem(validationResult.ToDictionary());

                // Same envelope the gRPC path produces
                var payload = new
                {
                    MessageId = request.MessageId ?? Guid.NewGuid().ToString(),
                    request.EquipmentId,
                    SequenceNumber = 0L,
                    OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    request.EngineTemperature,
                    request.OilPressure,
                };

                var message = new Message<string, string>
                {
                    Key = request.EquipmentId,
                    Value = JsonSerializer.Serialize(payload),
                };

                await kafkaProducer.ProduceAsync(kafkaOptions.Value.EventsTopic, message);

                return Results.Accepted();
            }
        );
    }
}

// Nullable let's me CURL it without those things (creates defaults for me)
public record TelemetryDto(
    string EquipmentId,
    double EngineTemperature,
    double OilPressure,
    string? MessageId = null,
    DateTimeOffset? OccurredAt = null
);
