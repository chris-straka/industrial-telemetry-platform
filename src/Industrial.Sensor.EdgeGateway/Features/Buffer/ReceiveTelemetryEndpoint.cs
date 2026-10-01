using System.Diagnostics;

using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Infrastructure;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// The reading a sensor posts to the gateway.
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
        // A static class cannot be the T in ILogger<T>, so create a logger from the singleton
        // factory. EdgeDbContext stays a per-request handler parameter.
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
                SensorSecurityState sensorSecurity,
                HttpContext http
            ) =>
            {
                // Authenticate before validating, because a well-formed reading from the wrong
                // device is still refused. The plaintext ops listener has no client certificate,
                // so enabling sensor mTLS also stops it from accepting readings.
                if (sensorSecurity.Options.Enabled)
                {
                    var identityError = SensorIdentityValidator.Validate(
                        http.Connection.ClientCertificate,
                        sensorSecurity.TrustedRoot,
                        req.EquipmentId
                    );
                    if (identityError is not null)
                    {
                        metrics.IdentityRejected.Add(1);
                        logger.LogWarning(
                            "Refused reading for {EquipmentId}: {Reason}.",
                            req.EquipmentId,
                            identityError
                        );
                        return Results.Json(
                            new { error = identityError },
                            statusCode: StatusCodes.Status403Forbidden
                        );
                    }
                }

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
                    // quarantine. Check all three before reserving capacity so a delayed retry
                    // stays idempotent even after its shorter-lived settled marker expires.
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

                    // Reserve before touching SQLite. Compare-exchange keeps the ceiling exact
                    // across concurrent requests, and a failed insert releases the reservation.
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
                        // One SaveChanges is one transaction and, with synchronous=FULL, one fsync.
                        // No CancellationToken: if the HTTP connection drops after the reservation,
                        // the sensor cannot tell what happened, so the durable write finishes.
                        await db.SaveChangesAsync();

                        metrics.Received.Add(1);

                        // 202 means the reading is durable locally but has not reached the cloud.
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

        // Counts rows on disk rather than reading BufferDepth, so it can cross-check the
        // edge.queue.depth gauge without Prometheus.
        app.MapGet(
            "/buffer",
            async (EdgeDbContext db) =>
                Results.Ok(new { queueDepth = await db.TelemetryRecords.CountAsync() })
        );

        // A bounded view of permanent cloud rejections. The limit is capped so one request cannot
        // load the whole quarantine into memory.
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

    // SQLite reports every constraint failure as code 19. Extended code 2067 is a UNIQUE
    // violation, and MessageId has the only unique index on this table.
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
