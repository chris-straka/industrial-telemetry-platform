// ImplicitUsings is hiding some packages
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

// Pretends to be physical equipment, only talks to Edge Gateway (doesn't know cloud exists)

// Microsoft.Extensions.Hosting turns the console app into a Generic Host
// This brings in DI (builder.Services), Configuration (appsettings.json), Logging
var builder = Host.CreateApplicationBuilder(args);

// Options will grab a chunk from IConfiguration and create types + validate it.
// Linux env vars can't contain ':' but builder.Configuration will foo__bar -> foo:bar
// .Bind() uses reflection to get class members at runtime
// Binding "produces a T from a key-value config"
builder
    .Services.AddOptions<OTelOptions>()
    .Bind(builder.Configuration.GetSection(OTelOptions.Section))
    .ValidateDataAnnotations() // validate on use
    .ValidateOnStart(); // validate at start of runtime
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

// DI is only available after builder.Build() and my Options above only run/validate after that
// To use them earlier (like to register services like Otel) I need this
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var emulator = builder.Configuration.GetSection(EmulatorOptions.Section).Get<EmulatorOptions>()!;

// Otel
// builder.Services is the IServiceCollection not the IServiceProvider (DI container)
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(otel.ServiceName))
    .WithLogging(log => log.AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint)))
    .WithMetrics(m =>
        m.AddMeter(SensorMetrics.MeterName)
            // Tracks outgoing HTTP metrics (the POST -> edge gateway)
            // This will tell us if the edge gateway is slow
            .AddHttpClientInstrumentation()
            // If the transmission loop can't get scheduled, it stops draining and drops readings
            // This will emit thread-pool queue length, thread count, GC pause stats
            // This will tell us if the runtime is slow
            .AddRuntimeInstrumentation()
            // This is where we send our otel data every 60s
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
    .WithTracing(trace =>
        trace
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

// The DI container registers things by type and not by name (DI Keys are types)
// AddHttpClient<T> registers T as transient but AddHttpClient(NAME) registers nothing.
// Injecting a transient into a singleton would leave it trapped in the singleton's lifetime (app lifetime)

// HttpClient is a thin wrapper over an HttpMessageHandler chain (Polly on top, Sockets at the bottom).
// A captured client holds onto one chain forever, and DNS is only resolved when a new connection opens.
// So, the undisposed client keeps talking to the gateway's old IP indefinitely even if it changed.
builder
    .Services.AddHttpClient(
        TelemetryClient.Name,
        (serviceProvider, client) =>
            client.BaseAddress = new Uri(
                // this grabs it from the DI container (no earlier .Get<T> necessary)
                serviceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value.Url
            )
    )
    .AddStandardResilienceHandler(); // Adds Polly

// Polly adds retries with exponential backoff + jitter and a circuit breaker.
// Some sensors won't have this, but some do have backoff + jitter at the firmware level.
//
// Retries reduce loss during short gateway interruptions, but this emulator is deliberately
// best-effort: after the bounded policy finishes, it discards the dequeued reading. The durable
// at-least-once contract begins only after the gateway replies 202. A retry happens when an
// outcome is ambiguous (the write may have worked even though the response was lost), so the
// gateway still needs MessageId idempotency.

// This is a decoupling buffer, not a durability buffer
// It prevents the emulator from waiting on the network (if it's slow or down)
// The emulator saves to RAM, while the gateway persists in storage
//
// A Channel is a thread-safe, async, in-memory queue
// FullMode decides what happens when it's full (Wait, DropOldest, DropNewest)
// FullMode.Wait means the channel will not evict an older reading. Acquisition deliberately uses
// TryWrite rather than WriteAsync, so a full buffer drops the new sample instead of slowing the
// simulated hardware loop.
builder.Services.AddSingleton(
    // Bounded to limit RAM usage (sensor is RAM limited)
    Channel.CreateBounded<TelemetryDto>(
        new BoundedChannelOptions(emulator.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false, // one writer per simulated device
        }
    )
);

// Singleton because a meter and its instruments are process-wide state.
builder.Services.AddSingleton<SensorMetrics>();

// These two are registered as singletons (careful what you inject into them)
// Writes sensor readings, never waits for transmission
// Transmission reads, never waits on acquisition
builder.Services.AddHostedService<AcquisitionWorker>();
builder.Services.AddHostedService<TransmissionWorker>();

var host = builder.Build();
host.Run(); // Blocks and listens for SIGTERM (Docker) or Ctrl+C

public static class TelemetryClient
{
    public const string Name = "telemetry";
    public const string Route = "/api/local/telemetry";
}

/// <summary>
/// LOOP 1: acquisition. Generates readings and never touches the network.
/// Acts as the producer in the Producer/Consumer (P/C pattern).
/// The acquisition rate is DeviceCount / IntervalSeconds (4 / 2 = 2 readings/sec)
///
/// IOptions&lt;T&gt; can only be injected from a built service provider (builder.Build())
/// </summary>
public class AcquisitionWorker(
    Channel<TelemetryDto> channel,
    IOptions<EmulatorOptions> emulatorOptions,
    SensorMetrics metrics,
    ILogger<AcquisitionWorker> logger
) : BackgroundService
{
    private readonly EmulatorOptions options = emulatorOptions.Value;

    // Each docker container runs multiple devices with 2 sensors
    // I couldn't 1:1 container:device because .NET Runtime is 50-100MB (500MB-1GB for 10 devices)
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 0, 4, 8 (device count = 4)
        var firstDevice = options.ReplicaId!.Value * options.DeviceCount;

        // Range(start, count) not (start, end)
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
        // Everything in here is per device, not per docker container

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));

        // Monotonic and used by the cloud to detect gaps (900 readings & 950 seq = 50 vanished)
        // Restarting the emulator resets seq, real firmware would persist seq.
        long seq = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            seq++;

            // Uses seq so each device has the same fault cadence
            bool isHardwareFailure = seq % 10 == 0;
            bool isOverHeating = seq % 7 == 0;

            // NextDouble() [0.0, 1.0], temp [70, 110]
            double temp =
                isHardwareFailure ? -999.0
                : isOverHeating ? 245.0
                : Random.Shared.NextDouble() * (110 - 70) + 70;

            // oilPressure [30, 60]
            double oilPressure = Random.Shared.NextDouble() * (60 - 30) + 30;

            // MessageId prevents duplicates (e.g., from a retry request -> gateway)
            // Event time is when the event happened, processing time is when it was received
            var data = new TelemetryDto(
                // v4 (Guid.NewGuid()) has random keys while v7 has time-based keys
                // Random keys dirty a different page in PG's DB index for every insert
                // If an arbitary page is full and a new random key needs to go in there
                // PG's DB index will split that page in half to add that random key
                // Time-based keys all land on the index's rightmost page, which stays cached.
                MessageId: Guid.CreateVersion7().ToString(),
                EquipmentId: equipmentId,
                SequenceNumber: seq,
                OccurredAt: DateTimeOffset.UtcNow, // Event time
                EngineTemperature: temp,
                OilPressure: oilPressure
            );

            metrics.Acquired.Add(1);

            // Non-blocking by design, if the buffer is full, drop the reading.
            if (!channel.Writer.TryWrite(data))
                metrics.Dropped.Add(1);
        }
    }
}

