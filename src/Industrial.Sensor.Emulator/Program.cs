using System.Diagnostics;
using System.Net.Http.Json;
using System.Threading.Channels;

using Industrial.Sensor.Emulator.Configuration;
using Industrial.Sensor.Emulator.Infrastructure;
using Industrial.Shared;

using Microsoft.Extensions.Options;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Simulates physical equipment. It talks only to the edge gateway and knows nothing of the cloud.
var builder = Host.CreateApplicationBuilder(args);

// Environment variables cannot contain ':', so configuration maps Foo__Bar to Foo:Bar.
builder
    .Services.AddOptions<OTelOptions>()
    .Bind(builder.Configuration.GetSection(OTelOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<EmulatorOptions>()
    .Bind(builder.Configuration.GetSection(EmulatorOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Bound directly because service registration runs before the service provider exists, and
// the validated options above are only available after Build().
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var emulator = builder.Configuration.GetSection(EmulatorOptions.Section).Get<EmulatorOptions>()!;
var gateway = builder.Configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>()!;

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(otel.ServiceName))
    .WithLogging(log => log.AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint)))
    .WithMetrics(m =>
        m.AddMeter(SensorMetrics.MeterName)
            // Shows whether the gateway is slow to answer.
            .AddHttpClientInstrumentation()
            // Thread-pool and GC stats. A starved transmission loop stops draining the channel,
            // which then drops readings.
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
    .WithTracing(trace =>
        trace
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

// Named clients, rather than a typed client injected into a singleton worker, let the factory
// rotate handler chains. A client captured for the app's lifetime keeps one chain and never
// re-resolves DNS if the gateway's address changes.
//
// Device certificates load here so a missing file fails at startup. An https gateway URL gives
// each device its own client and certificate. Plaintext uses one anonymous client for local runs.
var deviceCredentials = DeviceCredentials.LoadForShard(gateway, emulator);
builder.Services.AddSingleton(deviceCredentials);
if (deviceCredentials.UsesTls)
{
    foreach (var equipmentId in deviceCredentials.Certificates.Keys)
    {
        var deviceId = equipmentId;
        var deviceCertificate = deviceCredentials.Certificates[deviceId];
        builder
            .Services.AddHttpClient(
                TelemetryClient.NameFor(deviceId),
                (serviceProvider, client) =>
                    client.BaseAddress = new Uri(
                        serviceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value.Url
                    )
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                SensorTlsHandlerFactory.CreateDeviceHandler(
                    deviceCertificate,
                    deviceCredentials.TrustedRoot!
                )
            )
            .AddStandardResilienceHandler();
    }
}
else
{
    builder
        .Services.AddHttpClient(
            TelemetryClient.Name,
            (serviceProvider, client) =>
                client.BaseAddress = new Uri(
                    serviceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value.Url
                )
        )
        .AddStandardResilienceHandler();
}

// The standard resilience handler adds jittered exponential retries and a circuit breaker.
// Retries reduce loss during short gateway interruptions, but the emulator is best-effort. Once
// the policy gives up, the dequeued reading is discarded. At-least-once delivery starts only at
// the gateway's 202. A retry after a lost response can deliver a reading twice, so the gateway
// deduplicates on MessageId.

// An in-memory buffer that keeps acquisition from waiting on the network. It provides no
// durability; the gateway does. FullMode.Wait never evicts an older reading, and acquisition
// uses TryWrite rather than WriteAsync, so a full channel drops the new sample instead of
// slowing the simulated hardware loop. The bound limits RAM use.
builder.Services.AddSingleton(
    Channel.CreateBounded<TelemetryDto>(
        new BoundedChannelOptions(emulator.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false, // one writer per simulated device
        }
    )
);

builder.Services.AddSingleton<SensorMetrics>();

// Hosted services are singletons, so neither worker may capture a scoped dependency. The two
// loops share only the channel and never wait on each other.
builder.Services.AddHostedService<AcquisitionWorker>();
builder.Services.AddHostedService<TransmissionWorker>();

var host = builder.Build();
host.Run();

public static class TelemetryClient
{
    public const string Name = "telemetry";
    public const string Route = "/api/local/telemetry";

    // Each device gets its own named client when mTLS is on. The plaintext client uses Name.
    public static string NameFor(string equipmentId) => $"{Name}-{equipmentId}";
}

/// <summary>
/// Generates readings into the channel without touching the network.
/// </summary>
/// <remarks>
/// This is the producer side of the channel. Each replica produces DeviceCount / IntervalSeconds
/// readings per second.
/// </remarks>
public class AcquisitionWorker(
    Channel<TelemetryDto> channel,
    IOptions<EmulatorOptions> emulatorOptions,
    SensorMetrics metrics,
    ILogger<AcquisitionWorker> logger
) : BackgroundService
{
    private readonly EmulatorOptions options = emulatorOptions.Value;

    // Each container simulates several devices because one .NET runtime per device would cost
    // 50-100 MB each.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // With DeviceCount = 4, replicas start at EQ-0, EQ-4, EQ-8, and so on.
        var firstDevice = options.ReplicaId!.Value * options.DeviceCount;

        var devices = Enumerable
            .Range(firstDevice, options.DeviceCount)
            .Select(n => $"EQ-{n}")
            .ToArray();

        logger.LogInformation(
            "Acquisition started. Devices {First}..{Last} every {Interval}s.",
            devices[0],
            devices[^1],
            options.IntervalSeconds
        );

        await Task.WhenAll(devices.Select(id => RunDeviceAsync(id, stoppingToken)));
    }

    private async Task RunDeviceAsync(string equipmentId, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));

        // Monotonic per device so the cloud can detect gaps. A restart resets it; real firmware
        // would persist it.
        long seq = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            seq++;

            // Faults are keyed to seq so every device has the same fault cadence.
            bool isHardwareFailure = seq % 10 == 0;
            bool isOverHeating = seq % 7 == 0;

            // Normal temperature is uniform in [70, 110).
            double temp =
                isHardwareFailure ? -999.0
                : isOverHeating ? 245.0
                : Random.Shared.NextDouble() * (110 - 70) + 70;

            double oilPressure = Random.Shared.NextDouble() * (60 - 30) + 30;

            var data = new TelemetryDto(
                // Every hop deduplicates on MessageId. Version 7 GUIDs are time-ordered, so
                // Postgres inserts land on the index's rightmost, cached page. Random v4 keys
                // would touch a different page per insert and cause page splits.
                MessageId: Guid.CreateVersion7().ToString(),
                EquipmentId: equipmentId,
                SequenceNumber: seq,
                OccurredAt: DateTimeOffset.UtcNow,
                EngineTemperature: temp,
                OilPressure: oilPressure
            );

            metrics.Acquired.Add(1);

            // Never blocks. A full channel drops the reading.
            if (!channel.Writer.TryWrite(data))
                metrics.Dropped.Add(1);
        }
    }
}

