import { useState, useCallback, useRef } from 'react'
import type { TelemetryEvent, TelemetryAlert } from './types/telemetry'
import { useTelemetry, type ConnectionStatus } from './hooks/useTelemetry'
import { TelemetryChart } from './features/dashboard/TelemetryChart'
import { AlertsList } from './features/dashboard/AlertsList'
import { StatusBadge } from './features/dashboard/StatusBadge'
import { remember } from './lib/dedupe'
import './App.css'

function App() {
  // For Kafka
  const [events, setEvents] = useState<TelemetryEvent[]>([])
  const [alerts, setAlerts] = useState<TelemetryAlert[]>([])
  const [status, setStatus] = useState<ConnectionStatus>('connecting')
  const [lastMessageAt, setLastMessageAt] = useState<number | null>(null)
  const seenEventIds = useRef(new Set<string>())
  const eventIdOrder = useRef<string[]>([])
  const seenAlertIds = useRef(new Set<string>())
  const alertIdOrder = useRef<string[]>([])

  const handleEvent = useCallback((event: TelemetryEvent) => {
    if (!remember(event.MessageId, seenEventIds.current, eventIdOrder.current, 2_000)) return
    // Shared across every device, so this is ~50 points each at 4 devices.
    setEvents((prev) => [...prev.slice(-199), event])
    setLastMessageAt(Date.now())
  }, [])

  const handleAlert = useCallback((alert: TelemetryAlert) => {
    if (!remember(alert.MessageId, seenAlertIds.current, alertIdOrder.current, 500)) return
    setAlerts((prev) => [alert, ...prev.slice(0, 9)]) // Keep top 10
    setLastMessageAt(Date.now())
  }, [])

  // Kafka API updates happen here
  useTelemetry(handleEvent, handleAlert, setStatus)

  return (
    <div className="dashboard">
      <header className="dashboard-header">
        <h1>Industrial Control Panel</h1>
        <StatusBadge status={status} lastMessageAt={lastMessageAt} />
      </header>
      <div className="grid">
        <section>
          <TelemetryChart data={events} />
        </section>
        <aside>
          <AlertsList alerts={alerts} />
        </aside>
      </div>
    </div>
  )
}

export default App
