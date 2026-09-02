namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Applies permanent, retry-independent checks before a reading can consume durable capacity.
/// </summary>
public static class TelemetryAdmissionValidator
{
    public const int MaxEquipmentIdLength = 64;

    public static string? Validate(TelemetryDto reading, out string canonicalMessageId)
    {
        canonicalMessageId = string.Empty;

        if (!Guid.TryParseExact(reading.MessageId, "D", out var parsedMessageId))
            return "message_id_must_be_a_guid";

        canonicalMessageId = parsedMessageId.ToString("D");

        if (
            string.IsNullOrWhiteSpace(reading.EquipmentId)
            || reading.EquipmentId.Length > MaxEquipmentIdLength
            || reading.EquipmentId != reading.EquipmentId.Trim()
            || !reading.EquipmentId.All(IsEquipmentIdCharacter)
        )
            return "equipment_id_is_invalid";

        if (reading.SequenceNumber <= 0)
            return "sequence_number_must_be_positive";

        if (reading.OccurredAt == default)
            return "occurred_at_is_required";

        if (
            !IsSupportedMeasurement(reading.EngineTemperature)
            || !IsSupportedMeasurement(reading.OilPressure)
        )
            return "measurements_are_invalid";

        return null;
    }

    private static bool IsEquipmentIdCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or ':';

    private static bool IsSupportedMeasurement(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;
}
