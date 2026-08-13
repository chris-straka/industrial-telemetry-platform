export interface TelemetryEvent {
  EquipmentId: string
  EngineTemperature: number
  OilPressure: number
}

export interface TelemetryAlert {
  EquipmentId: string
  Diagnostics: string
}
