import { useState, useCallback } from 'react'
import type { TelemetryEvent, TelemetryAlert } from './types/telemetry'
import { useTelemetry } from './hooks/useTelemetry'
import { TelemetryChart } from './features/dashboard/TelemetryChart'
import { AlertsList } from './features/dashboard/AlertsList'
import './App.css'

function App() {
  // For Kafka
  const [events, setEvents] = useState<TelemetryEvent[]>([])
  const [alerts, setAlerts] = useState<TelemetryAlert[]>([])

  const handleEvent = useCallback((event: TelemetryEvent) => {
    setEvents((prev) => [...prev.slice(-19), event]) // Keep last 20
  }, [])

  const handleAlert = useCallback((alert: TelemetryAlert) => {
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
