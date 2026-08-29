using Industrial.Diagnostics.Worker.Features.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Industrial.Diagnostics.Worker.Infrastructure.Data;

// Where the schema is defined: tables, relationships, constraints, indexes, precision.
// Registered through AddPooledDbContextFactory, so the worker creates and disposes one
// per message instead of holding a long-lived context.
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<TelemetryReading> TelemetryReadings => Set<TelemetryReading>();

    // The indexes are [Index] attributes on TelemetryReading so each constraint sits
    // next to the field it protects: UNIQUE (MessageId) for dedupe, and
    // (EquipmentId, OccurredAt) for the dashboard's read pattern.
    //
    // Dedupe is a unique index rather than a check-then-insert in code
    // A code check is advisory and races across worker replicas; the index cannot be
    //
    // Changing this needs `make migrate name=X && make db-update`.
}
