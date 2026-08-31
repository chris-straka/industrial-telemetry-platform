using FluentValidation;

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

        // Unset in proto3 means a null message, and ToDateTimeOffset() throws on it.
        // Rejecting it here turns a NullReferenceException that faults the whole batch into one
        // named reading the gateway can drop.
        RuleFor(x => x.OccurredAt).NotNull();
    }
}