/// <summary>
/// Drains the channel and posts each reading to the gateway.
/// </summary>
public class TransmissionWorker(
    Channel<TelemetryDto> channel,
    IHttpClientFactory httpClientFactory,
    DeviceCredentials deviceCredentials,
    SensorMetrics metrics,
    ILogger<TransmissionWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Transmission started.");

        await foreach (var data in channel.Reader.ReadAllAsync(stoppingToken))
        {
            var startedAt = Stopwatch.GetTimestamp();
            var outcome = "cancelled";

            try
            {
                // CreateClient returns a new HttpClient over a pooled handler chain. The factory
                // replaces chains after HandlerLifetime (two minutes by default), which bounds
                // how long a stale DNS answer is used. With mTLS each device has its own named
                // client, so the presented certificate matches the reading's equipment ID.
                var clientName = deviceCredentials.UsesTls
                    ? TelemetryClient.NameFor(data.EquipmentId)
                    : TelemetryClient.Name;
                var client = httpClientFactory.CreateClient(clientName);

                // Cancelling on shutdown only stops the client from waiting. The gateway may still
                // store the reading, and MessageId deduplication covers a later resend.
                using var response = await client.PostAsJsonAsync(
                    TelemetryClient.Route,
                    data,
                    stoppingToken
                );

                // Only the gateway's 202 means it durably owns the reading. Any other 2xx, for
                // example from a proxy or misrouted endpoint, counts as a dropped delivery.
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    outcome = "accepted";
                    metrics.Sent.Add(1);
                    string status = data.EngineTemperature > 200 ? "[ALARM]" : "[OK]";
                    logger.LogInformation(
                        "{Status} Sent {EquipmentId} #{Seq}: {Temp:F1}C",
                        status,
                        data.EquipmentId,
                        data.SequenceNumber,
                        data.EngineTemperature
                    );
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    outcome = "backpressure";
                    // The gateway buffer is full. The resilience handler already honored
                    // Retry-After during its retries.
                    metrics.Rejected.Add(1);
                    metrics.DeliveryDropped.Add(1);
                    logger.LogWarning(
                        "[BACKPRESSURE] Gateway buffer stayed full after bounded retries; dropping reading. Retry-After was {RetryAfter}",
                        response.Headers.RetryAfter?.ToString() ?? "unspecified"
                    );
                }
                else
                {
                    outcome = response.IsSuccessStatusCode ? "unexpected_success" : "rejected";
                    metrics.Rejected.Add(1);
                    metrics.DeliveryDropped.Add(1);
                    var error = await response.Content.ReadAsStringAsync(stoppingToken);
                    logger.LogWarning(
                        "[REJECTED] Gateway refused {EquipmentId}; dropping reading: {Error}",
                        data.EquipmentId,
                        error
                    );
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                outcome = "failed";
                // Either the retries were exhausted (timeout, 5xx, connection reset) or the
                // circuit was already open and no request was sent. An open circuit later lets
                // one half-open probe through to decide whether to close.
                metrics.Failed.Add(1);
                metrics.DeliveryDropped.Add(1);
                logger.LogError(
                    "[DELIVERY DROPPED] Gateway did not accept the reading after bounded retries: {Message}",
                    ex.Message
                );
            }
            finally
            {
                metrics.RecordDeliveryDuration(
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    outcome
                );
            }
        }
    }
}

public record TelemetryDto(
    string MessageId,
    string EquipmentId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    double EngineTemperature,
    double OilPressure
);
