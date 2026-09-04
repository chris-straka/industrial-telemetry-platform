import { describe, expect, it } from 'vitest'
import { summarizeEquipment } from './equipment'
import type { TelemetryEvent } from '../types/telemetry'

function event(equipmentId: string, sequence: number, temp = 90): TelemetryEvent {
  return {
    MessageId: `${equipmentId}-${sequence}`,
    EquipmentId: equipmentId,
    SequenceNumber: sequence,
    OccurredAt: new Date(Date.UTC(2026, 0, 1, 0, 0, sequence)).toISOString(),
    ReceivedAt: new Date(Date.UTC(2026, 0, 1, 0, 0, sequence)).toISOString(),
    EngineTemperature: temp,
    OilPressure: 40,
  }
}

describe('summarizeEquipment', () => {
  it('returns one row per device sorted by id', () => {
    const summaries = summarizeEquipment([event('EQ-2', 1), event('EQ-1', 1)])
    expect(summaries.map((s) => s.equipmentId)).toEqual(['EQ-1', 'EQ-2'])
  })

  it('counts points and reports the latest temperature and sequence', () => {
    const summaries = summarizeEquipment([
      event('EQ-1', 1, 90),
      event('EQ-1', 2, 95.25),
    ])
    expect(summaries).toHaveLength(1)
    expect(summaries[0].count).toBe(2)
    expect(summaries[0].lastTemperature).toBe(95.25)
    expect(summaries[0].lastSequence).toBe(2)
    expect(summaries[0].gaps).toBe(0)
  })

  it('counts sequence jumps as gaps regardless of arrival order', () => {
    const summaries = summarizeEquipment([
      event('EQ-1', 1),
      event('EQ-1', 5),
      event('EQ-1', 3),
    ])
    expect(summaries[0].gaps).toBe(2)
  })

  it('returns an empty list when nothing arrived yet', () => {
    expect(summarizeEquipment([])).toEqual([])
  })
})
