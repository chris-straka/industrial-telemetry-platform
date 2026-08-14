using Confluent.Kafka;
using FluentValidation;
using Industrial.Ingestion.Api.Features.Ingestion;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Builder.Configuration pulls from a layered hierarchy
// 1. appsettings.json
// 2. appsettings.ENV.json
// 3. User secrets (local dev only)
// 4. env variables
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

// Logging & Observability
builder.Logging.AddOpenTelemetry(options =>
{
    options
        // resource is the entity that generates the telemetry
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otelEndpoint));
});

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ServiceName))
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

// Services & Dependencies
builder.Services.AddValidatorsFromAssemblyContaining<TelemetryValidator>();

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();
    var config = new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        AllowAutoCreateTopics = true,
        MetadataMaxAgeMs = 5000, // Force refresh every 5s for fast startup
    };

    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) => logger.LogError("Kafka Producer Error: {Reason}", e.Reason))
        .Build();
});

builder.Services.AddOpenApi();

var app = builder.Build();

// Lifecycle Management
app.Lifetime.ApplicationStopping.Register(() =>
{
    var producer = app.Services.GetRequiredService<IProducer<string, string>>();
    producer.Flush(TimeSpan.FromSeconds(5));
    producer.Dispose();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapIngestionEndpoints();

app.Run();
