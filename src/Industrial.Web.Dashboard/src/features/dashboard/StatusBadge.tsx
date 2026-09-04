import { useEffect, useState } from 'react'
import type { ConnectionStatus } from '../../hooks/useTelemetry'

interface Props {
  status: ConnectionStatus
  lastMessageAt: number | null
}

// Readings arrive every couple of seconds per device in the demo fleet, so ten
// quiet seconds on a live socket means the pipeline behind it stopped moving.
const STALE_AFTER_MS = 10_000

export function StatusBadge({ status, lastMessageAt }: Props) {
  const [now, setNow] = useState<number | null>(lastMessageAt)

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1_000)
    return () => window.clearInterval(timer)
  }, [])

  const ageMs =
    lastMessageAt === null || now === null ? null : Math.max(0, now - lastMessageAt)
  const stale = status === 'connected' && (ageMs === null || ageMs > STALE_AFTER_MS)

  const label =
    status === 'connected' && !stale
      ? 'Live'
      : status === 'reconnecting'
        ? 'Reconnecting…'
        : status === 'connected'
          ? `Stale${ageMs === null ? '' : ` · ${Math.round(ageMs / 1_000)}s`}`
          : 'Connecting…'

  const tone = label === 'Live' ? 'live' : label.startsWith('Stale') ? 'stale' : 'busy'

  return (
    <p className={`status status-${tone}`} role="status">
      <span className="status-dot" aria-hidden="true" />
      {label}
    </p>
  )
}
