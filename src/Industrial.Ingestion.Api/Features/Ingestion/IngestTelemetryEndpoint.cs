using System.Text.Json;
using Confluent.Kafka;
using FluentValidation;
using Industrial.Ingestion.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// The REST ingestion path. This is the MANUAL TEST door (see the .http file), not the
/// production one.
/// </summary>
/// <remarks>
/// Kept deliberately, because being able to curl a single reading into the pipeline is
/// worth a lot when debugging. But be clear about what it is NOT:
///
///   - It is not idempotent unless the caller supplies a MessageId. If it generates one
///     server-side, a retried POST becomes a second distinct reading -- which is exactly
///     the duplicate-write hazard Microsoft warns about with retrying POSTs, and exactly
///     why the durable path mints the id at the SENSOR instead.
///   - It has no store-and-forward. If Kafka is down, ProduceAsync eventually throws and
///     the reading really is gone -- unlike the gRPC path, where the gateway still holds
///     the row on disk and sends it again. Acceptable for a debugging door and nowhere
///     else, which is why real sensors do not come through here.
///
/// Real sensors go through the Edge Gateway and arrive over gRPC (TelemetryService.cs).
/// </remarks>
public record TelemetryDto(
    string EquipmentId,
    double EngineTemperature,
    double OilPressure,
    // Optional so that a hand-written curl stays a one-liner. Supply it and this
    // endpoint becomes idempotent end to end, because the consumer dedupes on it.
    string? MessageId = null,
    DateTimeOffset? OccurredAt = null
);

/// <summary>
/// What the REST door requires, deliberately stricter than TelemetryReadingValidator.
/// </summary>
/// <remarks>
/// Failing a rule means opposite things on the two paths. Here it is a 400 to someone typing
/// a curl, who fixes the number. On the gRPC path the gateway deletes the reading, so a
/// temperature range would discard the out-of-range readings the anomaly detector wants.
/// </remarks>
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
                IProducer<string, string> kafkaProducer,
                IOptions<KafkaOptions> kafkaOptions
            ) =>
            {
                var validationResult = await validator.ValidateAsync(request);
                if (!validationResult.IsValid)
                    return Results.ValidationProblem(validationResult.ToDictionary());

                // Same envelope the gRPC path produces. The consumer has exactly one
                // message shape to deserialize regardless of which door was used --
                // two producers writing different shapes to one topic is how you get
                // fields silently dropping to null downstream.
                var payload = new
                {
                    MessageId = request.MessageId ?? Guid.NewGuid().ToString(),
                    request.EquipmentId,
                    // No sequence on the manual path -- there is no device counting. That
                    // makes these rows poison for verify.sql, which proves nothing was
                    // lost by asserting max_seq = COUNT(*) per device: a zero here raises
                    // the count without raising the max. Hence the MANUAL- id convention
                    // in the .http file, which verify.sql filters out.
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

                // If Kafka is booting, this Task completes once the producer's internal
                // retry logic succeeds. If Kafka is genuinely down it throws, and this
                // endpoint has nowhere to put the reading -- see the remarks above.
                await kafkaProducer.ProduceAsync(kafkaOptions.Value.EventsTopic, message);

                return Results.Accepted();
            }
        );
    }
}
