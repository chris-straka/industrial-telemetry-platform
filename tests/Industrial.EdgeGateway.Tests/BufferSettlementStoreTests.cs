using Industrial.Sensor.EdgeGateway.Configuration;
using Industrial.Sensor.EdgeGateway.Features.Buffer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Industrial.EdgeGateway.Tests;

public sealed class BufferSettlementStoreTests
{
    [Fact]
    public async Task Rejection_is_quarantined_with_original_payload_in_same_settlement()
    {
        await using var connection = await OpenConnectionAsync();
        var dbOptions = new DbContextOptionsBuilder<EdgeDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EdgeDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var accepted = Reading("EQ-accepted", sequence: 10);
        var rejected = Reading("EQ-rejected", sequence: 20);
        db.TelemetryRecords.AddRange(accepted, rejected);
        await db.SaveChangesAsync();

        var depth = new BufferDepth();
        depth.Initialize(2);
        using var gate = new BufferMutationGate();
        var store = CreateStore(db, gate, depth, quarantineMaxRows: 100);

        await store.SettleAsync(
            [accepted, rejected],
            [rejected.MessageId],
            CancellationToken.None
        );

        Assert.Equal(0, await db.TelemetryRecords.CountAsync());
        Assert.Equal(2, await db.SettledMessages.CountAsync());
        Assert.Equal(0, depth.Current);

        var quarantined = await db.QuarantinedTelemetryRecords.SingleAsync();
        Assert.Equal(rejected.MessageId, quarantined.MessageId);
        Assert.Equal(rejected.Id, quarantined.OriginalQueueId);
        Assert.Equal(rejected.EquipmentId, quarantined.EquipmentId);
        Assert.Equal(rejected.SequenceNumber, quarantined.SequenceNumber);
        Assert.Equal(rejected.OccurredAt, quarantined.OccurredAt);
        Assert.Equal(rejected.BufferedAt, quarantined.BufferedAt);
        Assert.Equal(rejected.TraceParent, quarantined.TraceParent);
        Assert.Equal(rejected.EngineTemperature, quarantined.EngineTemperature);
        Assert.Equal(rejected.OilPressure, quarantined.OilPressure);
        Assert.Equal(BufferSettlementStore.CloudRejectionCode, quarantined.RejectionCode);
        Assert.Contains("does not provide a per-reading reason", quarantined.RejectionReason);
    }

    [Fact]
    public async Task Quarantine_prunes_expired_rows_and_enforces_the_hard_row_limit()
    {
        await using var connection = await OpenConnectionAsync();
        var dbOptions = new DbContextOptionsBuilder<EdgeDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EdgeDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var expired = Quarantined(Reading("EQ-old", sequence: 1), DateTimeOffset.UtcNow.AddDays(-2));
        var rejected = Enumerable.Range(2, 3).Select(i => Reading("EQ-new", i)).ToList();
        db.QuarantinedTelemetryRecords.Add(expired);
        db.TelemetryRecords.AddRange(rejected);
        await db.SaveChangesAsync();

        var depth = new BufferDepth();
        depth.Initialize(rejected.Count);
        using var gate = new BufferMutationGate();
        var store = CreateStore(db, gate, depth, quarantineMaxRows: 2);

        await store.SettleAsync(
            rejected,
            rejected.Select(row => row.MessageId).ToList(),
            CancellationToken.None
        );

        Assert.False(
            await db.QuarantinedTelemetryRecords.AnyAsync(row => row.MessageId == expired.MessageId)
        );
        Assert.Equal(2, await db.QuarantinedTelemetryRecords.CountAsync());
        Assert.Equal(0, depth.Current);
    }

    [Fact]
    public async Task Settlement_failure_rolls_back_quarantine_and_live_row_delete()
    {
        await using var connection = await OpenConnectionAsync();
        var dbOptions = new DbContextOptionsBuilder<EdgeDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EdgeDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var rejected = Reading("EQ-conflict", sequence: 1);
        db.TelemetryRecords.Add(rejected);
        db.SettledMessages.Add(
            new SettledMessage
            {
                MessageId = rejected.MessageId,
                SettledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            }
        );
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var trackedReading = await db.TelemetryRecords.SingleAsync();
        var depth = new BufferDepth();
        depth.Initialize(1);
        using var gate = new BufferMutationGate();
        var store = CreateStore(db, gate, depth, quarantineMaxRows: 100);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.SettleAsync(
                [trackedReading],
                [trackedReading.MessageId],
                CancellationToken.None
            )
        );

        await using var verificationDb = new EdgeDbContext(dbOptions);
        Assert.Equal(1, await verificationDb.TelemetryRecords.CountAsync());
        Assert.Equal(0, await verificationDb.QuarantinedTelemetryRecords.CountAsync());
        Assert.Equal(1, depth.Current);
    }

    private static BufferSettlementStore CreateStore(
        EdgeDbContext db,
        BufferMutationGate gate,
        BufferDepth depth,
        int quarantineMaxRows
    ) =>
        new(
            db,
            gate,
            depth,
            Options.Create(
                new BufferOptions
                {
                    Path = "unused.db",
                    MaxDepth = 100,
                    SettledIdRetentionHours = 24,
                    QuarantineRetentionHours = 24,
                    QuarantineMaxRows = quarantineMaxRows,
                }
            )
        );

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static TelemetryRecord Reading(string equipmentId, long sequence) =>
        new()
        {
            MessageId = Guid.CreateVersion7().ToString("D"),
            EquipmentId = equipmentId,
            SequenceNumber = sequence,
            OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            BufferedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            TraceParent = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
            EngineTemperature = 91.2,
            OilPressure = 44.8,
        };

    private static QuarantinedTelemetryRecord Quarantined(
        TelemetryRecord reading,
        DateTimeOffset rejectedAt
    ) =>
        new()
        {
            MessageId = reading.MessageId,
            OriginalQueueId = reading.Id,
            EquipmentId = reading.EquipmentId,
            SequenceNumber = reading.SequenceNumber,
            OccurredAt = reading.OccurredAt,
            BufferedAt = reading.BufferedAt,
            TraceParent = reading.TraceParent,
            EngineTemperature = reading.EngineTemperature,
            OilPressure = reading.OilPressure,
            RejectedAt = rejectedAt,
            RejectionCode = BufferSettlementStore.CloudRejectionCode,
            RejectionReason = BufferSettlementStore.CloudRejectionReason,
        };
}
