import type { TelemetryAlert } from '../../types/telemetry'

interface Props {
  alerts: TelemetryAlert[]
}

export function AlertsList({ alerts }: Props) {
  return (
    <table className="alerts">
      <caption>Anomaly Alerts</caption>
      <thead>
        <tr>
          <th>When</th>
          <th>Equipment</th>
          <th>AI Advice</th>
        </tr>
      </thead>
      <tbody>
        {alerts.length === 0 ? (
          <tr>
            <td colSpan={3} className="empty">
              No alerts yet.
            </td>
          </tr>
        ) : (
          alerts.map((alert) => (
            <tr key={alert.MessageId}>
              <td>{new Date(alert.OccurredAt).toLocaleTimeString()}</td>
              <td>{alert.EquipmentId}</td>
              <td>{alert.Diagnostics}</td>
            </tr>
          ))
        )}
      </tbody>
    </table>
  )
}
