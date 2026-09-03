using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Industrial.Shared;
using Industrial.Web.Api.Configuration;
using Industrial.Web.Api.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

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
    .Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.Section))
    .ValidateDataAnnotations()
    .Validate(
        options =>
            options.Origins.Length > 0
            && options.Origins.All(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
            ),
        "CORS:AllowedOrigins must contain one or more absolute HTTP(S) origins."
    )
    .ValidateOnStart();

// Needed during registration, before the container exists.
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var kafka = builder.Configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>()!;
var cors = builder.Configuration.GetSection(CorsOptions.Section).Get<CorsOptions>()!;

builder.Logging.AddOpenTelemetry(options =>
{
    options
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(otel.ServiceName))
        .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint));
});

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(otel.ServiceName))
    .WithMetrics(metrics =>
        metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(WebMetrics.MeterName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
    .WithTracing(tracing =>
        tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(WebTracing.SourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

builder.Services.AddSingleton<WebMetrics>();
builder.Services.AddSingleton<KafkaConsumerReadiness>();
builder.Services.AddSingleton<IAdminClient>(
    new AdminClientBuilder(
        new AdminClientConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            AllowAutoCreateTopics = false,
        }
    ).Build()
);
builder
    .Services.AddHealthChecks()
    .AddCheck<KafkaConsumerReadiness>(
        "kafka-consumer",
        tags: ["ready"]
    )
    .AddCheck<KafkaTopicsHealthCheck>(
        "kafka-topics",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(KafkaOptions.MaximumReadinessTimeoutSeconds + 1)
    );
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // CORS
        policy.WithOrigins(cors.Origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); // Required for SignalR
    });
});

builder.Services.AddHostedService<KafkaSignalRWorker>();

var app = builder.Build();

app.UseCors();
app.MapHub<TelemetryHub>("/telemetryHub");
app.MapGet("/health", () => Results.Ok());
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
    }
);

app.Run();

public interface ITelemetryClient
{
    // These method names must match what the Frontend listens for
    Task telemetry_events(string payload);
    Task telemetry_alerts(string payload);
}

public class TelemetryHub : Hub<ITelemetryClient> { }

public class KafkaSignalRWorker(
    IOptions<KafkaOptions> kafkaOptions,
    IHubContext<TelemetryHub, ITelemetryClient> hubContext,
    KafkaConsumerReadiness readiness,
    WebMetrics metrics,
    ILogger<KafkaSignalRWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kafka = kafkaOptions.Value;

        var config = new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            // SignalR clients are local to this process. A shared group would split Kafka
            // partitions across replicas and each browser would see only the subset assigned to
            // its pod, so every replica deliberately gets its own broadcast subscription.
            GroupId = $"{kafka.GroupId}-{Environment.MachineName}",
            // Only real-time data for dashboard
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true,
            AllowAutoCreateTopics = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
                readiness.PartitionsAssigned(partitions.Count)
            )
            .SetPartitionsRevokedHandler((_, _) => readiness.PartitionsRevoked())
            .SetPartitionsLostHandler((_, _) => readiness.PartitionsLost())
            .SetErrorHandler((_, error) =>
            {
                logger.LogError("Kafka consumer error: {Reason}", error.Reason);
            })
            .Build();
        consumer.Subscribe([kafka.EventsTopic, kafka.AlertsTopic]);

        logger.LogInformation("SignalR-Kafka Bridge Started. Listening for events...");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Activity? activity = null;

                try
                {
                    var result = consumer.Consume(stoppingToken);
                    if (result?.Message == null)
                        continue;

                    activity = WebTracing.Source.StartActivity(
                        "telemetry.relay",
                        ActivityKind.Consumer,
                        ReadTraceContext(result.Message.Headers),
                        tags:
                        [
                            new("messaging.system", "kafka"),
                            new("messaging.destination.name", result.Topic),
                            new("messaging.kafka.offset", result.Offset.Value),
                        ]
                    );

                    // Use the Topic name to decide which method to call
                    if (result.Topic == kafka.EventsTopic)
                    {
                        await hubContext.Clients.All.telemetry_events(result.Message.Value);
                    }
                    else if (result.Topic == kafka.AlertsTopic)
                    {
                        await hubContext.Clients.All.telemetry_alerts(result.Message.Value);
                    }

                    metrics.RecordRelayed(result.Topic);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    metrics.RelayFailures.Add(1);
                    logger.LogError(ex, "Error relaying Kafka to SignalR");
                    await SafeDelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
                }
                finally
                {
                    activity?.Dispose();
                }
            }
        }
        finally
        {
            readiness.PartitionsRevoked();
            // Leaves the consumer group cleanly, so a deploy does not wait out
            // session.timeout.ms before the replacement is assigned partitions.
            consumer.Close();
        }
    }

    private static ActivityContext ReadTraceContext(Headers? headers)
    {
        if (headers is null || !headers.TryGetLastBytes("traceparent", out var raw) || raw is null)
            return default;

        return ActivityContext.TryParse(
            Encoding.UTF8.GetString(raw),
            null,
            isRemote: true,
            out var ctx
        )
            ? ctx
            : default;
    }

    // Task.Delay throws when the token trips, and the caller is the loop's catch block.
    // A throw from there escapes ExecuteAsync and stops the host on an ordinary shutdown.
    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
