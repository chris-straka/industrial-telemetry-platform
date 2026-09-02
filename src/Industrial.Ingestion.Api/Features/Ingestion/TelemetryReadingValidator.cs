using System.Diagnostics;
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
    private const int MaxEquipmentIdLength = 64;
    private const int MaxTraceParentLength = 128;

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
            .MaximumLength(36)
            .Must(id => Guid.TryParseExact(id, "D", out _))
            .WithMessage("MessageId must use the canonical GUID format.");

        // The Kafka message key (used for partitions)
        RuleFor(x => x.EquipmentId)
            .NotEmpty()
            .MaximumLength(MaxEquipmentIdLength)
            .Matches("^[A-Za-z0-9._:-]+$")
            .Must(id => id is null || id == id.Trim())
            .WithMessage("EquipmentId cannot have leading or trailing whitespace.");

        // 0 should not be possible (sensors count from 1)
        RuleFor(x => x.SequenceNumber).GreaterThan(0);

        RuleFor(x => x.EngineTemperature)
            .Must(IsSupportedMeasurement)
            .WithMessage("EngineTemperature is non-finite or outside the detector's numeric range.");

        RuleFor(x => x.OilPressure)
            .Must(IsSupportedMeasurement)
            .WithMessage("OilPressure is non-finite or outside the supported numeric range.");

        RuleFor(x => x.OccurredAt)
            .NotNull()
            .Must(IsRepresentable)
            .WithMessage("OccurredAt is outside the range google.protobuf.Timestamp can hold.");

        // This becomes a Kafka header. Bounding and parsing it prevents an otherwise valid
        // reading from becoming an oversized or malformed downstream message.
        RuleFor(x => x.Traceparent)
            .MaximumLength(MaxTraceParentLength)
            .Must(IsTraceParent)
            .WithMessage("Traceparent is not a valid W3C trace context.");
    }

    // Filters out values from a broken clock
    private static bool IsRepresentable(Timestamp? occurredAt) =>
        occurredAt is null
        || (
            occurredAt.Seconds >= MinSeconds
            && occurredAt.Seconds <= MaxSeconds
            && occurredAt.Nanos is >= 0 and < NanosPerSecond
        );

    private static bool IsTraceParent(string traceParent) =>
        string.IsNullOrEmpty(traceParent)
        || ActivityContext.TryParse(traceParent, null, isRemote: true, out _);

    private static bool IsSupportedMeasurement(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;
}
