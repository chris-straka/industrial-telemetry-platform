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
    private const int MaxTraceParentLength = 128;

    public static void MapReceiverEndpoints(this IEndpointRouteBuilder app)
    {
        // ILogger<T> is the normal injection method but a static class cannot be T. Resolve only
        // the singleton factory here; EdgeDbContext remains a per-request handler parameter.
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
                BufferMutationGate mutationGate,
                IOptions<BufferOptions> bufferOptions,
                HttpContext http
            ) =>
            {
                var validationError = TelemetryAdmissionValidator.Validate(
                    req,
                    out var canonicalMessageId
                );
                if (validationError is not null)
                {
                    metrics.Malformed.Add(1);
                    return Results.BadRequest(new { error = validationError });
                }

                await mutationGate.EnterAsync(http.RequestAborted);
                try
                {
                    // A MessageId can be queued, recently settled, or retained in rejection
                    // quarantine. Check all durable states before reserving capacity so delayed
                    // retries stay idempotent even when their shorter-lived settled marker expired.
                    var alreadyKnown =
                        await db.SettledMessages.AnyAsync(
                            x => x.MessageId == canonicalMessageId,
                            http.RequestAborted
                        )
                        || await db.TelemetryRecords.AnyAsync(
                            x => x.MessageId == canonicalMessageId,
                            http.RequestAborted
                        )
                        || await db.QuarantinedTelemetryRecords.AnyAsync(
                            x => x.MessageId == canonicalMessageId,
                            http.RequestAborted
                        );

                    if (alreadyKnown)
                    {
                        metrics.Duplicates.Add(1);
                        logger.LogDebug(
                            "Duplicate MessageId {MessageId} from {EquipmentId} ignored.",
                            canonicalMessageId,
                            req.EquipmentId
                        );
                        return Results.Accepted();
                    }

                    // Reserve before touching SQLite. Compare-exchange makes this ceiling exact
                    // across concurrent HTTP requests; a failed insert releases the reservation.
                    var maxDepth = bufferOptions.Value.MaxDepth;
                    if (!bufferDepth.TryReserve(maxDepth))
                    {
                        metrics.Shed.Add(1);

                        // Jitter keeps a fleet from retrying in lockstep when space reappears.
                        var retryAfter = Random.Shared.Next(2, 15);

                        logger.LogWarning(
                            "Buffer full ({Depth}/{Max}). Shedding {EquipmentId}, retry in {RetryAfter}s.",
                            bufferDepth.Current,
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

                    var traceParent = Activity.Current?.Id;
                    if (traceParent?.Length > MaxTraceParentLength)
                        traceParent = null;

                    var record = new TelemetryRecord
                    {
                        MessageId = canonicalMessageId,
                        EquipmentId = req.EquipmentId,
                        SequenceNumber = req.SequenceNumber,
                        OccurredAt = req.OccurredAt,
                        BufferedAt = DateTimeOffset.UtcNow, // Never leaves the gateway.
                        TraceParent = traceParent,
                        EngineTemperature = req.EngineTemperature,
                        OilPressure = req.OilPressure,
                    };

                    db.TelemetryRecords.Add(record);

                    try
                    {
                        // One SaveChanges == one transaction == one fsync (synchronous=FULL).
                        // No CancellationToken on purpose: after reserving the reading, a lost HTTP
                        // connection makes the result ambiguous and the durable write must finish.
                        await db.SaveChangesAsync();

                        metrics.Received.Add(1);

                        // 202 means accepted but not finished: the reading is durable locally and
                        // still has to reach the cloud.
                        return Results.Accepted();
                    }
                    catch (DbUpdateException ex) when (IsDuplicateMessageId(ex))
                    {
                        bufferDepth.Release(1);
                        metrics.Duplicates.Add(1);
                        logger.LogDebug(
                            "Duplicate MessageId {MessageId} from {EquipmentId} ignored.",
                            canonicalMessageId,
                            req.EquipmentId
                        );
                        return Results.Accepted();
                    }
                    catch
                    {
                        bufferDepth.Release(1);
                        throw;
                    }
                }
                finally
                {
                    mutationGate.Exit();
                }
            }
        );

        // Same number as edge.queue.depth gauge, readable without Prometheus
        // Reports disk not cache
        app.MapGet(
            "/buffer",
            async (EdgeDbContext db) =>
                // This is an anonymous type specifying a response body
                Results.Ok(new { queueDepth = await db.TelemetryRecords.CountAsync() })
        );

        // A bounded forensic view of permanent cloud rejections. Limit is deliberately capped so
        // an operator cannot make one request materialize the entire quarantine in memory.
        app.MapGet(
            "/buffer/quarantine",
            async (int? limit, EdgeDbContext db, CancellationToken cancellationToken) =>
            {
                var take = Math.Clamp(limit ?? 100, 1, 200);
                var total = await db.QuarantinedTelemetryRecords.CountAsync(cancellationToken);
                // SQLite's EF provider cannot translate DateTimeOffset ordering. Keep the limit
                // parameterized in raw SQL, then shape at most 200 rows in memory.
                var rows = await db
                    .QuarantinedTelemetryRecords.FromSqlInterpolated(
                        $"""
                        SELECT *
                        FROM "QuarantinedTelemetryRecords"
                        ORDER BY "RejectedAt" DESC, "MessageId" DESC
                        LIMIT {take}
                        """
                    )
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
                var items = rows
                    .Select(row => new
                    {
                        row.MessageId,
                        row.OriginalQueueId,
                        row.EquipmentId,
                        row.SequenceNumber,
                        row.OccurredAt,
                        row.BufferedAt,
                        row.TraceParent,
                        row.EngineTemperature,
                        row.OilPressure,
                        row.RejectedAt,
                        row.RejectionCode,
                        row.RejectionReason,
                    })
                    .ToList();

                return Results.Ok(new { total, limit = take, items });
            }
        );
    }

    // SQLite reports every constraint failure as error code 19 (SQLITE_CONSTRAINT)
    // 2067 is the extended code for UNIQUE, and MessageId owns the only unique index here
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
