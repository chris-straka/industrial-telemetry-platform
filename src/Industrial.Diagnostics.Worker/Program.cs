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

#region config
var ServiceName =
    builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'OTel:ServiceName' configuration.");
var OTelEndpoint =
    builder.Configuration["OTel:Endpoint"]
    ?? throw new InvalidOperationException("Missing 'OTel:Endpoint' configuration.");
var bootstrapServers =
    builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Missing 'Kafka:BootstrapServers' configuration.");
var pgConnectionString =
    builder.Configuration.GetConnectionString("IndustrialDb")
    ?? throw new InvalidOperationException(
        "Missing 'ConnectionStrings:IndustrialDb' configuration."
    );
var geminiApiKey =
    builder.Configuration["Gemini:ApiKey"]
    ?? throw new InvalidOperationException("Missing 'Gemini:ApiKey' configuration.");
#endregion

// Setup DB
builder.Services.AddPooledDbContextFactory<AppDbContext>(options => options.UseNpgsql(pgConnectionString));

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ServiceName))
    .WithLogging(logging =>
        logging
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(OTelEndpoint))
    )
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

// Register Kafka
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

// Setup AI features
builder.Services.AddSingleton(new ModelEngine("model.zip"));
builder.Services.AddSingleton(new Client(apiKey: geminiApiKey));

// Setup a Kafka telemetry-events consumer as a background process
// Still tied to the app lifecycle but no longer blocking the main thread
builder.Services.AddHostedService<TelemetryConsumerWorker>();

var app = builder.Build();

// Cleanup Kafka producer
app.Lifetime.ApplicationStopping.Register(() =>
{
    var producer = app.Services.GetRequiredService<IProducer<string, string>>();
    producer.Flush(TimeSpan.FromSeconds(5));
    producer.Dispose();
});

// Automatically apply any pending EF Core migrations on startup
if (app.Environment.IsDevelopment())
{
    // app.Services lives for the entire lifetime of the application
    // We need to use services but only during startup (DB migration)
    // So we scope them here so that they're disposed of afterwards
    using var scope = app.Services.CreateScope();
    // Resolve dependencies from the scoped provider rather than the root provider
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    const int maxRetries = 5;
    var retryCount = 0;

    while (retryCount < maxRetries)
    {
        try
        {
            using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            // app now exists so we can use the logger directly
            app.Logger.LogInformation("DB Migrations Applied");
            break;
        }
        catch (Exception ex)
        {
            retryCount++;
            app.Logger.LogWarning(
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
