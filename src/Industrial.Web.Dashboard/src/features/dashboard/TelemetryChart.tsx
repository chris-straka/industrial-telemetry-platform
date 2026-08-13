import { Chart, Title, XAxis, YAxis } from '@highcharts/react'
import { LineSeries } from '@highcharts/react/series/Line'
import type { TelemetryEvent } from '../../types/telemetry'

interface Props {
  data: TelemetryEvent[]
}

export function TelemetryChart({ data }: Props) {
  const categories = data.map((d) => d.EquipmentId)
  const temperatures = data.map((d) => d.EngineTemperature)
  const oilPressures = data.map((d) => d.OilPressure)

  return (
    <Chart>
      <Title>Live Engine Temperatures</Title>
      <XAxis categories={categories} />
      <YAxis>Readings</YAxis>
      <YAxis>Temperature (°C)</YAxis>
      <LineSeries name="Temperature (°C)" data={temperatures} />
      <LineSeries name="Oil Pressure" data={oilPressures} />
    </Chart>
  )
}
