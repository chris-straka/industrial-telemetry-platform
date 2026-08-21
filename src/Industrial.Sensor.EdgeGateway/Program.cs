using Industrial.Ingestion.Api; // From gRPC Proto
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var cloudIngestionUrl = builder.Configuration["Cloud__ApiUrl"] ?? "http://ingestion-api:8080";

// 1. Setup Durable SQLite Queue
builder.Services.AddDbContext<EdgeDbContext>(opt => opt.UseSqlite("Data Source=edge-buffer.db"));

// 2. Setup gRPC connection to the Cloud
builder.Services.AddGrpcClient<TelemetryIngestion.TelemetryIngestionClient>(o =>
{
    o.Address = new Uri(cloudIngestionUrl);
});

// 3. Setup the background uploader
builder.Services.AddHostedService<UploaderWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.EnsureCreated();
}

// THE RECEIVER: Minimal API to accept data from dumb sensors instantly
app.MapPost(
    "/api/local/telemetry",
    async (TelemetryDto req, EdgeDbContext db) =>
    {
        var record = new TelemetryRecord
        {
            MessageId = Guid.NewGuid().ToString(), // Immutable Idempotency ID!
            EquipmentId = req.EquipmentId,
            EngineTemperature = req.EngineTemperature,
            OilPressure = req.OilPressure,
        };

        db.TelemetryRecords.Add(record);
        await db.SaveChangesAsync(); // Saved safely to disk!
        return Results.Accepted();
    }
);

app.Run();

// THE SENDER: Background loop moving SQLite data to the Cloud via gRPC
public class UploaderWorker(
    IServiceScopeFactory scopeFactory,
    TelemetryIngestion.TelemetryIngestionClient client,
    ILogger<UploaderWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

                var pending = await db.TelemetryRecords.Take(200).ToListAsync(stoppingToken);
                if (pending.Count > 0)
                {
                    logger.LogInformation("Uploading {Count} records to cloud...", pending.Count);

                    // Open gRPC Stream
                    using var call = client.StreamTelemetry(cancellationToken: stoppingToken);
                    foreach (var item in pending)
                    {
                        await call.RequestStream.WriteAsync(
                            new TelemetryRequest
                            {
                                MessageId = item.MessageId,
                                EquipmentId = item.EquipmentId,
                                EngineTemperature = item.EngineTemperature,
                                OilPressure = item.OilPressure,
                            }
                        );
                    }
                    await call.RequestStream.CompleteAsync();
                    var response = await call.ResponseAsync;

                    if (response.Success)
                    {
                        // Safely remove only what the cloud acknowledged
                        db.TelemetryRecords.RemoveRange(pending);
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }
                else
                {
                    await Task.Delay(500, stoppingToken);
                }
            }
            catch (Exception)
            {
                logger.LogWarning("Cloud unavailable. Edge Gateway buffering data...");
                await Task.Delay(3000, stoppingToken);
            }
        }
    }
}

public record TelemetryDto(string EquipmentId, double EngineTemperature, double OilPressure);

public class TelemetryRecord
{
    public string MessageId { get; set; } = string.Empty;
    public string EquipmentId { get; set; } = string.Empty;
    public double EngineTemperature { get; set; }
    public double OilPressure { get; set; }
}

public class EdgeDbContext(DbContextOptions<EdgeDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();
}
