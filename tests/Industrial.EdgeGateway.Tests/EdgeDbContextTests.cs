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
}
