import type { TelemetryEvent } from '../types/telemetry'

export interface EquipmentSummary {
  equipmentId: string
  count: number
  lastTemperature: number
  lastSequence: number
  // Number of sequence jumps larger than 1. The sensor emulator is best-effort
  // and drops samples under pressure, so a gap is expected loss, not proof of
  // a pipeline break; surfacing it here keeps that loss visible on the wall.
  gaps: number
}

export function summarizeEquipment(events: TelemetryEvent[]): EquipmentSummary[] {
  const byEquipment = new Map<string, TelemetryEvent[]>()
  for (const event of events) {
    const group = byEquipment.get(event.EquipmentId)
    if (group) group.push(event)
    else byEquipment.set(event.EquipmentId, [event])
  }

  const summaries: EquipmentSummary[] = []
  for (const [equipmentId, readings] of byEquipment) {
    const ordered = [...readings].sort((a, b) => a.SequenceNumber - b.SequenceNumber)
    let gaps = 0
    for (let i = 1; i < ordered.length; i++) {
      if (ordered[i].SequenceNumber - ordered[i - 1].SequenceNumber > 1) gaps++
    }
    const last = ordered[ordered.length - 1]
    summaries.push({
      equipmentId,
      count: ordered.length,
      lastTemperature: last.EngineTemperature,
      lastSequence: last.SequenceNumber,
      gaps,
    })
  }

  summaries.sort((a, b) => a.equipmentId.localeCompare(b.equipmentId))
  return summaries
}
