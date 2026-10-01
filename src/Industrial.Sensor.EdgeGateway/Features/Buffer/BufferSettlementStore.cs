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

    // UploadTelemetryResponse names rejected IDs without a per-reading reason or error code, so
    // the stored reason states only what the edge actually knows.
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
                    // A buffer written by an earlier build can hold a queued duplicate of an ID
                    // that is already quarantined. Keep the first forensic copy and let the
                    // duplicate settle so it does not block the queue.
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

            // The age bound keeps rows relevant. This count bound caps disk use during a sustained
            // run of rejections. SQLite reuses the pages this delete frees.
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

            // The in-memory depth tracks only live queue rows, so release after the durable commit.
            // If the process dies in between, the startup recount repairs it.
            bufferDepth.Release(settledRows.Count);
        }
        finally
        {
            mutationGate.Exit();
        }
    }
}
