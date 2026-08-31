using Confluent.Kafka;
using FluentValidation;
using Industrial.Shared;
using Industrial.Ingestion.Api.Configuration;
using Industrial.Ingestion.Api.Features.Ingestion;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ---------------------------------------------------------------------------------
// The cloud end of the store-and-forward path.
//
//   ┌──────────────┐   200 readings, one call ┌───────────────┐
//   │ edge-gateway │ ────── gRPC:8081 ──────► │ ingestion-api │
//   │              │ ◄──── MessageIds ─────── │               │
//   └──────────────┘                          └───────┬───────┘
//                          one message per reading    │
//                                key = EquipmentId    ▼
//                                        ┌─────────────────────────┐
//                                        │ Kafka  telemetry-events │
//                                        └─────────────────────────┘
//
// Keying by EquipmentId puts a machine's readings on one kafka partition.
// The reply sorts every id the gateway sent into:
//
//   accepted   the broker acknowledged the write
//   rejected   this service's validator refused it, so Kafka never saw it
//   neither    still the gateway's, and it sends it again
//
// POST /api/telemetry is a debugging door onto the same topic.
// ---------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// Validated at boot, fails fast (config pecking order: docs/NET.md)
builder
    .Services.AddOptions<OTelOptions>()
    .Bind(builder.Configuration.GetSection(OTelOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var kafka = builder.Configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>()!;

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otel.ServiceName)) // resouce => tel metadata
    .WithLogging(logging => logging.AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint)))
    .WithMetrics(metrics =>
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
    .WithTracing(tracing =>
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

builder.Services.AddValidatorsFromAssemblyContaining<TelemetryValidator>();

// Setup Kafka
builder.Services.AddSingleton(sp => // service provider
{
    // Get the .NET logger for the Kafka producer<Key, Value>
    // (loggers can only write, not read)
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();

    // Set here so the delivery guarantee reads off one screen instead of librdkafka's defaults
    // string/string keys and values get the built-in serializers for free
    var config = new ProducerConfig
    {
        BootstrapServers = kafka.BootstrapServers,

        // Every in-sync replica holds the write before we answer
        // Acks.Leader answers sooner and loses the batch when the leader dies before its followers
        Acks = Acks.All,

        // Defaults to FALSE, and without it a retry can duplicate and reorder within a partition
        // Caps in-flight requests at 5, which is what makes launching 200 produces at once safe
        EnableIdempotence = true,

        // How long librdkafka holds a request open to fill it (0 sends the first reading alone)
        LingerMs = 20,

        // Has to sit under the gateway's Uploader:UploadTimeoutSeconds of 30
        // The 5 minute default outlives that deadline by 4.5, holding a batch nobody waits for
        MessageTimeoutMs = 20_000,

        // A ceiling on attempts, MessageTimeoutMs is what actually stops the retrying
        MessageSendMaxRetries = 10,

        // The payload is JSON, so Zstd over Lz4 for the ratio (both ends are Confluent.Kafka)
        CompressionType = CompressionType.Zstd,

        // Nothing pre-creates the topic, so this is what lets `make upd` work from empty
        // The broker invents it with default partitions and replication, which is the thing to fix
        AllowAutoCreateTopics = true,

        MetadataMaxAgeMs = 5000, // Force refresh every 5s for fast startup
    };

    // Multiple msg types all use the same producer, not multiple
    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) => logger.LogError("Kafka Producer Error: {Reason}", e.Reason))
        .Build();
});

// Inheriting TelemetryIngestionBase is not enough on its own: without AddGrpc plus the
// MapGrpcService below, a gateway connects and gets UNIMPLEMENTED.
// A whole batch is one message, so AddGrpc's 4 MB MaxReceiveMessageSize default is the
// real ceiling on Uploader:BatchSize: ~84 bytes a reading puts it near 50,000.
// https://learn.microsoft.com/en-us/aspnet/core/grpc/configuration
builder.Services.AddGrpc();

builder.Services.AddOpenApi();

// After this, you can't register services anymore (immutable)
var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(() =>
{
    // cleanup Kafka
    var producer = app.Services.GetRequiredService<IProducer<string, string>>();
    producer.Flush(TimeSpan.FromSeconds(5));
    producer.Dispose();
});

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Register the POST /api/telemetry route (legacy/manual-test path)
app.MapIngestionEndpoints();

// Register the gRPC UploadTelemetry service (the durable path, used by the Edge Gateway).
// Bound to the Http2 Kestrel endpoint configured in appsettings.json -- see the note
// there about why plaintext gRPC needs its own port.
app.MapGrpcService<TelemetryService>();

app.Run();
