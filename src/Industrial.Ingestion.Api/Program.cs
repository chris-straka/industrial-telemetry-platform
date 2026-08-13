using Confluent.Kafka;
using FluentValidation;
using Industrial.Ingestion.Api.Features.Ingestion;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Standardized Configuration (Maps Env Vars like Kafka__BootstrapServers automatically)
var ServiceName =
    builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'serviceName' configuration.");
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
