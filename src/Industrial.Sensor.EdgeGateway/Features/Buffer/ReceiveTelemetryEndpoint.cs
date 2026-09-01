using System.Diagnostics;
using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// What the sensor sends the gateway
/// </summary>
public record TelemetryDto(
    string MessageId,
    string EquipmentId,
    long SequenceNumber,
    DateTimeOffset OccurredAt, // event time
    double EngineTemperature,
    double OilPressure
);

public static class ReceiveTelemetryEndpoint
{
    public static void MapReceiverEndpoints(this IEndpointRouteBuilder app)
    {
        // ILogger<T> is the normal injection method but can't be used in static classes
        // A scoped service pulled in here (EdgeDbContext) is captive for the process life
        var logger = app
            .ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ReceiveTelemetryEndpoint).FullName!);

        app.MapPost(
            "/api/local/telemetry",
            async (
                TelemetryDto req,
                EdgeDbContext db,
                EdgeMetrics metrics,
                BufferDepth bufferDepth,
                IOptions<BufferOptions> bufferOptions,
                HttpContext http
            ) =>
            {
                // MessageId "" inserts once, then every further "" MessageId is discarded as a dupe
                if (
                    string.IsNullOrWhiteSpace(req.MessageId)
                    || string.IsNullOrWhiteSpace(req.EquipmentId)
                )
                {
                    metrics.Malformed.Add(1);
                    return Results.BadRequest(
                        new { error = "message_id_and_equipment_id_required" }
                    );
                }

                // load shedding (drop readings to prevent disk overflow)
                var maxDepth = bufferOptions.Value.MaxDepth;
                var depth = bufferDepth.Current;
                if (depth >= maxDepth)
                {
                    metrics.Shed.Add(1);

                    // Server's answer to the thundering herd problem
                    // Works even if client's don't have Polly retrying automatically
                    var retryAfter = Random.Shared.Next(2, 15);

                    logger.LogWarning(
                        "Buffer full ({Depth}/{Max}). Shedding {EquipmentId}, retry in {RetryAfter}s.",
                        depth,
                        maxDepth,
                        req.EquipmentId,
                        retryAfter
                    );

                    http.Response.Headers.RetryAfter = retryAfter.ToString();
                    return Results.Json(
                        new { error = "buffer_full", retryAfterSeconds = retryAfter },
                        statusCode: StatusCodes.Status429TooManyRequests
                    );
                }

                var record = new TelemetryRecord
                {
                    MessageId = req.MessageId,
                    EquipmentId = req.EquipmentId,
                    SequenceNumber = req.SequenceNumber,
                    OccurredAt = req.OccurredAt,
                    BufferedAt = DateTimeOffset.UtcNow, // (Never leaves the gateway)
                    TraceParent = Activity.Current?.Id, // ASP.NET Core span for this request (Otel)
                    EngineTemperature = req.EngineTemperature,
                    OilPressure = req.OilPressure,
                };

                db.TelemetryRecords.Add(record);

                try
                {
                    // One SaveChanges == one transaction == one fsync (synchronous=FULL)
                    // Flushing every reading caps throughput to the HD's fsync rate (not CPU)

                    // No CancellationToken on purpose, passing it would throw away a reading
                    // Kestrel trips RequestAborted the moment the sensor hangs up
                    // We already hold the reading by then and would rather keep it
                    await db.SaveChangesAsync();

                    // Counters move after the await so a failed save can't inflate them
                    metrics.Received.Add(1);
                    bufferDepth.Increment();

                    // 202 means accepted but not finished, 201 means a resource was created
                    // The reading still has to reach the cloud (no URL to hand back yet)
                    return Results.Accepted();
                }
                catch (DbUpdateException ex) when (IsDuplicateMessageId(ex))
                {
                    // The sensor re-sent a reading whose 202 was lost (retry despite success)
                    metrics.Duplicates.Add(1);
                    logger.LogDebug(
                        "Duplicate MessageId {MessageId} from {EquipmentId} ignored.",
                        req.MessageId,
                        req.EquipmentId
                    );
                    return Results.Accepted();
                }
            }
        );

        // Liveness
        app.MapGet("/health", () => Results.Ok());

        // Same number as edge.queue.depth gauge, readable without Prometheus
        // Reports disk not cache
        app.MapGet(
            "/buffer",
            async (EdgeDbContext db) =>
                // This is an anonymous type specifying a response body
                Results.Ok(new { queueDepth = await db.TelemetryRecords.CountAsync() })
        );
    }

    // SQLite reports every constraint failure as error code 19 (SQLITE_CONSTRAINT)
    // 2067 is the extended code for UNIQUE, and MessageId owns the only unique index here
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
