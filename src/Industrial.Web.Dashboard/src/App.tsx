import { useState, useCallback, useRef } from 'react'
import type { TelemetryEvent, TelemetryAlert } from './types/telemetry'
import { useTelemetry, type ConnectionStatus } from './hooks/useTelemetry'
import { TelemetryChart } from './features/dashboard/TelemetryChart'
import { EquipmentPanel } from './features/dashboard/EquipmentPanel'
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
  const [hiddenEquipment, setHiddenEquipment] = useState<Set<string>>(new Set())

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

  const toggleEquipment = useCallback((equipmentId: string) => {
    setHiddenEquipment((prev) => {
      const next = new Set(prev)
      if (next.has(equipmentId)) next.delete(equipmentId)
      else next.add(equipmentId)
      return next
    })
  }, [])

  // Kafka API updates happen here
  useTelemetry(handleEvent, handleAlert, setStatus)

  // The filter hides chart lines only. Alerts stay global so unticking a noisy
  // device can never silently hide its anomalies.
  const visibleEvents =
    hiddenEquipment.size === 0
      ? events
      : events.filter((event) => !hiddenEquipment.has(event.EquipmentId))

  return (
    <div className="dashboard">
      <header className="dashboard-header">
        <h1>Industrial Control Panel</h1>
        <StatusBadge status={status} lastMessageAt={lastMessageAt} />
      </header>
      <div className="grid">
        <section>
          <TelemetryChart data={visibleEvents} />
          <EquipmentPanel events={events} hidden={hiddenEquipment} onToggle={toggleEquipment} />
        </section>
        <aside>
          <AlertsList alerts={alerts} />
        </aside>
      </div>
    </div>
  )
}

export default App
