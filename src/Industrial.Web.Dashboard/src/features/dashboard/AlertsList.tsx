import { AgGridReact } from 'ag-grid-react'
import type { ColDef } from 'ag-grid-community'
import type { TelemetryAlert } from '../../types/telemetry'

interface Props {
  alerts: TelemetryAlert[]
}

const colDefs: ColDef<TelemetryAlert>[] = [
  { field: 'EquipmentId', headerName: 'Equipment', width: 150 },
  { field: 'Diagnostics', headerName: 'AI Advice', flex: 1 },
]

export function AlertsList({ alerts }: Props) {
  return (
    <div style={{ height: '400px', width: '100%' }}>
      <AgGridReact rowData={alerts} columnDefs={colDefs} />
    </div>
  )
}
