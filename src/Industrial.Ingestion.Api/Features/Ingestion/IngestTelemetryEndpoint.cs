using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;

namespace Industrial.Ingestion.Api.Features.Ingestion;

public record TelemetryDto(string EquipmentId, double EngineTemperature, double OilPressure);

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
    public static void MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/telemetry",
            async (
                TelemetryDto request,
                IValidator<TelemetryDto> validator,
                IProducer<string, string> kafkaProducer
            ) =>
            {
                var validationResult = await validator.ValidateAsync(request);
                if (!validationResult.IsValid)
                    return Results.ValidationProblem(validationResult.ToDictionary());

                var message = new Message<string, string>
                {
                    Key = request.EquipmentId,
                    Value = JsonSerializer.Serialize(request),
                };

                // If Kafka is booting, this Task will complete once the producer's internal retry logic succeeds.
                // This is where we specify the Kafka topic
                await kafkaProducer.ProduceAsync("telemetry-events", message);

                return Results.Accepted();
            }
        );
    }
}
