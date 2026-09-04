import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'
import type { TelemetryEvent, TelemetryAlert } from '../types/telemetry'

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting'

export function useTelemetry(
    onEvent?: (data: TelemetryEvent) => void,
    onAlert?: (data: TelemetryAlert) => void,
    onStatus?: (status: ConnectionStatus) => void,
) {
    const eventRef = useRef(onEvent)
    const alertRef = useRef(onAlert)
    const statusRef = useRef(onStatus)

    // Update box every render so SignalR listener always pulls latest callbacks
    useEffect(() => {
        eventRef.current = onEvent
        alertRef.current = onAlert
        statusRef.current = onStatus
    })

    useEffect(() => {
        // Production routes the static site and API through one origin. Development can still
        // override it because Vite and the API normally use different localhost ports.
        const apiUrl = (import.meta.env.VITE_API_URL || window.location.origin).replace(/\/$/, '')

        const connection = new HubConnectionBuilder()
            .withUrl(`${apiUrl}/telemetryHub`, {
                transport: HttpTransportType.WebSockets,
                skipNegotiation: true,
            })
            .configureLogging(LogLevel.Information)
            .withAutomaticReconnect({
                // The built-in default stops after four tries. A cloud outage can be much
                // longer, so cap the delay without ever giving up on an open dashboard.
                nextRetryDelayInMilliseconds: ({ previousRetryCount }) =>
                    Math.min(1_000 * (2 ** Math.min(previousRetryCount, 5)), 30_000),
            })
            .build()

        let disposed = false
        let retryTimer: number | undefined
        let retryDelayMs = 1_000

        connection.on('telemetry_events', (payload: string) => {
            const parsed = parse(payload, isTelemetryEvent)
            if (parsed) eventRef.current?.(parsed)
        })

        connection.on('telemetry_alerts', (payload: string) => {
            const parsed = parse(payload, isTelemetryAlert)
            if (parsed) alertRef.current?.(parsed)
        })

        connection.onreconnecting(() => {
            statusRef.current?.('reconnecting')
        })

        connection.onreconnected(() => {
            statusRef.current?.('connected')
        })

        // With an unbounded retry policy onclose only fires on stop(), but a closed
        // connection that nobody stopped means the retry state machine died: report
        // it and restart the loop rather than freezing on a dead socket.
        connection.onclose(() => {
            if (disposed) return
            statusRef.current?.('connecting')
            void start()
        })

        // Automatic reconnect starts only after one successful connection. This loop covers the
        // common Compose race where the browser loads before Web.Api has finished starting.
        const start = async () => {
            statusRef.current?.('connecting')
            try {
                await connection.start()
                retryDelayMs = 1_000
                statusRef.current?.('connected')
            } catch (error) {
                if (disposed) return
                console.error('SignalR initial connection failed; retrying', error)
                retryTimer = window.setTimeout(() => { void start() }, retryDelayMs)
                retryDelayMs = Math.min(retryDelayMs * 2, 30_000)
            }
        }

        void start()

        return () => {
            disposed = true
            if (retryTimer !== undefined) window.clearTimeout(retryTimer)
            void connection.stop()
        }
    }, []) // Empty array = One connection for life of component
}

// A throw inside a SignalR handler kills the whole connection, so one bad payload
// would take the dashboard down until it reconnects.
function parse<T>(payload: string, isExpected: (value: unknown) => value is T): T | null {
    try {
        const value: unknown = JSON.parse(payload)
        if (isExpected(value)) return value
        console.error('Discarding payload with the wrong telemetry shape', value)
        return null
    } catch {
        console.error('Discarding unreadable payload', payload)
        return null
    }
}

// Exported for unit tests: the dashboard is the last dedupe and validation boundary,
// so its shape guards are a contract, not an implementation detail.
export function isTelemetryEvent(value: unknown): value is TelemetryEvent {
    if (!isRecord(value)) return false
    return hasIdentityAndEventTime(value)
        && isFiniteNumber(value.SequenceNumber)
        && Number.isInteger(value.SequenceNumber)
        && value.SequenceNumber > 0
        && typeof value.ReceivedAt === 'string'
        && !Number.isNaN(Date.parse(value.ReceivedAt))
        && isFiniteNumber(value.EngineTemperature)
        && isFiniteNumber(value.OilPressure)
}

export function isTelemetryAlert(value: unknown): value is TelemetryAlert {
    if (!isRecord(value)) return false
    return hasIdentityAndEventTime(value)
        && isFiniteNumber(value.EngineTemperature)
        && typeof value.Diagnostics === 'string'
}

function hasIdentityAndEventTime(value: Record<string, unknown>): boolean {
    return typeof value.MessageId === 'string'
        && value.MessageId.length > 0
        && typeof value.EquipmentId === 'string'
        && value.EquipmentId.length > 0
        && typeof value.OccurredAt === 'string'
        && !Number.isNaN(Date.parse(value.OccurredAt))
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null
}

function isFiniteNumber(value: unknown): value is number {
    return typeof value === 'number' && Number.isFinite(value)
}
