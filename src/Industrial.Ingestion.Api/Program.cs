using Confluent.Kafka;

using FluentValidation;

using Industrial.Ingestion.Api.Configuration;
using Industrial.Ingestion.Api.Features.Ingestion;
using Industrial.Ingestion.Api.Infrastructure;
using Industrial.Shared;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// --------------------------------------------------------------------------
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
// POST /api/debug/telemetry is a debugging door onto the same topic.
// --------------------------------------------------------------------------

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
builder
    .Services.AddOptions<TransportSecurityOptions>()
    .Bind(builder.Configuration.GetSection(TransportSecurityOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var kafka = builder.Configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>()!;
var transportSecurity =
    builder.Configuration.GetSection(TransportSecurityOptions.Section)
        .Get<TransportSecurityOptions>() ?? new TransportSecurityOptions();

// Assigned after the container builds (see below); the Kestrel handshake callback only
// runs once the server starts, and a null here fails closed.
ReloadingClientCertificatePolicy? reloadingPolicy = null;

if (transportSecurity.Enabled)
{
    var grpcEndpoint = builder.Configuration["Kestrel:Endpoints:Grpc:Url"];
    if (grpcEndpoint is null || !grpcEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The gRPC Kestrel endpoint must use https:// when transport security is enabled."
        );
    }

    // Fail fast on unreadable trust material, exactly as before. The snapshot is then
    // wrapped in a reloading policy so revoking a fingerprint is a file edit, not a restart.
    var initialPolicy = ClientCertificatePolicy.Load(
        transportSecurity.TrustedClientCaPath,
        transportSecurity.AllowedClientFingerprintsPath
    );
    builder.Services.AddSingleton(sp => new ReloadingClientCertificatePolicy(
        initialPolicy,
        transportSecurity.TrustedClientCaPath,
        transportSecurity.AllowedClientFingerprintsPath,
        TimeSpan.FromSeconds(transportSecurity.AllowlistReloadIntervalSeconds),
        sp.GetRequiredService<ILogger<ReloadingClientCertificatePolicy>>()
    ));

    builder.WebHost.ConfigureKestrel(options =>
        options.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) =>
                reloadingPolicy?.IsAllowed(certificate) ?? false;
        })
    );
}

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

// Gateway's batch (200 readings) != Kafka's batches
// One batch of 200 readings -> 200 kafka msgs (if none rejected)
// Each kafka msg is key -> EquipmentId, value -> JSON envelope
// These they get batched per partition into record batches

// Setup Kafka
builder.Services.AddSingleton(sp => // service provider
{
    // Loggers can only write, not read
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();

    // Hover each of these props for tooltip (librdkafka runs locally)
    var config = new ProducerConfig
    {
        BootstrapServers = kafka.BootstrapServers,
        // Broker identity is verified against the dev CA whenever UseTls is on.
        // Hostname verification stays at its default, so the broker certificate SAN
        // list must cover every advertised listener the clients use.
        SecurityProtocol = kafka.UseTls ? SecurityProtocol.Ssl : SecurityProtocol.Plaintext,
        SslCaLocation = kafka.UseTls ? kafka.SslCaLocation : null,
        SslCertificateLocation = kafka.UseTls ? kafka.SslCertificateLocation : null,
        SslKeyLocation = kafka.UseTls ? kafka.SslKeyLocation : null,
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = 20,
        MessageTimeoutMs = 20_000,
        CompressionType = CompressionType.Zstd,
        // A typo must fail delivery and leave the gateway row retryable, not create a silent
        // parallel topic that no consumer reads.
        AllowAutoCreateTopics = false,
        // MetadataMaxAgeMs = 5000,
    };

    // You want one producer for each message type (only one here)
    // <string, string> will use Kafka's default UTF-8 serializers
    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) => logger.LogError("Kafka Producer Error: {Reason}", e.Reason))
        .Build();
});

builder.Services.AddSingleton<IAdminClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
    return new AdminClientBuilder(
        new AdminClientConfig
        {
            BootstrapServers = options.BootstrapServers,
            SecurityProtocol = options.UseTls ? SecurityProtocol.Ssl : SecurityProtocol.Plaintext,
            SslCaLocation = options.UseTls ? options.SslCaLocation : null,
            SslCertificateLocation = options.UseTls ? options.SslCertificateLocation : null,
            SslKeyLocation = options.UseTls ? options.SslKeyLocation : null,
            AllowAutoCreateTopics = false,
        }
    ).Build();
});
builder
    .Services.AddHealthChecks()
    .AddCheck<KafkaTopicHealthCheck>(
        "kafka-events-topic",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(KafkaOptions.MaximumReadinessTimeoutSeconds + 1)
    );

builder.Services.AddGrpc();
builder.Services.AddOpenApi();
var app = builder.Build();

// Start the reload timer now that logging exists. The Kestrel callback above already
// closes over this variable, and the server (and its first handshake) starts after this.
if (transportSecurity.Enabled)
    reloadingPolicy = app.Services.GetRequiredService<ReloadingClientCertificatePolicy>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapDebugTelemetryEndpoint();
}

app.MapGrpcService<TelemetryService>();
app.MapGet("/health", () => Results.Ok());
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
    }
);

app.Run();
