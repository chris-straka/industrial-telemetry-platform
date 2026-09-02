using Google.Protobuf.WellKnownTypes;
using Industrial.Ingestion.Api;
using Industrial.Ingestion.Api.Features.Ingestion;

namespace Industrial.Ingestion.Tests;

public sealed class TelemetryReadingValidatorTests
{
    private readonly TelemetryReadingValidator _validator = new();

    [Fact]
    public void Emulator_shaped_reading_is_valid()
    {
        var result = _validator.Validate(ValidReading());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Oversized_or_nonfinite_fields_are_permanently_rejected()
    {
        var oversized = ValidReading();
        oversized.EquipmentId = new string('x', 65);
        var nonfinite = ValidReading();
        nonfinite.OilPressure = double.NaN;

        Assert.False(_validator.Validate(oversized).IsValid);
        Assert.False(_validator.Validate(nonfinite).IsValid);
    }

    [Fact]
    public void Malformed_trace_context_is_rejected_before_it_becomes_a_kafka_header()
    {
        var reading = ValidReading();
        reading.Traceparent = "not-a-traceparent";

        Assert.False(_validator.Validate(reading).IsValid);
    }

    [Fact]
    public void Current_response_field_numbers_are_frozen()
    {
        var proto = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "telemetry.proto"));

        Assert.Matches(@"accepted_message_ids\s*=\s*2\s*;", proto);
        Assert.Matches(@"rejected_message_ids\s*=\s*3\s*;", proto);
    }

    private static TelemetryReading ValidReading() =>
        new()
        {
            MessageId = Guid.CreateVersion7().ToString("D"),
            EquipmentId = "EQ-1",
            SequenceNumber = 1,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            EngineTemperature = 90,
            OilPressure = 45,
            Traceparent = string.Empty,
        };
}
