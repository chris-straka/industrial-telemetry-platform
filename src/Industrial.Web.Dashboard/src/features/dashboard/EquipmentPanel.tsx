import type { TelemetryEvent } from '../../types/telemetry'
import { summarizeEquipment } from '../../lib/equipment'

interface Props {
  events: TelemetryEvent[]
  hidden: Set<string>
  onToggle: (equipmentId: string) => void
}

export function EquipmentPanel({ events, hidden, onToggle }: Props) {
  const summaries = summarizeEquipment(events)

  if (summaries.length === 0) {
    return <p className="empty">No equipment seen yet.</p>
  }

  return (
    <table className="equipment">
      <caption>Fleet</caption>
      <thead>
        <tr>
          <th>Show</th>
          <th>Equipment</th>
          <th>Points</th>
          <th>Last °C</th>
          <th>Seq gaps</th>
        </tr>
      </thead>
      <tbody>
        {summaries.map((summary) => (
          <tr key={summary.equipmentId} className={hidden.has(summary.equipmentId) ? 'muted' : undefined}>
            <td>
              <input
                type="checkbox"
                aria-label={`Show ${summary.equipmentId}`}
                checked={!hidden.has(summary.equipmentId)}
                onChange={() => onToggle(summary.equipmentId)}
              />
            </td>
            <td>{summary.equipmentId}</td>
            <td>{summary.count}</td>
            <td>{summary.lastTemperature.toFixed(1)}</td>
            <td>{summary.gaps > 0 ? `${summary.gaps} gap${summary.gaps === 1 ? '' : 's'}` : '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
