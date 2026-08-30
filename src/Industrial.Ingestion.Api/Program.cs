using Confluent.Kafka;
using FluentValidation;
using Industrial.Shared;
using Microsoft.Extensions.Options;
using Industrial.Ingestion.Api.Configuration;
using Industrial.Ingestion.Api.Features.Ingestion;
using Microsoft.Extensions.Diagnostics.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configuration layers lowest to highest: appsettings.json, appsettings.ENV.json, user
// secrets, environment variables, CLI args. Env vars cannot contain a colon, so .NET
// rewrites Kafka__BootstrapServers to Kafka:BootstrapServers at load time -- which is
// why every key in this repo is READ with a colon.
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

// Needed during registration, before the container exists.
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var kafka = builder.Configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>()!;

// .NET registers every dependency up front against builder.Services, rather than
// discovering them from annotations on the classes themselves.
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
    // Get the .NET logger for the Kafka producer<Key, Value> (loggers can only write, not read)
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();

    // TODO: production needs more of ProducerConfig (acks, idempotence, linger).
    // string/string keys and values get the built-in serializers for free.
    var config = new ProducerConfig
    {
        BootstrapServers = kafka.BootstrapServers,
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
