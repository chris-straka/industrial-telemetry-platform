using Industrial.Ingestion.Api; // generated from Protos/telemetry.proto
using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Features.Buffer;
using Industrial.Sensor.EdgeGateway.Features.Upload;
using Industrial.Sensor.EdgeGateway.Infrastructure;
using Industrial.Shared;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// --------------------------------------------------------------------------------
// Sits between sensors and the cloud and durably owns every valid reading after replying 202,
// even when the cloud, network, or this process dies. The emulator is intentionally best-effort
// before that acknowledgement boundary.
//
//   sensor --HTTP--> [ receiver -> SQLite (durable) -> uploader ] --gRPC--> cloud
//                      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//                      two independent loops, coupled only by disk
//
// The sensor gets its 202 once the reading is on local disk, not once it reaches the cloud,
// so sensors keep producing through a cloud outage.
// --------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// A legitimate sensor reading is a few hundred bytes. Bound the HTTP body before JSON binding so
// padding or ignored properties cannot use the unauthenticated LAN endpoint to exhaust memory or
// disk.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);

#region config
builder
    .Services.AddOptions<OTelOptions>()
    .Bind(builder.Configuration.GetSection(OTelOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<CloudOptions>()
    .Bind(builder.Configuration.GetSection(CloudOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<BufferOptions>()
    .Bind(builder.Configuration.GetSection(BufferOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<UploaderOptions>()
    .Bind(builder.Configuration.GetSection(UploaderOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<TransportSecurityOptions>()
    .Bind(builder.Configuration.GetSection(TransportSecurityOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder
    .Services.AddOptions<SensorSecurityOptions>()
    .Bind(builder.Configuration.GetSection(SensorSecurityOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Bound directly because service registration runs before the service provider exists.
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var cloud = builder.Configuration.GetSection(CloudOptions.Section).Get<CloudOptions>()!;
var buffer = builder.Configuration.GetSection(BufferOptions.Section).Get<BufferOptions>()!;
var transportSecurity =
    builder.Configuration.GetSection(TransportSecurityOptions.Section)
        .Get<TransportSecurityOptions>() ?? new TransportSecurityOptions();
var sensorSecurity =
    builder.Configuration.GetSection(SensorSecurityOptions.Section)
        .Get<SensorSecurityOptions>() ?? new SensorSecurityOptions();

builder.AddSensorSecurity(sensorSecurity);
#endregion

if (
    transportSecurity.Enabled
    && !cloud.ApiUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
)
{
    throw new InvalidOperationException(
        "Cloud:ApiUrl must use https:// when transport security is enabled."
    );
}

// Sets synchronous=FULL on every pooled connection.
builder.Services.AddSingleton<SqlitePragmaInterceptor>();

builder.Services.AddDbContext<EdgeDbContext>(
    (sp, opt) =>
        opt.UseSqlite($"Data Source={buffer.Path}")
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>())
);

builder.Services.AddSingleton<BufferDepth>();
builder.Services.AddSingleton<BufferMutationGate>();
builder.Services.AddSingleton<EdgeMetrics>();
builder.Services.AddHealthChecks().AddCheck<EdgeReadinessCheck>("sqlite_write");
builder.Services.AddScoped<BufferSettlementStore>();

builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(otel.ServiceName))
    .WithLogging(log => log.AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint)))
    .WithMetrics(m =>
        m.AddMeter(EdgeMetrics.MeterName)
            .AddAspNetCoreInstrumentation() // Receive endpoint (all inbound traffic)
            .AddHttpClientInstrumentation() // gRPC included (all outbound traffic)
            .AddRuntimeInstrumentation() // Threads, etc
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
    .WithTracing(t =>
        t.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // The upload loop is background work that auto-instrumentation cannot see.
            .AddSource(EdgeTracing.SourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

var ingestionClient = builder.Services.AddGrpcClient<TelemetryIngestion.TelemetryIngestionClient>(o =>
{
    o.Address = new Uri(cloud.ApiUrl);
});
if (transportSecurity.Enabled)
{
    ingestionClient.ConfigurePrimaryHttpMessageHandler(() =>
        MutualTlsHttpHandlerFactory.Create(transportSecurity)
    );
}

builder.Services.AddHostedService<UploaderWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(buffer.Path))!);

    // EnsureCreated rather than migrations, because this database is a transient queue. Only
    // Diagnostics.Worker owns EF migrations.
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does not add newly introduced tables to an existing database. These compatible
    // additions preserve already-buffered telemetry across an application upgrade.
    await EdgeDatabaseInitializer.EnsureCompatibleSchemaAsync(db, buffer);

    // WAL appends to a log and folds changes into the database at checkpoints, so the uploader
    // can read while the receiver writes. The setting persists in the file header.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

    var buffered = await db.TelemetryRecords.CountAsync();
    app.Services.GetRequiredService<BufferDepth>().Initialize(buffered);
    if (buffered > 0)
    {
        // Rows present at startup were written by a previous run.
        app.Logger.LogInformation(
            "Recovered {Count} unsent readings from the local buffer at {Path}.",
            buffered,
            buffer.Path
        );
    }
}

app.MapReceiverEndpoints();
app.MapGet("/health", () => Results.Ok());
app.MapHealthChecks("/health/ready", new HealthCheckOptions());

app.Run();