/// <summary>
/// LOOP 2: Drains the buffer (channel) to send to the gateway.
/// </summary>
public class TransmissionWorker(
    Channel<TelemetryDto> channel,
    IHttpClientFactory httpClientFactory,
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
                // CreateClient() returns a new HttpClient but not a new handler chain.
                // The factory pools chains for TelemetryClient.Name that the client uses.
                // It then expires each chain after HandlerLifetime (2 mins by default).
                // This lets it detect DNS changes every 2 mins (DNS is only stale for 2 mins)
                var client = httpClientFactory.CreateClient(TelemetryClient.Name);

                // The stoppingToken will abort the HTTP call on shutdown
                // But that only stops the client from waiting for a response
                // This means the result on the server is ambiguous (EffO protects)
                using var response = await client.PostAsJsonAsync(
                    TelemetryClient.Route,
                    data,
                    stoppingToken
                );

                // Only the gateway's 202 carries the durable-ownership promise. Treat an
                // unexpected 2xx from a proxy or misrouted endpoint as a dropped delivery rather
                // than quietly widening the contract to every successful-looking response.
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
                    // BACKPRESSURE -> consumer telling producer to slow down
                    // Polly reads/respects the consumer's Retry-After response header
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
                // Two different paths land here (circuit breaker related)
                // 1. Polly exhausted its retries (timeout, 5xx, conn reset)
                // 2. The circuit is open from earlier failures (didn't even try)
                metrics.Failed.Add(1);
                metrics.DeliveryDropped.Add(1);
                logger.LogError(
                    "[DELIVERY DROPPED] Gateway did not accept the reading after bounded retries: {Message}",
                    ex.Message
                );
                // If 2. it will eventually send a half-open PROBE, not a retry to close the circuit
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
