using FluentValidation;
using Google.Protobuf.WellKnownTypes;

namespace Industrial.Ingestion.Api.Features.Ingestion;

/// <summary>
/// What the cloud requires of a reading before it will produce it to Kafka.
/// </summary>
/// <remarks>
/// Every rule here has to be one no retry can fix, because a reading that fails is named in
/// TelemetryResponse.rejected_message_ids and the gateway then deletes it.
/// A transient condition belongs in the catch around ProduceAsync instead, which leaves the
/// reading in the gateway's buffer.
///
/// FluentValidation rather than checks inline in the produce loop, because the REST door
/// already validates through it and one mechanism means one place to read the cloud's rules.
/// </remarks>
public class TelemetryReadingValidator : AbstractValidator<TelemetryReading>
{
    // Read off the conversion rather than written as literals, so they cannot drift from it
    private static readonly long MinSeconds = Timestamp
        .FromDateTimeOffset(DateTimeOffset.MinValue)
        .Seconds;

    private static readonly long MaxSeconds = Timestamp
        .FromDateTimeOffset(DateTimeOffset.MaxValue)
        .Seconds;

    private const int NanosPerSecond = 1_000_000_000;

    public TelemetryReadingValidator()
    {
        // Without the idempotency key the consumer cannot dedupe, so a Kafka redelivery would
        // land twice in Postgres and break the duplicates = 0 invariant.
        RuleFor(x => x.MessageId).NotEmpty();

        // The Kafka message key. An empty one spreads a device's readings across partitions and
        // loses their relative order.
        RuleFor(x => x.EquipmentId).NotEmpty();

        // Sensors count from 1, and verify.sql proves nothing was lost by asserting
        // MAX(sequence_number) = COUNT(*) per device. A zero raises the count without the max.
        RuleFor(x => x.SequenceNumber).GreaterThan(0);

        RuleFor(x => x.OccurredAt)
            // Unset in proto3 means a null message, and ToDateTimeOffset() throws on it.
            .NotNull()
            // A broken clock can send a seconds count no DateTimeOffset holds. Unchecked it
            // throws past this validator, reads as a fault, and the gateway resends it forever
            // Representable range only, since "within a day of now" would delete skewed readings
            .Must(IsRepresentable)
            .WithMessage("OccurredAt is outside the range google.protobuf.Timestamp can hold.");
    }

    // Null passes because NotNull already reported it and FluentValidation runs the chain anyway
    private static bool IsRepresentable(Timestamp? occurredAt) =>
        occurredAt is null
        || (
            occurredAt.Seconds >= MinSeconds
            && occurredAt.Seconds <= MaxSeconds
            && occurredAt.Nanos is >= 0 and < NanosPerSecond
        );
}
