import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { TelemetryEvent, TelemetryAlert } from '../types/telemetry'

export function useTelemetry(
    onEvent?: (data: TelemetryEvent) => void,
    onAlert?: (data: TelemetryAlert) => void,
) {
    const eventRef = useRef(onEvent)
    const alertRef = useRef(onAlert)

    // Update box every render so SignalR listener always pulls latest callbacks
    useEffect(() => {
        eventRef.current = onEvent
        alertRef.current = onAlert
    })

    useEffect(() => {
        const apiUrl = import.meta.env.VITE_API_URL
        if (!apiUrl) {
            throw new Error('VITE_API_URL is not set. Vite bakes it in at build time.')
        }

        const connection = new HubConnectionBuilder()
            .withUrl(`${apiUrl}/telemetryHub`)
            .configureLogging(LogLevel.Information)
            .withAutomaticReconnect()
            .build()

        connection.on('telemetry_events', (payload: string) => {
            const parsed = parse<TelemetryEvent>(payload)
            if (parsed) eventRef.current?.(parsed)
        })

        connection.on('telemetry_alerts', (payload: string) => {
            const parsed = parse<TelemetryAlert>(payload)
            if (parsed) alertRef.current?.(parsed)
        })

        connection.start().catch(console.error)

        return () => { connection.stop() }
    }, []) // Empty array = One connection for life of component
}

// A throw inside a SignalR handler kills the whole connection, so one bad payload
// would take the dashboard down until it reconnects.
function parse<T>(payload: string): T | null {
    try {
        return JSON.parse(payload) as T
    } catch {
        console.error('Discarding unreadable payload', payload)
        return null
    }
}
