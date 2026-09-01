using FluentValidation;
using Google.Protobuf.WellKnownTypes;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// What the cloud requires of a reading before it produces to Kafka.
/// </summary>
/// <remarks>
/// Every rule must be something no retry can fix (failures here delete the reading)
/// </remarks>
public class TelemetryReadingValidator : AbstractValidator<TelemetryReading>
{
    private static readonly long MinSeconds = Timestamp
        .FromDateTimeOffset(DateTimeOffset.MinValue)
        .Seconds;
    private static readonly long MaxSeconds = Timestamp
        .FromDateTimeOffset(DateTimeOffset.MaxValue)
        .Seconds;

    private const int NanosPerSecond = 1_000_000_000;

    // proto3 defaults are either 0 or null
    public TelemetryReadingValidator()
    {
        // Diagnostics.Worker dedupes on this too
        RuleFor(x => x.MessageId)
            .NotEmpty()
            .Must(id => Guid.TryParse(id, out _))
            .WithMessage("MessageId is not a GUID, so Diagnostics.Worker cannot dedupe on it.");

        // The Kafka message key (used for partitions)
        RuleFor(x => x.EquipmentId).NotEmpty();

        // 0 should not be possible (sensors count from 1)
        RuleFor(x => x.SequenceNumber).GreaterThan(0);

        RuleFor(x => x.EngineTemperature)
            .Must(double.IsFinite)
            .WithMessage("EngineTemperature is NaN or infinite.");

        RuleFor(x => x.OilPressure)
            .Must(double.IsFinite)
            .WithMessage("OilPressure is NaN or infinite.");

        RuleFor(x => x.OccurredAt)
            .NotNull()
            .Must(IsRepresentable)
            .WithMessage("OccurredAt is outside the range google.protobuf.Timestamp can hold.");
    }

    // Filters out values from a broken clock
    private static bool IsRepresentable(Timestamp? occurredAt) =>
        occurredAt is null
        || (
            occurredAt.Seconds >= MinSeconds
            && occurredAt.Seconds <= MaxSeconds
            && occurredAt.Nanos is >= 0 and < NanosPerSecond
        );
}
