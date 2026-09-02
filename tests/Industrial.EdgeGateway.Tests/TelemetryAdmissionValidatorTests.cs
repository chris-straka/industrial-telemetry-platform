using Industrial.Sensor.EdgeGateway.Features.Buffer;

namespace Industrial.EdgeGateway.Tests;

public sealed class TelemetryAdmissionValidatorTests
{
    [Fact]
    public void Valid_reading_is_accepted_and_its_guid_is_canonicalized()
    {
        var id = Guid.CreateVersion7();
        var reading = ValidReading() with { MessageId = id.ToString("D").ToUpperInvariant() };

        var error = TelemetryAdmissionValidator.Validate(reading, out var canonicalId);

        Assert.Null(error);
        Assert.Equal(id.ToString("D"), canonicalId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000000000000000000000000000")]
    public void Noncanonical_message_ids_are_rejected(string messageId)
    {
        var error = TelemetryAdmissionValidator.Validate(
            ValidReading() with { MessageId = messageId },
            out _
        );

        Assert.Equal("message_id_must_be_a_guid", error);
    }

    [Fact]
    public void Oversized_identity_and_nonfinite_measurement_are_rejected()
    {
        var oversized = TelemetryAdmissionValidator.Validate(
            ValidReading() with { EquipmentId = new string('x', 65) },
            out _
        );
        var nonfinite = TelemetryAdmissionValidator.Validate(
            ValidReading() with { EngineTemperature = double.PositiveInfinity },
            out _
        );

        Assert.Equal("equipment_id_is_invalid", oversized);
        Assert.Equal("measurements_are_invalid", nonfinite);
    }

    private static TelemetryDto ValidReading() =>
        new(
            Guid.CreateVersion7().ToString("D"),
            "EQ-1",
            1,
            DateTimeOffset.UtcNow,
            90,
            45
        );
}
