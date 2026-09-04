import { describe, expect, it } from 'vitest'
import { isTelemetryAlert, isTelemetryEvent } from './useTelemetry'

const event = {
  MessageId: '11111111-1111-4111-8111-111111111111',
  EquipmentId: 'EQ-0',
  SequenceNumber: 1,
  OccurredAt: '2026-09-04T00:00:00Z',
  ReceivedAt: '2026-09-04T00:00:01Z',
  EngineTemperature: 80,
  OilPressure: 40,
}

const alert = {
  MessageId: '22222222-2222-4222-8222-222222222222',
  EquipmentId: 'EQ-0',
  OccurredAt: '2026-09-04T00:00:00Z',
  EngineTemperature: 245,
  Diagnostics: 'spike',
}

describe('isTelemetryEvent', () => {
  it('accepts a well-formed reading', () => {
    expect(isTelemetryEvent(event)).toBe(true)
  })

  it('rejects lowercase keys that do not bind the contract', () => {
    expect(
      isTelemetryEvent({
        messageId: event.MessageId,
        equipmentId: event.EquipmentId,
        sequenceNumber: 1,
        occurredAt: event.OccurredAt,
        receivedAt: event.ReceivedAt,
        engineTemperature: 80,
        oilPressure: 40,
      }),
    ).toBe(false)
  })

  it('rejects non-objects, zero sequences, NaN, and bad timestamps', () => {
    expect(isTelemetryEvent(null)).toBe(false)
    expect(isTelemetryEvent('nope')).toBe(false)
    expect(isTelemetryEvent({ ...event, SequenceNumber: 0 })).toBe(false)
    expect(isTelemetryEvent({ ...event, SequenceNumber: 1.5 })).toBe(false)
    expect(isTelemetryEvent({ ...event, EngineTemperature: NaN })).toBe(false)
    expect(isTelemetryEvent({ ...event, OccurredAt: 'not-a-date' })).toBe(false)
    expect(isTelemetryEvent({ ...event, MessageId: '' })).toBe(false)
  })
})

describe('isTelemetryAlert', () => {
  it('accepts a well-formed alert', () => {
    expect(isTelemetryAlert(alert)).toBe(true)
  })

  it('rejects null-identity and missing-diagnostics payloads', () => {
    expect(isTelemetryAlert({ ...alert, MessageId: null })).toBe(false)
    expect(isTelemetryAlert({ ...alert, Diagnostics: 42 })).toBe(false)
    expect(isTelemetryAlert({ ...event })).toBe(false)
  })
})
