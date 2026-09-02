using Microsoft.EntityFrameworkCore;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// Durable SOF (store-and-forward) buffer.
/// </summary>
/// <remarks>
/// I chose SQLite because it's a single file with no server process to administer
/// Crash recovery and atomic batch deletes also come for free with the engine
///
/// The DBContext grabs its DB connection from a pool of connections.
/// Every entity a DbContext loads/adds is kept in its ChangeTracker.
///
/// AddDbContext&lt;EdgeDbContext&gt;() creates the context in a scoped DI lifetime
/// var db = scope.ServiceProvider.GetRequiredService&lt;EdgeDbContext&gt;();
/// Disposing the scope will Dispose() of the DbContext, freeing its DB connection for reuse
/// It also drops the ChangeTracker's references so they can be GC'd
/// A DBContext captive inside a singleton grows endlessly and serves stale data
/// </remarks>
public class EdgeDbContext(DbContextOptions<EdgeDbContext> options) : DbContext(options)
{
    // DBSet = table
    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();
    public DbSet<SettledMessage> SettledMessages => Set<SettledMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<TelemetryRecord>();

        // Live-queue idempotency is backed by this constraint, not only the endpoint pre-check.
        record.HasIndex(r => r.MessageId).IsUnique();

        var settled = modelBuilder.Entity<SettledMessage>();
        settled.HasKey(r => r.MessageId);
        settled.HasIndex(r => r.SettledAt);
    }
}
