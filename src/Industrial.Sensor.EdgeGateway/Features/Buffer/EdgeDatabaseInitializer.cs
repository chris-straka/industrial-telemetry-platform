using Industrial.Sensor.EdgeGateway.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Adds backwards-compatible tables that EnsureCreated cannot add to an existing SQLite buffer.
/// </summary>
public static class EdgeDatabaseInitializer
{
    public static async Task EnsureCompatibleSchemaAsync(
        EdgeDbContext db,
        BufferOptions bufferOptions,
        CancellationToken cancellationToken = default
    )
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SettledMessages" (
                "MessageId" TEXT NOT NULL CONSTRAINT "PK_SettledMessages" PRIMARY KEY,
                "SettledAt" TEXT NOT NULL
            );
            """,
            cancellationToken
        );
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_SettledMessages_SettledAt"
            ON "SettledMessages" ("SettledAt");
            """,
            cancellationToken
        );
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "QuarantinedTelemetryRecords" (
                "MessageId" TEXT NOT NULL CONSTRAINT "PK_QuarantinedTelemetryRecords" PRIMARY KEY,
                "OriginalQueueId" INTEGER NOT NULL,
                "EquipmentId" TEXT NOT NULL,
                "SequenceNumber" INTEGER NOT NULL,
                "OccurredAt" TEXT NOT NULL,
                "BufferedAt" TEXT NOT NULL,
                "TraceParent" TEXT NULL,
                "EngineTemperature" REAL NOT NULL,
                "OilPressure" REAL NOT NULL,
                "RejectedAt" TEXT NOT NULL,
                "RejectionCode" TEXT NOT NULL,
                "RejectionReason" TEXT NOT NULL
            );
            """,
            cancellationToken
        );
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_QuarantinedTelemetryRecords_RejectedAt"
            ON "QuarantinedTelemetryRecords" ("RejectedAt");
            """,
            cancellationToken
        );

        // Enforce both bounds immediately after an upgrade/reconfiguration. Normal pruning happens
        // inside the uploader's settlement transaction.
        var quarantineCutoff = DateTimeOffset.UtcNow.AddHours(
            -bufferOptions.QuarantineRetentionHours
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "QuarantinedTelemetryRecords"
            WHERE "RejectedAt" < {quarantineCutoff};
            """,
            cancellationToken
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM "QuarantinedTelemetryRecords"
            WHERE "MessageId" IN (
                SELECT "MessageId"
                FROM "QuarantinedTelemetryRecords"
                ORDER BY "RejectedAt" DESC, "MessageId" DESC
                LIMIT -1 OFFSET {bufferOptions.QuarantineMaxRows}
            );
            """,
            cancellationToken
        );
    }
}
