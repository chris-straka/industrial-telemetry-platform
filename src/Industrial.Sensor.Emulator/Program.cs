using System.Net.Http.Json;

using var client = new HttpClient();
var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
int count = 0;

var baseUrl = Environment.GetEnvironmentVariable("Ingestion__ApiUrl");
var API_URL = $"{baseUrl}/api/telemetry";

Console.WriteLine($"Industrial Emulator Started. Sending data to {API_URL}...");

while (await timer.WaitForNextTickAsync())
{
    count++;

    bool isHardwareFailure = count % 10 == 0; // Every 10th msg: Sensor error
    bool isOverHeating = count % 7 == 0; // Every 7th msg: Dangerous heat

    string equipmentId = $"EQ-{Random.Shared.Next(1, 5)}";

    double temp;

    if (isHardwareFailure)
        temp = -999.0; // Sentinel value for "Broken Sensor"
    else if (isOverHeating)
        temp = 245.0; // High value
    else
        temp = Random.Shared.NextDouble() * (110 - 70) + 70; // Normal operating range

    var data = new TelemetryDto(equipmentId, temp, Random.Shared.NextDouble() * (60 - 30) + 30);

    try
    {
        var response = await client.PostAsJsonAsync(API_URL, data);

        if (response.IsSuccessStatusCode)
        {
            string status = isOverHeating ? "[ALARM]" : "[OK]";
            Console.WriteLine($"{status} Sent {data.EquipmentId}: {data.EngineTemperature:F1}°C");
        }
        else
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine(
                $"[REJECTED] {data.EquipmentId} (Value: {data.EngineTemperature}): {error}"
            );
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CRITICAL] API is down: {ex.Message}");
    }
}

public record TelemetryDto(string EquipmentId, double EngineTemperature, double OilPressure);
