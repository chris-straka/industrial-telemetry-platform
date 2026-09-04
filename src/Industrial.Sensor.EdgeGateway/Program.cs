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
//                      two INDEPENDENT loops, coupled only by disk
//
// We send the sensor 202 the moment the reading is on local disk (not on cloud).
// This decoupling allows the sensor to produce through a cloud outage.
// --------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// A legitimate sensor reading is a few hundred bytes. Bound the HTTP body before JSON binding so
// ignored properties or whitespace cannot turn the unauthenticated LAN endpoint into a memory and
// disk pressure primitive.
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

// Needed during registration, before the service provider container exists.
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

// We're intercepting the conn to set synchronous=FULL for every conn
builder.Services.AddSingleton<SqlitePragmaInterceptor>();

// AddDbContext registers EdgeDbContext with a `scoped` DI lifetime
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

// OTel
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
            // upload loop is bg work, autoinstrumentation can't trace it, need 2 add my own
            .AddSource(EdgeTracing.SourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

// gRPC connection to the cloud (ingestion API).
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

// Uploads readings from SQLite to the cloud
builder.Services.AddHostedService<UploaderWorker>();

var app = builder.Build();

// Migrations belong cloud side (see Diagnostics.Worker)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(buffer.Path))!);

    // EnsureCreated (not Migrate), because this DB is transient (but durable)
    // If the schema changes, we drain it then migrate
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does not add newly introduced tables to an existing database. These compatible
    // additions preserve already-buffered telemetry across an application upgrade.
    await EdgeDatabaseInitializer.EnsureCompatibleSchemaAsync(db, buffer);

    // WAL will write to a log file first and fold changes into the DB later in a CP
    // Allows the uploader read and the receiver to write simultaneously
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

    var buffered = await db.TelemetryRecords.CountAsync();
    app.Services.GetRequiredService<BufferDepth>().Initialize(buffered);
    if (buffered > 0)
    {
        // Anything still left in the DB was written by a previous run (proof of durability)
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
