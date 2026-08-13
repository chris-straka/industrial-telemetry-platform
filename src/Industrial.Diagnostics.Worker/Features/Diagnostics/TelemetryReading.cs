namespace Industrial.Diagnostics.Worker.Features.Diagnostics;

public class TelemetryReading
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EquipmentId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }
    public bool IsAnomaly { get; set; }
}
