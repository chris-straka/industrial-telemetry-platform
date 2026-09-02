import { useState, useCallback, useRef } from 'react'
import type { TelemetryEvent, TelemetryAlert } from './types/telemetry'
import { useTelemetry } from './hooks/useTelemetry'
import { TelemetryChart } from './features/dashboard/TelemetryChart'
import { AlertsList } from './features/dashboard/AlertsList'
import './App.css'

function App() {
  // For Kafka
  const [events, setEvents] = useState<TelemetryEvent[]>([])
  const [alerts, setAlerts] = useState<TelemetryAlert[]>([])
  const seenEventIds = useRef(new Set<string>())
  const eventIdOrder = useRef<string[]>([])
  const seenAlertIds = useRef(new Set<string>())
  const alertIdOrder = useRef<string[]>([])

  const handleEvent = useCallback((event: TelemetryEvent) => {
    if (!remember(event.MessageId, seenEventIds.current, eventIdOrder.current, 2_000)) return
    // Shared across every device, so this is ~50 points each at 4 devices.
    setEvents((prev) => [...prev.slice(-199), event])
  }, [])

  const handleAlert = useCallback((alert: TelemetryAlert) => {
    if (!remember(alert.MessageId, seenAlertIds.current, alertIdOrder.current, 500)) return
    setAlerts((prev) => [alert, ...prev.slice(0, 9)]) // Keep top 10
  }, [])

  // Kafka API updates happen here
  useTelemetry(handleEvent, handleAlert)

  return (
    <div className="dashboard">
      <h1>Industrial Control Panel</h1>
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

// Kafka and the outbox are both at-least-once. Keep a bounded identity window so an ambiguous
// ACK can replay a message without drawing the same point or alert twice in the live UI.
function remember(id: string, seen: Set<string>, order: string[], capacity: number): boolean {
  if (!id || seen.has(id)) return false

  seen.add(id)
  order.push(id)

  if (order.length > capacity) {
    const expired = order.shift()
    if (expired !== undefined) seen.delete(expired)
  }

  return true
}
