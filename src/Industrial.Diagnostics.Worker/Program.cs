using Confluent.Kafka;
using Google.GenAI;
using Industrial.Shared;
using Microsoft.Extensions.Options;
using Industrial.Diagnostics.Worker.Configuration;
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
    .Services.AddOptions<GeminiOptions>()
    .Bind(builder.Configuration.GetSection(GeminiOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Needed during registration, before the container exists.
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var kafka = builder.Configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>()!;
var gemini = builder.Configuration.GetSection(GeminiOptions.Section).Get<GeminiOptions>()!;
var pgConnectionString =
    builder.Configuration.GetConnectionString("IndustrialDb")
    ?? throw new InvalidOperationException(
        "Missing 'ConnectionStrings:IndustrialDb' configuration."
    );
#endregion

// Setup DB
builder.Services.AddPooledDbContextFactory<AppDbContext>(options => options.UseNpgsql(pgConnectionString));

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otel.ServiceName))
    .WithLogging(logging =>
        logging
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(otel.ServiceName))
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
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
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

// Register Kafka
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IProducer<string, string>>>();
    var config = new ProducerConfig
    {
        BootstrapServers = kafka.BootstrapServers,
        AllowAutoCreateTopics = true,
        MetadataMaxAgeMs = 5000,
    };
    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) => logger.LogError("Kafka Producer Error: {Reason}", e.Reason))
        .Build();
});

// Setup AI features
builder.Services.AddSingleton(new ModelEngine("model.zip"));
builder.Services.AddSingleton(new Client(apiKey: gemini.ApiKey));

// A hosted service, so the consume loop follows the app lifecycle without blocking it.
builder.Services.AddHostedService<TelemetryConsumerWorker>();

var app = builder.Build();

// Cleanup Kafka producer
app.Lifetime.ApplicationStopping.Register(() =>
{
    var producer = app.Services.GetRequiredService<IProducer<string, string>>();
    producer.Flush(TimeSpan.FromSeconds(5));
    producer.Dispose();
});

// Migrate on startup in development only: in production this is a deploy step, because
// a racing replica should not be the thing that decides the schema.
if (app.Environment.IsDevelopment())
{
    // A scope so the startup-only services are disposed once migration is done, rather
    // than living on the root provider for the process.
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    const int maxRetries = 5;
    var retryCount = 0;

    while (retryCount < maxRetries)
    {
        try
        {
            using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
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
