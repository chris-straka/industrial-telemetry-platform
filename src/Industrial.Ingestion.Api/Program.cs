using Confluent.Kafka;
using FluentValidation;
using Industrial.Ingestion.Api.Features.Ingestion;
using Microsoft.Extensions.Diagnostics.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Builder.Configuration pulls from a layered hierarchy (LOWEST to HIGHEST)
// 1. appsettings.json
// 2. appsettings.ENV.json
// 3. User secrets (local dev only)
// 4. ENV variables
// 5. CLI args
// In Linux/Bash, env vars can't contain `:`
// .NET will convert __ from linux/bash/docker into : so it works in appsettings.json
// Kafka__BootstrapServers -> Kafka:BootstrapServers
var ServiceName =
    builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'OTel:ServiceName' configuration.");
var otelEndpoint =
    builder.Configuration["OTel:Endpoint"]
    ?? throw new InvalidOperationException("Missing 'OTel:Endpoint' configuration.");
var bootstrapServers =
    builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Missing 'Kafka:BootstrapServers' configuration.");

// Deps are registered with builder.Services (~ApplicationContext)
// A service is a dep (~Bean injected where needed by the Builder.Services container)
// .NET registers deps upfront instead of in @Configuration, @Service or @Component classes
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ServiceName)) // resouce => tel metadata
    .WithLogging(logging => logging.AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics =>
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint))
    )
    .WithTracing(tracing =>
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint))
    );

builder.Services.AddValidatorsFromAssemblyContaining<TelemetryValidator>();

// Setup Kafka
builder.Services.AddSingleton(sp => // service provider
{
    // Get the .NET logger for the Kafka producer<Key, Value> (loggers can only write, not read)
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();

    // TODO: There are more ProducerConfig properties for production
    // By default, it will serialize the key and value for you if left string, string
    var config = new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        AllowAutoCreateTopics = true,
        MetadataMaxAgeMs = 5000, // Force refresh every 5s for fast startup
    };

    // Multiple msg types all use the same producer, not multiple
    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) => logger.LogError("Kafka Producer Error: {Reason}", e.Reason))
        .Build();
});

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

// Register the POST /api/telemetry route
app.MapIngestionEndpoints();

app.Run();
