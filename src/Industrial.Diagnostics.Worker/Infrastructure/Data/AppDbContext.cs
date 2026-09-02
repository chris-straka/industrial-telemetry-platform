using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Infrastructure.Data;

// Creates the TelemetryReading table in my DB
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryReading> TelemetryReadings => Set<TelemetryReading>();
    public DbSet<AlertOutboxMessage> AlertOutboxMessages => Set<AlertOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<AlertOutboxMessage>();
        outbox.Property(x => x.EquipmentId).HasMaxLength(64);
        outbox.Property(x => x.TraceParent).HasMaxLength(128);
        outbox.Property(x => x.LastError).HasMaxLength(2_000);
    }
}
