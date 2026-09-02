namespace Industrial.Shared;

/// <summary>
/// The JSON value carried by the telemetry-events Kafka topic.
/// </summary>
/// <remarks>
/// Both producer and consumer reference this type so a field rename is a compiler-visible contract
/// change instead of silently deserializing a missing numeric field as zero.
/// </remarks>
public sealed record TelemetryEnvelope(
    string MessageId,
    string EquipmentId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    double EngineTemperature,
    double OilPressure
);

/// <summary>The JSON value carried by the telemetry-alerts Kafka topic.</summary>
public sealed record TelemetryAlertEnvelope(
    string MessageId,
    string EquipmentId,
    DateTimeOffset OccurredAt,
    double EngineTemperature,
    string Diagnostics
);
