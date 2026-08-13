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
        const apiUrl = import.meta.env.VITE_API_URL || 'http://localhost:5090'
        const connection = new HubConnectionBuilder()
            .withUrl(`${apiUrl}/telemetryHub`)
            .configureLogging(LogLevel.Information)
            .withAutomaticReconnect()
            .build()

        connection.on('telemetry_events', (payload: string) => {
            eventRef.current?.(JSON.parse(payload))
        })

        connection.on('telemetry_alerts', (payload: string) => {
            alertRef.current?.(JSON.parse(payload))
        })

        connection.start().catch(console.error)

        return () => { connection.stop() }
    }, []) // Empty array = One connection for life of component
}
