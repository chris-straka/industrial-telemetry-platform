using Industrial.Sensor.EdgeGateway.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Atomically moves cloud-settled rows out of the live queue.
/// </summary>
public sealed class BufferSettlementStore(
    EdgeDbContext db,
    BufferMutationGate mutationGate,
    BufferDepth bufferDepth,
    IOptions<BufferOptions> bufferOptions
)
{
    public const string CloudRejectionCode = "cloud_permanent_rejection";

    // UploadTelemetryResponse carries rejected IDs but no per-reading reason or error code. Store
    // that limitation explicitly instead of inventing a diagnosis that the edge cannot know.
    public const string CloudRejectionReason =
        "Ingestion named this MessageId as permanently rejected; the current protocol does not provide a per-reading reason.";

    private readonly TimeSpan _settledIdRetention = TimeSpan.FromHours(
        bufferOptions.Value.SettledIdRetentionHours
    );
    private readonly TimeSpan _quarantineRetention = TimeSpan.FromHours(
        bufferOptions.Value.QuarantineRetentionHours
    );
    private readonly int _quarantineMaxRows = bufferOptions.Value.QuarantineMaxRows;

    public async Task SettleAsync(
        IReadOnlyCollection<TelemetryRecord> settledRows,
        IReadOnlyCollection<string> rejectedIds,
        CancellationToken cancellationToken
    )
    {
        if (settledRows.Count == 0)
            return;

        var settledIds = settledRows
            .Select(row => row.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        var rejected = rejectedIds.ToHashSet(StringComparer.Ordinal);
        if (!rejected.IsSubsetOf(settledIds))
            throw new InvalidOperationException(
                "A rejected MessageId must refer to a row being settled."
            );

        await mutationGate.EnterAsync(cancellationToken);
        try
        {
            // Quarantine insert, completion marker, and live-row delete share one transaction. The
            // receiver holds the same gate while checking queued/settled IDs, so it cannot observe
            // or create a MessageId in the middle of this state transition.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var settledAt = DateTimeOffset.UtcNow;
            var rejectedRows = settledRows.Where(row => rejected.Contains(row.MessageId)).ToList();
            HashSet<string> alreadyQuarantinedIds = rejectedRows.Count == 0
                ? []
                : (await db.QuarantinedTelemetryRecords
                    .Where(row => rejected.Contains(row.MessageId))
                    .Select(row => row.MessageId)
                    .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.Ordinal);

            db.SettledMessages.AddRange(
                settledRows.Select(row => new SettledMessage
                {
                    MessageId = row.MessageId,
                    SettledAt = settledAt,
                })
            );
            db.QuarantinedTelemetryRecords.AddRange(
                rejectedRows
                    // Older versions could re-admit an ID after its settled marker expired while
                    // its longer-lived quarantine row remained. Preserve the first forensic copy
                    // and still let that already-queued duplicate settle instead of wedging here.
                    .Where(row => !alreadyQuarantinedIds.Contains(row.MessageId))
                    .Select(row => new QuarantinedTelemetryRecord
                    {
                        MessageId = row.MessageId,
                        OriginalQueueId = row.Id,
                        EquipmentId = row.EquipmentId,
                        SequenceNumber = row.SequenceNumber,
                        OccurredAt = row.OccurredAt,
                        BufferedAt = row.BufferedAt,
                        TraceParent = row.TraceParent,
                        EngineTemperature = row.EngineTemperature,
                        OilPressure = row.OilPressure,
                        RejectedAt = settledAt,
                        RejectionCode = CloudRejectionCode,
                        RejectionReason = CloudRejectionReason,
                    })
            );
            db.TelemetryRecords.RemoveRange(settledRows);
            await db.SaveChangesAsync(cancellationToken);

            var settledCutoff = settledAt - _settledIdRetention;
            // SQLite's EF provider cannot translate DateTimeOffset ordering/comparisons even
            // though its canonical TEXT representation is sortable for these UTC timestamps.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM "SettledMessages"
                WHERE "SettledAt" < {settledCutoff};
                """,
                cancellationToken
            );

            var quarantineCutoff = settledAt - _quarantineRetention;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM "QuarantinedTelemetryRecords"
                WHERE "RejectedAt" < {quarantineCutoff};
                """,
                cancellationToken
            );

            // The time bound controls usefulness; this hard count bound controls disk growth even
            // during a sustained rejection storm. SQLite reuses the pages released by this delete.
            var quarantineCount = await db.QuarantinedTelemetryRecords.CountAsync(
                cancellationToken
            );
            var excessRows = quarantineCount - _quarantineMaxRows;
            if (excessRows > 0)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    DELETE FROM "QuarantinedTelemetryRecords"
                    WHERE "MessageId" IN (
                        SELECT "MessageId"
                        FROM "QuarantinedTelemetryRecords"
                        ORDER BY "RejectedAt", "MessageId"
                        LIMIT {excessRows}
                    );
                    """,
                    cancellationToken
                );
            }

            await transaction.CommitAsync(cancellationToken);

            // The in-memory depth mirrors only live queue rows. Release after the durable commit;
            // startup recount repairs it if the process dies between those two operations.
            bufferDepth.Release(settledRows.Count);
        }
        finally
        {
            mutationGate.Exit();
        }
    }
}
