// Mirrors the envelope Industrial.Ingestion.Api produces to telemetry-events.
export interface TelemetryEvent {
  MessageId: string
  EquipmentId: string
  SequenceNumber: number
  // Sensor clock. Chart against this, not ReceivedAt.
  OccurredAt: string
  // Cloud clock.
  ReceivedAt: string
  EngineTemperature: number
  OilPressure: number
}

// Mirrors what Industrial.Diagnostics.Worker produces to telemetry-alerts.
export interface TelemetryAlert {
  MessageId: string
  EquipmentId: string
  OccurredAt: string
  EngineTemperature: number
  Diagnostics: string
}
