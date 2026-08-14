// No giant package block thanks to ImplicitUsings
using System.Net.Http.Json;

// This is a console application, we reach for Env directly with no builder
var baseUrl =
    Environment.GetEnvironmentVariable("Ingestion__ApiUrl")
    ?? throw new InvalidOperationException("Missing required environment variable 'Ingestion__ApiUrl'.");
var API_URL = $"{baseUrl}/api/telemetry";

using var client = new HttpClient();
var timer = new PeriodicTimer(TimeSpan.FromSeconds(2)); // ticks every 2s
int count = 0;

Console.WriteLine($"Industrial Emulator Started. Sending data to {API_URL}...");

while (await timer.WaitForNextTickAsync())
{
    count++;

    bool isHardwareFailure = count % 10 == 0; // Every 10th msg: Sensor error
    bool isOverHeating = count % 7 == 0; // Every 7th msg: Dangerous heat

    // Shared meaning thread safe
    string equipmentId = $"EQ-{Random.Shared.Next(1, 5)}";

    double temp;

    if (isHardwareFailure)
        // Sentinel value for "Broken Sensor"
        // (Sentinel values are markers for end of stream, terminate loop, missing value)
        temp = -999.0;
    else if (isOverHeating)
        temp = 245.0; // High value
    else
        // NextDouble [0.0, 1.0]
        temp = Random.Shared.NextDouble() * (110 - 70) + 70; // temp [70, 110]

    var oilPressure = Random.Shared.NextDouble() * (60 - 30) + 30; // [60, 30]

    var data = new TelemetryDto(equipmentId, temp, oilPressure);

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
