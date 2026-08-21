// ImplicitUsings is hiding some of these packages
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Microsoft.Extensions.Hosting turns the console app into a "Generic Host"
// This gives it DI (builder.Services), Configuration (appsettings.json), Logging, Otel.
var builder = Host.CreateApplicationBuilder(args);

var serviceName =
    builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'OTel:ServiceName' configuration.");
var otelEndpoint =
    builder.Configuration["OTel:Endpoint"]
    ?? throw new InvalidOperationException("Missing 'OTel:Endpoint' configuration.");
var ingestionApiUrl =
    builder.Configuration["Ingestion__ApiUrl"]
    ?? throw new InvalidOperationException("Missing 'Ingestion__ApiUrl'");

// Otel
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithLogging(log => log.AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithTracing(trace =>
        trace
            .AddHttpClientInstrumentation() // Automatically traces HTTP calls to the API!
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint))
    );

// Thundering Herd Problem:
// If the ingestion API crashes, 5,000 sensors will throw an exception.
// Without jitter and retries, they will all wait 2s and hammer the API at the exact same ms, crashing it again.

// Polly fixes this using:
// 1. Exponential Backoff (keep waiting longer between each failure: 2s, 4s, 8s)
// 2. Jitter: the sensors backoff/fire at different rates, one waits 2.1s, another 2.7s
// This gives the ingestion API more time to recover

// For synchronous network calls (A -> B) where B is down and A is getting 1,000 reqs/s.
// A will eventually crash too (cascading failure) because the connection pool and fds (file descriptors) will max out
// Every request creates a TCP socket / fd, and the limit per process (aka per app) in Linux is 1024 or 4096 (ulimit -n)

// Polly fixes unresponsive services via the circuit breaker pattern (to prevent A from crashing)
// 1. Closed (Normal): Traffic flows freely.
// 2. Open (Tripped): If failures cross a threshold (e.g., 5 in a row), the circuit "opens" (rejects all new reqs)
// 3. Half-Open (Testing): After a cooldown (e.g., 30s), it lets one test request through.

// Create the HttpClient client
builder
    .Services.AddHttpClient<EmulatorWorker>(client => client.BaseAddress = new Uri(ingestionApiUrl))
    .AddStandardResilienceHandler(); // Add polly (Resilience)

// When you dispose of a socket it takes 60s to cleanup because it waits for delayed network packets
// AddHttpClient keeps a pool of client sockets open and reuses them (new HttpClient() creates a socket)

// Register the BackgroundService
builder.Services.AddHostedService<EmulatorWorker>();

// JSON is text heavy, requires repetitive parsing string->binary->string (CPU intensive)
// HTTP/1.1 also requires opening/closing connections or dealing with HOL blocking.
// So I'm using gRCP streams instead

var host = builder.Build();
host.Run(); // Blocks and listens for SIGTERM (Docker) or Ctrl+C

public class EmulatorWorker(HttpClient client, ILogger<EmulatorWorker> logger) : BackgroundService
{
    const string TelemetryRoute = "/api/telemetry";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Industrial Emulator Started.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        int count = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            count++;
            bool isHardwareFailure = count % 10 == 0;
            bool isOverHeating = count % 7 == 0;

            string equipmentId = $"EQ-{Random.Shared.Next(1, 5)}";
            double temp =
                isHardwareFailure ? -999.0
                : isOverHeating ? 245.0
                : Random.Shared.NextDouble() * (110 - 70) + 70; // NextDouble() [0.0, 1.0], temp [70, 110]
            double oilPressure = Random.Shared.NextDouble() * (60 - 30) + 30;

            var data = new TelemetryDto(equipmentId, temp, oilPressure);

            try
            {
                // Polly handles retries/circuit breakers under the hood.
                // stoppingToken ensures we abort the HTTP call if the app is shutting down.
                var response = await client.PostAsJsonAsync(TelemetryRoute, data, stoppingToken);

                if (response.IsSuccessStatusCode)
                {
                    string status = isOverHeating ? "[ALARM]" : "[OK]";
                    logger.LogInformation(
                        "{Status} Sent {Id}: {Temp:F1}°C",
                        status,
                        data.EquipmentId,
                        data.EngineTemperature
                    );
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync(stoppingToken);
                    logger.LogWarning("[REJECTED] {Id} : {Error}", data.EquipmentId, error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Polly retries transient errors automatically. If it still fails, it throws here.
                logger.LogError("[CRITICAL] API is down: {Message}", ex.Message);
            }
        }
    }
}

public record TelemetryDto(string EquipmentId, double EngineTemperature, double OilPressure);
