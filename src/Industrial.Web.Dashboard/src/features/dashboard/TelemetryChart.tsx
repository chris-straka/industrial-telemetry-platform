import { Chart, Title, XAxis, YAxis } from '@highcharts/react'
import { LineSeries } from '@highcharts/react/series/Line'
import type { TelemetryEvent } from '../../types/telemetry'

interface Props {
  data: TelemetryEvent[]
}

export function TelemetryChart({ data }: Props) {
  // One series per machine. A single series over a mixed feed draws a line that
  // jumps between devices, which looks like noise the sensors never produced.
  const byEquipment = new Map<string, [number, number][]>()

  for (const reading of data) {
    const points = byEquipment.get(reading.EquipmentId) ?? []
    points.push([Date.parse(reading.OccurredAt), reading.EngineTemperature])
    byEquipment.set(reading.EquipmentId, points)
  }

  return (
    <Chart>
      <Title>Live Engine Temperatures</Title>
      {/* Event time, so readings drained after an outage land where they happened
          rather than bunching up at the moment the buffer flushed. */}
      <XAxis type="datetime" />
      <YAxis>Temperature (°C)</YAxis>
      {[...byEquipment].map(([equipmentId, points]) => (
        <LineSeries key={equipmentId} name={equipmentId} data={points} />
      ))}
    </Chart>
  )
}
