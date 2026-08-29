using Industrial.Ingestion.Api; // generated from Protos/telemetry.proto
using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Features.Buffer;
using Industrial.Sensor.EdgeGateway.Features.Upload;
using Industrial.Sensor.EdgeGateway.Infrastructure;
using Industrial.Shared;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ---------------------------------------------------------------------------------
// Sits between sensors and the cloud and makes sure sensor readings are never lost
// Even when the cloud, network, or this process dies.
//
//   sensor --HTTP--> [ receiver -> SQLite (durable) -> uploader ] --gRPC--> cloud
//                      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//                      two INDEPENDENT loops, coupled only by disk
//
// We send the sensor 202 the moment the reading is on local disk (not on cloud).
// This decoupling allows the sensor to produce through a cloud outage.
// ---------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

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

// Needed during registration, before the service provider container exists.
var otel = builder.Configuration.GetSection(OTelOptions.Section).Get<OTelOptions>()!;
var cloud = builder.Configuration.GetSection(CloudOptions.Section).Get<CloudOptions>()!;
var buffer = builder.Configuration.GetSection(BufferOptions.Section).Get<BufferOptions>()!;
#endregion

// We're intercepting the conn to set synchronous=FULL for every conn
builder.Services.AddSingleton<SqlitePragmaInterceptor>();

// AddDbContext registers EdgeDbContext with a `scoped` DI lifetime
builder.Services.AddDbContext<EdgeDbContext>(
    (sp, opt) =>
        opt.UseSqlite($"Data Source={buffer.Path}")
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>())
);

builder.Services.AddSingleton<BufferDepth>();
builder.Services.AddSingleton<EdgeMetrics>();

// OTel
builder
    .Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(otel.ServiceName))
    .WithLogging(log => log.AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint)))
    .WithMetrics(m =>
        m.AddMeter(EdgeMetrics.MeterName)
            .AddAspNetCoreInstrumentation() // Receive endpoint (all inbound traffic)
            .AddHttpClientInstrumentation() // gRPC included (all outbound traffic)
            .AddRuntimeInstrumentation() // Thread stuff
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    )
    .WithTracing(t =>
        t.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // Our own spans. Without this the upload span is never sampled and its
            // links to the buffered sensor traces are never emitted.
            .AddSource(EdgeTracing.SourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otel.Endpoint))
    );

// gRPC connection to the cloud (ingestion API).
builder.Services.AddGrpcClient<TelemetryIngestion.TelemetryIngestionClient>(o =>
{
    o.Address = new Uri(cloud.ApiUrl);
});

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

    // WAL will write to a log file first and fold changes into the DB later in a CP
    // Allows the uploader read and the receiver to write simultaneously
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

    var buffered = await db.TelemetryRecords.CountAsync();
    if (buffered > 0)
    {
        // Anything still left in the DB was written by a previous run (proof of durability)
        // This proves its durability (gateway dies during a cloud outage, readings remain)
        app.Logger.LogInformation(
            "Recovered {Count} unsent readings from the local buffer at {Path}.",
            buffered,
            buffer.Path
        );
    }
}

app.MapReceiverEndpoints();

app.Run();
