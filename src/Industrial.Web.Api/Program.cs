using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var ServiceName =
    builder.Configuration["OTel:ServiceName"]
    ?? throw new InvalidOperationException("Missing 'OTel:ServiceName' configuration.");
var OTelEndpoint =
    builder.Configuration["OTel:Endpoint"]
    ?? throw new InvalidOperationException("Missing 'OTel:Endpoint' configuration.");
var BootstrapServers =
    builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Missing 'Kafka:BootstrapServers' configuration.");
var AllowedOrigins =
    builder.Configuration["CORS:AllowedOrigins"]?.Split(',')
    ?? throw new InvalidOperationException();

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
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(OTelEndpoint))
    )
    .WithTracing(tracing =>
        tracing
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(OTelEndpoint))
    );

builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // CORS
        policy.WithOrigins(AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); // Required for SignalR
    });
});

builder.Services.AddHostedService<KafkaSignalRWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapHub<TelemetryHub>("/telemetryHub");

app.Run();

public interface ITelemetryClient
{
    // These method names must match what the Frontend listens for
    Task telemetry_events(string payload);
    Task telemetry_alerts(string payload);
}

public class TelemetryHub : Hub<ITelemetryClient> { }

public class KafkaSignalRWorker(
    IConfiguration configuration,
    IHubContext<TelemetryHub, ITelemetryClient> hubContext,
    ILogger<KafkaSignalRWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var BootstrapServers =
            configuration["Kafka:BootstrapServers"]
            ?? throw new Exception("Worker missing Kafka:BootstrapServers");
        var GroupId =
            configuration["Kafka:GroupId"] ?? throw new Exception("Worker missing Kafka:GroupId");
        var EventsTopic =
            configuration["Kafka:EventsTopic"]
            ?? throw new Exception("Worker missing Kafka:EventsTopic");
        var AlertsTopic =
            configuration["Kafka:AlertsTopic"]
            ?? throw new Exception("Worker missing Kafka:AlertsTopic");

        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = GroupId,
            // Only real-time data for dashboard
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = true,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe([EventsTopic, AlertsTopic]);

        logger.LogInformation("SignalR-Kafka Bridge Started. Listening for events...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result?.Message == null)
                    continue;

                // Use the Topic name to decide which method to call
                if (result.Topic == EventsTopic)
                {
                    await hubContext.Clients.All.telemetry_events(result.Message.Value);
                }
                else if (result.Topic == AlertsTopic)
                {
                    await hubContext.Clients.All.telemetry_alerts(result.Message.Value);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error relaying Kafka to SignalR");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
