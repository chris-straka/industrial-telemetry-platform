using Industrial.Sensor.EdgeGateway.Features.Buffer;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Industrial.EdgeGateway.Tests;

public sealed class EdgeDbContextTests
{
    [Fact]
    public async Task Settled_message_ids_are_unique_and_persisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EdgeDbContext>().UseSqlite(connection).Options;

        await using var db = new EdgeDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var id = Guid.CreateVersion7().ToString("D");

        db.SettledMessages.Add(new SettledMessage { MessageId = id, SettledAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.True(await db.SettledMessages.AnyAsync(x => x.MessageId == id));
    }

    [Fact]
    public async Task Compatible_initializer_adds_quarantine_to_an_existing_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Any existing table makes EnsureCreated a no-op, which is the upgrade case this tests.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE LegacyBuffer (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<EdgeDbContext>().UseSqlite(connection).Options;
        await using var db = new EdgeDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await EdgeDatabaseInitializer.EnsureCompatibleSchemaAsync(
            db,
            new Industrial.Sensor.EdgeGateway.Configuration.BufferOptions
            {
                Path = "unused.db",
                MaxDepth = 100,
                SettledIdRetentionHours = 24,
                QuarantineRetentionHours = 24,
                QuarantineMaxRows = 100,
            }
        );

        var id = Guid.CreateVersion7().ToString("D");
        db.QuarantinedTelemetryRecords.Add(
            new QuarantinedTelemetryRecord
            {
                MessageId = id,
                OriginalQueueId = 1,
                EquipmentId = "EQ-1",
                SequenceNumber = 1,
                OccurredAt = DateTimeOffset.UtcNow,
                BufferedAt = DateTimeOffset.UtcNow,
                EngineTemperature = 90,
                OilPressure = 45,
                RejectedAt = DateTimeOffset.UtcNow,
                RejectionCode = BufferSettlementStore.CloudRejectionCode,
                RejectionReason = BufferSettlementStore.CloudRejectionReason,
            }
        );
        await db.SaveChangesAsync();

        Assert.True(await db.QuarantinedTelemetryRecords.AnyAsync(row => row.MessageId == id));
    }
}
