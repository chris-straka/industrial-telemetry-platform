// Mirrors the envelope Industrial.Ingestion.Api produces to telemetry-events.
export interface TelemetryEvent {
  MessageId: string;
  EquipmentId: string;
  SequenceNumber: number;
  // Sensor clock. Chart against this, not ReceivedAt.
  OccurredAt: string;
  // Cloud clock.
  ReceivedAt: string;
  EngineTemperature: number;
  OilPressure: number;
}

// Mirrors what Industrial.Diagnostics.Worker produces to telemetry-alerts.
export interface TelemetryAlert {
  MessageId: string;
  EquipmentId: string;
  OccurredAt: string;
  EngineTemperature: number;
  Diagnostics: string;
}

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting';

// The dashboard is the last dedupe and validation boundary, so its shape
// guards are a contract, not an implementation detail.
export function isTelemetryEvent(value: unknown): value is TelemetryEvent {
  if (!isRecord(value)) return false;
  return (
    hasIdentityAndEventTime(value) &&
    isFiniteNumber(value['SequenceNumber']) &&
    Number.isInteger(value['SequenceNumber']) &&
    (value['SequenceNumber'] as number) > 0 &&
    typeof value['ReceivedAt'] === 'string' &&
    !Number.isNaN(Date.parse(value['ReceivedAt'] as string)) &&
    isFiniteNumber(value['EngineTemperature']) &&
    isFiniteNumber(value['OilPressure'])
  );
}

export function isTelemetryAlert(value: unknown): value is TelemetryAlert {
  if (!isRecord(value)) return false;
  return (
    hasIdentityAndEventTime(value) &&
    isFiniteNumber(value['EngineTemperature']) &&
    typeof value['Diagnostics'] === 'string'
  );
}

function hasIdentityAndEventTime(value: Record<string, unknown>): boolean {
  return (
    typeof value['MessageId'] === 'string' &&
    (value['MessageId'] as string).length > 0 &&
    typeof value['EquipmentId'] === 'string' &&
    (value['EquipmentId'] as string).length > 0 &&
    typeof value['OccurredAt'] === 'string' &&
    !Number.isNaN(Date.parse(value['OccurredAt'] as string))
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}
