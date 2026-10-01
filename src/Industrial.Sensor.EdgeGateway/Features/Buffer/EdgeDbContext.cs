using Microsoft.EntityFrameworkCore;

namespace Industrial.Sensor.EdgeGateway.Features.Buffer;

/// <summary>
/// The gateway's durable store-and-forward buffer.
/// </summary>
/// <remarks>
/// SQLite is a single file with no server to administer, and its engine provides crash recovery
/// and atomic batch deletes.
///
/// The context is registered with a scoped lifetime. Disposing the scope returns its pooled
/// connection and releases the ChangeTracker's entities. A context held by a singleton would
/// keep tracking entities indefinitely and serve stale data.
/// </remarks>
public class EdgeDbContext(DbContextOptions<EdgeDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();
    public DbSet<SettledMessage> SettledMessages => Set<SettledMessage>();
    public DbSet<QuarantinedTelemetryRecord> QuarantinedTelemetryRecords =>
        Set<QuarantinedTelemetryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<TelemetryRecord>();

        // Live-queue idempotency is backed by this constraint, not only the endpoint pre-check.
        record.HasIndex(r => r.MessageId).IsUnique();

        var settled = modelBuilder.Entity<SettledMessage>();
        settled.HasKey(r => r.MessageId);
        settled.HasIndex(r => r.SettledAt);

        var quarantine = modelBuilder.Entity<QuarantinedTelemetryRecord>();
        quarantine.HasKey(r => r.MessageId);
        quarantine.Property(r => r.EquipmentId).HasMaxLength(64);
        quarantine.Property(r => r.TraceParent).HasMaxLength(128);
        quarantine.Property(r => r.RejectionCode).HasMaxLength(64);
        quarantine.Property(r => r.RejectionReason).HasMaxLength(512);
        quarantine.HasIndex(r => r.RejectedAt);
    }
}
