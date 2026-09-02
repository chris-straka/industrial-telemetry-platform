using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;
using Industrial.Ingestion.Api.Configuration;
using Industrial.Shared;
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
        RuleFor(x => x.EquipmentId)
            .NotEmpty()
            .MaximumLength(64)
            .Matches("^[A-Za-z0-9._:-]+$")
            .Must(id => id is null || id == id.Trim());
        RuleFor(x => x.MessageId)
            .Must(id => id is null || Guid.TryParseExact(id, "D", out _))
            .WithMessage("MessageId must use the canonical GUID format.");
        RuleFor(x => x.EngineTemperature).Must(IsSupportedMeasurement);
        RuleFor(x => x.OilPressure).Must(IsSupportedMeasurement);
        RuleFor(x => x.OccurredAt).Must(value => value is null || value != default);
    }

    private static bool IsSupportedMeasurement(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;
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
                var payload = new TelemetryEnvelope(
                    request.MessageId ?? Guid.CreateVersion7().ToString("D"),
                    request.EquipmentId,
                    SequenceNumber: 1,
                    request.OccurredAt ?? DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    request.EngineTemperature,
                    request.OilPressure
                );

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
