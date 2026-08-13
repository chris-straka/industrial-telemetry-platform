using Confluent.Kafka;
using Google.GenAI;
using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Industrial.Diagnostics.Worker.Features.Diagnostics.ML;
using Industrial.Diagnostics.Worker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Standardized Configuration
// This maps env vars like DB__ConnectionString to "DB:ConnectionString"
var ServiceName =
    builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'OTel:ServiceName' configuration.");
var OTelEndpoint =
    builder.Configuration["OTel:Endpoint"]
    ?? throw new InvalidOperationException("Missing 'OTel:Endpoint' configuration.");
var bootstrapServers =
    builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Missing 'Kafka:BootstrapServers' configuration.");
var connectionString =
    builder.Configuration["DB:ConnectionString"]
    ?? throw new InvalidOperationException("Missing 'DB:ConnectionString' configuration.");
var aiKey =
    builder.Configuration["Gemini:ApiKey"]
    ?? throw new InvalidOperationException("Missing 'Gemini:ApiKey' configuration.");

// Observability
builder.Logging.AddOpenTelemetry(options =>
{
    options
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(OTelEndpoint));
});

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ServiceName))
    .WithMetrics(metrics =>
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(OTelEndpoint))
    )
    .WithTracing(tracing =>
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(OTelEndpoint))
    );

// Data & Infrastructure
builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString)
);

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();
    var config = new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        AllowAutoCreateTopics = true,
        MetadataMaxAgeMs = 5000,
    };
    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) => logger.LogError("Kafka Producer Error: {Reason}", e.Reason))
        .Build();
});

// Domain Services (ML & AI)
builder.Services.AddSingleton(new ModelEngine("model.zip"));
builder.Services.AddSingleton(new Client(apiKey: aiKey));
builder.Services.AddHostedService<TelemetryConsumerWorker>();

var app = builder.Build();

// Lifecycle & Startup
app.Lifetime.ApplicationStopping.Register(() =>
{
    var producer = app.Services.GetRequiredService<IProducer<string, string>>();
    producer.Flush(TimeSpan.FromSeconds(5));
    producer.Dispose();
});

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    var retryCount = 0;
    while (retryCount < 5)
    {
        try
        {
            using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            startupLogger.LogInformation("✅ Database Migrations Applied.");
            break;
        }
        catch (Exception ex)
        {
            retryCount++;
            startupLogger.LogWarning(
                "⏳ DB Migration Retry {Count}/5: {Error}",
                retryCount,
                ex.Message
            );
            if (retryCount == 5)
                throw;
            await Task.Delay(2000);
        }
    }
}

app.Run();
